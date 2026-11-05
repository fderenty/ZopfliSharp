// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from squeeze.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using System.Diagnostics;

namespace ZopfliSharp.Internal;

/// <summary>
/// Optimal LZ77 parsing with a cost model, iterated: each pass prices symbols with the
/// statistics of the previous one, since the Huffman tree that sets the true costs only exists
/// once the parse does. Port of squeeze.c.
/// </summary>
internal static class Squeeze
{
    private const int MaxMatch = Constants.MaxMatch;
    private const int MinMatch = Constants.MinMatch;
    private const int NumLL = Constants.NumLL;
    private const int NumD = Constants.NumD;
    private const int WindowSize = Constants.WindowSize;
    private const int WindowMask = Constants.WindowMask;

    /// <summary>Symbol frequencies and the cost in bits they imply. Port of SymbolStats.</summary>
    internal sealed class SymbolStats
    {
        /// <summary>The literal and length symbols.</summary>
        public readonly int[] LitLens = new int[NumLL];

        /// <summary>The 32 distance symbols, not the 32768 possible distances.</summary>
        public readonly int[] Dists = new int[NumD];

        /// <summary>Length of each literal/length symbol in bits.</summary>
        public readonly double[] LlSymbols = new double[NumLL];

        /// <summary>Length of each distance symbol in bits.</summary>
        public readonly double[] DSymbols = new double[NumD];

        public void CopyFrom(SymbolStats source)
        {
            source.LitLens.CopyTo(LitLens, 0);
            source.Dists.CopyTo(Dists, 0);
            source.LlSymbols.CopyTo(LlSymbols, 0);
            source.DSymbols.CopyTo(DSymbols, 0);
        }

        public void ClearFreqs()
        {
            Array.Clear(LitLens);
            Array.Clear(Dists);
        }

        /// <summary>Calculates the entropy of the statistics. Port of CalculateStatistics.</summary>
        public void Calculate()
        {
            Tree.CalculateEntropy(LitLens, LlSymbols);
            Tree.CalculateEntropy(Dists, DSymbols);
        }

        /// <summary>Adds the symbols of the store, then recalculates. Port of GetStatistics.</summary>
        public void Add(Lz77Store store)
        {
            for (int i = 0; i < store.Size; i++)
            {
                if (store.Dists[i] == 0)
                {
                    LitLens[store.LitLens[i]]++;
                }
                else
                {
                    LitLens[Symbols.GetLengthSymbol(store.LitLens[i])]++;
                    Dists[Symbols.GetDistSymbol(store.Dists[i])]++;
                }
            }

            LitLens[256] = 1;  // End symbol.
            Calculate();
        }
    }

    /// <summary>"Multiply-With-Carry" generator of G. Marsaglia. Port of RanState.</summary>
    private struct RanState
    {
        private uint _w;
        private uint _z;

        public RanState()
        {
            _w = 1;
            _z = 2;
        }

        public uint Next()
        {
            _z = 36969 * (_z & 65535) + (_z >> 16);
            _w = 18000 * (_w & 65535) + (_w >> 16);
            return (_z << 16) + _w;  // 32-bit result.
        }
    }

    /// <summary>Port of AddWeighedStatFreqs. <paramref name="result"/> may be one of the inputs.</summary>
    private static void AddWeighedStatFreqs(SymbolStats stats1, double w1, SymbolStats stats2, double w2, SymbolStats result)
    {
        for (int i = 0; i < NumLL; i++)
            result.LitLens[i] = (int)(stats1.LitLens[i] * w1 + stats2.LitLens[i] * w2);
        for (int i = 0; i < NumD; i++)
            result.Dists[i] = (int)(stats1.Dists[i] * w1 + stats2.Dists[i] * w2);
        result.LitLens[256] = 1;  // End symbol.
    }

    private static void RandomizeFreqs(ref RanState state, int[] freqs)
    {
        int n = freqs.Length;
        for (int i = 0; i < n; i++)
        {
            if ((state.Next() >> 4) % 3 == 0)
                freqs[i] = freqs[state.Next() % (uint)n];
        }
    }

    private static void RandomizeStatFreqs(ref RanState state, SymbolStats stats)
    {
        RandomizeFreqs(ref state, stats.LitLens);
        RandomizeFreqs(ref state, stats.Dists);
        stats.LitLens[256] = 1;  // End symbol.
    }

