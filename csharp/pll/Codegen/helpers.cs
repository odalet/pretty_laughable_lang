using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pll.Codegen;

internal static class Helper
{
    public static byte[] UShortToLittleEndian(ushort data)
    {
        var output = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt16LittleEndian(output, data);
        return output;
    }
    
    // See https://stackoverflow.com/questions/2350099/how-to-convert-an-int-to-a-little-endian-byte-array
    public static byte[] IntToLittleEndian(int data)
    {
        var output = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(output, data);
        return output;
    }
    
    public static byte[] UIntToLittleEndian(uint data)
    {
        var output = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(output, data);
        return output;
    }

    public static byte[] LongToLittleEndian(long data)
    {
        var output = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(output, data);
        return output;
    }

    public static byte[] ULongToLittleEndian(ulong data)
    {
        var output = new byte[sizeof(long)];
        BinaryPrimitives.WriteUInt64LittleEndian(output, data);
        return output;
    }
}

// I'd rather not use inheritance, but that's ok for now...
// This should also use spans and not a list...
internal abstract class BaseBufferHandler
{
    protected readonly List<byte> Buffer;

    protected BaseBufferHandler(List<byte> buffer, int alignment)
    {
        Buffer = buffer;
        Alignment = alignment;
    }

    protected int Alignment { get; }

    protected void Add(byte b) => Buffer.Add(b);
    protected void Add(params byte[] bytes) => Buffer.AddRange(bytes);

    protected void Add(string bytes) => Add(bytes
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => (byte)int.Parse(p, NumberStyles.HexNumber)).ToArray());


    protected void AddPadding()
    {
        if (Alignment == 0) return;
        Add(0xCC); // int3
        while (Buffer.Count % Alignment != 0)
            Add(0xCC); // int3
    }
    
    protected void AddI8(byte b) => Add(b);
    protected void AddI32(int i) => Add(Helper.IntToLittleEndian(i));
    protected void AddI64(long l) => Add(Helper.LongToLittleEndian(l));
}