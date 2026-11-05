// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from tree.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using System.Diagnostics;

namespace ZopfliSharp.Internal;

/// <summary>
/// Huffman tree utilities. Port of tree.c.
/// </summary>
internal static class Tree
{
    // 1.0 / log(2.0), to exactly the digits the C code writes: the truncation is part of the
    // cost model, so the full-precision value would change the output.
    private const double InvLog2 = 1.4426950408889;

    /// <summary>
    /// Bit lengths of a Huffman code of at most <paramref name="maxbits"/> bits for the given
    /// symbol counts. Port of ZopfliCalculateBitLengths.
    /// </summary>
    public static void CalculateBitLengths(ReadOnlySpan<int> count, int maxbits, Span<int> bitlengths)
    {
        bool ok = Katajainen.LengthLimitedCodeLengths(count, maxbits, bitlengths);
        Debug.Assert(ok);
    }

    /// <summary>
    /// Converts code lengths to the canonical codes of RFC 1951 section 3.2.2. Port of
    /// ZopfliLengthsToSymbols.
    /// </summary>
    public static void LengthsToSymbols(ReadOnlySpan<int> lengths, int maxbits, Span<int> symbols)
    {
        int n = lengths.Length;
        Span<int> blCount = stackalloc int[maxbits + 1];
        Span<int> nextCode = stackalloc int[maxbits + 1];
        blCount.Clear();
        nextCode.Clear();
        symbols[..n].Clear();

        // 1) Count the number of codes for each code length.
        for (int i = 0; i < n; i++)
        {
            Debug.Assert(lengths[i] <= maxbits);
            blCount[lengths[i]]++;
        }

        // 2) Find the numerical value of the smallest code for each code length.
        int code = 0;
        blCount[0] = 0;
        for (int bits = 1; bits <= maxbits; bits++)
        {
            code = (code + blCount[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        // 3) Assign consecutive values to all codes of the same length.
        for (int i = 0; i < n; i++)
        {
            int len = lengths[i];
            if (len != 0)
            {
                symbols[i] = nextCode[len];
                nextCode[len]++;
            }
        }
    }

    /// <summary>
    /// The entropy of each symbol in bits, from the symbol counts: the ideal, fractional code
    /// lengths. Port of ZopfliCalculateEntropy.
    /// </summary>
    public static void CalculateEntropy(ReadOnlySpan<int> count, Span<double> bitlengths)
    {
        int n = count.Length;

        // The C code sums into an unsigned int.
        uint sum = 0;
        for (int i = 0; i < n; i++)
            sum += (uint)count[i];

        double log2sum = (sum == 0 ? Math.Log(n) : Math.Log(sum)) * InvLog2;
        for (int i = 0; i < n; i++)
        {
            // When the count of the symbol is 0 but its cost is requested anyway, it means the
            // symbol will appear at least once, so give it the cost as if its count were 1.
            if (count[i] == 0)
                bitlengths[i] = log2sum;
            else
                bitlengths[i] = log2sum - Math.Log(count[i]) * InvLog2;

            // The subtraction may give a negative result very close to zero. Clamp it.
            if (bitlengths[i] < 0 && bitlengths[i] > -1e-5)
                bitlengths[i] = 0;

            Debug.Assert(bitlengths[i] >= 0);
        }
    }
}
