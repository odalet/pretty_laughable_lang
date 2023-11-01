using System;
using System.Text;

namespace Pll;

internal enum ScalarType
{
    Void,
    Int,
    Byte
}

internal sealed record TypeDefinition(ScalarType Type, int PointerLevel)
{
    public static TypeDefinition Void { get; } = new(ScalarType.Void, 0);
    public static TypeDefinition Int { get; } = new(ScalarType.Int, 0);
    public static TypeDefinition Byte { get; } = new(ScalarType.Byte, 0);
    public static TypeDefinition BytePtr { get; } = new(ScalarType.Byte, 1);

    public string Key { get; } = MakeKey(Type, PointerLevel);

    public bool IsPointer => PointerLevel > 0;

    public bool IsPointerTo(ScalarType scalarType) => Type == scalarType && PointerLevel == 1;
    public bool IsPointerTo(TypeDefinition t) => Type == t.Type && PointerLevel == t.PointerLevel + 1;
    public TypeDefinition MakePointer() => new(Type, PointerLevel + 1);
    
    public override string ToString()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < PointerLevel; i++)
            _ = builder.Append("ptr ");
        return builder
            .Append(Type.ToString().ToLowerInvariant())
            .ToString();
    }

    public bool CanCastTo(TypeDefinition other)
    {
        if (IsPointer) return other.IsPointer || other.Type == ScalarType.Int;
        if (Type == ScalarType.Int) return other.IsPointer || other.Type == ScalarType.Int;
        if (Type == ScalarType.Byte) return other.Type is ScalarType.Int or ScalarType.Byte;
        return false;
    }

    private static string MakeKey(ScalarType type, int pointerLevel)
    {
        var builder = new StringBuilder().Append(type.ToString());
        for (var i = 0; i < pointerLevel; i++)
            _ = builder.Append("Ptr");
        return builder.ToString();
    }
}