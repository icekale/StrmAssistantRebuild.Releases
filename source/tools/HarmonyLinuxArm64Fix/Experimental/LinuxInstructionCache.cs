using System.Runtime.InteropServices;

namespace StrmAssistantRebuild.Common;

/// <summary>Build-injected into the pinned MonoMod Linux executable-memory writer, not a runtime Harmony patch.</summary>
internal static class LinuxInstructionCache
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClearCache(IntPtr begin,IntPtr end);
    private static readonly Lazy<ClearCache> clear=new(LoadClearCache);
    private static IntPtr libraryHandle; // Kept loaded for process lifetime, including later JIT detour updates.

    private static bool Applies(int patchKind,int length)=>patchKind==0&&length!=0&&OperatingSystem.IsLinux()&&RuntimeInformation.ProcessArchitecture==Architecture.Arm64;
    internal static IntPtr CheckedEnd(IntPtr address,int length)
    {
        if(length<0)throw new ArgumentOutOfRangeException(nameof(length));
        if(length==0)return address;
        if(address.ToInt64()<=0)throw new ArgumentOutOfRangeException(nameof(address));
        return new IntPtr(checked(address.ToInt64()+length));
    }
    internal static void EnsureAvailable(int patchKind,IntPtr address,int length)
    {
        if(!Applies(patchKind,length))return;
        _=CheckedEnd(address,length);
        _=clear.Value; // Resolve before ProtectRWX/CopyTo so a missing export leaves code bytes untouched.
    }
    internal static void Flush(int patchKind,IntPtr address,int length)
    {
        if(!Applies(patchKind,length))return;
        var end=CheckedEnd(address,length);
        clear.Value(address,end); // GCC takes [begin,end), not (address,length).
    }
    private static ClearCache LoadClearCache()
    {
        var handle=NativeLibrary.Load("libgcc_s.so.1");
        try
        {
            var callback=Marshal.GetDelegateForFunctionPointer<ClearCache>(NativeLibrary.GetExport(handle,"__clear_cache"));
            libraryHandle=handle;return callback;
        }
        catch{NativeLibrary.Free(handle);throw;}
    }
}
