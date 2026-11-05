# ZopfliSharp

**Zopfli compression for .NET, in pure C#.**

A port of Google's [Zopfli](https://github.com/google/zopfli) that produces **the same bytes as
the C library**: deflate, zlib and gzip output typically 3 to 8% smaller than zlib at its best
level, readable by any standard decoder. One NuGet package, no `PackageReference`, **no native
libraries**, thread-safe, trimming and NativeAOT safe.

```csharp
using ZopfliSharp;

byte[] compressed = Zopfli.Compress(data, ZopfliFormat.Zlib);
```

---

## Why this exists

Zopfli spends about a hundred times longer than zlib to find a smaller encoding of the same
deflate format, so decoders read it as fast as anything else. That trade pays wherever data is
compressed once and read many times: PNG images, the `FlateDecode` streams of a PDF, static web
assets served pre-compressed.

In .NET the only way to it so far has been a wrapper around the native library, which means one
binary per platform to ship and load. ZopfliSharp is the algorithm itself, in C#.

What it cannot do is shrink data that is already compressed. On the JPEG files of a camera it
gains 0.5 to 2%, at some two and a half seconds a megabyte: not worth it.

**Faithful, not inspired.** The port follows the C sources function by function, down to the
details that decide which of two equal-looking choices wins: the costs of the optimal parse kept
as `float` while computed as `double`, a truncated `1/ln 2`, a block-size heuristic that looks at
the whole store rather than the block. The result is checked byte for byte against the C library
on every test run.

**Built to be embedded.** Every call is independent: no static state, no console output, an
`OperationCanceledException` when you cancel, and the input taken as a `ReadOnlySpan<byte>`.

---

## Installing

Targets .NET 10.

```bash
dotnet add package ZopfliSharp
```

---

## Usage

```csharp
using ZopfliSharp;

// The three containers.
byte[] zlib = Zopfli.Compress(data, ZopfliFormat.Zlib);       // PNG IDAT, PDF FlateDecode
byte[] gzip = Zopfli.Compress(data, ZopfliFormat.Gzip);       // .gz files, Content-Encoding: gzip
byte[] raw  = Zopfli.Compress(data, ZopfliFormat.Deflate);    // raw deflate, as in ZIP entries

// Straight onto a stream. Nothing is written until compression is complete.
Zopfli.Compress(data, stream, ZopfliFormat.Zlib);

// Fewer iterations: faster, slightly larger. Cancellation is checked between passes.
var options = new ZopfliOptions { Iterations = 5 };
byte[] quick = Zopfli.Compress(data, ZopfliFormat.Zlib, options, cancellationToken);
```

Decompress with the BCL as usual: `ZLibStream`, `GZipStream` or `DeflateStream`.

### Options

| Option | Default | Meaning |
|---|---|---|
| `Iterations` | 15 | Passes of the optimal parse. Time grows about linearly; 5 suits inputs of several megabytes. |
| `BlockSplitting` | `true` | Cut the data into deflate blocks with their own Huffman trees where that pays. |
| `BlockSplittingMax` | 15 | Most blocks per megabyte of input; 0 for no limit. |
| `MaxDegreeOfParallelism` | 1 | Most threads one call may use; -1 for no limit. Never changes the output. |

The defaults are those of the `zopfli` command line tool, and so is the output.

### Threads

Calls share nothing, so independent inputs compress in parallel with no further ado, and each
gives the same bytes it would alone:

```csharp
Parallel.ForEach(streams, s => s.Compressed = Zopfli.Compress(s.Data, ZopfliFormat.Zlib));
```

That scales with the cores, and is the better choice whenever there are several inputs: the
frames of an animation, the streams of a PDF. For a single large input, one call can spread
itself over several threads instead:

```csharp
var options = new ZopfliOptions { MaxDegreeOfParallelism = -1 };
byte[] compressed = Zopfli.Compress(image, ZopfliFormat.Zlib, options);
```

The blocks the data is split into are compressed concurrently, and so are the candidate split
points; the results are combined in a fixed order, so the bytes are the same as with one
thread. The largest block still takes as long as it does alone, which bounds the gain within
one megabyte; beyond, every megabyte is independent. The option is off by default because a
library should not take threads its caller did not offer, least of all in a server.

---

## Performance

Against the C library built by gcc `-O2`, on Linux x64 with 15 iterations, single-threaded:

| Input | Size | Compressed | ZopfliSharp (NativeAOT) | C | Identical |
|---|---:|---:|---:|---:|:-:|
| Zopfli's own C sources | 164,017 | 35,577 | 0.91 s | 0.74 s | yes |
| A TrueType font | 106,524 | 46,214 | 0.84 s | 0.74 s | yes |
| PNG scanlines of a 480×300 photo | 576,300 | 133,237 | 6.55 s | 6.60 s | yes |
| First megabyte of `libc.so.6` | 1,000,000 | 447,500 | 3.33 s | 3.04 s | yes |

