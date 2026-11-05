<#
.SYNOPSIS
    Publishes the benchmark with NativeAOT for each Windows target, runs it on the test corpus
    and compares every output byte for byte with Google's zopfli built by MSVC for the same
    architecture. The Windows counterpart of cross-check.sh.

.DESCRIPTION
    win-x86 is cross-compiled from x64 and runs there under WoW64. win-x64 and win-x86 both run
    on Arm64 Windows, under its emulation. win-arm64 is linked anywhere the MSVC ARM64 tools
    are installed, but runs only on an Arm64 machine: nothing emulates that direction, so on
    x64 it is built and reported as not run.

    Needs the .NET 10 SDK, git, and Visual Studio or its Build Tools with the C++ toolset of
    each target (VC.Tools.x86.x64; VC.Tools.ARM64 for win-arm64).

.PARAMETER Rid
    Targets to check. Defaults to win-x64 and win-x86.

.PARAMETER Iterations
    Zopfli iterations for every file, on both sides of the comparison.

.PARAMETER Files
    Files to compress beside the test corpus.

.EXAMPLE
    ./build/cross-check.ps1 -Rid win-x64, win-x86, win-arm64
#>
[CmdletBinding()]
param(
    [string[]] $Rid = @('win-x64', 'win-x86'),
    [int] $Iterations = 5,
    [string[]] $Files = @()
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = if ($env:CROSS_CHECK_DIR) { $env:CROSS_CHECK_DIR } else { Join-Path ([IO.Path]::GetTempPath()) 'zopflisharp-cross-check' }
New-Item -ItemType Directory -Force -Path $work | Out-Null

# The NativeAOT link can end up calling vswhere by name, and the Visual Studio installer does
# not put it on PATH; the link then fails with "'vswhere.exe' is not recognized".
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio, or its Build Tools, is not installed.' }
if (($env:PATH -split ';') -notcontains $installer) { $env:PATH = "$installer;$env:PATH" }

$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'The MSVC C++ toolset is not installed (Desktop development with C++).' }
$vcvarsall = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'

$hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$hostRid = "win-$hostArch"
$archOf = @{ 'win-x64' = 'x64'; 'win-x86' = 'x86'; 'win-arm64' = 'arm64' }

function Publish([string] $target) {
    $log = Join-Path $work "publish-$target.log"
    dotnet publish (Join-Path $repo 'bench/ZopfliSharp.Bench') -c Release -r $target -o (Join-Path $work "aot/$target") --nologo -v q *> $log
    # Any failure stops here: carrying on would compare the previous binaries.
    if ($LASTEXITCODE -ne 0) { Get-Content $log; throw "Publishing $target failed." }
    Select-String -Path $log -Pattern 'warning' | ForEach-Object { $_.Line }
}

# MSVC's defaults, as anybody building zopfli on Windows gets them: /fp:precise, which fuses no
# multiply-add, and SSE2 doubles on x86 as well.
function Build-Reference([string] $target) {
    $arch = $archOf[$target]
    $spec = if ($arch -eq $hostArch) { $arch } else { "${hostArch}_$arch" }
    $objects = Join-Path $work "obj-$target"
    New-Item -ItemType Directory -Force -Path $objects | Out-Null
    $exe = Join-Path $work "zopfli-$target.exe"
    $script = Join-Path $work "build-$target.cmd"
    @(
        '@echo off'
        "call `"$vcvarsall`" $spec >nul || exit /b 1"
        "pushd `"$objects`""
        "cl /nologo /O2 /W0 /Fe`"$exe`" `"$work\zopfli\src\zopfli\*.c`" > `"$work\cl-$target.log`" || (popd & exit /b 1)"
        'popd'
    ) | Set-Content -Path $script -Encoding ascii
    cmd /c "`"$script`""
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $work "cl-$target.log"); throw "Building the reference for $target failed." }
    return $exe
}

function Can-Run([string] $target) {
    # Arm64 Windows runs all three; x64 Windows runs x64 and x86.
    return $hostArch -eq 'arm64' -or $archOf[$target] -ne 'arm64'
}

$zopfli = Join-Path $work 'zopfli'
if (-not (Test-Path $zopfli)) {
    git clone --quiet --depth 1 https://github.com/google/zopfli.git $zopfli
    if ($LASTEXITCODE -ne 0) { throw 'Cloning google/zopfli failed.' }
}
Write-Host "reference: google/zopfli $(git -C $zopfli log -1 --format='%h %ci')"

# The corpus comes from the benchmark built for the host.
Publish $hostRid
$corpus = Join-Path $work 'corpus'
Remove-Item -Recurse -Force $corpus -ErrorAction SilentlyContinue
& (Join-Path $work "aot/$hostRid/ZopfliSharp.Bench.exe") --dump-corpus $corpus
$inputs = @(Get-ChildItem $corpus | ForEach-Object FullName) + $Files

$failed = @()
foreach ($target in $Rid) {
    Write-Host "=== $target" -ForegroundColor Cyan
    if ($target -ne $hostRid) { Publish $target }
    $reference = Build-Reference $target

    if (-not (Can-Run $target)) {
        Write-Host "built and linked; it runs only on Arm64 Windows" -ForegroundColor DarkGray
        continue
    }

    # The benchmark runs the reference itself on every file and exits 1 on any difference.
    & (Join-Path $work "aot/$target/ZopfliSharp.Bench.exe") --i $Iterations --c $reference @inputs
    if ($LASTEXITCODE -ne 0) { $failed += $target }
}

if ($failed) { throw "Output differs from the C reference on: $($failed -join ', ')" }
Write-Host 'all identical' -ForegroundColor Green
