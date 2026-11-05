// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from deflate.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using System.Diagnostics;

namespace ZopfliSharp.Internal;

/// <summary>
/// Writes deflate blocks and estimates their sizes: Huffman tree encoding, choice of block
/// type, block splitting and master blocks. Port of deflate.c.
/// </summary>
internal static class Deflate
{
    private const int NumLL = Constants.NumLL;
    private const int NumD = Constants.NumD;

    // The order in which code length code lengths are encoded, as per deflate.
    private static ReadOnlySpan<byte> CodeLengthOrder =>
        [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    /// <summary>
    /// Ensures there are at least two distance codes, for decoders that fail otherwise: zlib
    /// 1.2.1 and below without one, some mobile phones without two. Port of
    /// PatchDistanceCodesForBuggyDecoders.
    /// </summary>
    private static void PatchDistanceCodesForBuggyDecoders(Span<int> dLengths)
    {
        int numDistCodes = 0;  // Amount of non-zero distance codes.
        for (int i = 0; i < 30; i++)  // Ignore the two unused codes from the spec.
        {
            if (dLengths[i] != 0)
                numDistCodes++;
            if (numDistCodes >= 2)
                return;  // Two or more codes is fine.
        }

        if (numDistCodes == 0)
        {
            dLengths[0] = dLengths[1] = 1;
        }
        else if (numDistCodes == 1)
        {
            dLengths[dLengths[0] != 0 ? 1 : 0] = 1;
        }
    }

    /// <summary>
    /// Encodes the Huffman trees with the chosen run-length codes and returns the size of the
    /// encoding in bits. With no writer, only computes the size. Port of EncodeTree.
    /// </summary>
    private static int EncodeTree(
        ReadOnlySpan<int> llLengths, ReadOnlySpan<int> dLengths,
        bool use16, bool use17, bool use18, BitWriter? writer)
    {
        bool sizeOnly = writer is null;
        int hlit = 29;  // 286 - 257
        int hdist = 29;  // 32 - 1, but gzip does not like hdist > 29.

        Span<int> clcounts = stackalloc int[19];
        clcounts.Clear();

        // Runlength encoded version of lengths of litlen and dist trees, with the extra bits of
        // the run codes 16, 17 and 18.
        Span<int> rle = sizeOnly ? [] : stackalloc int[NumLL + NumD];
        Span<int> rleBits = sizeOnly ? [] : stackalloc int[NumLL + NumD];
        int rleSize = 0;

        // Trim zeros.
        while (hlit > 0 && llLengths[257 + hlit - 1] == 0)
            hlit--;
        while (hdist > 0 && dLengths[1 + hdist - 1] == 0)
            hdist--;

        int hlit2 = hlit + 257;
        int lldTotal = hlit2 + hdist + 1;  // Total amount of literal, length, distance codes.

        for (int i = 0; i < lldTotal; i++)
        {
            // This is an encoding of a Huffman tree, so now the length is a symbol.
            int symbol = i < hlit2 ? llLengths[i] : dLengths[i - hlit2];
            int count = 1;
            if (use16 || (symbol == 0 && (use17 || use18)))
            {
                for (int j = i + 1; j < lldTotal && symbol == (j < hlit2 ? llLengths[j] : dLengths[j - hlit2]); j++)
                    count++;
            }

            i += count - 1;

            // Repetitions of zeroes.
            if (symbol == 0 && count >= 3)
            {
                if (use18)
                {
                    while (count >= 11)
                    {
                        int count2 = count > 138 ? 138 : count;
                        if (!sizeOnly)
                        {
                            rle[rleSize] = 18;
                            rleBits[rleSize++] = count2 - 11;
                        }
                        clcounts[18]++;
                        count -= count2;
                    }
                }

                if (use17)
                {
                    while (count >= 3)
                    {
                        int count2 = count > 10 ? 10 : count;
                        if (!sizeOnly)
                        {
                            rle[rleSize] = 17;
                            rleBits[rleSize++] = count2 - 3;
                        }
                        clcounts[17]++;
                        count -= count2;
                    }
                }
            }

            // Repetitions of any symbol.
            if (use16 && count >= 4)
            {
                count--;  // Since the first one is hardcoded.
                clcounts[symbol]++;
                if (!sizeOnly)
                {
                    rle[rleSize] = symbol;
                    rleBits[rleSize++] = 0;
                }

                while (count >= 3)
                {
                    int count2 = count > 6 ? 6 : count;
                    if (!sizeOnly)
                    {
                        rle[rleSize] = 16;
                        rleBits[rleSize++] = count2 - 3;
                    }
                    clcounts[16]++;
                    count -= count2;
                }
            }

            // No or insufficient repetition.
            clcounts[symbol] += count;
            while (count > 0)
            {
                if (!sizeOnly)
                {
                    rle[rleSize] = symbol;
                    rleBits[rleSize++] = 0;
                }
                count--;
            }
        }

        Span<int> clcl = stackalloc int[19];  // Code length code lengths.
        Tree.CalculateBitLengths(clcounts, 7, clcl);

        int hclen = 15;
        // Trim zeros.
        ReadOnlySpan<byte> order = CodeLengthOrder;
        while (hclen > 0 && clcounts[order[hclen + 4 - 1]] == 0)
            hclen--;

        if (writer is not null)
        {
            Span<int> clsymbols = stackalloc int[19];
            Tree.LengthsToSymbols(clcl, 7, clsymbols);

            writer.AddBits(hlit, 5);
            writer.AddBits(hdist, 5);
            writer.AddBits(hclen, 4);

            for (int i = 0; i < hclen + 4; i++)
                writer.AddBits(clcl[order[i]], 3);

            for (int i = 0; i < rleSize; i++)
            {
                int sym = clsymbols[rle[i]];
                writer.AddHuffmanBits(sym, clcl[rle[i]]);
                // Extra bits.
                if (rle[i] == 16)
                    writer.AddBits(rleBits[i], 2);
                else if (rle[i] == 17)
                    writer.AddBits(rleBits[i], 3);
                else if (rle[i] == 18)
                    writer.AddBits(rleBits[i], 7);
            }
        }

        int resultSize = 14;  // hlit, hdist, hclen bits
        resultSize += (hclen + 4) * 3;  // clcl bits
        for (int i = 0; i < 19; i++)
            resultSize += clcl[i] * clcounts[i];
        // Extra bits.
        resultSize += clcounts[16] * 2;
        resultSize += clcounts[17] * 3;
        resultSize += clcounts[18] * 7;
        return resultSize;
    }

    /// <summary>
    /// Writes the trees with whichever combination of run-length codes is smallest. Port of
    /// AddDynamicTree.
    /// </summary>
    private static void AddDynamicTree(ReadOnlySpan<int> llLengths, ReadOnlySpan<int> dLengths, BitWriter writer)
    {
        int best = 0;
        int bestsize = 0;

        for (int i = 0; i < 8; i++)
        {
            int size = EncodeTree(llLengths, dLengths, (i & 1) != 0, (i & 2) != 0, (i & 4) != 0, null);
            if (bestsize == 0 || size < bestsize)
            {
                bestsize = size;
                best = i;
            }
        }

        EncodeTree(llLengths, dLengths, (best & 1) != 0, (best & 2) != 0, (best & 4) != 0, writer);
    }

    /// <summary>The exact size of the trees in bits, as they will be encoded. Port of CalculateTreeSize.</summary>
    private static int CalculateTreeSize(ReadOnlySpan<int> llLengths, ReadOnlySpan<int> dLengths)
    {
        int result = 0;
        for (int i = 0; i < 8; i++)
        {
            int size = EncodeTree(llLengths, dLengths, (i & 1) != 0, (i & 2) != 0, (i & 4) != 0, null);
            if (result == 0 || size < result)
                result = size;
        }

        return result;
    }

    /// <summary>
    /// Writes every literal/length and distance code of the range, without the end code 256.
    /// Port of AddLZ77Data.
    /// </summary>
    private static void AddLZ77Data(
        Lz77Store lz77, int lstart, int lend, int expectedDataSize,
        ReadOnlySpan<int> llSymbols, ReadOnlySpan<int> llLengths,
        ReadOnlySpan<int> dSymbols, ReadOnlySpan<int> dLengths,
        BitWriter writer)
    {
        int testlength = 0;
        for (int i = lstart; i < lend; i++)
        {
            int dist = lz77.Dists[i];
            int litlen = lz77.LitLens[i];
            if (dist == 0)
            {
                Debug.Assert(litlen < 256);
                Debug.Assert(llLengths[litlen] > 0);
                writer.AddHuffmanBits(llSymbols[litlen], llLengths[litlen]);
                testlength++;
            }
            else
            {
                int lls = Symbols.GetLengthSymbol(litlen);
                int ds = Symbols.GetDistSymbol(dist);
                Debug.Assert(litlen >= 3 && litlen <= 288);
                Debug.Assert(llLengths[lls] > 0);
                Debug.Assert(dLengths[ds] > 0);
                writer.AddHuffmanBits(llSymbols[lls], llLengths[lls]);
                writer.AddBits(Symbols.GetLengthExtraBitsValue(litlen), Symbols.GetLengthExtraBits(litlen));
                writer.AddHuffmanBits(dSymbols[ds], dLengths[ds]);
                writer.AddBits(Symbols.GetDistExtraBitsValue(dist), Symbols.GetDistExtraBits(dist));
                testlength += litlen;
            }
        }

        Debug.Assert(expectedDataSize == 0 || testlength == expectedDataSize);
    }

    /// <summary>The code lengths of the fixed Huffman tree of deflate. Port of GetFixedTree.</summary>
    private static void GetFixedTree(Span<int> llLengths, Span<int> dLengths)
    {
        llLengths[..144].Fill(8);
        llLengths[144..256].Fill(9);
        llLengths[256..280].Fill(7);
        llLengths[280..288].Fill(8);
        dLengths[..32].Fill(5);
    }

    /// <summary>
    /// Same as <see cref="CalculateBlockSymbolSize"/>, symbol by symbol, for ranges smaller than
    /// a histogram. Port of CalculateBlockSymbolSizeSmall.
    /// </summary>
    private static long CalculateBlockSymbolSizeSmall(
        ReadOnlySpan<int> llLengths, ReadOnlySpan<int> dLengths, Lz77Store lz77, int lstart, int lend)
    {
        long result = 0;
        for (int i = lstart; i < lend; i++)
        {
            Debug.Assert(i < lz77.Size);
            Debug.Assert(lz77.LitLens[i] < 259);
            if (lz77.Dists[i] == 0)
            {
                result += llLengths[lz77.LitLens[i]];
            }
            else
            {
                int llSymbol = Symbols.GetLengthSymbol(lz77.LitLens[i]);
                int dSymbol = Symbols.GetDistSymbol(lz77.Dists[i]);
                result += llLengths[llSymbol];
                result += dLengths[dSymbol];
                result += Symbols.GetLengthSymbolExtraBits(llSymbol);
                result += Symbols.GetDistSymbolExtraBits(dSymbol);
            }
        }

        result += llLengths[256];  // End symbol.
        return result;
    }

    /// <summary>
    /// Same as <see cref="CalculateBlockSymbolSize"/>, with the histogram given by the caller.
    /// Port of CalculateBlockSymbolSizeGivenCounts.
    /// </summary>
    private static long CalculateBlockSymbolSizeGivenCounts(
        ReadOnlySpan<int> llCounts, ReadOnlySpan<int> dCounts,
        ReadOnlySpan<int> llLengths, ReadOnlySpan<int> dLengths,
        Lz77Store lz77, int lstart, int lend)
    {
        if (lstart + NumLL * 3 > lend)
            return CalculateBlockSymbolSizeSmall(llLengths, dLengths, lz77, lstart, lend);

        long result = 0;
        for (int i = 0; i < 256; i++)
            result += (long)llLengths[i] * llCounts[i];

        for (int i = 257; i < 286; i++)
        {
            result += (long)llLengths[i] * llCounts[i];
            result += (long)Symbols.GetLengthSymbolExtraBits(i) * llCounts[i];
        }

        for (int i = 0; i < 30; i++)
        {
            result += (long)dLengths[i] * dCounts[i];
            result += (long)Symbols.GetDistSymbolExtraBits(i) * dCounts[i];
        }

        result += llLengths[256];  // End symbol.
        return result;
    }

    /// <summary>
    /// Size in bits of the part of an LZ77 block after its header and trees. Port of
    /// CalculateBlockSymbolSize.
    /// </summary>
    private static long CalculateBlockSymbolSize(
        ReadOnlySpan<int> llLengths, ReadOnlySpan<int> dLengths, Lz77Store lz77, int lstart, int lend)
    {
        if (lstart + NumLL * 3 > lend)
            return CalculateBlockSymbolSizeSmall(llLengths, dLengths, lz77, lstart, lend);

        Span<int> llCounts = stackalloc int[NumLL];
        Span<int> dCounts = stackalloc int[NumD];
        lz77.GetHistogram(lstart, lend, llCounts, dCounts);
        return CalculateBlockSymbolSizeGivenCounts(llCounts, dCounts, llLengths, dLengths, lz77, lstart, lend);
    }

    private static long AbsDiff(long x, long y) => x > y ? x - y : y - x;

    /// <summary>
    /// Changes the population counts so that the Huffman tree that follows, its run-length part
    /// especially, is more likely to compress well. Port of OptimizeHuffmanForRle.
    /// </summary>
    internal static void OptimizeHuffmanForRle(int length, Span<int> counts)
    {
        // 1) We don't want to touch the trailing zeros. We may break the rules of the format by
        // adding more data in the distance codes.
        for (; length >= 0; --length)
        {
            if (length == 0)
                return;
            if (counts[length - 1] != 0)
                break;  // Now counts[0..length - 1] does not have trailing zeros.
        }

        // 2) Let's mark all population counts that already can be encoded with an rle code.
        Span<bool> goodForRle = stackalloc bool[length];
        goodForRle.Clear();

        // Let's not spoil any of the existing good rle codes. Mark any seq of 0's that is longer
        // than 5 as a good_for_rle. Mark any seq of non-0's that is longer than 7 as a
        // good_for_rle.
        long symbol = counts[0];
        int stride = 0;
        for (int i = 0; i < length + 1; ++i)
        {
            if (i == length || counts[i] != symbol)
            {
                if ((symbol == 0 && stride >= 5) || (symbol != 0 && stride >= 7))
                {
                    for (int k = 0; k < stride; ++k)
                        goodForRle[i - k - 1] = true;
                }

                stride = 1;
                if (i != length)
                    symbol = counts[i];
            }
            else
            {
                ++stride;
            }
        }

        // 3) Let's replace those population counts that lead to more rle codes.
        stride = 0;
        long limit = counts[0];
        long sum = 0;
        for (int i = 0; i < length + 1; ++i)
        {
            if (i == length || goodForRle[i]
                // Heuristic for selecting the stride ranges to collapse.
                || AbsDiff(counts[i], limit) >= 4)
            {
                if (stride >= 4 || (stride >= 3 && sum == 0))
                {
                    // The stride must end, collapse what we have, if we have enough (4).
                    int count = (int)((sum + stride / 2) / stride);
                    if (count < 1)
                        count = 1;
                    if (sum == 0)
                    {
                        // Don't make an all zeros stride to be upgraded to ones.
                        count = 0;
                    }

                    for (int k = 0; k < stride; ++k)
                    {
                        // We don't want to change value at counts[i], that is already belonging
                        // to the next stride. Thus - 1.
                        counts[i - k - 1] = count;
                    }
                }

                stride = 0;
                sum = 0;
                if (i < length - 3)
                {
                    // All interesting strides have a count of at least 4, at least when
                    // non-zeros.
                    limit = ((long)counts[i] + counts[i + 1] + counts[i + 2] + counts[i + 3] + 2) / 4;
                }
                else if (i < length)
                {
                    limit = counts[i];
                }
                else
                {
                    limit = 0;
                }
            }

            ++stride;
            if (i != length)
                sum += counts[i];
        }
    }

    /// <summary>
    /// Tries <see cref="OptimizeHuffmanForRle"/> on the block and keeps whichever lengths are
    /// smaller. Returns the size of the trees and data in bits, without the 3-bit block header.
    /// Port of TryOptimizeHuffmanForRle.
    /// </summary>
    private static double TryOptimizeHuffmanForRle(
        Lz77Store lz77, int lstart, int lend,
        ReadOnlySpan<int> llCounts, ReadOnlySpan<int> dCounts,
        Span<int> llLengths, Span<int> dLengths)
    {
        Span<int> llCounts2 = stackalloc int[NumLL];
        Span<int> dCounts2 = stackalloc int[NumD];
        Span<int> llLengths2 = stackalloc int[NumLL];
        Span<int> dLengths2 = stackalloc int[NumD];

        double treesize = CalculateTreeSize(llLengths, dLengths);
        double datasize = CalculateBlockSymbolSizeGivenCounts(llCounts, dCounts, llLengths, dLengths, lz77, lstart, lend);

        llCounts.CopyTo(llCounts2);
        dCounts.CopyTo(dCounts2);
        OptimizeHuffmanForRle(NumLL, llCounts2);
        OptimizeHuffmanForRle(NumD, dCounts2);
        Tree.CalculateBitLengths(llCounts2, 15, llLengths2);
        Tree.CalculateBitLengths(dCounts2, 15, dLengths2);
        PatchDistanceCodesForBuggyDecoders(dLengths2);

        double treesize2 = CalculateTreeSize(llLengths2, dLengths2);
        double datasize2 = CalculateBlockSymbolSizeGivenCounts(llCounts, dCounts, llLengths2, dLengths2, lz77, lstart, lend);

        if (treesize2 + datasize2 < treesize + datasize)
        {
            llLengths2.CopyTo(llLengths);
            dLengths2.CopyTo(dLengths);
            return treesize2 + datasize2;
        }

        return treesize + datasize;
    }

    /// <summary>
    /// Bit lengths for a dynamic block, chosen for the smallest trees plus data rather than the
    /// ideal Huffman lengths. Returns the size of the trees and data in bits, without the 3-bit
    /// block header. Port of GetDynamicLengths.
    /// </summary>
    private static double GetDynamicLengths(Lz77Store lz77, int lstart, int lend, Span<int> llLengths, Span<int> dLengths)
    {
        Span<int> llCounts = stackalloc int[NumLL];
        Span<int> dCounts = stackalloc int[NumD];

        lz77.GetHistogram(lstart, lend, llCounts, dCounts);
        llCounts[256] = 1;  // End symbol.
        Tree.CalculateBitLengths(llCounts, 15, llLengths);
        Tree.CalculateBitLengths(dCounts, 15, dLengths);
        PatchDistanceCodesForBuggyDecoders(dLengths);
        return TryOptimizeHuffmanForRle(lz77, lstart, lend, llCounts, dCounts, llLengths, dLengths);
    }

    /// <summary>
    /// Size in bits of the range of LZ77 data as a block of the given type: 0 stored, 1 fixed,
    /// 2 dynamic. Port of ZopfliCalculateBlockSize.
    /// </summary>
    public static double CalculateBlockSize(Lz77Store lz77, int lstart, int lend, int btype)
    {
        Span<int> llLengths = stackalloc int[NumLL];
        Span<int> dLengths = stackalloc int[NumD];

        double result = 3;  // bfinal and btype bits

        if (btype == 0)
        {
            long length = lz77.GetByteRange(lstart, lend);
            long rem = length % 65535;
            long blocks = length / 65535 + (rem != 0 ? 1 : 0);
            // An uncompressed block must actually be split into multiple blocks if it's larger
            // than 65535 bytes long. Each block header is 5 bytes: 3 bits, padding, LEN and NLEN
            // (potential less padding for first one ignored).
            return blocks * 5 * 8 + length * 8;
        }

        if (btype == 1)
        {
            GetFixedTree(llLengths, dLengths);
            result += CalculateBlockSymbolSize(llLengths, dLengths, lz77, lstart, lend);
        }
        else
        {
            result += GetDynamicLengths(lz77, lstart, lend, llLengths, dLengths);
        }

        return result;
    }

    /// <summary>
    /// The smallest of the three block sizes. Port of ZopfliCalculateBlockSizeAutoType.
    /// </summary>
    public static double CalculateBlockSizeAutoType(Lz77Store lz77, int lstart, int lend)
    {
        double uncompressedcost = CalculateBlockSize(lz77, lstart, lend, 0);
        // Don't do the expensive fixed cost calculation for larger blocks that are unlikely to
        // use it. The test is on the whole store, not on the range, as in the C code.
        double fixedcost = lz77.Size > 1000 ? uncompressedcost : CalculateBlockSize(lz77, lstart, lend, 1);
        double dyncost = CalculateBlockSize(lz77, lstart, lend, 2);
        return (uncompressedcost < fixedcost && uncompressedcost < dyncost)
            ? uncompressedcost
            : (fixedcost < dyncost ? fixedcost : dyncost);
    }

    /// <summary>
    /// Writes stored blocks, as many as needed since one holds at most 65535 bytes. Port of
    /// AddNonCompressedBlock.
    /// </summary>
    private static void AddNonCompressedBlock(bool final, ReadOnlySpan<byte> input, int instart, int inend, BitWriter writer)
    {
        int pos = instart;
        while (true)
        {
            int blocksize = 65535;
            if (pos + blocksize > inend)
                blocksize = inend - pos;
            bool currentfinal = pos + blocksize >= inend;
            int nlen = ~blocksize & 0xFFFF;

            writer.AddBit(final && currentfinal ? 1 : 0);
            // BTYPE 00
            writer.AddBit(0);
            writer.AddBit(0);

            // Any bits of input up to the next byte boundary are ignored.
            writer.BitPointer = 0;

            writer.AppendByte((byte)(blocksize % 256));
            writer.AppendByte((byte)((blocksize / 256) % 256));
            writer.AppendByte((byte)(nlen % 256));
            writer.AppendByte((byte)((nlen / 256) % 256));
            writer.AppendBytes(input.Slice(pos, blocksize));

            if (currentfinal)
                break;
            pos += blocksize;
        }
    }

    /// <summary>
    /// Writes a deflate block of the given type with the range of LZ77 data. Port of
    /// AddLZ77Block.
    /// </summary>
    /// <param name="btype">0 stored, 1 fixed, 2 dynamic.</param>
    /// <param name="final">Whether to set the final bit, on the last block only.</param>
    /// <param name="input">The input the LZ77 data refers to.</param>
    /// <param name="lz77">The LZ77 data.</param>
    /// <param name="lstart">Where to start in the LZ77 data.</param>
    /// <param name="lend">Where to end in the LZ77 data, exclusive.</param>
    /// <param name="expectedDataSize">The uncompressed block size for checking, or 0.</param>
    /// <param name="writer">The output.</param>
    private static void AddLZ77Block(
        int btype, bool final, ReadOnlySpan<byte> input, Lz77Store lz77, int lstart, int lend,
        int expectedDataSize, BitWriter writer)
    {
        if (btype == 0)
        {
            int length = lz77.GetByteRange(lstart, lend);
            int pos = lstart == lend ? 0 : lz77.Pos[lstart];
            int end = pos + length;
            AddNonCompressedBlock(final, input, pos, end, writer);
            return;
        }

        Span<int> llLengths = stackalloc int[NumLL];
        Span<int> dLengths = stackalloc int[NumD];
        Span<int> llSymbols = stackalloc int[NumLL];
        Span<int> dSymbols = stackalloc int[NumD];

        writer.AddBit(final ? 1 : 0);
        writer.AddBit(btype & 1);
        writer.AddBit((btype & 2) >> 1);

        if (btype == 1)
        {
            // Fixed block.
            GetFixedTree(llLengths, dLengths);
        }
        else
        {
            // Dynamic block.
            Debug.Assert(btype == 2);
            GetDynamicLengths(lz77, lstart, lend, llLengths, dLengths);
            AddDynamicTree(llLengths, dLengths, writer);
        }

        Tree.LengthsToSymbols(llLengths, 15, llSymbols);
        Tree.LengthsToSymbols(dLengths, 15, dSymbols);

        AddLZ77Data(lz77, lstart, lend, expectedDataSize, llSymbols, llLengths, dSymbols, dLengths, writer);

        // End symbol.
        writer.AddHuffmanBits(llSymbols[256], llLengths[256]);
    }


    /// <summary>How one block will be written.</summary>
    /// <param name="Btype">0 stored, 1 fixed, 2 dynamic, or -1 for the empty block.</param>
    /// <param name="FixedStore">For a fixed block, LZ77 data recomputed for the fixed tree, if it was.</param>
    private readonly record struct BlockChoice(int Btype, Lz77Store? FixedStore);

    /// <summary>
    /// Everything decided about one master block, so that it can be written later, once those
    /// before it are: the C code decides and writes in one go, but a stored block aligns itself
    /// on the next byte of the output, so what it writes depends on where it lands.
    /// </summary>
    private sealed class PartPlan(int btype, bool final, int instart, int inend)
    {
        public readonly int Btype = btype;
        public readonly bool Final = final;
        public readonly int InStart = instart;
        public readonly int InEnd = inend;
        public Lz77Store Lz77 = new();
        public List<int> SplitPoints = [];
        public BlockChoice[] Choices = [];
    }

    /// <summary>
    /// The decision half of AddLZ77BlockAutoType: whichever block type comes out smallest,
    /// recomputing the LZ77 data for the fixed tree when that might win.
    /// </summary>
    private static BlockChoice ChooseBlockType(ReadOnlySpan<byte> input, Lz77Store lz77, int lstart, int lend, WorkspacePool workspaces)
    {
        double uncompressedcost = CalculateBlockSize(lz77, lstart, lend, 0);
        double fixedcost = CalculateBlockSize(lz77, lstart, lend, 1);
        double dyncost = CalculateBlockSize(lz77, lstart, lend, 2);

        // Whether to perform the expensive calculation of creating an optimal block with fixed
        // Huffman tree to check if smaller. Only do this for small blocks or blocks which
        // already are pretty good with fixed Huffman tree.
        bool expensivefixed = lz77.Size < 1000 || fixedcost <= dyncost * 1.1;

        if (lstart == lend)
            return new BlockChoice(-1, null);  // Smallest empty block is a fixed block.

        Lz77Store? fixedstore = null;
        if (expensivefixed)
        {
            // Recalculate the LZ77 with OptimalFixed.
            int instart = lz77.Pos[lstart];
            int inend = instart + lz77.GetByteRange(lstart, lend);
            fixedstore = new Lz77Store();
            Workspace workspace = workspaces.Rent();
            try
            {
                var s = new BlockState(instart, inend, workspace.Cache);
                Squeeze.OptimalFixed(s, input, instart, inend, fixedstore, workspace.Hash);
            }
            finally
            {
                workspaces.Return(workspace);
            }

            fixedcost = CalculateBlockSize(fixedstore, 0, fixedstore.Size, 1);
        }

        if (uncompressedcost < fixedcost && uncompressedcost < dyncost)
            return new BlockChoice(0, null);
        if (fixedcost < dyncost)
            return new BlockChoice(1, expensivefixed ? fixedstore : null);
        return new BlockChoice(2, null);
    }

    /// <summary>The writing half of AddLZ77BlockAutoType.</summary>
    private static void WriteBlock(
        BlockChoice choice, bool final, ReadOnlySpan<byte> input, Lz77Store lz77, int lstart, int lend, BitWriter writer)
    {
        switch (choice.Btype)
        {
            case -1:
                writer.AddBits(final ? 1 : 0, 1);
                writer.AddBits(1, 2);  // btype 01
                writer.AddBits(0, 7);  // end symbol has code 0000000
                break;

            case 1 when choice.FixedStore is { } fixedstore:
                AddLZ77Block(1, final, input, fixedstore, 0, fixedstore.Size, 0, writer);
                break;

            default:
                AddLZ77Block(choice.Btype, final, input, lz77, lstart, lend, 0, writer);
                break;
        }
    }

    /// <summary>
    /// Decides how [<paramref name="instart"/>, <paramref name="inend"/>) is compressed, with
    /// the bytes before <paramref name="instart"/> as dictionary. The decision half of
    /// ZopfliDeflatePart; the blocks it finds are compressed concurrently when allowed.
    /// </summary>
    /// <param name="settings">The compression settings.</param>
    /// <param name="btype">2 tries every block type; 0 or 1 forces stored or fixed.</param>
    /// <param name="final">Whether the last block carries the final bit.</param>
    /// <param name="input">The whole input.</param>
    /// <param name="instart">Start of the part, inclusive.</param>
    /// <param name="inend">End of the part, exclusive.</param>
    /// <param name="parallelism">Null to stay on the calling thread.</param>
    /// <param name="workspaces">Reusable memory, one workspace per thread.</param>
    /// <param name="cancellationToken">Checked between passes.</param>
    private static PartPlan PlanPart(
        in Settings settings, int btype, bool final, byte[] input, int instart, int inend,
        Parallelism? parallelism, WorkspacePool workspaces, CancellationToken cancellationToken)
    {
        var plan = new PartPlan(btype, final, instart, inend);

        // If btype=2 is specified, it tries all block types. If a lesser btype is given, then
        // however it forces that one. Neither of the lesser types needs block splitting as they
        // have no dynamic Huffman trees.
        if (btype == 0)
            return plan;

        if (btype == 1)
        {
            Workspace workspace = workspaces.Rent();
            var s = new BlockState(instart, inend, workspace.Cache);
            Squeeze.OptimalFixed(s, input, instart, inend, plan.Lz77, workspace.Hash);
            workspaces.Return(workspace);
            return plan;
        }

        // Split points in input coordinates.
        List<int> splitpointsUncompressed;
        if (settings.BlockSplitting)
        {
            Workspace workspace = workspaces.Rent();
            splitpointsUncompressed = BlockSplitter.Split(
                input, instart, inend, settings.BlockSplittingMax, workspace.Hash, parallelism, cancellationToken);
            workspaces.Return(workspace);
        }
        else
        {
            splitpointsUncompressed = [];
        }

        int npoints = splitpointsUncompressed.Count;

        // The blocks are independent: each sees the input before it as dictionary, never the
        // LZ77 data chosen for it.
        int iterations = settings.Iterations;
        var stores = new Lz77Store[npoints + 1];
        Parallelism.For(parallelism, npoints + 1, i =>
        {
            int start = i == 0 ? instart : splitpointsUncompressed[i - 1];
            int end = i == npoints ? inend : splitpointsUncompressed[i];

            Workspace workspace = workspaces.Rent();
            try
            {
                var s = new BlockState(start, end, workspace.Cache);
                var store = new Lz77Store();
                Squeeze.Optimal(s, input, start, end, iterations, store, workspace.Hash, cancellationToken);
                stores[i] = store;
            }
            finally
            {
                workspaces.Return(workspace);
            }
        });

        // In block order, as the C code sums them: the total depends on the order.
        Lz77Store lz77 = plan.Lz77;
        var splitpoints = new List<int>(npoints);
        double totalcost = 0;
        for (int i = 0; i <= npoints; i++)
        {
            totalcost += CalculateBlockSizeAutoType(stores[i], 0, stores[i].Size);
            lz77.Append(stores[i]);
            if (i < npoints)
                splitpoints.Add(lz77.Size);
        }

        // Second block splitting attempt.
        if (settings.BlockSplitting && npoints > 1)
        {
            List<int> splitpoints2 = BlockSplitter.SplitLz77(lz77, settings.BlockSplittingMax, parallelism, cancellationToken);
            int npoints2 = splitpoints2.Count;
            double totalcost2 = 0;

            for (int i = 0; i <= npoints2; i++)
            {
                int start = i == 0 ? 0 : splitpoints2[i - 1];
                int end = i == npoints2 ? lz77.Size : splitpoints2[i];
                totalcost2 += CalculateBlockSizeAutoType(lz77, start, end);
            }

            if (totalcost2 < totalcost)
            {
                splitpoints = splitpoints2;
                npoints = npoints2;
            }
        }

        plan.SplitPoints = splitpoints;
        var choices = new BlockChoice[npoints + 1];
        Parallelism.For(parallelism, npoints + 1, i =>
        {
            int start = i == 0 ? 0 : splitpoints[i - 1];
            int end = i == npoints ? lz77.Size : splitpoints[i];
            choices[i] = ChooseBlockType(input, lz77, start, end, workspaces);
        });
        plan.Choices = choices;
        return plan;
    }

    /// <summary>The writing half of ZopfliDeflatePart.</summary>
    private static void WritePart(PartPlan plan, ReadOnlySpan<byte> input, BitWriter writer)
    {
        if (plan.Btype == 0)
        {
            AddNonCompressedBlock(plan.Final, input, plan.InStart, plan.InEnd, writer);
            return;
        }

        if (plan.Btype == 1)
        {
            AddLZ77Block(1, plan.Final, input, plan.Lz77, 0, plan.Lz77.Size, 0, writer);
            return;
        }

        List<int> splitpoints = plan.SplitPoints;
        int npoints = splitpoints.Count;
        for (int i = 0; i <= npoints; i++)
        {
            int start = i == 0 ? 0 : splitpoints[i - 1];
            int end = i == npoints ? plan.Lz77.Size : splitpoints[i];
            WriteBlock(plan.Choices[i], i == npoints && plan.Final, input, plan.Lz77, start, end, writer);
        }
    }

    /// <summary>
    /// Compresses the whole input as a deflate stream, one master block at a time. Port of
    /// ZopfliDeflate.
    /// </summary>
    /// <remarks>
    /// Master blocks are independent but for where their bits land, so with more than one
    /// thread several are planned at once, then written in order.
    /// </remarks>
    public static void Compress(
        in Settings settings, int btype, bool final, byte[] input, BitWriter writer, CancellationToken cancellationToken)
    {
        int insize = input.Length;
        var parts = new List<(int Start, int End, bool Final)>();
        int i = 0;
        do
        {
            bool masterfinal = (long)i + Constants.MasterBlockSize >= insize;
            bool final2 = final && masterfinal;
            int size = masterfinal ? insize - i : Constants.MasterBlockSize;
            parts.Add((i, i + size, final2));
            i += size;
        }
        while (i < insize);

        var workspaces = new WorkspacePool();
        var parallelism = Parallelism.Create(settings.MaxDegreeOfParallelism, cancellationToken);

        // Plans wait in memory until written, so only a batch is planned ahead at a time.
        int batch = parallelism?.BatchSize ?? 1;
        var plans = new PartPlan[parts.Count];
        Settings copy = settings;
        for (int first = 0; first < parts.Count; first += batch)
        {
            int count = Math.Min(batch, parts.Count - first);
            int offset = first;
            Parallelism.For(parallelism, count, k =>
            {
                (int start, int end, bool partFinal) = parts[offset + k];
                plans[offset + k] = PlanPart(copy, btype, partFinal, input, start, end, parallelism, workspaces, cancellationToken);
            });

            for (int k = first; k < first + count; k++)
            {
                WritePart(plans[k], input, writer);
                plans[k] = null!;  // Its LZ77 data is no longer needed.
            }
        }
    }
}
