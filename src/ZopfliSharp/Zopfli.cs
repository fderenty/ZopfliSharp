// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from zopfli_lib.c, zlib_container.c and gzip_container.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011-2013 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using ZopfliSharp.Internal;

namespace ZopfliSharp;

/// <summary>
/// Compresses data with Zopfli: deflate, zlib or gzip output, typically 3 to 8% smaller than
/// zlib at its best level and about a hundred times slower to produce. Any standard decoder
/// reads the result; decompression is no slower than for any other deflate stream.
/// </summary>
/// <remarks>
/// <para>
/// The output is identical, byte for byte, to that of Google's C library given the same
/// options.
/// </para>
/// <para>
/// Every method is thread-safe: concurrent calls share no state, so independent inputs, such
/// as the streams of a PDF or the frames of an animation, can be compressed in parallel. A
/// single large input can also be spread over several threads with
/// <see cref="ZopfliOptions.MaxDegreeOfParallelism"/>.
/// </para>
/// </remarks>
public static class Zopfli
{
    /// <summary>Compresses data into a new array.</summary>
    /// <param name="data">The data to compress.</param>
    /// <param name="format">The container to wrap the compressed data in.</param>
    /// <param name="options">How hard to work; null for the defaults.</param>
    /// <param name="cancellationToken">
    /// Checked between the passes over the data, so a cancellation lands within a fraction of
    /// a second on typical inputs.
    /// </param>
    /// <returns>The compressed data.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not defined.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static byte[] Compress(
        ReadOnlySpan<byte> data, ZopfliFormat format, ZopfliOptions? options = null,
        CancellationToken cancellationToken = default)
        => CompressCore(data, format, options, cancellationToken).ToArray();

    /// <summary>Compresses data onto a stream.</summary>
    /// <param name="data">The data to compress.</param>
    /// <param name="destination">Where the compressed data is written. It is not closed.</param>
    /// <param name="format">The container to wrap the compressed data in.</param>
    /// <param name="options">How hard to work; null for the defaults.</param>
    /// <param name="cancellationToken">
    /// Checked between the passes over the data. Nothing is written to
    /// <paramref name="destination"/> before compression completes, so a cancelled call leaves
    /// it untouched.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not defined.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static void Compress(
        ReadOnlySpan<byte> data, Stream destination, ZopfliFormat format, ZopfliOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(CompressCore(data, format, options, cancellationToken).WrittenSpan);
    }

    private static BitWriter CompressCore(
        ReadOnlySpan<byte> data, ZopfliFormat format, ZopfliOptions? options, CancellationToken cancellationToken)
    {
        if (format is not (ZopfliFormat.Deflate or ZopfliFormat.Zlib or ZopfliFormat.Gzip))
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown format.");

        options ??= DefaultOptions;
        var settings = new Settings(options.Iterations, options.BlockSplitting, options.BlockSplittingMax, options.MaxDegreeOfParallelism);

        // The work items of a parallel compression need the input as an array, and a span may
        // live on the caller's stack. Next to the time Zopfli takes, the copy costs nothing.
        byte[] input = data.ToArray();

        // Zopfli rarely does worse than half the input; the writer grows if it must.
        var writer = new BitWriter(input.Length / 2 + 64);

        switch (format)
        {
            case ZopfliFormat.Deflate:
                Deflate.Compress(settings, 2 /* dynamic block */, final: true, input, writer, cancellationToken);
                break;

            case ZopfliFormat.Zlib:
                WriteZlib(settings, input, writer, cancellationToken);
                break;

            case ZopfliFormat.Gzip:
                WriteGzip(settings, input, writer, cancellationToken);
                break;
        }

        return writer;
    }

    // Never handed out, so nobody can change the defaults for everyone else.
    private static readonly ZopfliOptions DefaultOptions = new();

    /// <summary>The zlib container (RFC 1950). Port of ZopfliZlibCompress.</summary>
    private static void WriteZlib(in Settings settings, byte[] data, BitWriter writer, CancellationToken cancellationToken)
    {
        uint checksum = Checksums.Adler32(data);

        const int Cmf = 120;  // CM 8, CINFO 7. See zlib spec.
        const int Flevel = 3;
        const int Fdict = 0;
        int cmfflg = 256 * Cmf + Fdict * 32 + Flevel * 64;
        int fcheck = 31 - cmfflg % 31;
        cmfflg += fcheck;

        writer.AppendByte((byte)(cmfflg / 256));
        writer.AppendByte((byte)(cmfflg % 256));

        Deflate.Compress(settings, 2 /* dynamic block */, final: true, data, writer, cancellationToken);

        writer.AppendByte((byte)(checksum >> 24));
        writer.AppendByte((byte)(checksum >> 16));
        writer.AppendByte((byte)(checksum >> 8));
        writer.AppendByte((byte)checksum);
    }

    /// <summary>The gzip container (RFC 1952). Port of ZopfliGzipCompress.</summary>
    private static void WriteGzip(in Settings settings, byte[] data, BitWriter writer, CancellationToken cancellationToken)
    {
        uint crc = Checksums.Crc32(data);

        writer.AppendByte(31);  // ID1
        writer.AppendByte(139);  // ID2
        writer.AppendByte(8);  // CM
        writer.AppendByte(0);  // FLG
        // MTIME
        writer.AppendByte(0);
        writer.AppendByte(0);
        writer.AppendByte(0);
        writer.AppendByte(0);
        writer.AppendByte(2);  // XFL, 2 indicates best compression.
        writer.AppendByte(3);  // OS follows Unix conventions.

        Deflate.Compress(settings, 2 /* dynamic block */, final: true, data, writer, cancellationToken);

        // CRC
        writer.AppendByte((byte)crc);
        writer.AppendByte((byte)(crc >> 8));
        writer.AppendByte((byte)(crc >> 16));
        writer.AppendByte((byte)(crc >> 24));

        // ISIZE: the size modulo 2^32.
        uint isize = (uint)data.Length;
        writer.AppendByte((byte)isize);
        writer.AppendByte((byte)(isize >> 8));
        writer.AppendByte((byte)(isize >> 16));
        writer.AppendByte((byte)(isize >> 24));
    }
}
