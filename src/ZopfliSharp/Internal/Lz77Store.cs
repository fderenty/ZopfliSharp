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
/// A sequence of LZ77 commands, literals and length/distance pairs, with cumulative symbol
/// histograms so the histogram of any range costs little. Port of ZopfliLZ77Store (lz77.c).
/// </summary>
/// <remarks>
/// The C store also keeps a pointer to the input. Here the input travels as an explicit
/// argument instead, so it can stay a span.
/// </remarks>
internal sealed class Lz77Store
{
    private const int NumLL = Constants.NumLL;
    private const int NumD = Constants.NumD;

    /// <summary>The literal byte, or the length of a match.</summary>
    public ushort[] LitLens = [];

    /// <summary>0 for a literal, otherwise the distance of the match.</summary>
    public ushort[] Dists = [];

    /// <summary>Position in the input where each command begins.</summary>
    public int[] Pos = [];

    /// <summary>The literal/length symbol of each command.</summary>
    public ushort[] LlSymbol = [];

    /// <summary>The distance symbol of each command, 0 for a literal.</summary>
    public ushort[] DSymbol = [];

    /// <summary>
    /// Cumulative histograms, one per chunk of <see cref="Constants.NumLL"/> commands: the one
    /// at chunk c counts every symbol up to the end of that chunk. Any other point is reached by
    /// subtracting the symbols after it within its chunk.
    /// </summary>
    public int[] LlCounts = [];

    /// <summary>Same as <see cref="LlCounts"/> for distance symbols, per chunk of <see cref="Constants.NumD"/>.</summary>
    public int[] DCounts = [];

    /// <summary>Number of commands.</summary>
    public int Size;

    /// <summary>Empties the store and keeps its memory.</summary>
    public void Clear() => Size = 0;

    private void EnsureCapacity(int size)
    {
        if (LitLens.Length >= size)
            return;

        int capacity = Math.Max(size, Math.Max(64, LitLens.Length * 2));
        Array.Resize(ref LitLens, capacity);
        Array.Resize(ref Dists, capacity);
        Array.Resize(ref Pos, capacity);
        Array.Resize(ref LlSymbol, capacity);
        Array.Resize(ref DSymbol, capacity);
    }

    private static void EnsureCounts(ref int[] counts, int size)
    {
        if (counts.Length < size)
            Array.Resize(ref counts, Math.Max(size, counts.Length * 2));
    }

    /// <summary>Appends a literal (dist 0) or a match. Port of ZopfliStoreLitLenDist.</summary>
    public void StoreLitLenDist(int length, int dist, int pos)
    {
        int origsize = Size;
        int llstart = NumLL * (origsize / NumLL);
        int dstart = NumD * (origsize / NumD);

        // Every time the index wraps around, a new cumulative histogram is made: one histogram
        // value per LZ77 symbol rather than a full histogram for each.
        if (origsize % NumLL == 0)
        {
            EnsureCounts(ref LlCounts, origsize + NumLL);
            if (origsize == 0)
                LlCounts.AsSpan(0, NumLL).Clear();
            else
                Array.Copy(LlCounts, origsize - NumLL, LlCounts, origsize, NumLL);
        }

        if (origsize % NumD == 0)
        {
            EnsureCounts(ref DCounts, origsize + NumD);
            if (origsize == 0)
                DCounts.AsSpan(0, NumD).Clear();
            else
                Array.Copy(DCounts, origsize - NumD, DCounts, origsize, NumD);
        }

        EnsureCapacity(origsize + 1);
        Debug.Assert(length < 259);
        LitLens[origsize] = (ushort)length;
        Dists[origsize] = (ushort)dist;
        Pos[origsize] = pos;

        if (dist == 0)
        {
            LlSymbol[origsize] = (ushort)length;
            DSymbol[origsize] = 0;
            LlCounts[llstart + length]++;
        }
        else
        {
            int lsym = Symbols.GetLengthSymbol(length);
            int dsym = Symbols.GetDistSymbol(dist);
            LlSymbol[origsize] = (ushort)lsym;
            DSymbol[origsize] = (ushort)dsym;
            LlCounts[llstart + lsym]++;
            DCounts[dstart + dsym]++;
        }

        Size = origsize + 1;
    }

