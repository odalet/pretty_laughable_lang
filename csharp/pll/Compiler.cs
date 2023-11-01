using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Pll;

internal sealed class Compiler
{
    private static readonly string[] compops = new[] { "eq", "ge", "gt", "le", "lt", "ne" };
    private static readonly string[] binops = new[] { "%", "*", "+", "-", "/", "and", "or", "eq", "ge", "gt", "le", "lt", "ne" };
    private static readonly string[] unops = new[] { "-", "not" };
    private static readonly string[] scopeKeywords = new[] { "do", "then", "else" };

    public Function Compile(Node node)
    {
        var fenv = new Function("$");
        _ = CompileMain(fenv, node);
        return fenv;
    }

    private (TypeDefinition, int) CompileMain(Function fenv, Node node)
    {
        AssertIsMain(node);
        var function = ScanFunction(fenv, node);
        return CompileFunction(function, node);
    }

    // function preprocessing:
    // make the function visible to the whole scope before its definition.
    private static Function ScanFunction(Function fenv, Node node)
    {
        // (def (functionName retval) (arguments) (body))

        // Plenty of casts and unchecked indexing here... C# is not Python!
        var nodes = (Nodes)node;

        var functionNameAndType = (Nodes)nodes[1];

        // Function name and return type
        var name = ((IdentifierNode)functionNameAndType[0]).Name;
        var rtyp = ValidateType(functionNameAndType, 1);

        // add the (name, arg-type) pairs to the map
        var argNodes = (Nodes)nodes[2];
        var args = new List<(string name, TypeDefinition typ)>();
        foreach (var argNameAndType in argNodes.Children.Cast<Nodes>())
        {
            if (argNameAndType == null) throw new InvalidCastException("Argument node should be a list");
            var argName = ((IdentifierNode)argNameAndType[0]).Name;
            var argTyp = ValidateType(argNameAndType, 1);
            args.Add((argName, argTyp));
        }

        var functionKey = MakeFunctionKey(name, args.Select(a => a.typ));
        var humanReadableSignature = $"{name}({string.Join(", ", args.Select(x => $"{x.name}: {x.typ}"))}) -> {rtyp}";
        if (fenv.Scope.Names.ContainsKey(functionKey))
            throw new InvalidOperationException($"Duplicated Function ({humanReadableSignature})");
        fenv.Scope.Names.Add(functionKey, (rtyp, fenv.Funcs.Count));

        var func = new Function(name, humanReadableSignature, fenv, rtyp);
        fenv.Funcs.Add(func);
        return func;
    }

    // actually compile the function definition.
    // note that the `fenv` argument is the target function!
    private (TypeDefinition, int) CompileFunction(Function fenv, Node node)
    {
        // (def (functionName retval) (arguments) (body))

        // Plenty of casts and unchecked indexing here... C# is not Python!
        var nodes = (Nodes)node;
        var argNodes = (Nodes)nodes[2];
        var bodyNodes = (Nodes)nodes[3];

        // treat arguments as local variables
        foreach (var argNameAndType in argNodes.Children.Cast<Nodes>())
        {
            var argName = ((IdentifierNode)argNameAndType[0]).Name;
            var argTyp = ValidateType(argNameAndType, 1);
            if (argTyp.Type == ScalarType.Void) throw new InvalidOperationException("An argument cannot be of type void");
            _ = fenv.AddVar(argName, argTyp);
        }

        Debug.Assert(fenv.StackTop == argNodes.Children.Count);

        // Compile the function body
        var (bodyTyp, retValue) = CompileExpression(fenv, bodyNodes);

        // TODO: uncomment when all the compilation is done
        // if (fenv.ReturnType != TypeDefinition.Void && fenv.ReturnType != bodyTyp)
        //     throw new InvalidOperationException("Function return type and body type do not agree");

        if (fenv.ReturnType == TypeDefinition.Void)
            retValue = -1;

        // TODO: this creates an additional ret instruction if there was an explicit 'return' statement in the function's code
        fenv.AddInstruction("ret", retValue);
        return (TypeDefinition.Void, -1);
    }

