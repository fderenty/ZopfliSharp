// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from katajainen.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp.Internal;

/// <summary>
/// Length-limited Huffman code lengths by the boundary package-merge algorithm, from "A Fast
/// and Space-Economical Algorithm for Length-Limited Coding" by Jyrki Katajainen, Alistair
/// Moffat and Andrew Turpin. Port of katajainen.c.
/// </summary>
/// <remarks>
/// The C code links nodes with pointers into a pool. Here the pool is a set of parallel arrays
/// and a link is an index into them, -1 standing for the null pointer. The arrays are kept per
/// thread and reused, because this runs thousands of times per block while the block splitter
/// weighs its candidates.
/// </remarks>
internal static class Katajainen
{
    private const int NoNode = -1;

    // Largest pool any call needs: maxbits (at most 15) * 2 * numsymbols (at most 288).
    private const int MaxPool = 15 * 2 * Constants.NumLL;

    [ThreadStatic] private static Scratch? _scratch;

    private sealed class Scratch
    {
        // The node pool. A node is a chain: total weight, the leaf count it covers, and the
        // previous chain in its list.
        public readonly long[] Weight = new long[MaxPool];
        public readonly int[] Count = new int[MaxPool];
        public readonly int[] Tail = new int[MaxPool];

        // The leaves, sorted from lightest to heaviest: weight and the symbol each stands for.
        public readonly long[] LeafKey = new long[Constants.NumLL];
        public readonly long[] LeafWeight = new long[Constants.NumLL];
        public readonly int[] LeafSymbol = new int[Constants.NumLL];

        // Two lookahead chains per list: lists[i][j] lives at [i * 2 + j].
        public readonly int[] Lists = new int[2 * 15];

        public int Next;
        public int NumSymbols;
    }

    /// <summary>
    /// Computes code lengths of at most <paramref name="maxbits"/> bits for symbols with the given
    /// frequencies. Symbols of frequency zero get length zero.
    /// </summary>
    /// <returns>False when <paramref name="maxbits"/> is too small for the number of used symbols.</returns>
    public static bool LengthLimitedCodeLengths(ReadOnlySpan<int> frequencies, int maxbits, Span<int> bitlengths)
    {
        int n = frequencies.Length;
        Scratch s = _scratch ??= new Scratch();

        bitlengths[..n].Clear();

        // Count used symbols and place them in the leaves. The symbol index goes in the low 9
        // bits of the sort key, which makes every key distinct: the order is then the same
        // whatever sort algorithm runs, which is what keeps the output identical to C's qsort.
        int numsymbols = 0;
        for (int i = 0; i < n; i++)
        {
            if (frequencies[i] != 0)
                s.LeafKey[numsymbols++] = ((long)frequencies[i] << 9) | (uint)i;
        }

        // Check special cases and error conditions.
        if ((1 << maxbits) < numsymbols)
            return false;
        if (numsymbols == 0)
            return true;
        if (numsymbols == 1)
        {
            bitlengths[(int)(s.LeafKey[0] & 511)] = 1;
            return true;
        }
        if (numsymbols == 2)
        {
            bitlengths[(int)(s.LeafKey[0] & 511)]++;
            bitlengths[(int)(s.LeafKey[1] & 511)]++;
            return true;
        }

        Span<long> keys = s.LeafKey.AsSpan(0, numsymbols);
        keys.Sort();
        for (int i = 0; i < numsymbols; i++)
        {
            s.LeafWeight[i] = keys[i] >> 9;
            s.LeafSymbol[i] = (int)(keys[i] & 511);
        }

        if (numsymbols - 1 < maxbits)
            maxbits = numsymbols - 1;

        s.Next = 0;
        s.NumSymbols = numsymbols;
        InitLists(s, maxbits);

        // In the last list, 2 * numsymbols - 2 active chains need to be created. Two are already
        // created in the initialization. Each BoundaryPM run creates one.
        int numBoundaryPMRuns = 2 * numsymbols - 4;
        for (int i = 0; i < numBoundaryPMRuns - 1; i++)
            BoundaryPM(s, maxbits - 1);

        BoundaryPMFinal(s, maxbits - 1);
        ExtractBitLengths(s, s.Lists[(maxbits - 1) * 2 + 1], bitlengths);
        return true;
    }

