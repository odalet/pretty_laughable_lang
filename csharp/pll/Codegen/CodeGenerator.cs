using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Pll.Codegen;

using static Helper;
using static Register;

// Registers encoding
// @formatter:off
internal enum Register { A = 0, C = 1, D = 2, B = 3, SP = 4, BP = 5, SI = 6, DI = 7, R8 = 8, R9 = 9, R10 = 10 }
// @formatter:on

internal sealed class CodeGenerator : BaseBufferHandler
{
    private static readonly Register[] syscallArgRegisters = { DI, SI, D, R10, R8, R9 };

    private static readonly Dictionary<string, byte[]> binops = new()
    {
        ["+"] = new byte[] { 0x48, 0x03 }, // add reg, rm
        ["-"] = new byte[] { 0x48, 0x2B }, // sub reg, rm
        ["*"] = new byte[] { 0x48, 0x0F, 0xAF }, // imul reg, rm
        ["eq"] = new byte[] { 0x0F, 0x94, 0xC0 }, // sete al
        ["ne"] = new byte[] { 0x0F, 0x95, 0xC0 }, // setne al
        ["ge"] = new byte[] { 0x0F, 0x9D, 0xC0 }, // setge al
        ["gt"] = new byte[] { 0x0F, 0x9F, 0xC0 }, // setg al
        ["le"] = new byte[] { 0x0F, 0x9E, 0xC0 }, // setle al
        ["lt"] = new byte[] { 0x0F, 0x9C, 0xC0 }, // setl al
    };

    private readonly Dictionary<int, List<int>> jmps = new(); // label -> offset list
    private readonly Dictionary<int, List<int>> calls = new(); // function index -> offset list
    private readonly Dictionary<string, List<int>> strings = new(); // string literal -> offset list
    private readonly List<int> func2off = new(); // function index -> offset
    private readonly bool targetIsWindows;

    public CodeGenerator(bool? targetWindows = null, int alignment = 16) : base(new(), alignment) =>
        targetIsWindows = targetWindows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public byte[] MakeElf(Function root)
    {
        var elf = new ElfHelper(Buffer, Alignment);
        elf.Begin(); // ELF header + program header 
        AddCodeEntry(); // Machine code
        foreach (var function in root.Funcs)
            AddFunction(function);
        EndCode();
        elf.End(); // fill in some ELF fields

        /*
         # ELF header + program header
         self.elf_begin()
         # machine code
         self.code_entry()
         for func in root.funcs:
             self.func(func)
         self.code_end()
         # fill in some ELF fields
         self.elf_end()
        */

        return Buffer.ToArray();
    }

    public byte[] MakeInMemory(Function root)
    {
        BeginMemoryCode();
        foreach (var function in root.Funcs)
            AddFunction(function);
        EndCode();
        return Buffer.ToArray();
    }

    // Only for Elf
    private void AddCodeEntry()
    {
        // create the data stack (8M)
        CreateStack(0x800000); // TODO That's not 8M though...
        AddAsmCall(0); // call the main function
        // Exit
        Add(0xB8, 0x3C, 0x00, 0x00, 0x00); // mov eax, 60
        Add(0x48, 0x8B, 0x3B); // mov rdi, [rbx]
        Add(0x0F, 0x05); // syscall
    }

    private void CreateStack(int data)
    {
        // syscall ref: https://blog.rchapman.org/posts/Linux_System_Call_Table_for_x86_64/
        // syscall abi: https://github.com/torvalds/linux/blob/v5.0/arch/x86/entry/entry_64.S#L107
        
        // mmap
        Add(0xB8, 0x09, 0x00, 0x00, 0x00); //       mov eax, 9
        //Add(0x31, 0xFF); //                         xor edi, edi      // addr = NULL
        Add(0xBF, 0x00, 0x10, 0x00, 0x00); //       mov edi, 4096     // addr
        Add(0x48, 0xC7, 0xC6); //                   mov rsi, xxx      // len
        Add(IntToLittleEndian(data + 4096));
        Add(0xBA, 0x03, 0x00, 0x00, 0x00); //       mov edx, 3        // prot = PROT_READ|PROT_WRITE
        Add(0x41, 0xBA, 0x22, 0x00, 0x00, 0x00); // mov r10d, 0x22    // flags = MAP_PRIVATE|MAP_ANONYMOUS
        Add(0x49, 0x83, 0xC8, 0xFF); //             or r8, -1         // fd = -1
        Add(0x4D, 0x31, 0xC9); //                   xor r9, r9        // offset = 0
        Add(0x0F, 0x05); //                         syscall
        Add(0x48, 0x89, 0xC3); //                   mov rbx, rax      // the data stack
        
        // mprotect
        Add(0xB8, 0x0A, 0x00, 0x00, 0x00); //       mov eax, 10
        Add(0x48, 0x8D, 0xBB); //                   lea rdi, [rbx + data]
        Add(IntToLittleEndian(data));
        Add(0xBE, 0x00, 0x10, 0x00, 0x00); //       mov esi, 4096
        Add(0x31, 0xD2); //                         xor edx, edx
        Add(0x0F, 0x05); //                         syscall
        
        // TODO: Check the syscall return value
    }

