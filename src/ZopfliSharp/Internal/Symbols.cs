// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from symbols.h of Zopfli, https://github.com/google/zopfli
// Copyright 2016 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace ZopfliSharp.Internal;

/// <summary>
/// Mapping between lengths and distances and the deflate symbols that encode them, as laid
/// down in RFC 1951 section 3.2.5. Port of symbols.h.
/// </summary>
internal static class Symbols
{
    /// <summary>Number of extra bits that follow the symbol of a distance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDistExtraBits(int dist)
        => dist < 5 ? 0 : BitOperations.Log2((uint)(dist - 1)) - 1;

    /// <summary>Value of the extra bits that follow the symbol of a distance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDistExtraBitsValue(int dist)
    {
        if (dist < 5)
            return 0;

        int l = BitOperations.Log2((uint)(dist - 1));
        return (dist - (1 + (1 << l))) & ((1 << (l - 1)) - 1);
    }

    /// <summary>The distance symbol, 0 to 29, for a distance from 1 to 32768.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDistSymbol(int dist)
    {
        if (dist < 5)
            return dist - 1;

        int l = BitOperations.Log2((uint)(dist - 1));
        int r = ((dist - 1) >> (l - 1)) & 1;
        return l * 2 + r;
    }

    /// <summary>Number of extra bits that follow the symbol of a length from 3 to 258.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLengthExtraBits(int l) => LengthExtraBits[l];

    /// <summary>Value of the extra bits that follow the symbol of a length from 3 to 258.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLengthExtraBitsValue(int l) => LengthExtraBitsValue[l];

    /// <summary>The length symbol, 257 to 285, for a length from 3 to 258.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLengthSymbol(int l) => LengthSymbol[l];

    /// <summary>Number of extra bits that follow a length symbol from 257 to 285.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLengthSymbolExtraBits(int s) => LengthSymbolExtraBits[s - 257];

    /// <summary>Number of extra bits that follow a distance symbol from 0 to 29.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDistSymbolExtraBits(int s) => DistSymbolExtraBits[s];

    private static ReadOnlySpan<byte> LengthExtraBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 0,
    ];

    private static ReadOnlySpan<byte> LengthExtraBitsValue =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 3, 0,
        1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5,
        6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6,
        7, 8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2,
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
        29, 30, 31, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17,
        18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 0, 1, 2, 3, 4, 5, 6,
        7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26,
        27, 28, 29, 30, 31, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 0,
    ];

    private static ReadOnlySpan<ushort> LengthSymbol =>
    [
        0, 0, 0, 257, 258, 259, 260, 261, 262, 263, 264,
        265, 265, 266, 266, 267, 267, 268, 268,
        269, 269, 269, 269, 270, 270, 270, 270,
        271, 271, 271, 271, 272, 272, 272, 272,
        273, 273, 273, 273, 273, 273, 273, 273,
        274, 274, 274, 274, 274, 274, 274, 274,
        275, 275, 275, 275, 275, 275, 275, 275,
        276, 276, 276, 276, 276, 276, 276, 276,
        277, 277, 277, 277, 277, 277, 277, 277,
        277, 277, 277, 277, 277, 277, 277, 277,
        278, 278, 278, 278, 278, 278, 278, 278,
        278, 278, 278, 278, 278, 278, 278, 278,
        279, 279, 279, 279, 279, 279, 279, 279,
        279, 279, 279, 279, 279, 279, 279, 279,
        280, 280, 280, 280, 280, 280, 280, 280,
        280, 280, 280, 280, 280, 280, 280, 280,
        281, 281, 281, 281, 281, 281, 281, 281,
        281, 281, 281, 281, 281, 281, 281, 281,
        281, 281, 281, 281, 281, 281, 281, 281,
        281, 281, 281, 281, 281, 281, 281, 281,
        282, 282, 282, 282, 282, 282, 282, 282,
        282, 282, 282, 282, 282, 282, 282, 282,
        282, 282, 282, 282, 282, 282, 282, 282,
        282, 282, 282, 282, 282, 282, 282, 282,
        283, 283, 283, 283, 283, 283, 283, 283,
        283, 283, 283, 283, 283, 283, 283, 283,
        283, 283, 283, 283, 283, 283, 283, 283,
        283, 283, 283, 283, 283, 283, 283, 283,
        284, 284, 284, 284, 284, 284, 284, 284,
        284, 284, 284, 284, 284, 284, 284, 284,
        284, 284, 284, 284, 284, 284, 284, 284,
        284, 284, 284, 284, 284, 284, 284, 285,
    ];

    private static ReadOnlySpan<byte> LengthSymbolExtraBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    ];

    private static ReadOnlySpan<byte> DistSymbolExtraBits =>
    [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8,
        9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    ];
}
