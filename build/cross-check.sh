#!/bin/bash
# Publishes the benchmark with NativeAOT for each runtime identifier given, runs it on the test
# corpus (and any extra files), and compares every output byte for byte with Google's zopfli
# built for the same architecture. Targets the host cannot run natively run under qemu-user.
#
#   build/cross-check.sh [--i N] [--files "a b c"] linux-x64 linux-arm64 linux-arm
#
# Needs: the .NET 10 SDK; clang, lld and zlib headers (the NativeAOT link); for each foreign
# target its cross gcc, binutils and libc (aarch64-linux-gnu, arm-linux-gnueabihf) and
# qemu-user. Ubuntu packages: clang lld zlib1g-dev gcc-aarch64-linux-gnu
# binutils-aarch64-linux-gnu libc6-dev-arm64-cross gcc-arm-linux-gnueabihf
# binutils-arm-linux-gnueabihf libc6-dev-armhf-cross qemu-user.
#
# The reference is built twice. With -ffp-contract=off it is the C code to the letter, and
# ZopfliSharp must match it exactly. With the compiler's defaults, gcc may fuse a*b+c into one
# FMA instruction on ARM, rounding once instead of twice; that build is compared too, but a
# difference there is reported rather than failed, since it is the C build that departs.
set -euo pipefail

iterations=5
extra=""
rids=()
while [ $# -gt 0 ]; do
    case "$1" in
        --i) iterations=$2; shift 2 ;;
        --files) extra=$2; shift 2 ;;
        *) rids+=("$1"); shift ;;
    esac
done
[ ${#rids[@]} -gt 0 ] || { echo "usage: $0 [--i N] [--files \"...\"] rid..." >&2; exit 2; }

repo=$(cd "$(dirname "$0")/.." && pwd)
work=${CROSS_CHECK_DIR:-$(mktemp -d)}
mkdir -p "$work"

case "$(uname -m)" in
    x86_64) host=linux-x64 ;;
    aarch64) host=linux-arm64 ;;
    armv7l) host=linux-arm ;;
    *) echo "unsupported host $(uname -m)" >&2; exit 2 ;;
esac

# The C compiler and the way to run a binary, per target.
toolchain() {
    case "$1" in
        linux-x64) cc=gcc; qemu="" ;;
        linux-arm64) cc=aarch64-linux-gnu-gcc; qemu="qemu-aarch64 -L /usr/aarch64-linux-gnu" ;;
        linux-arm) cc=arm-linux-gnueabihf-gcc; qemu="qemu-arm -L /usr/arm-linux-gnueabihf" ;;
        *) echo "unknown target $1" >&2; exit 2 ;;
    esac
    if [ "$1" = "$host" ]; then
        cc=gcc
        qemu=""
    fi
}

publish() {
    local rid=$1
    # Any failure stops here: carrying on would compare the previous binaries.
    if ! dotnet publish "$repo/bench/ZopfliSharp.Bench" -c Release -r "$rid" -o "$work/aot/$rid" \
            --nologo -v q > "$work/publish-$rid.log" 2>&1; then
        cat "$work/publish-$rid.log"
        exit 1
    fi
    grep -E 'warning' "$work/publish-$rid.log" || true
}

if [ ! -d "$work/zopfli" ]; then
    git clone --quiet --depth 1 https://github.com/google/zopfli.git "$work/zopfli"
fi
echo "reference: google/zopfli $(git -C "$work/zopfli" log -1 --format='%h %ci')"

# The corpus comes from the benchmark built for the host.
publish "$host"
rm -rf "$work/corpus"
"$work/aot/$host/ZopfliSharp.Bench" --dump-corpus "$work/corpus"
# shellcheck disable=SC2206
files=("$work"/corpus/* $extra)

status=0
for rid in "${rids[@]}"; do
    toolchain "$rid"
    echo "=== $rid${qemu:+ (under ${qemu%% *})}"

    [ "$rid" = "$host" ] || publish "$rid"
    file -b "$work/aot/$rid/ZopfliSharp.Bench" | cut -d, -f1-2

    "$cc" "$work"/zopfli/src/zopfli/*.c -O2 -ffp-contract=off -static -lm -o "$work/zopfli-$rid-exact"
    "$cc" "$work"/zopfli/src/zopfli/*.c -O2 -static -lm -o "$work/zopfli-$rid-default"

    out="$work/out/$rid"
    rm -rf "$out"
    start=$(date +%s)
    $qemu "$work/aot/$rid/ZopfliSharp.Bench" --i "$iterations" --write "$out/cs" "${files[@]}" > /dev/null
    echo "ZopfliSharp: $(( $(date +%s) - start ))s for ${#files[@]} files, $iterations iterations"

    for variant in exact default; do
        mkdir -p "$out/$variant"
        for f in "${files[@]}"; do
            $qemu "$work/zopfli-$rid-$variant" --i"$iterations" --deflate -c "$f" > "$out/$variant/$(basename "$f").deflate"
        done
    done

    for f in "${files[@]}"; do
        n=$(basename "$f").deflate
        if ! cmp -s "$out/exact/$n" "$out/cs/$n"; then
            echo "DIFFERS from the C reference: $(basename "$f")"
            status=1
        fi
        if ! cmp -s "$out/default/$n" "$out/cs/$n"; then
            echo "note: differs from the C build with default flags: $(basename "$f")"
        fi
    done
done

[ $status -eq 0 ] && echo "all identical"
exit $status
