# ZopfliSharp

[Zopfli](https://github.com/google/zopfli) compression in pure C#, with no native dependencies at all.

A faithful port of Google's C library: the deflate, zlib and gzip output is byte-for-byte identical to
the original's. It is typically 3 to 8% smaller than zlib at its best level, about a hundred times
slower to produce, and read by any standard decoder at the usual speed. That trade suits data
compressed once and read many times: PNG images, PDF streams, static web assets.

```csharp
using ZopfliSharp;

byte[] zlib = Zopfli.Compress(data, ZopfliFormat.Zlib);          // PNG IDAT, PDF FlateDecode
byte[] gzip = Zopfli.Compress(data, ZopfliFormat.Gzip);          // .gz files, HTTP
byte[] raw  = Zopfli.Compress(data, ZopfliFormat.Deflate);       // raw deflate

var options = new ZopfliOptions { Iterations = 5 };              // faster, slightly larger
Zopfli.Compress(data, stream, ZopfliFormat.Zlib, options, cancellationToken);

var threads = new ZopfliOptions { MaxDegreeOfParallelism = -1 }; // one large input, all cores,
byte[] same = Zopfli.Compress(data, ZopfliFormat.Zlib, threads); // the very same bytes
```

Thread-safe, trimming and NativeAOT safe, checked against the C library on Windows x64, x86 and
Arm64, Linux x64, Arm64 and 32-bit ARM, and macOS. Licensed under Apache-2.0, like Zopfli itself.
