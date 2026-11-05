using ZopfliSharp.Internal;

namespace ZopfliSharp.Tests;

/// <summary>
/// The hand-typed tables and the bit tricks, against RFC 1951 itself rather than the code they
/// came from.
/// </summary>
[TestClass]
public sealed class InternalsTests
{
    // RFC 1951 section 3.2.5: base length and extra bits of length codes 257 to 285.
    private static readonly int[] LengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra =
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    // Base distance and extra bits of distance codes 0 to 29.
    private static readonly int[] DistBase =
        [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistExtra =
        [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    [TestMethod]
    public void LengthSymbolsFollowTheSpecification()
    {
        for (int length = 3; length <= 258; length++)
        {
            // The last code whose base does not exceed the length; 258 has its own code.
            int code = length == 258 ? 28 : Array.FindLastIndex(LengthBase, b => b <= length && b != 258);
            Assert.AreEqual(257 + code, Symbols.GetLengthSymbol(length), $"symbol of length {length}");
            Assert.AreEqual(LengthExtra[code], Symbols.GetLengthExtraBits(length), $"extra bits of length {length}");
            Assert.AreEqual(length - LengthBase[code], Symbols.GetLengthExtraBitsValue(length), $"extra value of length {length}");
            Assert.AreEqual(LengthExtra[code], Symbols.GetLengthSymbolExtraBits(257 + code));
        }
    }

    [TestMethod]
    public void DistanceSymbolsFollowTheSpecification()
    {
        for (int dist = 1; dist <= 32768; dist++)
        {
            int code = Array.FindLastIndex(DistBase, b => b <= dist);
            Assert.AreEqual(code, Symbols.GetDistSymbol(dist), $"symbol of distance {dist}");
            Assert.AreEqual(DistExtra[code], Symbols.GetDistExtraBits(dist), $"extra bits of distance {dist}");
            Assert.AreEqual(dist - DistBase[code], Symbols.GetDistExtraBitsValue(dist), $"extra value of distance {dist}");
            Assert.AreEqual(DistExtra[code], Symbols.GetDistSymbolExtraBits(code));
        }
    }

    [TestMethod]
    public void CodeLengthsAreBoundedAndComplete()
    {
        var random = new Random(7);
        for (int round = 0; round < 200; round++)
        {
            int n = random.Next(1, 289);
            var counts = new int[n];
            for (int i = 0; i < n; i++)
                counts[i] = random.Next(4) == 0 ? 0 : random.Next(1, round % 3 == 0 ? 100_000 : 50);

            int maxbits = round % 2 == 0 ? 15 : 9;
            var lengths = new int[n];
            if (!Katajainen.LengthLimitedCodeLengths(counts, maxbits, lengths))
            {
                Assert.IsTrue(counts.Count(c => c != 0) > 1 << maxbits);
                continue;
            }

            int used = counts.Count(c => c != 0);
            double kraft = 0;
            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(counts[i] == 0, lengths[i] == 0, "only used symbols get a code");
                Assert.IsTrue(lengths[i] <= maxbits);
                if (lengths[i] != 0)
                    kraft += Math.Pow(2, -lengths[i]);
            }

            // A complete prefix code, except for a lone symbol, which gets one bit.
            if (used >= 2)
                Assert.AreEqual(1.0, kraft, 1e-12);
        }
    }

    [TestMethod]
    public void ChecksumsMatchKnownValues()
    {
        byte[] text = "The quick brown fox jumps over the lazy dog"u8.ToArray();
        Assert.AreEqual(0x414FA339u, Checksums.Crc32(text));
        Assert.AreEqual(0x5BDC0FDAu, Checksums.Adler32(text));
        Assert.AreEqual(1u, Checksums.Adler32([]));

        // Long enough to need the periodic reduction of the Adler sums.
        var ones = new byte[100_000];
        Array.Fill(ones, (byte)0xFF);
        Assert.AreEqual(ReferenceAdler32(ones), Checksums.Adler32(ones));
    }

    private static uint ReferenceAdler32(byte[] data)
    {
        ulong a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (uint)((b << 16) | a);
    }
}
