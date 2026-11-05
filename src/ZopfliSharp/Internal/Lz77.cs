// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from lz77.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using System.Diagnostics;

namespace ZopfliSharp.Internal;

/// <summary>
/// Longest match search and greedy LZ77 with lazy matching. Port of lz77.c.
/// </summary>
internal static class Lz77
{
    private const int MaxMatch = Constants.MaxMatch;
    private const int MinMatch = Constants.MinMatch;
    private const int WindowSize = Constants.WindowSize;
    private const int WindowMask = Constants.WindowMask;

    /// <summary>
    /// Gets a score of the length given the distance: the length itself, but one less when the
    /// distance is long enough to cost many extra bits. A heuristic for the greedy parser only.
    /// Port of GetLengthScore.
    /// </summary>
    private static int GetLengthScore(int length, int distance)
        // At 1024, the distance uses 9+ extra bits and this seems to be the sweet spot on
        // tested files.
        => distance > 1024 ? length - 1 : length;

    [Conditional("DEBUG")]
    public static void VerifyLenDist(ReadOnlySpan<byte> data, int datasize, int pos, int dist, int length)
    {
        Debug.Assert(pos + length <= datasize);
        for (int i = 0; i < length; i++)
        {
            if (data[pos - dist + i] != data[pos + i])
            {
                Debug.Fail($"Invalid match at {pos}: dist {dist}, length {length}");
                break;
            }
        }
    }

    /// <summary>
    /// Returns the position after the last byte from <paramref name="scan"/> on that equals the
    /// corresponding byte from <paramref name="match"/> on, stopping at <paramref name="end"/>.
    /// Port of GetMatch.
    /// </summary>
    private static int GetMatch(ReadOnlySpan<byte> array, int scan, int match, int end)
    {
        int n = end - scan;
        if (n <= 0)
            return scan;

        return scan + array.Slice(scan, n).CommonPrefixLength(array.Slice(match, n));
    }

    /// <summary>
    /// Gets distance, length and sublen values from the cache if possible. Returns true when it
    /// did; otherwise may lower <paramref name="limit"/> to what the cache knows. Port of
    /// TryGetFromLongestMatchCache.
    /// </summary>
    private static bool TryGetFromLongestMatchCache(
        BlockState s, int pos, ref int limit, Span<ushort> sublen, ref ushort distance, ref ushort length)
    {
        LongestMatchCache? lmc = s.Lmc;
        if (lmc is null)
            return false;

        // The cache starts at the beginning of the block rather than of the whole input.
        int lmcpos = pos - s.BlockStart;
        bool hasSublen = !sublen.IsEmpty;
        int cachedLength = lmc.Length[lmcpos];

        // Length > 0 and dist 0 is an invalid combination, which marks on purpose that this
        // cache value is not filled in yet.
        bool cacheAvailable = cachedLength == 0 || lmc.Dist[lmcpos] != 0;
        bool limitOkForCache = cacheAvailable
            && (limit == MaxMatch || cachedLength <= limit
                || (hasSublen && lmc.MaxCachedSublen(lmcpos) >= limit));

        if (limitOkForCache && cacheAvailable)
        {
            if (!hasSublen || cachedLength <= lmc.MaxCachedSublen(lmcpos))
            {
                length = (ushort)cachedLength;
                if (length > limit)
                    length = (ushort)limit;

                if (hasSublen)
                {
                    lmc.CacheToSublen(lmcpos, length, sublen);
                    // Below MinMatch this reads a slot nobody filled; no caller uses the
                    // distance of such a short length.
                    distance = sublen[length];
                    Debug.Assert(limit != MaxMatch || length < MinMatch || sublen[length] == lmc.Dist[lmcpos]);
                }
                else
                {
                    distance = lmc.Dist[lmcpos];
                }

                return true;
            }

            // Can't use much of the cache, since the sublens need to be calculated, but at least
            // we already know when to stop.
            limit = cachedLength;
        }

        return false;
    }

