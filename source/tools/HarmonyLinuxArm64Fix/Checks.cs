using System.Reflection;
using System.Runtime.InteropServices;
using StrmAssistantRebuild.Common;

var count=0;
void Check(bool ok,string label){count++;if(!ok)throw new Exception(label);}
void Throws<T>(Action action)where T:Exception{try{action();}catch(T){count++;return;}throw new Exception("Expected "+typeof(T).Name);}
Check(LinuxInstructionCache.CheckedEnd(new IntPtr(0x1000),16)==new IntPtr(0x1010),"GCC end pointer, not length");
Check(LinuxInstructionCache.CheckedEnd(IntPtr.Zero,0)==IntPtr.Zero,"zero-length empty range");
Throws<ArgumentOutOfRangeException>(()=>LinuxInstructionCache.CheckedEnd(IntPtr.Zero,1));
Throws<ArgumentOutOfRangeException>(()=>LinuxInstructionCache.CheckedEnd(new IntPtr(-1),1));
Throws<ArgumentOutOfRangeException>(()=>LinuxInstructionCache.CheckedEnd(new IntPtr(0x1000),-1));
Throws<OverflowException>(()=>LinuxInstructionCache.CheckedEnd(new IntPtr(long.MaxValue-1),16));
LinuxInstructionCache.EnsureAvailable(1,IntPtr.Zero,-1);LinuxInstructionCache.Flush(1,IntPtr.Zero,-1);
LinuxInstructionCache.EnsureAvailable(0,IntPtr.Zero,0);LinuxInstructionCache.Flush(0,IntPtr.Zero,0);
if(!OperatingSystem.IsLinux()||RuntimeInformation.ProcessArchitecture!=Architecture.Arm64)
{
    LinuxInstructionCache.EnsureAvailable(0,new IntPtr(0x1000),16);LinuxInstructionCache.Flush(0,new IntPtr(0x1000),16);
    Check((IntPtr)typeof(LinuxInstructionCache).GetField("libraryHandle",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)! == IntPtr.Zero,"non-Linux-ARM64 never loads native helper");
}
else
{
    // This test validates symbol resolution only. Real cache behavior is proved by the host fixture.
    var memory=Marshal.AllocHGlobal(16);
    try{LinuxInstructionCache.EnsureAvailable(0,memory,16);LinuxInstructionCache.Flush(0,memory,16);Check(true,"native clear-cache export accepts exact range");}
    finally{Marshal.FreeHGlobal(memory);}
}
Check(typeof(LinuxInstructionCache).Assembly.GetReferencedAssemblies().All(a=>!a.Name!.StartsWith("Mono.Cecil")&&!a.Name.StartsWith("MonoMod")&&!a.Name.Contains("Harmony")),"helper adds no runtime dependency assembly");
Console.WriteLine($"PASS cache-helper checks={count} runtime={Environment.Version} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture}");
