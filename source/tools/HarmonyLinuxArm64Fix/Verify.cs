using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=3)throw new ArgumentException("Verify <unpatched-test.dll> <patched-test.dll> <tampered-test-output.dll>");
using var before=AssemblyDefinition.ReadAssembly(args[0]);using var after=AssemblyDefinition.ReadAssembly(args[1]);
MethodDefinition Target(AssemblyDefinition a)=>a.MainModule.GetType("MonoMod.Core.Platforms.Systems.LinuxSystem").Methods.Single(m=>m.Name=="PatchData");
var old=Target(before);var patched=Target(after);var ins=patched.Body.Instructions;var checks=0;
void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
var calls=ins.Select((i,n)=>(i,n)).Where(p=>p.i.Operand is MethodReference m&&m.DeclaringType.FullName=="StrmAssistantRebuild.Common.LinuxInstructionCache").ToArray();
Check(old.Body.CodeSize==74&&patched.Body.CodeSize==102,"pinned old/new code size");
Check(calls.Length==2&&((MethodReference)calls[0].i.Operand).Name=="EnsureAvailable"&&((MethodReference)calls[1].i.Operand).Name=="Flush","exact helper call count/order");
Check(calls[0].n==4&&ins[5].OpCode==old.Body.Instructions[0].OpCode,"EnsureAvailable before original first instruction/protection/copy");
Check(ins[^1].OpCode==OpCodes.Ret&&calls[1].n==ins.Count-2,"Flush immediately before final return");
Check(ins[calls[1].n-5].Operand is MethodReference copy&&copy.Name=="CopyTo"&&copy.DeclaringType.FullName=="System.ReadOnlySpan`1<System.Byte>","postcopy flush argument sequence has no intervening callbacks");
foreach(var entry in calls)
{
    Check(ins[entry.n-4].OpCode==OpCodes.Ldarg_1&&ins[entry.n-3].OpCode==OpCodes.Ldarg_2&&ins[entry.n-2].OpCode==OpCodes.Ldarga_S&&((ParameterDefinition)ins[entry.n-2].Operand).Index==2&&ins[entry.n-1].Operand is MethodReference length&&length.Name=="get_Length","helper receives exact kind/address/data.Length");
}
Check(before.MainModule.AssemblyReferences.Select(r=>r.FullName).SequenceEqual(after.MainModule.AssemblyReferences.Select(r=>r.FullName)),"no additional assembly dependencies");
Check(!after.MainModule.AssemblyReferences.Any(r=>r.Name is "Mono.Cecil" or "HarmonyLinuxArm64Fix"),"build-only tool never becomes runtime dependency");
Check(patched.Body.MaxStackSize==3&&patched.Body.InitLocals&&patched.Body.ExceptionHandlers.Count==0,"stack/local/EH envelope preserved");
// Negative fixture only; the production tool must reject this drift without modifying it.
old.Body.Instructions[0].OpCode=OpCodes.Ldarg_2;before.Write(args[2]);
Console.WriteLine($"PASS cache-injection checks={checks}; generated negative drift fixture={args[2]}");