    // C function: int64_t (*)(void *stack)
    private void BeginMemoryCode()
    {
        // the first argument is the data stack
        Add(0x53); // push rbx
        if (targetIsWindows) Add(0x48, 0x89, 0xCB); // mov rbx, rcx
        else Add(0x48, 0x89, 0xFB); // mov rbx, rdi

        // call the main function
        AddAsmCall(0);

        // Return value
        Add(0x48, 0x8B, 0x03); // mov rax, [rbx]
        Add(0x5B); // pop rbx
        Add(0xC3); // ret
    }

    private void EndCode()
    {
        // fill in the call address
        foreach (var (label, offsetList) in calls)
        {
            var destinationOffset = func2off[label];
            foreach (var patchOffset in offsetList)
                PatchAddress(patchOffset, destinationOffset);
        }

        calls.Clear();
        AddPadding();

        // strings
        foreach (var (s, offsetList) in strings)
        {
            var destinationOffset = Buffer.Count;
            foreach (var patchOffset in offsetList)
                PatchAddress(patchOffset, destinationOffset);
            Add(Encoding.UTF8.GetBytes(s));
            Add(0);
        }

        strings.Clear();
    }

    // fill in a 4-byte `rip` relative offset
    private void PatchAddress(int patchOffset, int destinationOffset)
    {
        var sourceOffset = patchOffset + 4; // rip
        var relative = IntToLittleEndian(destinationOffset - sourceOffset); // <i is 'little-endian integer'
        for (var i = 0; i < 4; i++)
            Buffer[patchOffset + i] = relative[i];
    }

    private void AddFunction(Function function)
    {
        AddPadding();

        // offsets
        func2off.Add(Buffer.Count); // function index -> code offset
        var pos2off = new List<int>(); // virtual instruction -> code offset

        // call the method for each instruction
        foreach (var instruction in function.Instructions)
        {
            pos2off.Add(Buffer.Count);
            AddInstruction(instruction);
        }

        // fill in the jmp address
        foreach (var (label, offsetList) in jmps)
        {
            var index = function.Labels[label];
            if (!index.HasValue)
                throw new InvalidOperationException("Encountered an invalid label");

            var destinationOffset = pos2off[index.Value];
            foreach (var patchOffset in offsetList)
                PatchAddress(patchOffset, destinationOffset);
        }

        jmps.Clear();
    }

    private void AddInstruction(Instruction instruction)
    {
        static byte getByte(object o) => o switch
        {
            byte b => b,
            int i => (byte)i,
            long l => (byte)l,
            _ => throw new InvalidCastException($"Cannot convert {o.GetType()} to a byte")
        };
        
        var a = instruction.Args;
        switch (instruction.Op)
        {
            case "const":
                AddConst(a[0], (int)a[1]);
                break;
            case "mov":
                AddMov((int)a[0], (int)a[1]);
                break;
            case "binop":
                AddBinop((string)a[0], (int)a[1], (int)a[2], (int)a[3]);
                break;
            case "unop":
                AddUnop((string)a[0], (int)a[1], (int)a[2]);
                break;
            case "jmpf":
                AddJmpf((int)a[0], (int)a[1]);
                break;
            case "jmp":
                AddJmp((int)a[0]);
                break;
            case "call":
                AddCall((int)a[0], (int)a[1], (int)a[2], (int)a[3]);
                break;
            case "ret":
                AddRet((int)a[0]);
                break;
            case "get_env":
                AddGetEnv((int)a[0], (int)a[1], (int)a[2]);
                break;
            case "set_env":
                AddSetEnv((int)a[0], (int)a[1], (int)a[2]);
                break;
            case "lea":
                AddLea((int)a[0], (int)a[1], (int)a[2], (int)a[3]);
                break;
            case "peek":
                AddPeek((int)a[0], (int)a[1]);
                break;
            case "peek8":
                AddPeek8((int)a[0], (int)a[1]);
                break;
            case "poke":
                AddPoke((int)a[0], (int)a[1]);
                break;
            case "poke8":
                AddPoke8((int)a[0], getByte(a[1]));
                break;
            case "ref_var":
                AddRefVar((int)a[0], (int)a[1]);
                break;
            case "ref_env":
                AddRefEnv((int)a[0], (int)a[1], (int)a[2]);
                break;
            case "cast8":
                AddCast8((int)a[0]);
                break;
            case "syscall":
                AddSyscall((int)a[0], (int)a[1], a.Skip(2).Cast<int>().ToArray());
                break;
            case "debug":
                Add(0xCC); // int3
                break;
            default: throw new NotSupportedException($"Instruction '{instruction.Op}' is not supported");
        }
    }

