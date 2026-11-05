namespace ZopfliSharp.Tests;

/// <summary>
/// Spreading one compression over threads must not change a single byte: the work is split
/// along independent lines and recombined in a fixed order.
/// </summary>
[TestClass]
public sealed class ParallelismTests
{
    public static IEnumerable<object[]> SampleNames => Corpus.Samples.Keys.Select(name => new object[] { name });

    [TestMethod]
    [DynamicData(nameof(SampleNames))]
    public void ParallelOutputIsIdenticalToSequential(string sample)
    {
        byte[] data = Corpus.Samples[sample];
        byte[] sequential = Zopfli.Compress(data, ZopfliFormat.Zlib, new ZopfliOptions { Iterations = 3 });

        foreach (int degree in new[] { 2, 3, -1 })
        {
            byte[] parallel = Zopfli.Compress(data, ZopfliFormat.Zlib, new ZopfliOptions { Iterations = 3, MaxDegreeOfParallelism = degree });
            ReferenceTests.AssertSameBytes(sequential, parallel, $"{sample} with {degree} threads");
        }
    }

    [TestMethod]
    public void MasterBlocksPlannedConcurrentlyAreWrittenInOrder()
    {
        byte[] data = Corpus.Large();
        byte[] sequential = Zopfli.Compress(data, ZopfliFormat.Gzip, new ZopfliOptions { Iterations = 1 });

        foreach (int degree in new[] { 2, -1 })
        {
            byte[] parallel = Zopfli.Compress(data, ZopfliFormat.Gzip, new ZopfliOptions { Iterations = 1, MaxDegreeOfParallelism = degree });
            ReferenceTests.AssertSameBytes(sequential, parallel, $"large with {degree} threads");
        }
    }

    [TestMethod]
    [DataRow("mixed-60k")]
    [DataRow("png-like-24k")]
    public void ParallelOutputMatchesTheCLibrary(string sample)
    {
        byte[] data = Corpus.Samples[sample];
        var options = new ZopfliOptions { MaxDegreeOfParallelism = -1 };

        byte[] expected = ReferenceZopfli.Compress(data, ZopfliFormat.Zlib, options);
        byte[] actual = Zopfli.Compress(data, ZopfliFormat.Zlib, options);

        ReferenceTests.AssertSameBytes(expected, actual, sample);
    }

    [TestMethod]
    public void DegreeMustBeMinusOneOrPositive()
    {
        var options = new ZopfliOptions();
        Assert.AreEqual(1, options.MaxDegreeOfParallelism);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => options.MaxDegreeOfParallelism = 0);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => options.MaxDegreeOfParallelism = -2);

        options.MaxDegreeOfParallelism = -1;
        options.MaxDegreeOfParallelism = 64;
        Assert.AreEqual(64, options.MaxDegreeOfParallelism);
    }

    [TestMethod]
    public void CancellationStopsEveryThread()
    {
        using var cts = new CancellationTokenSource();
        var options = new ZopfliOptions { Iterations = 1000, MaxDegreeOfParallelism = -1 };

        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsException<OperationCanceledException>(
            () => Zopfli.Compress(Corpus.Samples["mixed-60k"], ZopfliFormat.Zlib, options, cts.Token));

        // A thousand iterations would take minutes; a pass of this input takes milliseconds.
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }
}
