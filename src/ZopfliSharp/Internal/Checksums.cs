// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from zlib_container.c and gzip_container.c of Zopfli, https://github.com/google/zopfli
// Copyright 2013 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp.Internal;

/// <summary>
/// The checksums of the zlib and gzip containers. Port of the adler32 of zlib_container.c and
/// the CRC of gzip_container.c.
/// </summary>
internal static class Checksums
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        // CRC polynomial: 0xedb88320.
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>The CRC-32 of gzip (RFC 1952).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint[] table = CrcTable;
        uint result = 0xFFFFFFFFu;
        foreach (byte b in data)
            result = table[(result ^ b) & 0xFF] ^ (result >> 8);
        return result ^ 0xFFFFFFFFu;
    }

    /// <summary>The Adler-32 of zlib (RFC 1950).</summary>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        // The largest number of bytes the sums can take before they must be reduced, the same
        // as in the C code.
        const int SumsOverflow = 5550;

        uint s1 = 1;
        uint s2 = 0;
        while (!data.IsEmpty)
        {
            int amount = Math.Min(data.Length, SumsOverflow);
            foreach (byte b in data[..amount])
            {
                s1 += b;
                s2 += s1;
            }

            s1 %= 65521;
            s2 %= 65521;
            data = data[amount..];
        }

        return (s2 << 16) | s1;
    }
}