    private void AddConst(object value, int dest)
    {
        // value should be an integer or a string
        if (value is string s)
        {
            Add(0x48, 0x8D, 0x05); // lea rax, [rip + offset]
            StringOffsets(s).Add(Buffer.Count);
            Add(0, 0, 0, 0); // Placeholder for the string location
        }
        else
        {
            var longValue = value switch
            {
                long l => l,
                int i => (long)i,
                byte b => (long)b,
                _ => throw new InvalidCastException($"Cannot cast from {value.GetType()} to long")
            };
            
            if (longValue == 0L)
                Add(0x31, 0xC0); // xor eax, eax
            else if (longValue == -1L)
                Add(0x48, 0x83, 0xC8, 0xFF); // or rax, -1
            else if (longValue >> 31 == 0)
            {
                Add(0xB8); // mov eax, imm32
                AddI32((int)longValue);
            }
            else if (longValue >> 31 == -1) // sign-extended
            {
                Add(0x48, 0xC7, 0xC0); // mov rax, imm32
                AddI32((int)longValue);
            }
            else
            {
                Add(0x48, 0xB8); // mov rax, imm64
                AddI64(longValue);
            }
        }

        StoreRax(dest);
    }

    private void AddMov(int src, int dest)
    {
        if (src == dest) return;
        LoadRax(src);
        StoreRax(dest);
    }

    private void AddBinop(string op, int a1, int a2, int dest)
    {
        LoadRax(a1);
        switch (op)
        {
            case "/":
            case "%":
                Add(0x31, 0xD2); // xor edx, edx
                Add(0x48, 0xF7, 0xBB); // idiv rax, [rbx + a2*8]
                AddI32(a2 * 8);
                if (op == "%")
                    Add(0x48, 0x89, 0xD0); // mov rax, rdx
                break;
            case "+":
            case "-":
            case "*":
                AddAsmDisp(binops[op], A, B, a2 * 8); // op rax, [rbx + a2*8]
                break;
            case "eq":
            case "ne":
            case "ge":
            case "gt":
            case "le":
            case "lt":
                AddAsmDisp(new byte[] { 0x48, 0x3B }, A, B, a2 * 8); // cmp rax, [rbx + a2*8]
                Add(binops[op]); // setxx al
                Add(0x0F, 0xB6, 0xC0); // movzx eax, al
                break;
            case "and":
                Add(0x48, 0x85, 0xC0); // test rax, rax
                Add(0x0F, 0x95, 0xC0); // setne al
                AddAsmLoad(D, B, a2 * 8); // mov rdx, [rbx + a2*8]
                Add(0x48, 0x85, 0xD2); // test rdx, rdx
                Add(0x0F, 0x95, 0xC2); // setne dl
                Add(0x21, 0xD0); // and eax, edx
                Add(0x0F, 0xB6, 0xC0); // movzx eax, al
                break;
            case "or":
                AddAsmDisp(new byte[] { 0x48, 0x0B }, A, B, a2 * 8); // or rax, [rbx + a2*8]
                Add(0x0F, 0x95, 0xC0); // setne al
                Add(0x0F, 0xB6, 0xC0); // movzx eax, al
                break;
            default:
                throw new NotSupportedException($"Binary operator '{op}' is not supported");
        }
        
        StoreRax(dest);
    }

    private void AddUnop(string op, int a1, int dest)
    {
        LoadRax(a1);
        switch (op)
        {
            case "-":
                Add(0x48, 0xF7, 0xD8); // neg rax
                break;
            case "not":
                Add(0x48, 0x85, 0xC0); // test rax, rax
                Add(0x0F, 0x94, 0xC0); // sete al
                Add(0x0F, 0xB6, 0xC0); // movzx eax, al
                break;
            default:
                throw new NotSupportedException($"Unary operator '{op}' is not supported");
        }
    }

