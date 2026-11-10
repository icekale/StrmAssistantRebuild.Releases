using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Buffers.Binary;

if(args.Length is not (2 or 3))throw new ArgumentException("ThunkChecks <unpatched-merged.dll> <patched-merged.dll> [--no-cache-helper]");
var checks=0;
void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
(object[] Patterns,Func<byte[],bool> Matches) Open(string path)
{
    var load=new AssemblyLoadContext(Guid.NewGuid().ToString(),true);
    var assembly=load.LoadFromAssemblyPath(Path.GetFullPath(path));
    if(args.Length==3){Check(args[2]=="--no-cache-helper","expected optional no-cache-helper check");Check(assembly.GetType("StrmAssistantRebuild.Common.LinuxInstructionCache")==null,"cache experiment excluded from production-shaped artifact");Check(assembly.GetReferencedAssemblies().All(r=>r.Name is not ("Mono.Cecil" or "HarmonyLinuxArm64Fix")),"no build-tool runtime dependency");}
    var method=assembly.GetType("MonoMod.Core.Platforms.Architectures.Arm64Arch",true)!.GetMethod("CreateKnownMethodThunks",BindingFlags.Static|BindingFlags.NonPublic)!;
    var collection=method.Invoke(null,null)!;
    var target=collection.GetType().GetMethods().Single(m=>m.Name=="TryMatchAt"&&m.GetParameters()[1].ParameterType==typeof(ulong).MakeByRefType());
    var proxy=new DynamicMethod("ActualPatternMatch",typeof(bool),new[]{typeof(object),typeof(byte[])},typeof(Program).Module,true);var il=proxy.GetILGenerator();
    var addr=il.DeclareLocal(typeof(ulong));var pattern=il.DeclareLocal(target.GetParameters()[2].ParameterType.GetElementType()!);var length=il.DeclareLocal(typeof(int));
    il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,collection.GetType());il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Newobj,typeof(ReadOnlySpan<byte>).GetConstructor(new[]{typeof(byte[])})!);il.Emit(OpCodes.Ldloca,addr);il.Emit(OpCodes.Ldloca,pattern);il.Emit(OpCodes.Ldloca,length);il.Emit(OpCodes.Callvirt,target);il.Emit(OpCodes.Ret);
    var match=(Func<object,byte[],bool>)proxy.CreateDelegate(typeof(Func<object,byte[],bool>));
    return (((IEnumerable)collection).Cast<object>().ToArray(),bytes=>match(collection,bytes));
}
byte[] Pattern(object p)=>((ReadOnlyMemory<byte>)p.GetType().GetField("pattern",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(p)!).ToArray();
byte[] Mask(object p)=>((ReadOnlyMemory<byte>)p.GetType().GetField("bitmask",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(p)!).ToArray();
var old=Open(args[0]);var fixedVersion=Open(args[1]);Check(old.Patterns.Length==fixedVersion.Patterns.Length,"pattern count preserved");
var changed=0;
for(var i=0;i<old.Patterns.Length;i++)
{
    var a=Pattern(old.Patterns[i]);var b=Pattern(fixedVersion.Patterns[i]);Check(Mask(old.Patterns[i]).SequenceEqual(Mask(fixedVersion.Patterns[i])),"all masks unchanged");
    if(a.SequenceEqual(b))continue;
    changed++;Check(a.Length==36&&b.Length==36&&a.Select((v,n)=>(v,n)).Where(p=>p.v!=b[p.n]).Select(p=>p.n).SequenceEqual(new[]{28})&&a[28]==0xa9&&b[28]==0xaa,"only counting-stub byte28 x9→x10 changed");
}
Check(changed==5,"exact five page-size counting patterns changed, all other patterns unchanged");
uint Ldr(int at,int slot,int reg)=>0x58000000u|((uint)((slot-at)/4)&0x7ffffu)<<5|(uint)reg;
byte[] Words(params uint[] words){var bytes=new byte[words.Length*4];for(var i=0;i<words.Length;i++)BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i*4),words[i]);return bytes;}
foreach(var page in new[]{4096,8192,16384,32768,65536})
{
    // Exact .NET8 thunktemplates.S ldr x10/BR x10 threshold arm, active target at slot+8.
    var counting=Words(Ldr(0,page,9),0x7940012a,0x7100054a,0x7900012a,0x54000060,Ldr(20,page+8,9),0xd61f0120,Ldr(28,page+16,10),0xd61f0140);
    Check(!old.Matches(counting)&&fixedVersion.Matches(counting),"actual matcher false→true for counting page "+page);
    var wrong=counting.ToArray();wrong[28]=(byte)((wrong[28]&0xe0)|9);Check(old.Matches(wrong)&&!fixedVersion.Matches(wrong),"bad x9 matcher no longer accepts invalid threshold arm");
    var fixup=Words(Ldr(0,page,11),0xd61f0160,Ldr(8,page+8,12),Ldr(12,page+16,11),0xd61f0160);
    Check(old.Matches(fixup)&&fixedVersion.Matches(fixup),"other fixup pattern behavior unchanged");
}
Console.WriteLine($"PASS actual-thunk-matcher checks={checks} totalPatterns={old.Patterns.Length} changedPatterns={changed} runtime={Environment.Version}");
