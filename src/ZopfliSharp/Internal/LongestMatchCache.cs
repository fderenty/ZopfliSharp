// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from cache.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using System.Diagnostics;

namespace ZopfliSharp.Internal;

/// <summary>
/// Remembers the length and distance found at each position of a block, together with the
/// shortest distance for each shorter length (the "sublen" array), because the squeeze passes
/// ask for the same positions again and again. Port of cache.c.
/// </summary>
/// <remarks>
/// It costs 28 bytes per input byte. It never changes the output, only the speed.
/// </remarks>
internal sealed class LongestMatchCache
{
    private const int CacheLength = Constants.CacheLength;

    public ushort[] Length = [];
    public ushort[] Dist = [];
    public byte[] Sublen = [];

    /// <summary>
    /// Prepares the cache for a block of <paramref name="blocksize"/> bytes, reusing the arrays
    /// when they are large enough. Port of ZopfliInitCache.
    /// </summary>
    public void Reset(int blocksize)
    {
        if (Length.Length < blocksize)
        {
            Length = new ushort[blocksize];
            Dist = new ushort[blocksize];
            Sublen = new byte[CacheLength * 3 * blocksize];
        }

        // Length > 0 and dist 0 is an invalid combination, which marks on purpose that this
        // cache value is not filled in yet.
        Length.AsSpan(0, blocksize).Fill(1);
        Dist.AsSpan(0, blocksize).Clear();
        Sublen.AsSpan(0, CacheLength * 3 * blocksize).Clear();
    }

    /// <summary>Stores a sublen array in the cache. Port of ZopfliSublenToCache.</summary>
    public void SublenToCache(ReadOnlySpan<ushort> sublen, int pos, int length)
    {
        if (length < 3)
            return;

        Span<byte> cache = Sublen.AsSpan(CacheLength * pos * 3, CacheLength * 3);
        int j = 0;
        int bestlength = 0;
        for (int i = 3; i <= length; i++)
        {
            if (i == length || sublen[i] != sublen[i + 1])
            {
                cache[j * 3] = (byte)(i - 3);
                cache[j * 3 + 1] = (byte)sublen[i];
                cache[j * 3 + 2] = (byte)(sublen[i] >> 8);
                bestlength = i;
                j++;
                if (j >= CacheLength)
                    break;
            }
        }

        if (j < CacheLength)
        {
            Debug.Assert(bestlength == length);
            cache[(CacheLength - 1) * 3] = (byte)(bestlength - 3);
        }
        else
        {
            Debug.Assert(bestlength <= length);
        }

        Debug.Assert(bestlength == MaxCachedSublen(pos));
    }

    /// <summary>Extracts a sublen array from the cache. Port of ZopfliCacheToSublen.</summary>
    public void CacheToSublen(int pos, int length, Span<ushort> sublen)
    {
        if (length < 3)
            return;

        int maxlength = MaxCachedSublen(pos);
        int prevlength = 0;
        ReadOnlySpan<byte> cache = Sublen.AsSpan(CacheLength * pos * 3, CacheLength * 3);
        for (int j = 0; j < CacheLength; j++)
        {
            int len = cache[j * 3] + 3;
            ushort dist = (ushort)(cache[j * 3 + 1] + 256 * cache[j * 3 + 2]);
            for (int i = prevlength; i <= len; i++)
                sublen[i] = dist;

            if (len == maxlength)
                break;

            prevlength = len + 1;
        }
    }

    /// <summary>
    /// The length up to which the sublen of a position is cached, or 0 when none is. Port of
    /// ZopfliMaxCachedSublen, whose length argument is unused.
    /// </summary>
    public int MaxCachedSublen(int pos)
    {
        int c = CacheLength * pos * 3;
        if (Sublen[c + 1] == 0 && Sublen[c + 2] == 0)
            return 0;  // No sublen cached.

        return Sublen[c + (CacheLength - 1) * 3] + 3;
    }
}