    /// <summary>Replaces the content with a copy of <paramref name="source"/>. Port of ZopfliCopyLZ77Store.</summary>
    public void CopyFrom(Lz77Store source)
    {
        int size = source.Size;
        int llsize = NumLL * CeilDiv(size, NumLL);
        int dsize = NumD * CeilDiv(size, NumD);

        EnsureCapacity(size);
        EnsureCounts(ref LlCounts, llsize);
        EnsureCounts(ref DCounts, dsize);

        Array.Copy(source.LitLens, LitLens, size);
        Array.Copy(source.Dists, Dists, size);
        Array.Copy(source.Pos, Pos, size);
        Array.Copy(source.LlSymbol, LlSymbol, size);
        Array.Copy(source.DSymbol, DSymbol, size);
        Array.Copy(source.LlCounts, LlCounts, llsize);
        Array.Copy(source.DCounts, DCounts, dsize);
        Size = size;
    }

    /// <summary>Appends every command of <paramref name="store"/>. Port of ZopfliAppendLZ77Store.</summary>
    public void Append(Lz77Store store)
    {
        for (int i = 0; i < store.Size; i++)
            StoreLitLenDist(store.LitLens[i], store.Dists[i], store.Pos[i]);
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    /// <summary>
    /// Number of input bytes the commands from <paramref name="lstart"/> to
    /// <paramref name="lend"/> (exclusive) span. Port of ZopfliLZ77GetByteRange.
    /// </summary>
    public int GetByteRange(int lstart, int lend)
    {
        if (lstart == lend)
            return 0;

        int l = lend - 1;
        return Pos[l] + (Dists[l] == 0 ? 1 : LitLens[l]) - Pos[lstart];
    }

    /// <summary>
    /// The histogram of every symbol up to and including command <paramref name="lpos"/>.
    /// Port of ZopfliLZ77GetHistogramAt.
    /// </summary>
    private void GetHistogramAt(int lpos, Span<int> llCounts, Span<int> dCounts)
    {
        // The histogram of the chunk, with the symbols after lpos in that chunk taken back out.
        int llpos = NumLL * (lpos / NumLL);
        int dpos = NumD * (lpos / NumD);

        LlCounts.AsSpan(llpos, NumLL).CopyTo(llCounts);
        for (int i = lpos + 1; i < llpos + NumLL && i < Size; i++)
            llCounts[LlSymbol[i]]--;

        DCounts.AsSpan(dpos, NumD).CopyTo(dCounts);
        for (int i = lpos + 1; i < dpos + NumD && i < Size; i++)
        {
            if (Dists[i] != 0)
                dCounts[DSymbol[i]]--;
        }
    }

    /// <summary>
    /// The histogram of the commands from <paramref name="lstart"/> to <paramref name="lend"/>
    /// (exclusive), without the end symbol 256. Port of ZopfliLZ77GetHistogram.
    /// </summary>
    public void GetHistogram(int lstart, int lend, Span<int> llCounts, Span<int> dCounts)
    {
        if (lstart + NumLL * 3 > lend)
        {
            llCounts[..NumLL].Clear();
            dCounts[..NumD].Clear();
            for (int i = lstart; i < lend; i++)
            {
                llCounts[LlSymbol[i]]++;
                if (Dists[i] != 0)
                    dCounts[DSymbol[i]]++;
            }
        }
        else
        {
            // Subtract the cumulative histograms at the end and the start to get the histogram
            // for this range.
            GetHistogramAt(lend - 1, llCounts, dCounts);
            if (lstart > 0)
            {
                Span<int> llCounts2 = stackalloc int[NumLL];
                Span<int> dCounts2 = stackalloc int[NumD];
                GetHistogramAt(lstart - 1, llCounts2, dCounts2);

                for (int i = 0; i < NumLL; i++)
                    llCounts[i] -= llCounts2[i];
                for (int i = 0; i < NumD; i++)
                    dCounts[i] -= dCounts2[i];
            }
        }
    }
}
