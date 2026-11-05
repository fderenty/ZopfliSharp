// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from util.h of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp.Internal;

/// <summary>
/// The tuning constants of Zopfli. Port of util.h.
/// </summary>
/// <remarks>
/// Every value here takes part in the output. Changing any of them gives a valid stream that is
/// no longer identical to the one the C library produces.
/// </remarks>
internal static class Constants
{
    /// <summary>Maximum length that can be encoded in deflate.</summary>
    public const int MaxMatch = 258;

    /// <summary>Minimum length that can be encoded in deflate.</summary>
    public const int MinMatch = 3;

    /// <summary>Number of distinct literal/length symbols in deflate.</summary>
    public const int NumLL = 288;

    /// <summary>Number of distinct distance symbols in deflate.</summary>
    public const int NumD = 32;

    /// <summary>The deflate window: the farthest back a match may reach.</summary>
    public const int WindowSize = 32768;

    /// <summary>Wraps a position into the window. <see cref="WindowSize"/> is a power of two.</summary>
    public const int WindowMask = WindowSize - 1;

    /// <summary>
    /// Input is cut into master blocks of this size, each compressed independently (with the
    /// previous window as dictionary). It bounds the memory used on huge inputs, at a small cost.
    /// </summary>
    public const int MasterBlockSize = 1000000;

    /// <summary>Stands for infinity in cost comparisons.</summary>
    public const double LargeFloat = 1e30;

    /// <summary>
    /// Entries kept per position in the longest match cache. Speed only: the cache never changes
    /// the result.
    /// </summary>
    public const int CacheLength = 8;

    /// <summary>
    /// How many hash chain links the match finder follows before giving up. This one does affect
    /// the output, on inputs where one hash value repeats very often.
    /// </summary>
    public const int MaxChainHits = 8192;
}
