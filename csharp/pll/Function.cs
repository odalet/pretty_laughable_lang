using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Pll;

internal sealed class Scope
{
    // Variable names to (type, index) tuples.
    // For functions, the key includes argument types and the index is the index of `Func.funcs`.
    public readonly Dictionary<string, (TypeDefinition type, int index)> Names;

    public Scope(Scope? previousScope)
    {
        Previous = previousScope;
        NLocal = 0;
        Names = new();
        LoopStart = Previous?.LoopStart ?? -1;
        LoopEnd = Previous?.LoopEnd ?? -1;
    }

    public Scope? Previous { get; } // the parent scope
    public int NLocal { get; set; } // the number of local variables seen
    public int SavedStack { get; init; }

    public int LoopStart { get; set; }
    public int LoopEnd { get; set; }

    public (TypeDefinition type, int index) Lookup(string name)
    {
        var scope = this;
        while (scope != null)
        {
            if (scope.Names.TryGetValue(name, out var lookup))
                return lookup;
            scope = scope.Previous;
        }

        return (TypeDefinition.Void, -1); // not found
    }
}

internal sealed record Instruction(string Op, object[] Args);

internal sealed class Function
{
    private readonly Function? previous; // the parent function (linked list)
    private List<Instruction> code; // the output: a list of instructions
    private int nvar; // current number of local variable in the stack (non-temporary)
    private List<int?> labels; // label IDs to instruction locations

    public Function(string name, string? signature = null, Function? previousFunction = null, TypeDefinition? returnType = null)
    {
        Name = name;
        Signature = signature ?? name;
        previous = previousFunction;
        Level = previous?.Level + 1 ?? 0;
        ReturnType = returnType ?? TypeDefinition.Void;
        Funcs = previous?.Funcs ?? new();
        Scope = new(null);
        code = new();
        nvar = 0;
        StackTop = 0;
        labels = new();
    }

    public string Name { get; }
    public string Signature { get; }
    public List<Function> Funcs { get; } // a list of all functions. shared by all functions in a program.
    public Scope Scope { get; private set; }
    public TypeDefinition ReturnType { get; } // the return type of this function
    public int Level { get; } // nested function level. the level of `main` is 1.
    public int StackTop { get; set; }
    public int VarCount => nvar; // Only used by Debug.Assert

    public IReadOnlyList<Instruction> Instructions => code;
    public IReadOnlyList<int?> Labels => labels;

    // enter a new scope
    public void EnterScope() => Scope = new(Scope) { SavedStack = StackTop };

    // exit a scope and revert the stack
    public void LeaveScope()
    {
        StackTop = Scope.SavedStack;
        nvar -= Scope.NLocal;
        Scope = Scope.Previous ?? throw new InvalidOperationException("Imbalanced scope");
    }

    // allocate a new local variable in the current scope
    public int AddVar(string name, TypeDefinition typ)
    {
        if (Scope.Names.ContainsKey(name))
            throw new InvalidOperationException($"Identifier '{name}' already exists");
        Scope.Names.Add(name, (typ, nvar));
        Scope.NLocal++;
        Debug.Assert(StackTop == nvar);
        var dest = StackTop;
        StackTop++;
        nvar++;
        return dest;
    }

    // lookup a name. returns a tuple of (function_level, type, index)
    public (int level, TypeDefinition type, int index) GetVar(string name)
    {
        var (typ, index) = Scope.Lookup(name);
        if (index >= 0)
            return (Level, typ, index);

        return previous?.GetVar(name)
               ?? throw new InvalidOperationException($"Undefined identifier: '{name}'");
    }

    // allocate a temporary variable on the stack top and return its index
    public int MakeTempVar()
    {
        var dest = StackTop;
        StackTop++;
        return dest;
    }

    // allocate a new label ID
    public int NewLabel()
    {
        var length = labels.Count;
        labels.Add(null); // filled later
        return length;
    }

    // associate the label ID to the current location
    public void SetLabel(int label)
    {
        Debug.Assert(label < labels.Count);
        labels[label] = code.Count;
    }

    public void AddInstruction(string op, params object[] args) => AddInstruction(new Instruction(op, args));
    private void AddInstruction(Instruction instruction) => code.Add(instruction);
}

internal static class FunctionExtensions
{
    public static string Dump(this Function function)
    {
        var builder = new StringBuilder();

        var indexToName = new Dictionary<int, (string name, string signature)>();
        for (var i = 0; i < function.Funcs.Count; i++)
            indexToName.Add(i, (function.Funcs[i].Name, function.Funcs[i].Signature));
        
        for (var i = 0; i < function.Funcs.Count; i++)
        {
            var func = function.Funcs[i]; 
            _ = builder.AppendLine($"#{i} (level: {func.Level}) - {indexToName[i].signature}:");
            DumpFunction(func, builder, indexToName);
        }

        return builder.ToString();
    }

    private static void DumpFunction(Function function, StringBuilder builder, Dictionary<int, (string name, string signature)> functionNames)
    {
        // const string tab = "    ";
        // var tabs = "";
        // for (var i = 0; i < indent; i++) 
        //     tabs += tab;

        var instructionToLabels = new Dictionary<int, List<int>>();
        for (var labelIndex = 0; labelIndex < function.Labels.Count; labelIndex++)
        {
            var label = function.Labels[labelIndex];
            if (!label.HasValue) continue;
            var instruction = label.Value;
            if (!instructionToLabels.ContainsKey(instruction))
                instructionToLabels.Add(instruction, new());
            instructionToLabels[instruction].Add(labelIndex);
        }

        for (var instructionIndex = 0; instructionIndex < function.Instructions.Count; instructionIndex++)
        {
            // Emit the instruction index
            _ = builder
                .Append($"{instructionIndex.ToString().PadLeft(4)} ");

            // Look for labels
            if (instructionToLabels.TryGetValue(instructionIndex, out var label) && label.Count != 0)
            {
                _ = builder.Append(string.Join(", ", label.Select(l => $"L{l}"))).AppendLine(": ");
                _ = builder.Append("         ");
            }
            else _ = builder.Append("    ");

            // Emit the instruction
            var instruction = function.Instructions[instructionIndex];
            _ = builder.Append(instruction.Op).Append(' ');
            if (instruction.Op is "jmp" or "jmpf")
            {
                for (var argIndex = 0; argIndex < instruction.Args.Length - 1; argIndex++)
                    _ = builder.Append(instruction.Args[argIndex]).Append(", ");

                var arg = (int)instruction.Args[^1];
                _ = builder.AppendLine($"L{arg}");
            }
            else if (instruction.Op is "call")
            {
                var arg = (int)instruction.Args[0];
                _ = builder.Append(functionNames[arg].name);
                
                for (var argIndex = 1; argIndex < instruction.Args.Length; argIndex++)
                    _ = builder.Append(", ").Append(instruction.Args[argIndex]);
                _ = builder.AppendLine();
            }
            else _ = builder.AppendLine(string.Join(", ", instruction.Args));
        }

        _ = builder.AppendLine();
    }
}