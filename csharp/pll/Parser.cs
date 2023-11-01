using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pll;

internal static class ParseTreeHelper
{
    private const string tab = "  ";

    public static string Dump(this Node node)
    {
        var builder = new StringBuilder();
        Walk(node, builder, 0);
        return builder.ToString();
    }

    private static void Walk(this Node node, StringBuilder builder, int level)
    {
        if (node is not Nodes nodes)
        {
            builder.AppendTabs(level).AppendLine(node.ToString() ?? "<NULL>");
            return;
        }

        if (nodes.Children.Count == 0)
        {
            builder.AppendTabs(level).AppendLine("[]");
            return;
        }

        builder.AppendTabs(level).AppendLine("[");
        foreach (var child in nodes.Children)
            Walk(child, builder, level + 1);
        builder.AppendTabs(level).AppendLine("]");
    }

    private static StringBuilder AppendTabs(this StringBuilder builder, int count)
    {
        for (var i = 0; i < count; i++)
            builder.Append(tab);
        return builder;
    }
}

internal sealed class Parser
{
    private readonly string text;

    // Wrap the script inside a 'main' function
    public Parser(string originalText) => text = $"(def (main int) () (do {originalText}))";

    public Node Parse()
    {
        var (index, node) = ParseExpr(0);
        index = SkipSpaces(index);
        if (index < text.Length)
            throw new InvalidDataException("Trailing garbage");

        return node;
    }

    private (int index, Node node) ParseQuotes(int index)
    {
        var term = text[index];
        var end = index + 1;
        while (end < text.Length)
        {
            if (text[end] == term) break;
            if (text[end] == '\\') end++;
            end++;
        }

        if (end >= text.Length || text[end] != term)
            throw new InvalidDataException("Invalid quoted expression");

        var textValue = text[(index + 1)..end];
        if (term == '"')
            return (end + 1, new StringValueNode(textValue));

        // Single-quoted text: should be a unique character, but may be:
        // \u1234 or \n \t...?

        if (textValue.StartsWith("\\u"))
        {
            if (TryParseInteger(textValue[2..], out var longValue) &&
                (longValue is >= 0 and <= ushort.MaxValue))
                textValue = Encoding.Unicode.GetString(new[] { (byte)(longValue << 8), (byte)longValue });
            else
                throw new InvalidDataException($"Invalid character: '{textValue}'");
        }

        if (textValue.Length != 1)
            throw new InvalidDataException("Invalid character");

        // Hacky, but not more than relying on json...
        var bytes = Encoding.UTF8.GetBytes(textValue);
        return (end + 1, new ByteValueNode(bytes[0]));
    }

    private static Node ParseValue(string s)
    {
        if (TryParseInteger(s, out var longValue))
            return new LongValueNode(longValue);

        if (s.EndsWith("u8"))
        {
            var substring = s[..^2];
            if (TryParseInteger(substring, out var byteValue))
            {
                if (byteValue is > 255 or < 0)
                    throw new InvalidDataException($"Invalid byte value: '{s}'");
                return new ByteValueNode((byte)byteValue);
            }
        }

        if (char.IsDigit(s[0])) throw new InvalidDataException($"Invalid identifier: '{s}'");
        return new IdentifierNode(s);
    }

    private static bool TryParseInteger(string s, out long integer)
    {
        var hasRadix = s.ToLowerInvariant().StartsWith("0x");
        var radix = hasRadix ? 16 : 10;
        var input = hasRadix ? s[2..] : s;

        try
        {
            integer = Convert.ToInt64(input, radix);
            return true;
        }
        catch
        {
            integer = 0L;
            return false;
        }
    }

    private (int index, Node node) ParseExpr(int index)
    {
        index = SkipSpaces(index);
        switch (text[index])
        {
            case '(':
            {
                index++;
                var nodes = new List<Node>();
                while (true)
                {
                    index = SkipSpaces(index);
                    if (index >= text.Length)
                        throw new InvalidDataException("Unbalanced parens");
                    if (text[index] == ')')
                    {
                        index++;
                        break;
                    }

                    var (i, n) = ParseExpr(index);
                    index = i;
                    nodes.Add(n);
                }

                return (index, new Nodes(nodes));
            }
            case ')': throw new InvalidDataException("Unexpected closing paren");
            case '"' or '\'': return ParseQuotes(index);
        }

        // Constant or name
        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && !"()".Contains(text[index]))
            index++;

        if (start == index) throw new InvalidDataException("Empty program");

        return (index, ParseValue(text[start..index]));
    }

    private int SkipSpaces(int index)
    {
        while (true)
        {
            var save = index;

            // Spaces
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            // Line comments
            if (index < text.Length && text[index] == ';')
            {
                index++;
                while (index < text.Length && text[index] != '\n')
                    index++;
            }

            if (index == save)
                break;
        }

        return index;
    }
}