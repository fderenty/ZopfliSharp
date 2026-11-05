// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from zopfli.h and util.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp;

/// <summary>
/// How hard Zopfli works. The defaults are those of the <c>zopfli</c> command line tool.
/// </summary>
public sealed class ZopfliOptions
{
    /// <summary>
    /// How many times the optimal parse is repeated, each time with the statistics of the
    /// previous one. More gives smaller output, slowly: the time grows about linearly. Defaults
    /// to 15. Good values: 10 or 15 for small inputs, 5 for inputs of several megabytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int Iterations
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 15;

    /// <summary>
    /// Whether to split the data into several deflate blocks, each with its own Huffman trees,
    /// at the boundaries that give the smallest output. Gives better compression. On by default.
    /// </summary>
    public bool BlockSplitting { get; set; } = true;

    /// <summary>
    /// The most blocks one megabyte of input is split into; 0 means no limit, which can hurt
    /// compression on some inputs. Defaults to 15.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int BlockSplittingMax
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 15;

    /// <summary>
    /// The most threads one compression may use: 1, the default, keeps it on the calling
    /// thread; -1 sets no limit but the thread pool's. The output is the same whatever the value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Within each megabyte of input, the blocks it is split into are compressed concurrently.
    /// The largest block still takes as long as it does alone, which bounds the gain on inputs
    /// under a megabyte to about 1.5 to 3 times. Beyond that, each megabyte is independent and
    /// the gain follows the number of cores.
    /// </para>
    /// <para>
    /// Independent inputs, such as the frames of an animation or the streams of a PDF, scale
    /// better compressed in parallel by the caller, one thread each; this option is for a single
    /// large input. Each thread at work holds its own buffers, about 30 bytes per byte of the
    /// block it is compressing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is 0 or less than -1.</exception>
    public int MaxDegreeOfParallelism
    {
        get;
        set
        {
            if (value is 0 or < -1)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Must be -1 or at least 1.");
            field = value;
        }
    } = 1;
}
