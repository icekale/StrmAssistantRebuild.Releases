using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static partial class Program
{
    private const string ArmType="MonoMod.Core.Platforms.Architectures.Arm64Arch";
    private static MethodDefinition ThunkMethod(AssemblyDefinition a)=>a.MainModule.GetType(ArmType).Methods.Single(m=>m.Name=="CreateKnownMethodThunks"&&m.IsStatic&&m.Parameters.Count==0);
    private static int FixThunk(string targetPath,string originalPath)
    {
        var target=Path.GetFullPath(targetPath);var original=Path.GetFullPath(originalPath);
        Require(target!=original,"Upstream package stays read-only.");var sourceBytes=File.ReadAllBytes(original);
        Require(Sha(sourceBytes)==HarmonySha,"Pinned original Harmony SHA differs.");
        using var sourceAssembly=Read(sourceBytes);var source=ThunkMethod(sourceAssembly);
        var before=File.ReadAllBytes(target);using var merged=Read(before);var method=ThunkMethod(merged);
        Require(MethodState(source)==MethodState(method),"Call-counting matcher differs from pinned source or is already corrected.");
        var baseline=MethodStates(merged);var refs=References(merged);var resources=Resources(merged);
        // Pinned package emits nine EncodeLdr calls: final three are offsets 0/20/28 of the counting stub.
        var calls=method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Call&&i.Operand is MethodReference m&&m.DeclaringType.FullName==ArmType&&m.Name=="EncodeLdr64LiteralTo").ToArray();
        Require(calls.Length==9,"Unexpected EncodeLdr call count.");
        var last=calls[^1];var reg=last.Previous;
        Require(reg.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(reg.Operand)==9,"Expected final counting-stub register x9 operand.");
        Require(calls[^2].Previous.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(calls[^2].Previous.Operand)==9,"Expected prior active-target register x9.");
        var window=method.Body.Instructions.Skip(method.Body.Instructions.IndexOf(calls[^2])+1).TakeWhile(i=>i!=last).ToArray();
        Require(window.Count(i=>i.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(i.Operand)==28)==1&&window.Count(i=>i.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(i.Operand)==-28)==1&&window.Count(i=>i.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(i.Operand)==16)==1,"Expected exact offset28/relative-minus28/target-slot16 final encoding.");
        var oldOffset=reg.Offset;reg.Operand=(sbyte)10;var intended=MethodState(method);var temp=target+".thunkfix."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            merged.Write(temp,new WriterParameters{WriteSymbols=false});var after=File.ReadAllBytes(temp);using var check=Read(after);var changed=ThunkMethod(check);var states=MethodStates(check);
            Require(states.Count==baseline.Count&&baseline.All(p=>p.Key==method.MetadataToken.ToString()?states[p.Key]==intended:states.TryGetValue(p.Key,out var now)&&now==p.Value),"Unexpected method changes outside single matcher operand.");
            Require(References(check)==refs&&Resources(check)==resources,"Matcher repair changed references/resources.");
            Require(changed.Body.CodeSize==source.Body.CodeSize,"One-byte operand correction changed code size.");
            File.Move(temp,target,true);
            Console.WriteLine(JsonSerializer.Serialize(new{status="callcounting-x10-corrected",target,originalHarmonySha256=HarmonySha,beforeSha256=Sha(before),afterSha256=Sha(after),method=method.FullName,codeSize=source.Body.CodeSize,ilOperandInstructionOffset=oldOffset,oldRegister=9,newRegister=10,stubInstructionOffset=28,untouchedMethods=baseline.Count-1,refsResourcesUnchanged=true,mode="--callcounting-x10"}));return 0;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
