// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from lz77.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp.Internal;

/// <summary>
/// The block being compressed and its longest match cache. Port of ZopfliBlockState (lz77.c).
/// </summary>
internal sealed class BlockState
{
    /// <summary>The cache of matches found so far in this block, or null when not wanted.</summary>
    public LongestMatchCache? Lmc;

    /// <summary>Start of the block in the input, inclusive.</summary>
    public int BlockStart;

    /// <summary>End of the block in the input, exclusive.</summary>
    public int BlockEnd;

    /// <summary>Port of ZopfliInitBlockState.</summary>
    /// <param name="blockStart">Start of the block, inclusive.</param>
    /// <param name="blockEnd">End of the block, exclusive.</param>
    /// <param name="lmc">
    /// A cache to reset for this block and use, or null to search without one.
    /// </param>
    public BlockState(int blockStart, int blockEnd, LongestMatchCache? lmc)
    {
        BlockStart = blockStart;
        BlockEnd = blockEnd;
        Lmc = lmc;
        lmc?.Reset(blockEnd - blockStart);
    }
}