    /// <summary>The cost in bits of a literal (dist 0) or of a length/distance pair.</summary>
    /// <remarks>
    /// The C code takes a function pointer. A struct type parameter makes the JIT compile one
    /// copy of the caller per model, with the cost inlined into its loops.
    /// </remarks>
    private interface ICostModel
    {
        double Cost(int litlen, int dist);
    }

    /// <summary>Cost model which exactly matches the fixed tree. Port of GetCostFixed.</summary>
    private readonly struct FixedCostModel : ICostModel
    {
        public double Cost(int litlen, int dist)
        {
            if (dist == 0)
                return litlen <= 143 ? 8 : 9;

            int dbits = Symbols.GetDistExtraBits(dist);
            int lbits = Symbols.GetLengthExtraBits(litlen);
            int lsym = Symbols.GetLengthSymbol(litlen);
            int cost = 0;
            if (lsym <= 279)
                cost += 7;
            else
                cost += 8;
            cost += 5;  // Every dist symbol has length 5.
            return cost + dbits + lbits;
        }
    }

    /// <summary>Cost model based on symbol statistics. Port of GetCostStat.</summary>
    private readonly struct StatCostModel(SymbolStats stats) : ICostModel
    {
        private readonly double[] _llSymbols = stats.LlSymbols;
        private readonly double[] _dSymbols = stats.DSymbols;

        public double Cost(int litlen, int dist)
        {
            if (dist == 0)
                return _llSymbols[litlen];

            int lsym = Symbols.GetLengthSymbol(litlen);
            int lbits = Symbols.GetLengthExtraBits(litlen);
            int dsym = Symbols.GetDistSymbol(dist);
            int dbits = Symbols.GetDistExtraBits(dist);
            // Same order of operations as the C code: the integer sum first.
            return lbits + dbits + _llSymbols[lsym] + _dSymbols[dsym];
        }
    }

    // The first distance of each distance symbol (RFC 1951 section 3.2.5). Only different
    // symbols affect the cost model, so only these need to be checked.
    private static ReadOnlySpan<ushort> DSymbolStarts =>
    [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513,
        769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
    ];

    /// <summary>
    /// The minimum cost the model can return for a valid length and distance. Port of
    /// GetCostModelMinCost.
    /// </summary>
    private static double GetCostModelMinCost<TCost>(TCost costmodel)
        where TCost : struct, ICostModel
    {
        int bestlength = 0;  // Length that has lowest cost in the cost model.
        int bestdist = 0;  // Distance that has lowest cost in the cost model.

        double mincost = Constants.LargeFloat;
        for (int i = 3; i < 259; i++)
        {
            double c = costmodel.Cost(i, 1);
            if (c < mincost)
            {
                bestlength = i;
                mincost = c;
            }
        }

        mincost = Constants.LargeFloat;
        ReadOnlySpan<ushort> dsymbols = DSymbolStarts;
        for (int i = 0; i < 30; i++)
        {
            double c = costmodel.Cost(3, dsymbols[i]);
            if (c < mincost)
            {
                bestdist = dsymbols[i];
                mincost = c;
            }
        }

        return costmodel.Cost(bestlength, bestdist);
    }