    /// <summary>
    /// Stores the found sublen, distance and length in the cache, if possible. Port of
    /// StoreInLongestMatchCache.
    /// </summary>
    private static void StoreInLongestMatchCache(
        BlockState s, int pos, int limit, ReadOnlySpan<ushort> sublen, ushort distance, ushort length)
    {
        LongestMatchCache? lmc = s.Lmc;
        if (lmc is null)
            return;

        int lmcpos = pos - s.BlockStart;
        bool cacheAvailable = lmc.Length[lmcpos] == 0 || lmc.Dist[lmcpos] != 0;

        if (limit == MaxMatch && !sublen.IsEmpty && !cacheAvailable)
        {
            Debug.Assert(lmc.Length[lmcpos] == 1 && lmc.Dist[lmcpos] == 0);
            lmc.Dist[lmcpos] = length < MinMatch ? (ushort)0 : distance;
            lmc.Length[lmcpos] = length < MinMatch ? (ushort)0 : length;
            Debug.Assert(!(lmc.Length[lmcpos] == 1 && lmc.Dist[lmcpos] == 0));
            lmc.SublenToCache(sublen, lmcpos, length);
        }
    }

    /// <summary>
    /// Finds the longest match (length and distance) at <paramref name="pos"/>. Port of
    /// ZopfliFindLongestMatch.
    /// </summary>
    /// <param name="s">The block state, whose cache is used when it has one.</param>
    /// <param name="h">The hash, updated up to and including <paramref name="pos"/>.</param>
    /// <param name="array">The input.</param>
    /// <param name="pos">Position to find the match for.</param>
    /// <param name="size">End of the data that may be matched.</param>
    /// <param name="limit">
    /// Maximum length, from <see cref="Constants.MinMatch"/> to <see cref="Constants.MaxMatch"/>.
    /// A lower limit can find a shorter distance for that length.
    /// </param>
    /// <param name="sublen">
    /// Empty, or 259 entries receiving, for each length, the smallest distance that reaches it.
    /// Only with one is the cache used, so it pays to pass one even when not needed.
    /// </param>
    /// <param name="distance">The distance found.</param>
    /// <param name="length">The length found.</param>
    public static void FindLongestMatch(
        BlockState s, Hash h, ReadOnlySpan<byte> array, int pos, int size, int limit,
        Span<ushort> sublen, out ushort distance, out ushort length)
    {
        distance = 0;
        length = 0;
        if (TryGetFromLongestMatchCache(s, pos, ref limit, sublen, ref distance, ref length))
        {
            Debug.Assert(pos + length <= size);
            return;
        }

        Debug.Assert(limit <= MaxMatch);
        Debug.Assert(limit >= MinMatch);
        Debug.Assert(pos < size);

        if (size - pos < MinMatch)
        {
            // The rest of the code assumes there are at least MinMatch bytes to try.
            length = 0;
            distance = 0;
            return;
        }

        if (pos + limit > size)
            limit = size - pos;

        int arrayend = pos + limit;
        int hpos = pos & WindowMask;
        int bestdist = 0;
        int bestlength = 1;
        int chainCounter = Constants.MaxChainHits;  // For quitting early.
        bool hasSublen = !sublen.IsEmpty;

        ushort[] hprev = h.Prev;
        ushort[] same = h.Same;
        bool onSecondHash = false;

        int pp = h.Head[h.Val];  // During the whole loop, p == hprev[pp].
        int p = hprev[pp];
        Debug.Assert(pp == hpos);

        int dist = p < pp ? pp - p : (WindowSize - p) + pp;

        // Go through all distances.
        while (dist < WindowSize)
        {
            int currentlength = 0;
            Debug.Assert(p < WindowSize);

            if (dist > 0)
            {
                Debug.Assert(dist <= pos);
                int scan = pos;
                int match = pos - dist;

                // Testing the byte at position bestlength first goes slightly faster.
                if (pos + bestlength >= size || array[scan + bestlength] == array[match + bestlength])
                {
                    int same0 = same[pos & WindowMask];
                    if (same0 > 2 && array[scan] == array[match])
                    {
                        int same1 = same[(pos - dist) & WindowMask];
                        int skip = same0 < same1 ? same0 : same1;
                        if (skip > limit)
                            skip = limit;
                        scan += skip;
                        match += skip;
                    }

                    scan = GetMatch(array, scan, match, arrayend);
                    currentlength = scan - pos;  // The found length.
                }

                if (currentlength > bestlength)
                {
                    if (hasSublen)
                    {
                        for (int j = bestlength + 1; j <= currentlength; j++)
                            sublen[j] = (ushort)dist;
                    }

                    bestdist = dist;
                    bestlength = currentlength;
                    if (currentlength >= limit)
                        break;
                }
            }

            // Switch to the other hash once this will be more efficient: the one that encodes
            // the length of the run of identical bytes and the first byte.
            if (!onSecondHash && bestlength >= same[hpos] && h.Val2 == h.HashVal2[p])
            {
                onSecondHash = true;
                hprev = h.Prev2;
            }

            pp = p;
            p = hprev[p];
            if (p == pp)
                break;  // Uninited prev value.

            dist += p < pp ? pp - p : (WindowSize - p) + pp;

            chainCounter--;
            if (chainCounter <= 0)
                break;
        }

        StoreInLongestMatchCache(s, pos, limit, sublen, (ushort)bestdist, (ushort)bestlength);

        Debug.Assert(bestlength <= limit);
        distance = (ushort)bestdist;
        length = (ushort)bestlength;
        Debug.Assert(pos + length <= size);
    }

