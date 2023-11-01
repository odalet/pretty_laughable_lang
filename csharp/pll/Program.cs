using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using Pll.Codegen;

// ReSharper disable ParameterTypeCanBeEnumerable.Local
// ReSharper disable ConvertIfStatementToSwitchStatement

namespace Pll;

internal static class Program
{
    private static void Main(params string[] args)
    {
        var arguments = new Arguments(args);
        if (arguments.ShowHelp)
        {
            Help();
            return;
        }

        if (arguments.InputFileName == null || !File.Exists(arguments.InputFileName))
        {
            Console.Error.WriteLine("Invalid arguments");
            Help();
            return;
        }

        var text = File.ReadAllText(arguments.InputFileName);

        Console.WriteLine(text);

        // Parse
        var parser = new Parser(text);
        var rootNode = parser.Parse();
        // Console.WriteLine();
        // Console.WriteLine("ParseTree:");
        // Console.WriteLine(rootNode.Dump());
        
        // Compile
        var compiler = new Compiler();
        var root = compiler.Compile(rootNode);
        Console.WriteLine();
        Console.WriteLine("Code:");
        Console.WriteLine(root.Dump());
        
        // Codegen
        var generator = new CodeGenerator();
        // var bytes = generator.MakeElf(root);
        // File.WriteAllBytes("/tmp/test-exe", bytes);
        
        var bytes = generator.MakeInMemory(root);
        try
        {
            Console.WriteLine("--> Executing program");
            using var runnable = new InMemoryProgram(bytes);
            var ret = runnable.Invoke();
            Console.WriteLine($"--> Return value: {ret}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex);
        }
    }

    private static void Help()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("pll <input source file> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("-o, --output     The output path");
        Console.WriteLine("--exec           Compile to memory and execute");
        Console.WriteLine("--print-ir       Dump the intermediate representation");
        Console.WriteLine("--alignment      Alignment. Default is 16");
    }

    private sealed class Arguments
    {
        public Arguments(string[] args)
        {
            Alignment = 16; // defaults

            Action<string>? todo = null;

            foreach (var arg in args)
                if (!arg.StartsWith('-'))
                {
                    if (todo == null)
                        InputFileName = arg;
                    else todo(arg);
                }
                else
                {
                    var normalized = arg.ToLowerInvariant().Trim();
                    if (normalized is "-h" or "--help")
                        ShowHelp = true;
                    else if (normalized == "--exec")
                        Exec = true;
                    else if (normalized == "--print-ir")
                        PrintIR = true;
                    else if (normalized == "--alignment")
                        todo = x => Alignment = int.Parse(x);
                    else if (normalized is "-o" or "--output")
                        todo = x => OutputFileName = x;
                }
        }

        public string? InputFileName { get; }
        public string? OutputFileName { get; private set; }
        public bool Exec { get; }
        public bool PrintIR { get; }
        public bool ShowHelp { get; }
        public int Alignment { get; private set; }
    }
}