    // the entry point of compilation.
    // returns a (type, index) tuple. the index is -1 if the type is `('void',)`
    private (TypeDefinition, int) CompileExpression(Function fenv, Node body, bool allowVar = false)
    {
        if (allowVar) Debug.Assert(fenv.StackTop == fenv.VarCount);
        var savedStackTop = fenv.StackTop;

        // The actual implementation
        var (tp, variable) = CompileExpressionTemp(fenv, body, allowVar);
        Debug.Assert(variable < fenv.StackTop);

        // Discard temporaries from the above compilation
        fenv.StackTop = allowVar
            ? fenv.VarCount // The stack is either local variables only
            : savedStackTop; // or reverts to its previous state

        // The result is either a temporary stored at the top of the stack
        // or a local variable.
        Debug.Assert(variable <= fenv.StackTop);
        return (tp, variable);
    }

    private (TypeDefinition, int) CompileExpressionTemp(Function fenv, Node body, bool allowVar = false)
    {
        if (body is ValueNode val) return CompileConstant(fenv, val);
        if (body is IdentifierNode idn) return CompileGetVar(fenv, idn);
        if (body is not Nodes nodes) throw new ArgumentException($"Invalid Node type: {body.GetType()}");

        // Anything else
        // var list = nodes.Children;
        if (nodes.Count == 0) throw new InvalidOperationException("Empty list");

        if (nodes[0] is IdentifierNode id)
        {
            if (nodes.Count == 3 && binops.Contains(id.Name)) return CompileBinop(fenv, id.Name, nodes);
            if (nodes.Count == 2 && unops.Contains(id.Name)) return CompileUnop(fenv, id.Name, nodes);
            if (scopeKeywords.Contains(id.Name)) return CompileScope(fenv, nodes);
            if (nodes.Count == 3 && id.Name == "var")
            {
                if (!allowVar)
                    throw new InvalidOperationException("Variable declarations are allowed only as children of scopes and conditions");
                return CompileNewVar(fenv, nodes);
            }

            if (nodes.Count == 3 && id.Name == "set") return CompileSetVar(fenv, nodes);
            if (nodes.Count is 3 or 4 && id.Name is "?" or "if") return CompileConditional(fenv, nodes);
            if (nodes.Count == 3 && id.Name == "loop") return CompileLoop(fenv, nodes);
            if (nodes.Count == 1 && id.Name == "break")
            {
                if (fenv.Scope.LoopEnd < 0)
                    throw new InvalidOperationException("'break' was encountered outside a loop");
                fenv.AddInstruction("jmp", fenv.Scope.LoopEnd);
                return (TypeDefinition.Void, -1);
            }

            if (nodes.Count == 1 && id.Name == "continue")
            {
                if (fenv.Scope.LoopEnd < 0)
                    throw new InvalidOperationException("'continue' was encountered outside a loop");
                fenv.AddInstruction("jmp", fenv.Scope.LoopStart);
                return (TypeDefinition.Void, -1);
            }

            if (nodes.Count >= 2 && id.Name == "call") return CompileCall(fenv, nodes);
            if (nodes.Count >= 2 && id.Name == "syscall") return CompileSysCall(fenv, nodes);
            if (nodes.Count is 1 or 2 && id.Name == "return") return CompileReturn(fenv, nodes);
            if (id.Name == "ptr") // Null pointer
            {
                var type = ValidateType(nodes);
                var dest = fenv.MakeTempVar();
                fenv.AddInstruction("const", 0, dest);
                return (type, dest);
            }

            if (nodes.Count == 3 && id.Name == "cast") return CompileCast(fenv, nodes);
            if (nodes.Count == 2 && id.Name == "peek") return CompilePeek(fenv, nodes);
            if (nodes.Count == 3 && id.Name == "poke") return CompilePoke(fenv, nodes);
            if (nodes.Count == 2 && id.Name == "ref") return CompileRef(fenv, nodes);
            if (nodes.Count == 1 && id.Name == "debug")
            {
                fenv.AddInstruction("debug");
                return (TypeDefinition.Void, -1);
            }
        }

        throw new InvalidOperationException("Unknown Expression");
    }

    private static (TypeDefinition, int) CompileConstant(Function fenv, ValueNode value)
    {
        switch (value)
        {
            case StringValueNode s: // String literal
            {
                var dest = fenv.MakeTempVar();
                fenv.AddInstruction("const", s.Value, dest);
                return (TypeDefinition.BytePtr, dest); // string <=> byte*
            }
            case ByteValueNode b: // Byte literal
            {
                var dest = fenv.MakeTempVar();
                fenv.AddInstruction("const", b.Value, dest);
                return (TypeDefinition.Byte, dest);
            }
            case LongValueNode i: // Integer literal
            {
                var dest = fenv.MakeTempVar();
                fenv.AddInstruction("const", i.Value, dest);
                return (TypeDefinition.Int, dest);
            }
            default: throw new ArgumentException($"Unsupported Value Type: {value.GetType()}");
        }
    }

