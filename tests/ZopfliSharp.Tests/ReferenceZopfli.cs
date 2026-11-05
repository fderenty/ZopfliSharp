using System.Diagnostics;
using System.Globalization;

namespace ZopfliSharp.Tests;

/// <summary>
/// The original C Zopfli, reached through its Python binding (<c>pip install zopfli</c>), which
/// compiles Google's sources unchanged. It is the oracle every output is compared with.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter is the one named by the <c>ZOPFLISHARP_PYTHON</c> environment variable, or
/// else the first of <c>python3</c>, <c>python</c> and <c>py</c> on the path that can import the
/// binding.
/// </para>
/// <para>
/// Without it the comparisons report inconclusive, since its absence says nothing about the
/// port. Setting <c>ZOPFLISHARP_REQUIRE_REFERENCE=1</c>, as CI does, makes them fail instead,
/// so a broken setup cannot pass for a green run.
/// </para>
/// </remarks>
internal static class ReferenceZopfli
{
    private static readonly Lock Gate = new();
    private static bool _probed;
    private static string? _python;
    private static string _unavailableReason = "not probed";

    // Reads the data on stdin, writes the compressed bytes on stdout. The binding offers zlib
    // and gzip only; raw deflate is the zlib stream without its 2-byte header and 4-byte
    // trailer, which is exactly how the C library builds it.
    private const string Script = """
        import sys
        import zopfli.zopfli
        fmt, iterations, splitting, splitmax = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
        data = sys.stdin.buffer.read()
        out = zopfli.zopfli.compress(data, numiterations=iterations, blocksplitting=splitting,
                                     blocksplittingmax=splitmax, gzip_mode=1 if fmt == "gzip" else 0)
        out = bytes(out)
        if fmt == "deflate":
            out = out[2:-4]
        sys.stdout.buffer.write(out)
        """;

    /// <summary>Compresses with the C library, or skips the calling test when it is unavailable.</summary>
    public static byte[] Compress(byte[] data, ZopfliFormat format, ZopfliOptions options)
    {
        string python = RequirePython();

        string fmt = format switch
        {
            ZopfliFormat.Deflate => "deflate",
            ZopfliFormat.Zlib => "zlib",
            ZopfliFormat.Gzip => "gzip",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        var info = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-I");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(Script);
        info.ArgumentList.Add(fmt);
        info.ArgumentList.Add(options.Iterations.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(options.BlockSplitting ? "1" : "0");
        info.ArgumentList.Add(options.BlockSplittingMax.ToString(CultureInfo.InvariantCulture));

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Python.");
        Task<string> error = process.StandardError.ReadToEndAsync();

        // The script reads all of its input before writing anything, so writing first and
        // reading after cannot deadlock.
        using (Stream stdin = process.StandardInput.BaseStream)
            stdin.Write(data);

        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();

        if (process.ExitCode != 0)
            Assert.Fail($"The reference zopfli failed ({process.ExitCode}): {error.Result}");

        return output.ToArray();
    }

    private static string RequirePython()
    {
        lock (Gate)
        {
            if (!_probed)
            {
                _probed = true;
                Probe();
            }
        }

        if (_python is not null)
            return _python;

        string message = $"Reference zopfli unavailable: {_unavailableReason}";
        if (Environment.GetEnvironmentVariable("ZOPFLISHARP_REQUIRE_REFERENCE") == "1")
            Assert.Fail(message);

        Assert.Inconclusive(message);
        return null!;
    }

    private static void Probe()
    {
        string? configured = Environment.GetEnvironmentVariable("ZOPFLISHARP_PYTHON");
        string[] candidates = string.IsNullOrEmpty(configured) ? ["python3", "python", "py"] : [configured];

        var reasons = new List<string>();
        foreach (string candidate in candidates)
        {
            var info = new ProcessStartInfo(candidate)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-I");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("import zopfli.zopfli, zopfli; print(zopfli.__version__)");

            try
            {
                using Process? process = Process.Start(info);
                if (process is null)
                    continue;

                string version = process.StandardOutput.ReadToEnd().Trim();
                string error = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    _python = candidate;
                    return;
                }

                reasons.Add($"{candidate}: {LastLine(error)}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                reasons.Add($"{candidate}: not found");
            }
        }

        _unavailableReason = "no Python with the zopfli binding (pip install zopfli). " + string.Join("; ", reasons);
    }

    private static string LastLine(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "failed" : lines[^1];
    }
}
