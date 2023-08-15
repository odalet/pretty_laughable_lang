using System.Buffers.Binary;

namespace Pll.Codegen;

internal static class helpers
{
    // See https://stackoverflow.com/questions/2350099/how-to-convert-an-int-to-a-little-endian-byte-array
    public static byte[] IntToLittleEndian(int data)
    {
        var output = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(output, data);
        return output;
    }
    
    public static byte[] LongToLittleEndian(long data)
    {
        var output = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(output, data);
        return output;
    }
}