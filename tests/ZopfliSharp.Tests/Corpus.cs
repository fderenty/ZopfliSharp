using System.Text;

namespace ZopfliSharp.Tests;

/// <summary>
/// Inputs generated deterministically, each aimed at a different path through the compressor.
/// Nothing binary is committed: the same seed gives the same bytes on every machine.
/// </summary>
internal static class Corpus
{
    // Declared before Samples: static fields initialize in textual order.
    private static readonly string[] Words =
    [
        "the", "of", "and", "a", "to", "in", "is", "you", "that", "it", "he", "was", "for", "on",
        "are", "as", "with", "his", "they", "I", "at", "be", "this", "have", "from", "or", "one",
        "had", "by", "word", "but", "not", "what", "all", "were", "we", "when", "your", "can",
        "said", "there", "use", "an", "each", "which", "she", "do", "how", "their", "if", "will",
        "up", "other", "about", "out", "many", "then", "them", "these", "so", "some", "her",
        "would", "make", "like", "him", "into", "time", "has", "look", "two", "more", "write",
        "compression", "deflate", "Huffman", "window", "stream", "zopfli", "block", "symbol",
    ];

    /// <summary>Every sample, by name.</summary>
    public static readonly IReadOnlyDictionary<string, byte[]> Samples = new Dictionary<string, byte[]>
    {
        ["empty"] = [],
        ["one-byte"] = [0x41],
        ["two-bytes"] = [0x41, 0x42],
        ["short-text"] = Encoding.ASCII.GetBytes("hello hello hello hello"),
        // Small enough that the fixed Huffman tree wins.
        ["fixed-tree"] = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. The quick brown fox."),
        ["text-12k"] = Text(12_000, seed: 1),
        // Incompressible: stored blocks.
        ["random-20k"] = Random(20_000, seed: 2),
        // Long runs of one byte: the run-length shortcuts of the hash and the forward pass.
        ["zeros-100k"] = new byte[100_000],
        ["runs-30k"] = Runs(30_000, seed: 3),
        ["png-like-24k"] = PngLike(width: 96, height: 64, seed: 4),
        // Text, noise and zeros in turn: block splitting has real boundaries to find, and the
        // blocks want different types.
        ["mixed-60k"] = Concat(Text(15_000, seed: 5), Random(12_000, seed: 6), new byte[9_000], Text(10_000, seed: 7), Runs(14_000, seed: 8)),
        // Repeats from near the far edge of the 32K window.
        ["far-window-80k"] = FarWindow(seed: 9),
        ["binary-structured-40k"] = Structured(40_000, seed: 10),
    };

    /// <summary>
    /// More than one master block, so compression runs in two parts with the first megabyte as
    /// dictionary for the second. Kept apart: it is slow at full iterations.
    /// </summary>
    public static byte[] Large() => Concat(Text(700_000, seed: 11), Random(150_000, seed: 12), Runs(250_000, seed: 13), Text(100_000, seed: 14));

    private static uint Next(ref uint state)
    {
        state = state * 1103515245 + 12345;
        return state >> 8;
    }

    private static byte[] Random(int length, uint seed)
    {
        var data = new byte[length];
        uint state = seed;
        for (int i = 0; i < length; i++)
            data[i] = (byte)Next(ref state);
        return data;
    }

    private static byte[] Text(int length, uint seed)
    {
        var builder = new StringBuilder(length + 32);
        uint state = seed;
        int sentence = 0;
        while (builder.Length < length)
        {
            string word = Words[Next(ref state) % (uint)Words.Length];
            builder.Append(sentence == 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word);
            sentence++;
            if (sentence > 6 && Next(ref state) % 5 == 0)
            {
                builder.Append(Next(ref state) % 7 == 0 ? ".\n" : ". ");
                sentence = 0;
            }
            else
            {
                builder.Append(Next(ref state) % 11 == 0 ? ", " : " ");
            }
        }

        return Encoding.ASCII.GetBytes(builder.ToString(0, length));
    }

    private static byte[] Runs(int length, uint seed)
    {
        var data = new byte[length];
        uint state = seed;
        int i = 0;
        while (i < length)
        {
            byte value = (byte)Next(ref state);
            // Mostly short runs, some far longer than 2 * 258.
            int run = Next(ref state) % 9 == 0 ? 300 + (int)(Next(ref state) % 1500) : 1 + (int)(Next(ref state) % 40);
            for (int k = 0; k < run && i < length; k++)
                data[i++] = value;
        }

        return data;
    }

    /// <summary>Filtered RGBA scanlines of a gradient with some noise, as in a PNG's IDAT.</summary>
    private static byte[] PngLike(int width, int height, uint seed)
    {
        int stride = width * 4;
        var data = new byte[height * (stride + 1)];
        uint state = seed;
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            data[o++] = (byte)(y % 5);  // The filter type byte.
            for (int x = 0; x < width; x++)
            {
                bool noisy = Next(ref state) % 13 == 0;
                data[o++] = (byte)(noisy ? Next(ref state) : (uint)(x * 2));
                data[o++] = (byte)(noisy ? Next(ref state) : (uint)(y * 3));
                data[o++] = (byte)((x + y) & 0xF);
                data[o++] = 255;
            }
        }

        return data;
    }

    private static byte[] FarWindow(uint seed)
    {
        byte[] head = Random(8_000, seed);
        byte[] filler = Text(24_000, seed + 1);
        // head reappears 32,000 bytes after it began, so matches sit right at the window's edge,
        // then again beyond the window, where they must not be found.
        return Concat(head, filler, head, Text(1_000, seed + 2), head, Random(30_000, seed + 3), head);
    }

    private static byte[] Structured(int length, uint seed)
    {
        // Little-endian records with slowly changing fields: short repeats at odd distances.
        var data = new byte[length];
        uint state = seed;
        int counter = 0;
        for (int i = 0; i + 16 <= length; i += 16)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i), counter++);
            BitConverter.TryWriteBytes(data.AsSpan(i + 4), (short)(counter * 7));
            BitConverter.TryWriteBytes(data.AsSpan(i + 6), 3.25f * (counter % 17));
            BitConverter.TryWriteBytes(data.AsSpan(i + 10), (ushort)(Next(ref state) % 300));
            data[i + 12] = 0xFE;
            data[i + 13] = (byte)(counter % 3);
        }

        return data;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var data = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(data, o);
            o += part.Length;
        }

        return data;
    }
}