That is between level with the C code and a quarter slower. On Windows, against the C library
built by MSVC, it is faster more often than not: from 22% faster to 18% slower, in x64 and in
x86 alike. Under the JIT the first call also pays for compilation.

With `MaxDegreeOfParallelism`, on 8 cores, the same bytes:

| Input | 1 thread | 4 threads | 8 threads |
|---|---:|---:|---:|
| Zopfli's own C sources, 164 KB | 0.98 s | 0.65 s | 0.66 s |
| A TrueType font, 107 KB | 0.87 s | 0.49 s | 0.40 s |
| PNG scanlines of a photo, 576 KB | 6.58 s | 3.80 s | 3.80 s |
| First megabyte of `libc.so.6` | 3.32 s | 1.93 s | 1.28 s |
| All of the above and more, 3.3 MB | 20.93 s | 10.25 s | 6.25 s |

The photo shows the limit: one of its blocks takes 2.5 seconds alone, and no number of threads
shortens it.

`bench/ZopfliSharp.Bench` reproduces both tables (`--threads N`, and `--c path/to/zopfli` for
the comparison with a binary built from Google's sources).

---

## How it is tested

The test suite compares every output with the C library's, byte for byte, through its Python
binding (`pip install zopfli`), which compiles Google's sources unchanged. The inputs are
generated deterministically, each aimed at a path through the code: stored, fixed and dynamic
blocks, long runs of one byte, matches at the far edge of the window, block splitting with and
without limits, more than one master block. Every output is also decoded back with the BCL.

```bash
pip install zopfli
dotnet test
```

Without the binding, the comparisons report inconclusive. `ZOPFLISHARP_PYTHON` names the
interpreter to use; `ZOPFLISHARP_REQUIRE_REFERENCE=1`, as set in CI, makes a missing binding a
failure instead.

One caveat on "identical": the cost model calls the platform's `log`, as the C code does, so the
promise is the same output as the C library *on the same platform*. CI checks it on Linux x64
and Arm64, Windows and macOS.

### NativeAOT, on every architecture

`build/cross-check.sh` publishes the benchmark with NativeAOT for each target, runs it on the
test corpus (under `qemu-user` for a foreign architecture) and compares every output with
Google's `zopfli` built for that same architecture. `build/cross-check.ps1` does the same on
Windows, with the reference built by MSVC:

```bash
bash build/cross-check.sh linux-x64 linux-arm64 linux-arm
```

```powershell
./build/cross-check.ps1 -Rid win-x64, win-x86, win-arm64
```

CI runs both on every push: Linux x64, Arm64 and 32-bit ARM, Windows x64 and x86, and Windows
Arm64 on its own hardware. The test suite itself also passes in a 32-bit process
(`dotnet test --arch x86`). One subtlety it settles: on ARM, gcc fuses `a*b+c` into a single FMA
instruction by default, which rounds once where the C source rounds twice, and .NET never does
that on its own. The reference is therefore built with `-ffp-contract=off`, the C code to the
letter, and must match exactly; the build with default flags is compared too, for information.
So far they have never differed.

---

## What is not here

- **Decompression.** Zopfli only compresses; the BCL's decoders read its output.
- **ZopfliPNG.** The PNG optimizer that ships beside Zopfli, which also tries filter strategies
  and colour reductions, is a separate tool. Zopfli itself is all a PNG encoder needs to call.
- **Streaming input.** The optimal parse and the block splitter look at a whole megabyte at a
  time, so the input is taken whole.

---

## How this was built

The idea, the direction and every decision about what this should be are mine.
The code was written by an AI — Claude Opus — working to that direction: translated from
Google's C sources, and held to their output byte for byte.

## Licence

Apache-2.0, like Zopfli itself: ZopfliSharp is a derived work. [NOTICE](NOTICE) credits the
authors of the original, and every translated file names the C file it comes from.

## Prior art

- [google/zopfli](https://github.com/google/zopfli), the original by Lode Vandevenne and Jyrki
  Alakuijala.
- [libzopfli-sharp](https://github.com/echovoice/libzopfli-sharp) and
  [Koturn.Zopfli](https://github.com/koturn/Koturn.Zopfli), wrappers around the native library.
- [CafeUndZopfli](https://github.com/eustas/CafeUndZopfli), a Java port.
- [zopfli-csharp](https://github.com/Tisit/zopfli-csharp), a C# port written as a command line
  experiment.
