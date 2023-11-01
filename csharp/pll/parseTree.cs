using System.Collections.Generic;

namespace Pll;

internal abstract record Node;

internal record TempNode : Node;

internal sealed record Nodes(List<Node> Children) : Node
{
    public Node this[int index] => Children[index];
    public int Count => Children.Count;
    public override string ToString() => "List:";
}

internal abstract record ValueNode : Node;

internal abstract record ValueNode<T>(T Value) : ValueNode;

internal sealed record StringValueNode(string Value) : ValueNode<string>(Fix(Value))
{
    public override string ToString() => $"Str: {Value}";

    private static string Fix(string s) => s
        .Replace("\\n", "\n")
        .Replace("\\r", "\r")
        .Replace("\\t", "\t");
}

internal sealed record ByteValueNode(byte Value) : ValueNode<byte>(Value)
{
    public override string ToString() => $"Byte: {Value}";
}

internal sealed record LongValueNode(long Value) : ValueNode<long>(Value)
{
    public override string ToString() => $"Long: {Value}";
}

internal sealed record IdentifierNode(string Name) : Node
{
    public override string ToString() => $"Id: {Name}";
}