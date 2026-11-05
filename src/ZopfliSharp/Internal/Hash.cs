// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from hash.c of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp.Internal;

/// <summary>
/// The rolling hash chains that <see cref="Lz77.FindLongestMatch"/> walks. Port of hash.c,
/// with ZOPFLI_HASH_SAME and ZOPFLI_HASH_SAME_HASH enabled as in the C build.
/// </summary>
/// <remarks>
/// Positions are stored modulo the window. A second hash, mixing in the length of the run of
/// identical bytes at each position, lets the match finder skip quickly through long runs.
/// One instance is reused for every pass of a compression: each pass starts with
/// <see cref="Reset"/>, as the C code does with a freshly allocated hash.
/// </remarks>
internal sealed class Hash
{
    private const int HashShift = 5;
    private const int HashMask = 32767;

    // The C code allocates 65536 heads, but a hash value never exceeds HashMask: half suffice.
    private const int HeadSize = HashMask + 1;

    private static readonly ushort[] Identity = BuildIdentity();

    /// <summary>Hash value to the index of its most recent occurrence, or -1.</summary>
    public readonly int[] Head = new int[HeadSize];

    /// <summary>Index to the index of the previous occurrence of the same hash.</summary>
    public readonly ushort[] Prev = new ushort[Constants.WindowSize];

    /// <summary>Index to the hash value at this index.</summary>
    public readonly int[] HashVal = new int[Constants.WindowSize];

    /// <summary>Current hash value.</summary>
    public int Val;

    /// <summary>Same as <see cref="Head"/>, for the second hash.</summary>
    public readonly int[] Head2 = new int[HeadSize];

    /// <summary>Same as <see cref="Prev"/>, for the second hash.</summary>
    public readonly ushort[] Prev2 = new ushort[Constants.WindowSize];

    /// <summary>Same as <see cref="HashVal"/>, for the second hash.</summary>
    public readonly int[] HashVal2 = new int[Constants.WindowSize];

    /// <summary>Current value of the second hash.</summary>
    public int Val2;

    /// <summary>Number of repetitions of the same byte after this index.</summary>
    public readonly ushort[] Same = new ushort[Constants.WindowSize];

    private static ushort[] BuildIdentity()
    {
        var identity = new ushort[Constants.WindowSize];
        for (int i = 0; i < identity.Length; i++)
            identity[i] = (ushort)i;
        return identity;
    }

    /// <summary>Resets all fields. Port of ZopfliResetHash.</summary>
    public void Reset()
    {
        Val = 0;
        Array.Fill(Head, -1);
        // If prev[j] == j, then prev[j] is uninitialized.
        Array.Copy(Identity, Prev, Constants.WindowSize);
        Array.Fill(HashVal, -1);
        Array.Clear(Same);

        Val2 = 0;
        Array.Fill(Head2, -1);
        Array.Copy(Identity, Prev2, Constants.WindowSize);
        Array.Fill(HashVal2, -1);
    }

    private void UpdateValue(int c) => Val = ((Val << HashShift) ^ c) & HashMask;

    /// <summary>
    /// Fills in the initial hash value before <see cref="Update"/> can be used. Port of
    /// ZopfliWarmupHash.
    /// </summary>
    public void Warmup(ReadOnlySpan<byte> array, int pos, int end)
    {
        UpdateValue(array[pos]);
        if (pos + 1 < end)
            UpdateValue(array[pos + 1]);
    }

    /// <summary>
    /// Adds position <paramref name="pos"/> to the hash. Calls must be made for consecutive
    /// positions. Port of ZopfliUpdateHash.
    /// </summary>
    public void Update(ReadOnlySpan<byte> array, int pos, int end)
    {
        int hpos = pos & Constants.WindowMask;

        UpdateValue(pos + Constants.MinMatch <= end ? array[pos + Constants.MinMatch - 1] : 0);
        HashVal[hpos] = Val;
        int head = Head[Val];
        Prev[hpos] = head != -1 && HashVal[head] == Val ? (ushort)head : (ushort)hpos;
        Head[Val] = hpos;

        // Update "same". At pos 0 the C code reads index (size_t)-1 & mask, the last slot, which
        // a reset leaves at zero; the signed arithmetic here lands on the same slot.
        int amount = 0;
        int previous = Same[(pos - 1) & Constants.WindowMask];
        if (previous > 1)
            amount = previous - 1;

        byte first = array[pos];
        while (pos + amount + 1 < end && first == array[pos + amount + 1] && amount < ushort.MaxValue)
            amount++;

        Same[hpos] = (ushort)amount;

        Val2 = ((amount - Constants.MinMatch) & 255) ^ Val;
        HashVal2[hpos] = Val2;
        int head2 = Head2[Val2];
        Prev2[hpos] = head2 != -1 && HashVal2[head2] == Val2 ? (ushort)head2 : (ushort)hpos;
        Head2[Val2] = hpos;
    }
}
