using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Pll.Codegen;

internal sealed class InMemoryProgram : IDisposable
{
    private IntPtr pointer;
    private int size;
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int AsmFunc();
    
    public InMemoryProgram(byte[] data)
    {
        // copy the code to an executable memory buffer
        var flags = MMap.MAP_PRIVATE | MMap.MAP_ANONYMOUS;
        var prot = MMap.PROT_EXEC | MMap.PROT_READ | MMap.PROT_WRITE;
        size = data.Length; // 4096?
        pointer = Native.mmap(IntPtr.Zero, size, prot, flags, 0, IntPtr.Zero);
        Marshal.Copy(data, 0, pointer, data.Length);
    }

    public int Invoke()
    {
        var func = Marshal.GetDelegateForFunctionPointer<AsmFunc>(pointer);
        return func();
    }

    public void Dispose()
    {
        Native.munmap(pointer, size);
    }
}

internal static class MMap
{
    public static int MAP_ANON = 32;
    public static int MAP_ANONYMOUS = 32;
    public static int MAP_DENYWRITE = 2048;
    public static int MAP_EXECUTABLE = 4096;
    public static int MAP_POPULATE = 32768;
    public static int MAP_PRIVATE = 2;
    public static int MAP_SHARED = 1;
    
    public static int PROT_EXEC = 4;
    public static int PROT_READ = 1;
    public static int PROT_WRITE = 2;
}