    private (TypeDefinition, int) CompileGetVar(Function fenv, IdentifierNode identifier)
    {
        var (level, type, variable) = fenv.GetVar(identifier.Name);
        if (level == fenv.Level) // Local variable
            return (type, variable);

        var temp = fenv.MakeTempVar();
        fenv.AddInstruction("get_env", level, variable, temp);
        return (type, temp);
    }

    private (TypeDefinition, int) CompileBinop(Function fenv, string op, Nodes nodes)
    {
        var lhs = nodes[1];
        var rhs = nodes[2];

        // compile subexpressions
        // TODO: booleans short circuit
        var savedStackTop = fenv.StackTop;
        var (t1, a1) = CompileExpressionTemp(fenv, lhs);
        var (t2, a2) = CompileExpressionTemp(fenv, rhs);
        fenv.StackTop = savedStackTop; // Discard temporaries

        // Pointers
        if (op == "+" && t1 == TypeDefinition.Int && t2.IsPointer) // Rewrite offset + ptr into ptr + offset
            (t1, a1, t2, a2) = (t2, a2, t1, a1);

        if (op is "+" or "-" && t1.IsPointer && t2 == TypeDefinition.Int)
        {
            // ptr + offset
            var scale = t1.IsPointerTo(ScalarType.Byte) ? 1 : 8;
            if (op == "-") scale = -scale;

            var dest = fenv.MakeTempVar();
            fenv.AddInstruction("lea", a1, a2, scale, dest);
            return (t1, dest);
        }

        if (op == "-" && t1.IsPointer && t2.IsPointer)
        {
            if (t1 != t2) throw new InvalidOperationException($"Comparison of different pointer types: '{t1}' != '{t2}'");
            if (t1 != TypeDefinition.BytePtr) throw new NotImplementedException("Only 'byte ptr' is supported");

            var dest1 = fenv.MakeTempVar();
            fenv.AddInstruction("binop", "-", a1, a2, dest1);
            return (TypeDefinition.Int, dest1);
        }

        // Type checks
        // TODO: Allow different types
        var areInts = t1 == t2 && (t1 == TypeDefinition.Byte || t2 == TypeDefinition.Int);
        var isPointerComparison = t1 == t2 && t1.IsPointer && compops.Contains(op);
        if (!areInts && !isPointerComparison) throw new InvalidOperationException("Invalid types in binary operation");

        var rtype = compops.Contains(op) ? TypeDefinition.Int : t1; // Booleans are represented wth an int
        var suffix = t1 == t2 && t1 == TypeDefinition.Byte ? "8" : "";

        var dest2 = fenv.MakeTempVar();
        fenv.AddInstruction("binop" + suffix, op, a1, a2, dest2);
        return (rtype, dest2);
    }

    private (TypeDefinition, int) CompileUnop(Function fenv, string op, Nodes nodes)
    {
        var arg = nodes[1];
        var (t1, a1) = CompileExpression(fenv, arg);

        var suffix = "";
        var rtype = t1;

        if (op == "-")
        {
            if (t1 != TypeDefinition.Byte && t1 != TypeDefinition.Int)
                throw new InvalidOperationException($"Invalid Type for unary operator '-': '{t1}'");
            if (t1 == TypeDefinition.Byte)
                suffix = "8";
        }
        else if (op == "not")
        {
            if (t1 != TypeDefinition.Byte && t1 != TypeDefinition.Int && !t1.IsPointer)
                throw new InvalidOperationException($"Invalid Type for unary operator 'not': '{t1}'");
            rtype = TypeDefinition.Int; // aka boolean
        }
        else throw new InvalidOperationException($"Unsupported unary operator '{op}'");

        var dest = fenv.MakeTempVar();
        fenv.AddInstruction("unop" + suffix, op, a1, dest);
        return (rtype, dest);
    }

