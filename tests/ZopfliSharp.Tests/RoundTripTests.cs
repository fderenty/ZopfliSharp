using System.IO.Compression;

namespace ZopfliSharp.Tests;

/// <summary>
/// Every output decodes back to its input with the BCL's own decoders. This needs no
/// reference implementation, so it always runs.
/// </summary>
[TestClass]
public sealed class RoundTripTests
{
    public static IEnumerable<object[]> Cases =>
        from name in Corpus.Samples.Keys
        from format in new[] { ZopfliFormat.Deflate, ZopfliFormat.Zlib, ZopfliFormat.Gzip }
        select new object[] { name, format };

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void DecodesBackToTheInput(string sample, ZopfliFormat format)
    {
        byte[] data = Corpus.Samples[sample];
        // Few iterations: decodability does not depend on how hard the parse worked.
        byte[] compressed = Zopfli.Compress(data, format, new ZopfliOptions { Iterations = 2 });

        CollectionAssert.AreEqual(data, Decompress(compressed, format));
    }

    [TestMethod]
    public void BeatsTheBclAtItsSmallestSize()
    {
        byte[] data = Corpus.Samples["mixed-60k"];

        using var bcl = new MemoryStream();
        using (var zlib = new ZLibStream(bcl, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);

        byte[] zopfli = Zopfli.Compress(data, ZopfliFormat.Zlib);

        Assert.IsTrue(zopfli.Length < bcl.Length, $"zopfli {zopfli.Length} bytes, BCL {bcl.Length} bytes");
    }

    internal static byte[] Decompress(byte[] compressed, ZopfliFormat format)
    {
        using var input = new MemoryStream(compressed);
        using Stream decoder = format switch
        {
            ZopfliFormat.Deflate => new DeflateStream(input, CompressionMode.Decompress),
            ZopfliFormat.Zlib => new ZLibStream(input, CompressionMode.Decompress),
            ZopfliFormat.Gzip => new GZipStream(input, CompressionMode.Decompress),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }
}
