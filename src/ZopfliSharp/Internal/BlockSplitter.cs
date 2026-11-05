// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from blocksplitter.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using System.Diagnostics;

namespace ZopfliSharp.Internal;

/// <summary>
/// Chooses where to cut the input into deflate blocks, each with its own Huffman trees. Port
/// of blocksplitter.c.
/// </summary>
internal static class BlockSplitter
{
    private const int Num = 9;  // Points tried per round of FindMinimum. Good value: 9.

    /// <summary>
    /// Estimated cost of a block in bits: the trees plus every symbol and its extra bits. Port
    /// of EstimateCost.
    /// </summary>
    private static double EstimateCost(Lz77Store lz77, int lstart, int lend)
        => Deflate.CalculateBlockSizeAutoType(lz77, lstart, lend);

    /// <summary>
    /// The cost of cutting [<paramref name="start"/>, <paramref name="end"/>) at
    /// <paramref name="i"/>: the cost of both halves. Port of SplitCost.
    /// </summary>
    private static double SplitCost(Lz77Store lz77, int start, int end, int i)
        => EstimateCost(lz77, start, i) + EstimateCost(lz77, i, end);

    /// <summary>
    /// Finds the split point in [<paramref name="start"/>, <paramref name="end"/>) of least
    /// <see cref="SplitCost"/>: exhaustively on small ranges, otherwise by narrowing in on the
    /// best of a few evenly spaced points. Port of FindMinimum.
    /// </summary>
    /// <remarks>
    /// The costs of the candidates are independent, so they may be computed concurrently; the
    /// minimum is then searched in order, which keeps the first of equal costs as C does.
    /// </remarks>
    private static int FindMinimum(
        Lz77Store lz77, int cstart, int cend, int start, int end, Parallelism? parallelism, out double smallest)
    {
        if (end - start < 1024)
        {
            var costs = new double[end - start];
            int first = start;
            Parallelism.For(parallelism, costs.Length, k => costs[k] = SplitCost(lz77, cstart, cend, first + k));

            double best = Constants.LargeFloat;
            int result = start;
            for (int i = start; i < end; i++)
            {
                double v = costs[i - start];
                if (v < best)
                {
                    best = v;
                    result = i;
                }
            }

            smallest = best;
            return result;
        }
        else
        {
            // Try to find minimum faster by recursively checking multiple points.
            var p = new int[Num];
            var vp = new double[Num];
            double lastbest = Constants.LargeFloat;
            int pos = start;

            while (true)
            {
                if (end - start <= Num)
                    break;

                for (int i = 0; i < Num; i++)
                    p[i] = start + (i + 1) * ((end - start) / (Num + 1));

                Parallelism.For(parallelism, Num, i => vp[i] = SplitCost(lz77, cstart, cend, p[i]));

                int besti = 0;
                double best = vp[0];
                for (int i = 1; i < Num; i++)
                {
                    if (vp[i] < best)
                    {
                        best = vp[i];
                        besti = i;
                    }
                }

                if (best > lastbest)
                    break;

                start = besti == 0 ? start : p[besti - 1];
                end = besti == Num - 1 ? end : p[besti + 1];

                pos = p[besti];
                lastbest = best;
            }

            smallest = lastbest;
            return pos;
        }
    }

    /// <summary>Inserts a value, keeping the list sorted. Port of AddSorted.</summary>
    private static void AddSorted(int value, List<int> list)
    {
        int i = 0;
        while (i < list.Count && list[i] <= value)
            i++;
        list.Insert(i, value);
    }

    /// <summary>
    /// Finds the next block to try to split: the largest not yet known to be unsplittable, so
    /// that a limited number of blocks still comes out evenly sized. Port of
    /// FindLargestSplittableBlock.
    /// </summary>
    /// <returns>False when every block is done.</returns>
    private static bool FindLargestSplittableBlock(
        int lz77size, bool[] done, List<int> splitpoints, ref int lstart, ref int lend)
    {
        int longest = 0;
        bool found = false;
        int npoints = splitpoints.Count;
        for (int i = 0; i <= npoints; i++)
        {
            int start = i == 0 ? 0 : splitpoints[i - 1];
            // The last block ends one short of the store, exactly as in the C code.
            int end = i == npoints ? lz77size - 1 : splitpoints[i];
            if (!done[start] && end - start > longest)
            {
                lstart = start;
                lend = end;
                found = true;
                longest = end - start;
            }
        }

        return found;
    }

    /// <summary>
    /// Splits LZ77 data into blocks where that lowers the estimated total cost. Port of
    /// ZopfliBlockSplitLZ77.
    /// </summary>
    /// <param name="lz77">The LZ77 data.</param>
    /// <param name="maxblocks">Maximum number of blocks, 0 for no limit.</param>
    /// <param name="parallelism">Null to stay on the calling thread.</param>
    /// <param name="cancellationToken">Checked between candidate splits.</param>
    /// <returns>The split points, as indices into the LZ77 data, sorted.</returns>
    public static List<int> SplitLz77(Lz77Store lz77, int maxblocks, Parallelism? parallelism, CancellationToken cancellationToken)
    {
        var splitpoints = new List<int>();
        if (lz77.Size < 10)
            return splitpoints;  // This code fails on tiny files.

        var done = new bool[lz77.Size];
        int lstart = 0;
        int lend = lz77.Size;
        int numblocks = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (maxblocks > 0 && numblocks >= maxblocks)
                break;

            Debug.Assert(lstart < lend);
            int llpos = FindMinimum(lz77, lstart, lend, lstart + 1, lend, parallelism, out double splitcost);

            Debug.Assert(llpos > lstart);
            Debug.Assert(llpos < lend);

            double origcost = EstimateCost(lz77, lstart, lend);

            if (splitcost > origcost || llpos == lstart + 1 || llpos == lend)
            {
                done[lstart] = true;
            }
            else
            {
                AddSorted(llpos, splitpoints);
                numblocks++;
            }

            if (!FindLargestSplittableBlock(lz77.Size, done, splitpoints, ref lstart, ref lend))
                break;  // No further split will probably reduce compression.

            if (lend - lstart < 10)
                break;
        }

        return splitpoints;
    }

    /// <summary>
    /// Chooses split points for the input from a greedy LZ77 parse, which, unintuitively,
    /// gives better blocks than the optimal one. Port of ZopfliBlockSplit.
    /// </summary>
    /// <returns>The split points as positions in the input, sorted.</returns>
    public static List<int> Split(
        ReadOnlySpan<byte> input, int instart, int inend, int maxblocks, Hash h,
        Parallelism? parallelism, CancellationToken cancellationToken)
    {
        var store = new Lz77Store();
        var s = new BlockState(instart, inend, lmc: null);

        Lz77.Greedy(s, input, instart, inend, store, h);

        List<int> lz77splitpoints = SplitLz77(store, maxblocks, parallelism, cancellationToken);

        // Convert LZ77 positions to positions in the uncompressed input.
        var splitpoints = new List<int>(lz77splitpoints.Count);
        int pos = instart;
        if (lz77splitpoints.Count > 0)
        {
            for (int i = 0; i < store.Size; i++)
            {
                int length = store.Dists[i] == 0 ? 1 : store.LitLens[i];
                if (lz77splitpoints[splitpoints.Count] == i)
                {
                    splitpoints.Add(pos);
                    if (splitpoints.Count == lz77splitpoints.Count)
                        break;
                }

                pos += length;
            }
        }

        Debug.Assert(splitpoints.Count == lz77splitpoints.Count);
        return splitpoints;
    }
}