    /// <summary>
    /// LZ77 like gzip's, greedy with one step of lazy matching, rather than the slow but better
    /// optimal parse. Bytes before <paramref name="instart"/> serve as dictionary. Port of
    /// ZopfliLZ77Greedy.
    /// </summary>
    public static void Greedy(
        BlockState s, ReadOnlySpan<byte> input, int instart, int inend, Lz77Store store, Hash h)
    {
        if (instart == inend)
            return;

        int windowstart = instart > WindowSize ? instart - WindowSize : 0;
        Span<ushort> dummysublen = stackalloc ushort[259];

        // Lazy matching.
        int prevLength = 0;
        int prevMatch = 0;
        bool matchAvailable = false;

        h.Reset();
        h.Warmup(input, windowstart, inend);
        for (int i = windowstart; i < instart; i++)
            h.Update(input, i, inend);

        for (int i = instart; i < inend; i++)
        {
            h.Update(input, i, inend);

            FindLongestMatch(s, h, input, i, inend, MaxMatch, dummysublen, out ushort dist, out ushort leng);
            int lengthscore = GetLengthScore(leng, dist);

            // Lazy matching.
            int prevlengthscore = GetLengthScore(prevLength, prevMatch);
            if (matchAvailable)
            {
                matchAvailable = false;
                if (lengthscore > prevlengthscore + 1)
                {
                    store.StoreLitLenDist(input[i - 1], 0, i - 1);
                    if (lengthscore >= MinMatch && leng < MaxMatch)
                    {
                        matchAvailable = true;
                        prevLength = leng;
                        prevMatch = dist;
                        continue;
                    }
                }
                else
                {
                    // Add previous to output.
                    leng = (ushort)prevLength;
                    dist = (ushort)prevMatch;
                    VerifyLenDist(input, inend, i - 1, dist, leng);
                    store.StoreLitLenDist(leng, dist, i - 1);
                    for (int j = 2; j < leng; j++)
                    {
                        Debug.Assert(i < inend);
                        i++;
                        h.Update(input, i, inend);
                    }
                    continue;
                }
            }
            else if (lengthscore >= MinMatch && leng < MaxMatch)
            {
                matchAvailable = true;
                prevLength = leng;
                prevMatch = dist;
                continue;
            }
            // End of lazy matching.

            // Add to output.
            if (lengthscore >= MinMatch)
            {
                VerifyLenDist(input, inend, i, dist, leng);
                store.StoreLitLenDist(leng, dist, i);
            }
            else
            {
                leng = 1;
                store.StoreLitLenDist(input[i], 0, i);
            }

            for (int j = 1; j < leng; j++)
            {
                Debug.Assert(i < inend);
                i++;
                h.Update(input, i, inend);
            }
        }
    }
}
