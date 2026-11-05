// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from zopfli.h of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp.Internal;

/// <summary>
/// The settings of one compression, copied from <see cref="ZopfliOptions"/> when it starts so
/// that changes made to the options meanwhile cannot reach it. Port of ZopfliOptions (zopfli.h),
/// plus the degree of parallelism, which the C library does not have.
/// </summary>
internal readonly record struct Settings(int Iterations, bool BlockSplitting, int BlockSplittingMax, int MaxDegreeOfParallelism);

/// <summary>
/// Memory reused across the passes of one compression. The C code allocates a fresh hash and
/// cache for each pass; every pass resets them first, so reusing them changes nothing but the
/// number of allocations.
/// </summary>
/// <remarks>
/// A workspace serves one pass at a time; <see cref="WorkspacePool"/> hands one to each
/// thread.
/// </remarks>
internal sealed class Workspace
{
    public readonly Hash Hash = new();
    public readonly LongestMatchCache Cache = new();
}