    private (TypeDefinition, int) CompileScope(Function fenv, Nodes nodes)
    {
        var rtype = TypeDefinition.Void;
        var variable = -1;

        fenv.EnterScope();
        try
        {
            var groups = new List<List<Node>>();
            var current = new List<Node>();
            groups.Add(current);
            foreach (var n in nodes.Children.Skip(1))
            {
                current.Add(n);
                if (n is Nodes ns && ns[0] is not IdentifierNode { Name: "var" }) continue;

                // Initiate new group
                current = new List<Node>();
                groups.Add(current);
            }

            // Functions are visible before they are defined as long as they don't cross a variable declaration.
            // This allows adjacent functions to call each other mutually.

            static bool isDef(Node n) => n is Nodes ns && ns[0] is IdentifierNode { Name: "def" } && ns.Children.Count == 4;

            foreach (var g in groups)
            {
                var functions = new Queue<Function>();
                foreach (var n in g.Where(isDef))
                    functions.Enqueue(ScanFunction(fenv, n));

                foreach (var n in g)
                {
                    if (isDef(n))
                    {
                        var func = functions.Dequeue();
                        (rtype, variable) = CompileFunction(func, n);
                    }
                    else (rtype, variable) = CompileExpression(fenv, n, allowVar: true);
                }
            }
        }
        finally
        {
            fenv.LeaveScope();
        }

        // We return either a local variable or a new temporary
        if (variable >= fenv.StackTop)
            variable = MoveTo(fenv, variable, fenv.MakeTempVar());
        return (rtype, variable);
    }

    private (TypeDefinition, int) CompileNewVar(Function fenv, Nodes nodes)
    {
        var name = ((IdentifierNode)nodes[1]).Name;

        // Compile the initialization expression
        var (type, variable) = CompileExpression(fenv, nodes[2]);
        if (variable < 0) // -> void
            throw new InvalidOperationException("A void expression cannot be used to initialize a variable");

        // Store the initialization value into the new variable
        var dest = fenv.AddVar(name, type);
        return (type, MoveTo(fenv, variable, dest));
    }

    private (TypeDefinition, int) CompileSetVar(Function fenv, Nodes nodes)
    {
        var name = ((IdentifierNode)nodes[1]).Name;

        var (level, destType, destVariable) = fenv.GetVar(name);
        var (type, variable) = CompileExpression(fenv, nodes[2]);
        if (destType != type)
            throw new InvalidOperationException("Assigned variable and expression do not agree on the type");

        if (level == fenv.Level) // Local
            return (destType, MoveTo(fenv, variable, destVariable));

        // Non local
        fenv.AddInstruction("set_env", level, destVariable, variable);
        return (destType, MoveTo(fenv, variable, fenv.MakeTempVar()));
    }

    private (TypeDefinition, int) CompileConditional(Function fenv, Nodes nodes)
    {
        var condition = nodes[1];
        var yes = nodes[2];
        var no = nodes.Count > 3 ? nodes[3] : null;

        var trueLabel = fenv.NewLabel();
        var falseLabel = fenv.NewLabel();

        var (t1, a1) = (TypeDefinition.Void, -1);
        var (t2, a2) = (TypeDefinition.Void, -1);

        fenv.EnterScope();
        try
        {
            // Condition expression
            var (type, variable) = CompileExpression(fenv, condition, allowVar: true);
            if (type == TypeDefinition.Void)
                throw new InvalidOperationException("A void expression cannot be used as a condition");
            fenv.AddInstruction("jmpf", variable, falseLabel); // Go to `else` if false

            // Then
            (t1, a1) = CompileExpression(fenv, yes);
            if (a1 >= 0) // Both `then` and `else` go to the same variable; hence a temporary is needed.
                MoveTo(fenv, a1, fenv.StackTop);

            // Else (optional)
            if (no != null)
                fenv.AddInstruction("jmp", trueLabel); // Skip `else` after `then`
            fenv.SetLabel(falseLabel);
            if (no != null)
            {
                (t2, a2) = CompileExpression(fenv, no); // TODO: Original code uses no[0] here... why?
                if (a2 >= 0) MoveTo(fenv, a2, fenv.StackTop); // Use the same variable for `then`
            }

            fenv.SetLabel(trueLabel);
        }
        finally
        {
            fenv.LeaveScope();
        }

        if (a1 < 0 || a2 < 0 || t1 != t2)
            return (TypeDefinition.Void, -1); // Different types or void -> no return value

        return (t1, fenv.MakeTempVar()); // Allocate the temporary for the result
    }