    private void AddJmpf(int a1, int label)
    {
        LoadRax(a1);
        Add(0x48, 0x85, 0xC0); // test rax, rax
        Add(0x0F, 0x84); // je
        JumpOffsets(label).Add(Buffer.Count);
        Add(0, 0, 0, 0); // placeholder for the target address
    }

    private void AddJmp(int label)
    {
        Add(0xE9); // jmp
        JumpOffsets(label).Add(Buffer.Count);
        Add(0, 0, 0, 0); // placeholder for the target address
    }

    private void AddCall(int func, int argStart, int curLevel, int newLevel)
    {
        if (curLevel <= 0) throw new InvalidEnumArgumentException(nameof(curLevel));
        if (newLevel <= 0 || newLevel > curLevel + 1) throw new InvalidEnumArgumentException(nameof(newLevel));

        // put a list of pointers to outer frames in the `rsp` stack
        if (newLevel > curLevel) // grow the list by one
            Add(0x53); // push rbx

        // copy the previous list
        foreach (var l in Enumerable.Range(0, Min(curLevel, newLevel) - 1))
        {
            Add(0xFF, 0xB4, 0x24); // push [rsp + (level_new-1)*8]
            AddI32((newLevel - 1) * 8);
        }

        // make a new frame and call the target
        if (argStart != 0)
        {
            Add(0x48, 0x81, 0xC3); // add rbx, arg_start*8
            AddI32(argStart * 8);
        }

        AddAsmCall(func); // call func

        if (argStart != 0)
        {
            Add(0x48, 0x81, 0xC3); // add rbx, -arg_start*8
            AddI32(-argStart * 8);
        }

        // discard the list of pointers
        Add(0x48, 0x81, 0xC4); // add rsp, (level_new - 1)*8
        AddI32((newLevel - 1) * 8);
    }

    private void AddRet(int a1)
    {
        if (a1 > 0)
        {
            LoadRax(a1);
            StoreRax(0);
        }

        Add(0xC3); // ret
    }

    private void AddGetEnv(int variableLevel, int variable, int dest)
    {
        LoadEnvAdr(variableLevel);
        AddAsmLoad(A, A, variable * 8); // mov rax, [rax + var*8]
        StoreRax(dest); // mov [rbx + dst*8], rax
    }

    private void AddSetEnv(int variableLevel, int variable, int src)
    {
        LoadEnvAdr(variableLevel);
        AddAsmLoad(D, B, src * 8); // mov rdx, [rbx + src*8]
        AddAsmStore(A, variable * 8, D); // mov [rax + var*8], rdx
    }

    private void AddLea(int a1, int a2, int scale, int dest)
    {
        LoadRax(a1);
        AddAsmLoad(D, B, a2 * 8); // mov rdx, [rbx + a2*8]
        if (scale < 0) Add(0x48, 0xF7, 0xDA); // neg rdx
        var absScale = scale >= 0 ? scale : -scale;
        Add(absScale switch
        {
            1 => new byte[] { 0x48, 0x8D, 0x04, 0x10 }, // lea rax, [rax + rdx]
            2 => new byte[] { 0x48, 0x8D, 0x04, 0x50 }, // lea rax, [rax + rdx * 2]
            4 => new byte[] { 0x48, 0x8D, 0x04, 0x90 }, // lea rax, [rax + rdx * 4]
            8 => new byte[] { 0x48, 0x8D, 0x04, 0xD0 }, // lea rax, [rax + rdx * 8]
            _ => throw new InvalidOperationException($"Invalid scale '{absScale}' in lea opcode")
        });
        StoreRax(dest);
    }

    private void AddPeek(int variable, int dest)
    {
        LoadRax(variable);
        AddAsmLoad(A, A, 0); // mov rax, [rax]
        StoreRax(dest);
    }

    private void AddPeek8(int variable, int dest)
    {
        LoadRax(variable);
        Add(0x0F, 0xB6, 0x00); // movzx eax, byte ptr [rax]
        StoreRax(dest);
    }

    private void AddPoke(int pointer, int value)
    {
        LoadRax(value);
        AddAsmLoad(D, B, pointer * 8); // mov rdx, [rbx + ptr*8]
        AddAsmStore(D, 0, A); // mov [rdx], rax
    }