    private static int NewNode(Scratch s, long weight, int count, int tail)
    {
        int node = s.Next++;
        s.Weight[node] = weight;
        s.Count[node] = count;
        s.Tail[node] = tail;
        return node;
    }

    /// <summary>Initializes each list with the two lightest leaves as lookahead chains.</summary>
    private static void InitLists(Scratch s, int maxbits)
    {
        int node0 = NewNode(s, s.LeafWeight[0], 1, NoNode);
        int node1 = NewNode(s, s.LeafWeight[1], 2, NoNode);
        for (int i = 0; i < maxbits; i++)
        {
            s.Lists[i * 2] = node0;
            s.Lists[i * 2 + 1] = node1;
        }
    }

    /// <summary>
    /// Performs a Boundary Package-Merge step: puts a new chain in list <paramref name="index"/>,
    /// either a leaf or a combination of the two lookahead chains of the previous list.
    /// </summary>
    private static void BoundaryPM(Scratch s, int index)
    {
        int[] lists = s.Lists;
        int lastcount = s.Count[lists[index * 2 + 1]];  // Count of last chain of list.

        if (index == 0 && lastcount >= s.NumSymbols)
            return;

        int oldchain = lists[index * 2 + 1];
        int newchain = s.Next++;

        // These are set up before the recursive calls below, so that there is a list pointing
        // to the new node.
        lists[index * 2] = oldchain;
        lists[index * 2 + 1] = newchain;

        if (index == 0)
        {
            // New leaf node in list 0.
            Init(s, newchain, s.LeafWeight[lastcount], lastcount + 1, NoNode);
        }
        else
        {
            long sum = s.Weight[lists[(index - 1) * 2]] + s.Weight[lists[(index - 1) * 2 + 1]];
            if (lastcount < s.NumSymbols && sum > s.LeafWeight[lastcount])
            {
                // New leaf inserted in list, so count is incremented.
                Init(s, newchain, s.LeafWeight[lastcount], lastcount + 1, s.Tail[oldchain]);
            }
            else
            {
                Init(s, newchain, sum, lastcount, lists[(index - 1) * 2 + 1]);
                // Two lookahead chains of previous list used up, create new ones.
                BoundaryPM(s, index - 1);
                BoundaryPM(s, index - 1);
            }
        }
    }

    private static void BoundaryPMFinal(Scratch s, int index)
    {
        int[] lists = s.Lists;
        int lastcount = s.Count[lists[index * 2 + 1]];  // Count of last chain of list.
        long sum = s.Weight[lists[(index - 1) * 2]] + s.Weight[lists[(index - 1) * 2 + 1]];

        if (lastcount < s.NumSymbols && sum > s.LeafWeight[lastcount])
        {
            // The C code takes the next free node without advancing the pool and sets only its
            // count and tail; its weight is never read again.
            int newchain = s.Next;
            int oldchain = s.Tail[lists[index * 2 + 1]];
            lists[index * 2 + 1] = newchain;
            s.Count[newchain] = lastcount + 1;
            s.Tail[newchain] = oldchain;
        }
        else
        {
            s.Tail[lists[index * 2 + 1]] = lists[(index - 1) * 2 + 1];
        }
    }

    private static void Init(Scratch s, int node, long weight, int count, int tail)
    {
        s.Weight[node] = weight;
        s.Count[node] = count;
        s.Tail[node] = tail;
    }

    /// <summary>
    /// Converts the result of boundary package-merge to bit lengths. The last chain of the last
    /// list holds the number of active leaves in each list.
    /// </summary>
    private static void ExtractBitLengths(Scratch s, int chain, Span<int> bitlengths)
    {
        Span<int> counts = stackalloc int[16];
        counts.Clear();
        int end = 16;
        int ptr = 15;
        int value = 1;

        for (int node = chain; node != NoNode; node = s.Tail[node])
            counts[--end] = s.Count[node];

        int val = counts[15];
        while (ptr >= end)
        {
            for (; val > counts[ptr - 1]; val--)
                bitlengths[s.LeafSymbol[val - 1]] = value;
            ptr--;
            value++;
        }
    }
}
