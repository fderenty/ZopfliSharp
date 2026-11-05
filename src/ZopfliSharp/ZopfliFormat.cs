// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from zopfli.h of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp;

/// <summary>The container around the compressed data.</summary>
public enum ZopfliFormat
{
    /// <summary>
    /// A raw deflate stream (RFC 1951), with no header or checksum. Read it with
    /// <see cref="System.IO.Compression.DeflateStream"/>.
    /// </summary>
    Deflate,

    /// <summary>
    /// A zlib stream (RFC 1950): deflate with a two-byte header and an Adler-32 checksum. This
    /// is what PNG image data and PDF <c>FlateDecode</c> streams hold. Read it with
    /// <see cref="System.IO.Compression.ZLibStream"/>.
    /// </summary>
    Zlib,

    /// <summary>
    /// A gzip file (RFC 1952): deflate with a header and a CRC-32 checksum, as written by the
    /// <c>zopfli</c> command line tool. Read it with <see cref="System.IO.Compression.GZipStream"/>.
    /// </summary>
    Gzip,
}
