using System;
using System.Runtime.InteropServices;

namespace Pll.Codegen;

// See https://www.ok.unsode.com/post/2015/12/06/csharp-dotnet-x32-x64-assembler-and-cross-platform-code
internal static class Native
{
    #region Windows

    [Flags]
    private enum AllocationTypes : uint
    {
        Commit = 0x1000,
        Reserve = 0x2000,
        Reset = 0x80000,
        LargePages = 0x20000000,
        Physical = 0x400000,
        TopDown = 0x100000,
        WriteWatch = 0x200000
    }

    [Flags]
    private enum MemoryProtections : uint
    {
        Execute = 0x10,
        ExecuteRead = 0x20,
        ExecuteReadWrite = 0x40,
        ExecuteWriteCopy = 0x80,
        NoAccess = 0x01,
        ReadOnly = 0x02,
        ReadWrite = 0x04,
        WriteCopy = 0x08,
        GuartModifierflag = 0x100,
        NoCacheModifierflag = 0x200,
        WriteCombineModifierflag = 0x400
    }

    [Flags]
    private enum FreeTypes : uint
    {
        Decommit = 0x4000,
        Release = 0x8000
    }


    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, AllocationTypes flAllocationType, MemoryProtections flProtect);

    [DllImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr lpAddress, uint dwSize, FreeTypes flFreeType);

    #endregion

    #region Unix

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Enum | AttributeTargets.Field | AttributeTargets.Struct)]
    private class MapAttribute : Attribute
    {
        private string nativeType;
        private string suppressFlags;

        public MapAttribute() { }
        public MapAttribute(string nativeType) => this.nativeType = nativeType;


        public string NativeType => nativeType;

        public string SuppressFlags
        {
            get => suppressFlags;
            set => suppressFlags = value;
        }
    }

    private const string MPH = "MonoPosixHelper";
    private const string LIBC = "msvcrt";

    [Map, Flags]
    private enum MmapProts : int
    {
        PROT_READ = 0x1, // Page can be read.
        PROT_WRITE = 0x2, // Page can be written.
        PROT_EXEC = 0x4, // Page can be executed.
        PROT_NONE = 0x0, // Page can not be accessed.
        PROT_GROWSDOWN = 0x01000000, // Extend change to start of

        //   growsdown vma (mprotect only).
        PROT_GROWSUP = 0x02000000, // Extend change to start of
        //   growsup vma (mprotect only).
    }

    [Map, Flags]
    private enum MmapFlags : int
    {
        MAP_SHARED = 0x01, // Share changes.

        MAP_PRIVATE = 0x02, // Changes are private.

        MAP_TYPE = 0x0f, // Mask for type of mapping.

        MAP_FIXED = 0x10, // Interpret addr exactly.

        MAP_FILE = 0,

        MAP_ANONYMOUS = 0x20, // Don't use a file.

        MAP_ANON = MAP_ANONYMOUS,


        // These are Linux-specific.

        MAP_GROWSDOWN = 0x00100, // Stack-like segment.

        MAP_DENYWRITE = 0x00800, // ETXTBSY

        MAP_EXECUTABLE = 0x01000, // Mark it as an executable.

        MAP_LOCKED = 0x02000, // Lock the mapping.

        MAP_NORESERVE = 0x04000, // Don't check for reservations.

        MAP_POPULATE = 0x08000, // Populate (prefault) pagetables.

        MAP_NONBLOCK = 0x10000, // Do not block on IO.
    }


    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    public static extern IntPtr mmap(IntPtr addr, IntPtr length, int prot, int flags, int fd, IntPtr offset);

    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    public static extern int munmap(IntPtr addr, IntPtr length);


    // [DllImport(MPH, SetLastError = true, EntryPoint = "Mono_Posix_Syscall_mmap")]
    // private static extern IntPtr mmap(IntPtr start, ulong length, MmapProts prot, MmapFlags flags, int fd, long offset);
    //
    // [DllImport(MPH, SetLastError = true, EntryPoint = "Mono_Posix_Syscall_munmap")]
    // public static extern int munmap(IntPtr start, ulong length);
    //
    // [DllImport(MPH, SetLastError = true, EntryPoint = "Mono_Posix_Syscall_mprotect")]
    // private static extern int mprotect(IntPtr start, ulong len, MmapProts prot);
    //
    // [DllImport(MPH, CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "Mono_Posix_Stdlib_malloc")]
    // private static extern IntPtr malloc(ulong size);
    //
    // [DllImport(LIBC, CallingConvention = CallingConvention.Cdecl)]
    // public static extern void free(IntPtr ptr);

    #endregion


    // [UnmanagedFunctionPointerAttribute(CallingConvention.Cdecl)]
    // public unsafe delegate void asmFunc();
    //
    //
    // public static IntPtr VirtualAlloc(uint size)
    //
    // {
    //     IntPtr ptr = IntPtr.Zero;
    //
    //     if (RunningPlatform() == Platform.Windows)
    //
    //     {
    //         ptr = VirtualAlloc(
    //             IntPtr.Zero,
    //             new UIntPtr(size),
    //             AllocationTypes.Commit | AllocationTypes.Reserve,
    //             MemoryProtections.ExecuteReadWrite);
    //     }
    //
    //     else
    //
    //     {
    //         Console.WriteLine("Linux memory allocation...");
    //
    //
    //         ptr = mmap(IntPtr.Zero, 4096, MmapProts.PROT_EXEC | MmapProts.PROT_READ | MmapProts.PROT_WRITE, MmapFlags.MAP_ANONYMOUS | MmapFlags.MAP_PRIVATE, 0, 0);
    //
    //
    //         Console.WriteLine("memory ptr: " + ptr.ToInt64());
    //     }
    //
    //     return ptr;
    // }
    //
    //
    // public static void VirtualFree(IntPtr ptr, uint size)
    //
    // {
    //     if (RunningPlatform() == Platform.Windows)
    //
    //     {
    //         VirtualFree(ptr, size, FreeTypes.Release);
    //     }
    //
    //     else
    //
    //     {
    //         Console.WriteLine("Free memory ptr: " + ptr.ToInt64());
    //
    //         int r = munmap(ptr, size);
    //
    //         Console.WriteLine("memory free status: " + r);
    //     }
    // }
}