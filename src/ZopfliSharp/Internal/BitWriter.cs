// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Translated to C# from deflate.c and util.h of Zopfli, https://github.com/google/zopfli
// Copyright 2011 Google Inc. All Rights Reserved.
// Authors of the original: Lode Vandevenne and Jyrki Alakuijala.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

namespace ZopfliSharp.Internal;

/// <summary>
/// A growable byte buffer written bit by bit, least significant bit first, as deflate wants.
/// Port of AddBit, AddBits and AddHuffmanBits (deflate.c) and of ZOPFLI_APPEND_DATA.
/// </summary>
/// <remarks>
/// Like the C code, the bit pointer stays apart from the bytes: <see cref="AppendByte"/> adds
/// a whole byte after whatever partial one precedes it, which is how the containers write
/// their trailers and how stored blocks align themselves.
/// </remarks>
internal sealed class BitWriter(int initialCapacity)
{
    private byte[] _buffer = new byte[Math.Max(16, initialCapacity)];
    private int _size;

    /// <summary>The bit position in the last byte, from 0 to 7; 0 means a new byte starts next.</summary>
    public int BitPointer;

    /// <summary>Number of bytes written, the last one possibly partial.</summary>
    public int Length => _size;

    /// <summary>The bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _size);

    private void Grow(int needed)
    {
        long capacity = Math.Max((long)_buffer.Length * 2, (long)_size + needed);
        Array.Resize(ref _buffer, (int)Math.Min(capacity, Array.MaxLength));
    }

    /// <summary>Appends a whole byte, leaving the bit pointer alone.</summary>
    public void AppendByte(byte value)
    {
        if (_size == _buffer.Length)
            Grow(1);
        _buffer[_size++] = value;
    }

    /// <summary>Appends whole bytes, leaving the bit pointer alone.</summary>
    public void AppendBytes(ReadOnlySpan<byte> values)
    {
        if (_buffer.Length - _size < values.Length)
            Grow(values.Length);
        values.CopyTo(_buffer.AsSpan(_size));
        _size += values.Length;
    }

    /// <summary>Adds one bit.</summary>
    public void AddBit(int bit)
    {
        if (BitPointer == 0)
            AppendByte(0);
        _buffer[_size - 1] |= (byte)(bit << BitPointer);
        BitPointer = (BitPointer + 1) & 7;
    }

    /// <summary>Adds the low <paramref name="length"/> bits of a value, least significant first.</summary>
    public void AddBits(int symbol, int length)
    {
        for (int i = 0; i < length; i++)
        {
            int bit = (symbol >> i) & 1;
            if (BitPointer == 0)
                AppendByte(0);
            _buffer[_size - 1] |= (byte)(bit << BitPointer);
            BitPointer = (BitPointer + 1) & 7;
        }
    }

    /// <summary>
    /// Adds a Huffman code: like <see cref="AddBits"/> but most significant bit first, the
    /// other order the deflate specification uses.
    /// </summary>
    public void AddHuffmanBits(int symbol, int length)
    {
        for (int i = 0; i < length; i++)
        {
            int bit = (symbol >> (length - i - 1)) & 1;
            if (BitPointer == 0)
                AppendByte(0);
            _buffer[_size - 1] |= (byte)(bit << BitPointer);
            BitPointer = (BitPointer + 1) & 7;
        }
    }

    /// <summary>A copy of the bytes written.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();
}
