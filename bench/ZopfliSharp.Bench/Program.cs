using System.Diagnostics;
using System.Globalization;
using ZopfliSharp;

// Times ZopfliSharp on files and, given the path of a zopfli binary built from Google's
// sources, times that too on the same files and checks the outputs are identical.
//
//   ZopfliSharp.Bench [--i N] [--threads N] [--runs N] [--c path/to/zopfli] [--write dir] file...
//
// --write keeps each raw deflate output as dir/<file name>.deflate, for comparing runs made
// elsewhere: under qemu, on another architecture.
//
//   ZopfliSharp.Bench --dump-corpus dir
//
// writes the test suite's generated inputs as files, to feed them to the above.

if (args is ["--dump-corpus", string corpusDirectory])
{
    Directory.CreateDirectory(corpusDirectory);
    foreach ((string name, byte[] sample) in ZopfliSharp.Tests.Corpus.Samples)
        File.WriteAllBytes(Path.Combine(corpusDirectory, name), sample);
    File.WriteAllBytes(Path.Combine(corpusDirectory, "large"), ZopfliSharp.Tests.Corpus.Large());
    return 0;
}

int iterations = 15;
int runs = 1;
int threads = 1;
string? reference = null;
string? writeTo = null;
var files = new List<string>();

for (int a = 0; a < args.Length; a++)
{
    switch (args[a])
    {
        case "--i": iterations = int.Parse(args[++a], CultureInfo.InvariantCulture); break;
        case "--runs": runs = int.Parse(args[++a], CultureInfo.InvariantCulture); break;
        case "--threads": threads = int.Parse(args[++a], CultureInfo.InvariantCulture); break;
        case "--c": reference = args[++a]; break;
        case "--write": writeTo = args[++a]; break;
        default: files.Add(args[a]); break;
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("usage: ZopfliSharp.Bench [--i N] [--threads N] [--runs N] [--c path/to/zopfli] [--write dir] file...");
    return 2;
}

if (writeTo is not null)
    Directory.CreateDirectory(writeTo);

var options = new ZopfliOptions { Iterations = iterations, MaxDegreeOfParallelism = threads };
string runtime = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT";
Console.WriteLine($"iterations {iterations}, threads {threads}, best of {runs} run(s), {runtime} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores");
Console.WriteLine($"{"file",-28} {"size",10} {"zopfli",10} {"C# s",8} {"C s",8} {"ratio",6}  identical");

bool allIdentical = true;
foreach (string file in files)
{
    byte[] data = File.ReadAllBytes(file);

    byte[] ours = [];
    double best = double.MaxValue;
    for (int r = 0; r < runs; r++)
    {
        var watch = Stopwatch.StartNew();
        ours = Zopfli.Compress(data, ZopfliFormat.Deflate, options);
        best = Math.Min(best, watch.Elapsed.TotalSeconds);
    }

    if (writeTo is not null)
        File.WriteAllBytes(Path.Combine(writeTo, Path.GetFileName(file) + ".deflate"), ours);

    string cTime = "-", ratio = "-", identical = "-";
    if (reference is not null)
    {
        byte[] theirs = [];
        double cBest = double.MaxValue;
        for (int r = 0; r < runs; r++)
        {
            var info = new ProcessStartInfo(reference) { RedirectStandardOutput = true, UseShellExecute = false };
            info.ArgumentList.Add($"--i{iterations}");
            info.ArgumentList.Add("--deflate");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(file);

            var watch = Stopwatch.StartNew();
            using Process process = Process.Start(info)!;
            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();
            cBest = Math.Min(cBest, watch.Elapsed.TotalSeconds);
            theirs = output.ToArray();
        }

        bool same = theirs.AsSpan().SequenceEqual(ours);
        allIdentical &= same;
        cTime = cBest.ToString("F2", CultureInfo.InvariantCulture);
        ratio = (best / cBest).ToString("F2", CultureInfo.InvariantCulture);
        identical = same ? "yes" : $"NO ({theirs.Length} vs {ours.Length})";
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{Path.GetFileName(file),-28} {data.Length,10} {ours.Length,10} {best,8:F2} {cTime,8} {ratio,6}  {identical}"));
}

return allIdentical ? 0 : 1;
