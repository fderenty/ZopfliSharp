namespace ZopfliSharp.Tests;

[TestClass]
public sealed class ApiTests
{
    [TestMethod]
    public void OptionsRejectOutOfRangeValues()
    {
        var options = new ZopfliOptions();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => options.Iterations = 0);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => options.BlockSplittingMax = -1);

        options.Iterations = 1;
        options.BlockSplittingMax = 0;
        Assert.AreEqual(1, options.Iterations);
        Assert.AreEqual(0, options.BlockSplittingMax);
    }

    [TestMethod]
    public void DefaultsAreThoseOfTheCommandLineTool()
    {
        var options = new ZopfliOptions();
        Assert.AreEqual(15, options.Iterations);
        Assert.IsTrue(options.BlockSplitting);
        Assert.AreEqual(15, options.BlockSplittingMax);
    }

    [TestMethod]
    public void UnknownFormatIsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Zopfli.Compress([1, 2, 3], (ZopfliFormat)42));
    }

    [TestMethod]
    public void StreamOverloadWritesTheSameBytes()
    {
        byte[] data = Corpus.Samples["text-12k"];
        var options = new ZopfliOptions { Iterations = 3 };

        using var stream = new MemoryStream();
        stream.WriteByte(0xAA);  // Whatever is already there stays.
        Zopfli.Compress(data, stream, ZopfliFormat.Zlib, options);

        byte[] expected = Zopfli.Compress(data, ZopfliFormat.Zlib, options);
        CollectionAssert.AreEqual(expected, stream.ToArray()[1..]);
        Assert.AreEqual(0xAA, stream.ToArray()[0]);
    }

    [TestMethod]
    public void NullStreamIsRejected()
    {
        Assert.ThrowsException<ArgumentNullException>(() => Zopfli.Compress([1], null!, ZopfliFormat.Zlib));
    }

    [TestMethod]
    public void CancellationStopsTheCompression()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsException<OperationCanceledException>(
            () => Zopfli.Compress(Corpus.Samples["text-12k"], ZopfliFormat.Zlib, null, cts.Token));
    }

    [TestMethod]
    public void ConcurrentCallsGiveTheSameOutputAsSequentialOnes()
    {
        string[] names = ["text-12k", "png-like-24k", "runs-30k", "binary-structured-40k", "mixed-60k", "short-text"];
        var options = new ZopfliOptions { Iterations = 3 };

        byte[][] sequential = names.Select(n => Zopfli.Compress(Corpus.Samples[n], ZopfliFormat.Zlib, options)).ToArray();

        var parallel = new byte[names.Length][];
        Parallel.For(0, names.Length * 3, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            int k = i % names.Length;
            byte[] result = Zopfli.Compress(Corpus.Samples[names[k]], ZopfliFormat.Zlib, options);
            CollectionAssert.AreEqual(sequential[k], result, names[k]);
            parallel[k] = result;
        });
    }

    [TestMethod]
    public void EmptyInputGivesTheSmallestValidStreams()
    {
        // A single empty fixed block: final bit, type 01, end code 0000000.
        CollectionAssert.AreEqual(new byte[] { 0x03, 0x00 }, Zopfli.Compress([], ZopfliFormat.Deflate));
        CollectionAssert.AreEqual(new byte[] { 0x78, 0xDA, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 }, Zopfli.Compress([], ZopfliFormat.Zlib));
    }
}