    /// <summary>
    /// The forward pass: the cheapest way to reach every byte from a previous one under the
    /// cost model. Port of GetBestLengths.
    /// </summary>
    /// <param name="s">The block state, whose cache speeds up the match search.</param>
    /// <param name="input">The input.</param>
    /// <param name="instart">Start of the block, inclusive.</param>
    /// <param name="inend">End of the block, exclusive.</param>
    /// <param name="costmodel">Prices literals and length/distance pairs.</param>
    /// <param name="lengthArray">Receives, for each byte, the length used to reach it.</param>
    /// <param name="h">The hash, reset here.</param>
    /// <param name="costs">Scratch of at least blocksize + 1 entries.</param>
    /// <returns>The cost of reaching the end according to the model.</returns>
    /// <remarks>
    /// The costs are stored as float while computed in double, exactly as in the C code. The
    /// rounding on each store is part of which path wins.
    /// </remarks>
    private static double GetBestLengths<TCost>(
        BlockState s, ReadOnlySpan<byte> input, int instart, int inend, TCost costmodel,
        ushort[] lengthArray, Hash h, float[] costs)
        where TCost : struct, ICostModel
    {
        int blocksize = inend - instart;
        Span<ushort> sublen = stackalloc ushort[259];
        int windowstart = instart > WindowSize ? instart - WindowSize : 0;
        double mincost = GetCostModelMinCost(costmodel);

        if (instart == inend)
            return 0;

        h.Reset();
        h.Warmup(input, windowstart, inend);
        for (int i = windowstart; i < instart; i++)
            h.Update(input, i, inend);

        costs.AsSpan(1, blocksize).Fill((float)Constants.LargeFloat);
        costs[0] = 0;  // Because it's the start.
        lengthArray[0] = 0;

        ushort[] same = h.Same;
        for (int i = instart; i < inend; i++)
        {
            int j = i - instart;  // Index in the costs array and lengthArray.
            h.Update(input, i, inend);

            // If we're in a long repetition of the same character and have more than MaxMatch
            // characters before and after our position.
            if (same[i & WindowMask] > MaxMatch * 2
                && i > instart + MaxMatch + 1
                && i + MaxMatch * 2 + 1 < inend
                && same[(i - MaxMatch) & WindowMask] > MaxMatch)
            {
                double symbolcost = costmodel.Cost(MaxMatch, 1);
                // Set the length to reach each one to MaxMatch, and the cost to the cost
                // corresponding to that length. Doing this, we skip MaxMatch values to avoid
                // calling FindLongestMatch.
                for (int k = 0; k < MaxMatch; k++)
                {
                    costs[j + MaxMatch] = (float)(costs[j] + symbolcost);
                    lengthArray[j + MaxMatch] = MaxMatch;
                    i++;
                    j++;
                    h.Update(input, i, inend);
                }
            }

            Lz77.FindLongestMatch(s, h, input, i, inend, MaxMatch, sublen, out _, out ushort leng);

            // Literal.
            if (i + 1 <= inend)
            {
                double newCost = costmodel.Cost(input[i], 0) + costs[j];
                Debug.Assert(newCost >= 0);
                if (newCost < costs[j + 1])
                {
                    costs[j + 1] = (float)newCost;
                    lengthArray[j + 1] = 1;
                }
            }

            // Lengths.
            int kend = Math.Min((int)leng, inend - i);
            double mincostaddcostj = mincost + costs[j];
            for (int k = 3; k <= kend; k++)
            {
                // Calling the cost model is expensive, avoid this if we are already at the
                // minimum possible cost that it can return.
                if (costs[j + k] <= mincostaddcostj)
                    continue;

                double newCost = costmodel.Cost(k, sublen[k]) + costs[j];
                Debug.Assert(newCost >= 0);
                if (newCost < costs[j + k])
                {
                    Debug.Assert(k <= MaxMatch);
                    costs[j + k] = (float)newCost;
                    lengthArray[j + k] = (ushort)k;
                }
            }
        }

        Debug.Assert(costs[blocksize] >= 0);
        return costs[blocksize];
    }

    /// <summary>
    /// Follows <paramref name="lengthArray"/> back from the end to give the lengths to use, in
    /// order. Port of TraceBackwards.
    /// </summary>
    private static void TraceBackwards(int size, ushort[] lengthArray, List<ushort> path)
    {
        path.Clear();
        if (size == 0)
            return;

        int index = size;
        while (true)
        {
            ushort length = lengthArray[index];
            path.Add(length);
            Debug.Assert(length <= index);
            Debug.Assert(length <= MaxMatch);
            Debug.Assert(length != 0);
            index -= length;
            if (index == 0)
                break;
        }

        // Mirror result.
        path.Reverse();
    }

    /// <summary>
    /// Turns a path of lengths into LZ77 commands, looking up the distance of each match.
    /// Port of FollowPath.
    /// </summary>
    private static void FollowPath(
        BlockState s, ReadOnlySpan<byte> input, int instart, int inend, List<ushort> path,
        Lz77Store store, Hash h)
    {
        if (instart == inend)
            return;

        int windowstart = instart > WindowSize ? instart - WindowSize : 0;

        h.Reset();
        h.Warmup(input, windowstart, inend);
        for (int i = windowstart; i < instart; i++)
            h.Update(input, i, inend);

        int pos = instart;
        for (int i = 0; i < path.Count; i++)
        {
            int length = path[i];
            Debug.Assert(pos < inend);

            h.Update(input, pos, inend);

            // Add to output.
            if (length >= MinMatch)
            {
                // Get the distance by recalculating longest match. The found length should
                // match the length from the path.
                Lz77.FindLongestMatch(s, h, input, pos, inend, length, [], out ushort dist, out ushort dummyLength);
                Debug.Assert(!(dummyLength != length && length > 2 && dummyLength > 2));
                Lz77.VerifyLenDist(input, inend, pos, dist, length);
                store.StoreLitLenDist(length, dist, pos);
            }
            else
            {
                length = 1;
                store.StoreLitLenDist(input[pos], 0, pos);
            }

            Debug.Assert(pos + length <= inend);
            for (int j = 1; j < length; j++)
                h.Update(input, pos + j, inend);

            pos += length;
        }
    }