    private (TypeDefinition, int) CompileLoop(Function fenv, Nodes nodes)
    {
        var condition = nodes[1];
        var body = nodes[2];

        fenv.Scope.LoopStart = fenv.NewLabel();
        fenv.Scope.LoopEnd = fenv.NewLabel();

        try
        {
            // Enter
            fenv.EnterScope();
            fenv.SetLabel(fenv.Scope.LoopStart);

            // Condition
            var (_, variable) = CompileExpression(fenv, condition, allowVar: true);
            if (variable < 0) // -> void
                throw new InvalidOperationException("A void expression cannot be used as a loop condition");
            fenv.AddInstruction("jmpf", variable, fenv.Scope.LoopEnd);
            _ = CompileExpression(fenv, body); // Body
            fenv.AddInstruction("jmp", fenv.Scope.LoopStart); // Loop back to the beginning of the scope

            // Leave
            fenv.SetLabel(fenv.Scope.LoopEnd);
        }
        finally
        {
            fenv.LeaveScope();
        }

        return (TypeDefinition.Void, -1);
    }

    private (TypeDefinition, int) CompileCall(Function fenv, Nodes nodes)
    {
        var name = ((IdentifierNode)nodes[1]).Name;
        var argTypes = new List<TypeDefinition>();
        foreach (var argNode in nodes.Children.Skip(2))
        {
            var (t, a) = CompileExpression(fenv, argNode);
            argTypes.Add(t);
            MoveTo(fenv, a, fenv.MakeTempVar()); // Stored continuously
        }

        fenv.StackTop -= argTypes.Count; // Points to the first argument

        // Look up the target `Func`
        var key = MakeFunctionKey(name, argTypes);
        var (_, _, index) = fenv.GetVar(key);
        var func = fenv.Funcs[index];

        fenv.AddInstruction("call", index, fenv.StackTop, fenv.Level, func.Level);
        var dest = -1;
        if (func.ReturnType != TypeDefinition.Void)
            dest = fenv.MakeTempVar(); // The return value is on the top of the stack
        return (func.ReturnType, dest);
    }

    private (TypeDefinition, int) CompileSysCall(Function fenv, Nodes nodes)
    {
        var number = -1;
        if (nodes[1] is LongValueNode ln) number = (int)ln.Value;
        if (nodes[1] is ByteValueNode bn) number = bn.Value;
        if (number < 0) throw new InvalidOperationException("Invalid syscall number");
        // We do not have 'val' in this implementation and we do not support (yet?) computing the syscall
        // number from a sub-expression

        var savedStackTop = fenv.StackTop;
        var sysVars = new List<int>();
        foreach (var argNode in nodes.Children.Skip(2))
        {
            var (t, a) = CompileExpressionTemp(fenv, argNode);
            if (a < 0) // -> void
                throw new InvalidOperationException("A void expression cannot be used as a syscall parameter");
            sysVars.Add(a);
        }

        fenv.StackTop = savedStackTop;
        var parameters = new List<object> { fenv.StackTop, number };
        parameters.AddRange(sysVars.Cast<object>());
        
        fenv.AddInstruction("syscall", parameters.ToArray());
        return (TypeDefinition.Int, fenv.MakeTempVar());
    }

    private (TypeDefinition, int) CompileReturn(Function fenv, Nodes nodes)
    {
        var (type, variable) = nodes.Count > 1
            ? CompileExpressionTemp(fenv, nodes[1])
            : (TypeDefinition.Void, -1);

        if (type != fenv.ReturnType)
            throw new InvalidOperationException($"Invalid Return Type: Expected {fenv.ReturnType}, but got {type}");

        fenv.AddInstruction("ret", variable);
        return (type, variable);
    }

    private (TypeDefinition, int) CompileCast(Function fenv, Nodes nodes)
    {
        var destinationType = ValidateType((Nodes)nodes[1]);
        var value = nodes[2];
        var (valueType, variable) = CompileExpressionTemp(fenv, value);

        if (valueType.CanCastTo(destinationType))
            return (destinationType, variable);
        if (destinationType.Type == ScalarType.Byte && valueType.Type == ScalarType.Int)
        {
            fenv.AddInstruction("cast8", variable);
            return (destinationType, variable);
        }

        throw new InvalidOperationException($"Invalid cast: from {valueType} to {destinationType}");
    }

