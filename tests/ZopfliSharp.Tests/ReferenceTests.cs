namespace ZopfliSharp.Tests;

/// <summary>
/// The port's output against the C library's, byte for byte. A difference anywhere means the
/// port took a different decision somewhere, which is a bug even when both streams decode.
/// </summary>
[TestClass]
public sealed class ReferenceTests
{
    public static IEnumerable<object[]> SampleNames => Corpus.Samples.Keys.Select(name => new object[] { name });

    [TestMethod]
    [DynamicData(nameof(SampleNames))]
    public void ZlibWithDefaultOptionsMatchesTheCLibrary(string sample)
        => AssertMatches(sample, ZopfliFormat.Zlib, new ZopfliOptions());

    [TestMethod]
    [DataRow("text-12k")]
    [DataRow("mixed-60k")]
    [DataRow("empty")]
    [DataRow("one-byte")]
    public void DeflateMatchesTheCLibrary(string sample)
        => AssertMatches(sample, ZopfliFormat.Deflate, new ZopfliOptions());

    [TestMethod]
    [DataRow("text-12k")]
    [DataRow("png-like-24k")]
    [DataRow("empty")]
    public void GzipMatchesTheCLibrary(string sample)
        => AssertMatches(sample, ZopfliFormat.Gzip, new ZopfliOptions());

    [TestMethod]
    [DataRow("mixed-60k", 1, true, 15)]
    [DataRow("mixed-60k", 5, false, 15)]
    [DataRow("mixed-60k", 3, true, 0)]
    [DataRow("mixed-60k", 3, true, 3)]
    [DataRow("text-12k", 40, true, 15)]
    [DataRow("runs-30k", 2, false, 0)]
    [DataRow("far-window-80k", 2, true, 0)]
    [DataRow("binary-structured-40k", 1, true, 2)]
    public void NonDefaultOptionsMatchTheCLibrary(string sample, int iterations, bool splitting, int splitMax)
        => AssertMatches(sample, ZopfliFormat.Zlib, new ZopfliOptions
        {
            Iterations = iterations,
            BlockSplitting = splitting,
            BlockSplittingMax = splitMax,
        });

    [TestMethod]
    public void MoreThanOneMasterBlockMatchesTheCLibrary()
    {
        // Two master blocks; one iteration keeps both sides of the comparison to seconds.
        byte[] data = Corpus.Large();
        var options = new ZopfliOptions { Iterations = 1 };

        byte[] expected = ReferenceZopfli.Compress(data, ZopfliFormat.Zlib, options);
        byte[] actual = Zopfli.Compress(data, ZopfliFormat.Zlib, options);

        AssertSameBytes(expected, actual, "large");
    }

    private static void AssertMatches(string sample, ZopfliFormat format, ZopfliOptions options)
    {
        byte[] data = Corpus.Samples[sample];
        byte[] expected = ReferenceZopfli.Compress(data, format, options);
        byte[] actual = Zopfli.Compress(data, format, options);
        AssertSameBytes(expected, actual, sample);
    }

    internal static void AssertSameBytes(byte[] expected, byte[] actual, string sample)
    {
        int length = Math.Min(expected.Length, actual.Length);
        int first = expected.AsSpan(0, length).CommonPrefixLength(actual.AsSpan(0, length));
        if (first == length && expected.Length == actual.Length)
            return;

        Assert.Fail(
            $"{sample}: output differs from the C library at byte {first} " +
            $"(expected {expected.Length} bytes, got {actual.Length}).");
    }
}
