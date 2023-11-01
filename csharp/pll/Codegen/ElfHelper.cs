using System;
using System.Collections.Generic;

namespace Pll.Codegen;

internal sealed class ElfHelper : BaseBufferHandler
{
    private const int vaddr = 0x1000;
    private readonly Dictionary<string, (int size, int offset)> fields = new(); // ELF field name -> (size, offset)

    public ElfHelper(List<byte> targetBuffer, int alignment) : base(targetBuffer, alignment) { }

    public void Begin()
    {
        Header();
        var phdrStart = Buffer.Count; // The program header starts here
        ProgramHeader();
        SetF("e_phentsize", (ushort)(Buffer.Count - phdrStart)); // program header size
        SetF("e_phnum", (ushort)1);// number of program headers: 1
        AddPadding();
        // the entry point: the virtual address where the program start
        SetF("e_entry", (ulong)(vaddr + Buffer.Count));
    }

    public void End()
    {
        // fields in program header: the size of the mapping. we're mapping the whole file here.
        SetF("p_filesz", (ulong)Buffer.Count);
        SetF("p_memsz", (ulong)Buffer.Count);
    }

    private void Header()
    {
        // ref: https://www.muppetlabs.com/~breadbox/software/tiny/tiny-elf64.asm.txt
        Add("7F 45 4C 46 02 01 01 00");
        Add("00 00 00 00 00 00 00 00");
        Add("02 00 3E 00 01 00 00 00"); // e_type, e_machine, e_version
        F64("e_entry");
        F64("e_phoff");
        F64("e_shoff");
        F32("e_flags");
        F16("e_ehsize");
        F16("e_phentsize");
        F16("e_phnum");
        F16("e_shentsize");
        F16("e_shnum");
        F16("e_shstrndx");
        SetF("e_phoff", (ulong)Buffer.Count); // offset of the program header
        SetF("e_ehsize", (ushort)Buffer.Count); // size of the ELF header
    }

    private void ProgramHeader()
    {
        Add("01 00 00 00 05 00 00 00"); // p_type, p_flags
        AddI64(0L); // p_offset
        AddI64(vaddr); // p_vaddr, p_paddr
        AddI64(vaddr); // useless
        F64("p_filesz");
        F64("p_memsz");
        AddI64(0x1000); // p_align
    }

    // append placeholder fields
    private void F16(string name)
    {
        fields.Add(name, (2, Buffer.Count));
        Add(0, 0);
    }

    private void F32(string name)
    {
        fields.Add(name, (4, Buffer.Count));
        Add(0, 0, 0, 0);
    }

    private void F64(string name)
    {
        fields.Add(name, (8, Buffer.Count));
        Add(0, 0, 0, 0, 0, 0, 0, 0);
    }

    private void SetF(string name, ushort value) => SetF(name, Helper.UShortToLittleEndian(value));
    private void SetF(string name, uint value) => SetF(name, Helper.UIntToLittleEndian(value));
    private void SetF(string name, ulong value) => SetF(name, Helper.ULongToLittleEndian(value));

    private void SetF(string name, byte[] bytes)
    {
        var (size, offset) = fields[name];
        for (var i = 0; i < size; i++)
            Buffer[offset + i] = bytes[i];
    }
}