    private void AddPoke8(int pointer, byte value)
    {
        LoadRax(value);
        AddAsmLoad(D, B, pointer * 8); // mov rdx, [rbx + ptr*8]
        Add(0x88, 0x02); // mov [rdx], al
    }

    private void AddRefVar(int variable, int dest)
    {
        Add(0x48, 0x80, 0x83); // lea rax, [rbx + var*8]
        AddI32(variable * 8);
        StoreRax(dest);
    }

    private void AddRefEnv(int variableLevel, int variable, int dest)
    {
        LoadEnvAdr(variableLevel); // mov rax, [rsp + level_var*8]
        Add(0x48, 0x05); // add rax, var*8
        AddI32(variable * 8);
        StoreRax(dest);
    }

    private void AddCast8(int variable)
    {
        // and qword ptr [rbx + var*8], 0xff
        AddAsmDisp(new byte[] { 0x48, 0x81 }, 4, (int)B, variable * 8); // TODO: is this ok?
        //AddAsmDisp(new byte[] { 0x48, 0x81 }, B, SP, variable * 8); // TODO: is it not rather this?
        AddI32(0xFF);
    }

    private void AddSyscall(int dest, int num, int[] args)
    {
        // syscall ref: https://blog.rchapman.org/posts/Linux_System_Call_Table_for_x86_64/
        Add(0xB8); // mov eax, imm32
        AddI32(num);
        if (args.Length > syscallArgRegisters.Length)
            throw new ArgumentException("Syscall has too many arguments");

        for (var i = 0; i < args.Length; i++)
            AddAsmLoad(syscallArgRegisters[i], B, args[i] * 8); // mov reg, [rbx + arg*8]
        Add(0x0F, 0x05); // syscall
        StoreRax(dest); // mov [rbx + dst*8], rax
    }

    private void AddAsmCall(int label)
    {
        Add(0xE8); // call
        CallOffsets(label).Add(Buffer.Count);
        Add(0, 0, 0, 0); // placeholder for the target address
    }

    // mov [rbx + dst*8], rax
    private void StoreRax(int dest) => AddAsmStore(B, dest * 8, A);

    // mov rax, [rbx + src*8]
    private void LoadRax(int src) => AddAsmLoad(A, B, src * 8);

    private void LoadEnvAdr(int variableLevel)
    {
        Add(0x48, 0x8B, 0x84, 0x24); // mov rax, [rsp + variableLevel*8]
        AddI32(variableLevel);
    }

    // mov [rm + disp], reg
    private void AddAsmStore(Register rm, int disp, Register reg) =>
        AddAsmDisp(new byte[] { 0x48, 0x89 }, reg, rm, disp);

    // mov reg, [rm + disp]
    private void AddAsmLoad(Register reg, Register rm, int disp) =>
        AddAsmDisp(new byte[] { 0x48, 0x8B }, reg, rm, disp);

    private void AddAsmDisp(byte[] lead, Register reg, Register rm, int disp) => AddAsmDisp(lead, (int)reg, (int)rm, disp);

    // instr reg, [rm + disp] or instr [rm + disp], reg
    private void AddAsmDisp(byte[] lead, int reg, int rm, int disp)
    {
        if (reg >= 16 || rm >= 16 | rm == (int)SP) throw new ArgumentException("Invalid arguments");
        if (reg >= 8 || rm >= 8)
        {
            if (lead[0] >> 4 != 0b0100) // REX
                throw new ArgumentException("Invalid lead bytes", nameof(lead));
            lead[0] |= (byte)((reg >> 3) << 2); // REX.R
            lead[0] |= (byte)(rm >> 3); // REX.B

            reg &= 0b111;
            rm &= 0b111;
        }

        Add(lead);
        var mod = disp switch
        {
            0 => 0, // rm
            >= -128 and < 128 => 1, // rm + disp8
            _ => 2 // rm + disp32
        };

        Add((byte)((mod << 6) | (reg << 3) | rm)); // ModR/M
        if (mod == 1) AddI8((byte)disp);
        if (mod == 2) AddI32(disp);
    }

    private List<int> CallOffsets(int label)
    {
        if (!calls.ContainsKey(label)) calls.Add(label, new List<int>());
        return calls[label];
    }

    private List<int> JumpOffsets(int label)
    {
        if (!jmps.ContainsKey(label)) jmps.Add(label, new List<int>());
        return jmps[label];
    }

    private List<int> StringOffsets(string s)
    {
        if (!strings.ContainsKey(s)) strings.Add(s, new List<int>());
        return strings[s];
    }

    private static int Min(int a, int b) => a < b ? a : b;
}