    /// <summary>
    /// One run of the optimal parse: forward pass, trace back, follow the path. Port of
    /// LZ77OptimalRun.
    /// </summary>
    private static double OptimalRun<TCost>(
        BlockState s, ReadOnlySpan<byte> input, int instart, int inend, List<ushort> path,
        ushort[] lengthArray, TCost costmodel, Lz77Store store, Hash h, float[] costs)
        where TCost : struct, ICostModel
    {
        double cost = GetBestLengths(s, input, instart, inend, costmodel, lengthArray, h, costs);
        TraceBackwards(inend - instart, lengthArray, path);
        FollowPath(s, input, instart, inend, path, store, h);
        Debug.Assert(cost < Constants.LargeFloat);
        return cost;
    }

    /// <summary>
    /// Computes the LZ77 commands of a block by iterated optimal parsing. Bytes before
    /// <paramref name="instart"/> serve as dictionary. Port of ZopfliLZ77Optimal.
    /// </summary>
    public static void Optimal(
        BlockState s, ReadOnlySpan<byte> input, int instart, int inend, int numiterations,
        Lz77Store store, Hash h, CancellationToken cancellationToken)
    {
        // Dist to get to here with smallest cost.
        int blocksize = inend - instart;
        var lengthArray = new ushort[blocksize + 1];
        var costs = new float[blocksize + 1];
        var path = new List<ushort>();
        var currentstore = new Lz77Store();
        var stats = new SymbolStats();
        var beststats = new SymbolStats();
        var laststats = new SymbolStats();
        double bestcost = Constants.LargeFloat;
        double lastcost = 0;

        // Try randomizing the costs a bit once the size stabilizes.
        var ranState = new RanState();
        int lastrandomstep = -1;

        // Do regular deflate, then loop multiple shortest path runs, each time using the
        // statistics of the previous run.

        // Initial run.
        Lz77.Greedy(s, input, instart, inend, currentstore, h);
        stats.Add(currentstore);

        // Repeat statistics with each time the cost model from the previous stat run.
        for (int i = 0; i < numiterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            currentstore.Clear();
            OptimalRun(s, input, instart, inend, path, lengthArray, new StatCostModel(stats), currentstore, h, costs);
            double cost = Deflate.CalculateBlockSize(currentstore, 0, currentstore.Size, 2);

            if (cost < bestcost)
            {
                // Copy to the output store.
                store.CopyFrom(currentstore);
                beststats.CopyFrom(stats);
                bestcost = cost;
            }

            laststats.CopyFrom(stats);
            stats.ClearFreqs();
            stats.Add(currentstore);

            if (lastrandomstep != -1)
            {
                // This makes it converge slower but better. Do it only once the randomness kicks
                // in so that if the user does few iterations, it gives a better result sooner.
                AddWeighedStatFreqs(stats, 1.0, laststats, 0.5, stats);
                stats.Calculate();
            }

            if (i > 5 && cost == lastcost)
            {
                stats.CopyFrom(beststats);
                RandomizeStatFreqs(ref ranState, stats);
                stats.Calculate();
                lastrandomstep = i;
            }

            lastcost = cost;
        }
    }

    /// <summary>
    /// The best LZ77 commands for the fixed Huffman tree of deflate. The tree is known in
    /// advance, so a single run is enough. Port of ZopfliLZ77OptimalFixed.
    /// </summary>
    public static void OptimalFixed(
        BlockState s, ReadOnlySpan<byte> input, int instart, int inend, Lz77Store store, Hash h)
    {
        int blocksize = inend - instart;
        var lengthArray = new ushort[blocksize + 1];
        var costs = new float[blocksize + 1];
        var path = new List<ushort>();

        s.BlockStart = instart;
        s.BlockEnd = inend;

        // Shortest path for fixed tree. This one should give the shortest possible result for
        // fixed tree, no repeated runs are needed since the tree is known.
        OptimalRun(s, input, instart, inend, path, lengthArray, default(FixedCostModel), store, h, costs);
    }
}