    private (TypeDefinition, int) CompilePeek(Function fenv, Nodes nodes)
    {
        var pointerExpression = nodes[1];
        var (type, variable) = CompileExpression(fenv, pointerExpression);
        if (!type.IsPointer)
            throw new InvalidOperationException("Cannot peek into a non pointer value");
        var suffix = type.Type == ScalarType.Byte ? "8" : "";
        fenv.AddInstruction("peek" + suffix, variable, fenv.StackTop);
        return (new TypeDefinition(type.Type, type.PointerLevel - 1), fenv.MakeTempVar());
    }

    private (TypeDefinition, int) CompilePoke(Function fenv, Nodes nodes)
    {
        var pointerExpression = nodes[1];
        var valueExpression = nodes[2];

        var savedStackTop = fenv.StackTop;
        var (t2, value) = CompileExpressionTemp(fenv, valueExpression);
        var (t1, pointer) = CompileExpressionTemp(fenv, pointerExpression);
        if (!t1.IsPointerTo(t2))
            throw new InvalidOperationException("Pointer mismatch");
        fenv.StackTop = savedStackTop;
        
        var suffix = t2.Type == ScalarType.Byte ? "8" : "";
        fenv.AddInstruction("poke" + suffix, pointer, value);
        return (t2, MoveTo(fenv, value, fenv.MakeTempVar()));
    }

    private (TypeDefinition, int) CompileRef(Function fenv, Nodes nodes)
    {
        var name = ((IdentifierNode)nodes[1]).Name;
        var (level, type, variable) = fenv.GetVar(name);
        var dest = fenv.MakeTempVar();
        if (level == fenv.Level) // Local
            fenv.AddInstruction("ref_var", variable, dest);
        else
            fenv.AddInstruction("ref_env", level, variable, dest);
        return (type.MakePointer(), dest);
    }

    // check for accepted types. returns a tuple.
    private static TypeDefinition ValidateType(Nodes nodes, int startIndex = 0)
    {
        static string[] nodesToNames(IEnumerable<Node> input) =>
            input.Cast<IdentifierNode>().Select(id => id.Name).ToArray();

        var names = nodesToNames(nodes.Children.ToArray()[startIndex..]);
        var tdef = ValidateType(names);
        return tdef;
    }

    private static TypeDefinition ValidateType(string[] input, int pointerLevel = 0)
    {
        if (input.Length == 0)
            throw new InvalidDataException("Missing Type");

        switch (input[0])
        {
            case "ptr":
            {
                var result = ValidateType(input[1..], pointerLevel + 1);
                if (result.Type == ScalarType.Void)
                    throw new InvalidDataException("Invalid pointed type");
                return result;
            }
            case "void" or "int" or "byte":
            {
                var scalar = input[0] switch
                {
                    "void" => ScalarType.Void,
                    "int" => ScalarType.Int,
                    "byte" => ScalarType.Byte,
                    _ => throw new InvalidDataException($"Invalid scalar type: '{input[0]}'")
                };

                if (input.Length > 1)
                    throw new InvalidDataException(
                        $"Invalid scalar type: additional identifiers were found after '{input[0]}'");

                return new(scalar, pointerLevel);
            }
            default:
                throw new InvalidDataException($"Invalid Type: '{string.Join(' ', input)}'");
        }
    }

    private static void AssertIsMain(Node node)
    {
        if (node is not Nodes nodes)
        {
            Debug.Assert(false, "Root node is not a node list");
            return;
        }

        Debug.Assert(nodes.Children.Count == 4);
        Debug.Assert(nodes[0] is IdentifierNode { Name: "def" });

        if (nodes[1] is not Nodes mainChildren)
        {
            Debug.Assert(false, "2nd child node is not a node list");
            return;
        }

        Debug.Assert(mainChildren[0] is IdentifierNode { Name: "main" });
        Debug.Assert(mainChildren[1] is IdentifierNode { Name: "int" });

        if (nodes[2] is not Nodes empty)
        {
            Debug.Assert(false, "3rd child node is not an empty list");
            return;
        }

        Debug.Assert(empty.Children.Count == 0);
    }

    private static int MoveTo(Function fenv, int source, int dest)
    {
        if (dest != source)
            fenv.AddInstruction("mov", source, dest);
        return dest;
    }


    private static string MakeFunctionKey(string name, IEnumerable<TypeDefinition> argTypes) =>
        name + string.Join("", argTypes.Select(t => t.Key));
}