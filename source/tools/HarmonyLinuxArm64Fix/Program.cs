using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using AssemblyDefinition=Mono.Cecil.AssemblyDefinition;
using MethodDefinition=Mono.Cecil.MethodDefinition;
using TypeDefinition=Mono.Cecil.TypeDefinition;
using TypeReference=Mono.Cecil.TypeReference;

// Build-only pinned MonoMod repair. Production invokes explicit --callcounting-x10 after ILRepack.
// The two-argument cache experiment remains for retained evidence only; upstream stays read-only.
internal static partial class Program
{
    private const string HarmonySha="12EED32DE73312B376F616EE8659BA5D3766D96859181728437FA85C6B5CE309";
    private const string PatchIlSha="055E0F8E9FF933B899A48AD62C4E89F061D6534A6D1AD4224C217C43A0603FB5";
    private const string LinuxType="MonoMod.Core.Platforms.Systems.LinuxSystem";
    private const string HelperType="StrmAssistantRebuild.Common.LinuxInstructionCache";
    private static IAssemblyResolver? referenceResolver;
    public static int Main(string[] args)
    {
        using var resolver = new DefaultAssemblyResolver();
        referenceResolver = resolver;
        try
        {
            if(args.Length is 3 or 4 && args[0]=="--callcounting-x10")
            {
                // Rewriting optional constants backed by external enums requires the same host refs as compilation.
                if (args.Length == 4)
                {
                    var directory = Path.GetFullPath(args[3]);
                    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Reference directory is missing.");
                    resolver.AddSearchDirectory(directory);
                }
                return FixThunk(args[1],args[2]);
            }
            if(args.Length!=2)throw new ArgumentException("Usage: HarmonyLinuxArm64Fix <merged.dll> <pinned-original-0Harmony.dll>");
            var target=Path.GetFullPath(args[0]);var original=Path.GetFullPath(args[1]);
            if(target==original)throw new ArgumentException("The upstream dependency is read-only.");
            var originalBytes=File.ReadAllBytes(original);Require(Sha(originalBytes)==HarmonySha,"Pinned original Harmony SHA differs.");
            Require(RawIlSha(originalBytes)==PatchIlSha,"Pinned original PatchData IL hash differs.");
            using var dependency=Read(originalBytes);var source=PatchMethod(dependency);
            Require(source.Body.CodeSize==74&&source.Body.MaxStackSize==3&&source.Body.InitLocals&&source.Body.ExceptionHandlers.Count==0,"Pinned PatchData body envelope differs.");
            var oldBytes=File.ReadAllBytes(target);using var merged=Read(oldBytes);var method=PatchMethod(merged);
            var baseline=MethodStates(merged);var refs=References(merged);var resources=Resources(merged);
            var helper=merged.MainModule.GetType(HelperType)??throw new InvalidDataException("Compiled cache helper is absent from merged assembly.");
            var ensure=Helper(helper,"EnsureAvailable");var flush=Helper(helper,"Flush");
            Require(MethodState(method)==MethodState(source),"Merged PatchData differs from pinned original or was already patched.");
            Require(method.Body.Instructions.Count(i=>i.OpCode==OpCodes.Ret)==1&&method.Body.Instructions.Last().OpCode==OpCodes.Ret,"Expected unique final return.");
            var copies=method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Call&&i.Operand is MethodReference m&&m.Name=="CopyTo"&&m.DeclaringType.FullName=="System.ReadOnlySpan`1<System.Byte>").ToArray();
            Require(copies.Length==1&&copies[0].Next==method.Body.Instructions.Last(),"Expected unique ReadOnlySpan CopyTo immediately before ret.");
            var length=(MethodReference)method.Body.Instructions.First(i=>i.Operand is MethodReference m&&m.Name=="get_Length"&&m.DeclaringType.FullName=="System.ReadOnlySpan`1<System.Byte>").Operand;
            var first=method.Body.Instructions[0];var ret=method.Body.Instructions.Last();
            AddCall(method,first,ensure,length);AddCall(method,ret,flush,length);
            var intended=MethodState(method);var temporary=target+".cachefix."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                merged.Write(temporary,new WriterParameters{WriteSymbols=false});
                var newBytes=File.ReadAllBytes(temporary);using var verified=Read(newBytes);var actual=PatchMethod(verified);var states=MethodStates(verified);
                Require(states.Count==baseline.Count&&baseline.All(p=>p.Key==method.MetadataToken.ToString()?states[p.Key]==intended:states.TryGetValue(p.Key,out var now)&&p.Value==now),"Unexpected method-body or method metadata change.");
                Require(References(verified)==refs,"Cache repair added an assembly dependency.");
                Require(Resources(verified)==resources,"Cache repair changed embedded resources.");
                Require(actual.Body.Instructions.Count(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName==HelperType)==2,"Expected exactly two injected helper calls.");
                File.Move(temporary,target,true);
                Console.WriteLine(JsonSerializer.Serialize(new{status="patched",target,originalHarmonySha256=HarmonySha,originalMethodIlSha256=RawIlSha(originalBytes),mergedBeforeSha256=Sha(oldBytes),mergedAfterSha256=Sha(newBytes),targetMethod=method.FullName,oldCodeSize=source.Body.CodeSize,newCodeSize=actual.Body.CodeSize,untouchedMethods=baseline.Count-1,assemblyReferencesUnchanged=true,embeddedResourcesUnchanged=true,helperCalls=new[]{ensure.FullName,flush.FullName}}));
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine("CACHEFIX FAILED: "+error);return 1;}
    }
    private static AssemblyDefinition Read(byte[] bytes)=>AssemblyDefinition.ReadAssembly(new MemoryStream(bytes,false),new ReaderParameters{InMemory=true,ReadSymbols=false,AssemblyResolver=referenceResolver});
    private static MethodDefinition PatchMethod(AssemblyDefinition assembly)
    {
        var type=assembly.MainModule.GetType(LinuxType)??throw new InvalidDataException("Pinned LinuxSystem type missing.");
        var method=type.Methods.Single(m=>m.Name=="PatchData");
        Require(!method.IsStatic&&method.ReturnType.FullName=="System.Void"&&method.Parameters.Select(p=>p.ParameterType.FullName).SequenceEqual(new[]{"MonoMod.Core.Platforms.PatchTargetKind","System.IntPtr","System.ReadOnlySpan`1<System.Byte>","System.Span`1<System.Byte>"}),"PatchData signature differs.");
        var kind=assembly.MainModule.GetType("MonoMod.Core.Platforms.PatchTargetKind")!;
        Require(Convert.ToInt32(kind.Fields.Single(f=>f.Name=="Executable").Constant)==0,"Executable patch-kind value changed.");
        return method;
    }
    private static MethodDefinition Helper(TypeDefinition helper,string name)
    {
        var method=helper.Methods.Single(m=>m.Name==name);
        Require(method.IsStatic&&method.ReturnType.FullName=="System.Void"&&method.Parameters.Select(p=>p.ParameterType.FullName).SequenceEqual(new[]{"System.Int32","System.IntPtr","System.Int32"}),"Cache helper signature differs.");return method;
    }
    private static void AddCall(MethodDefinition method,Instruction before,MethodDefinition helper,MethodReference length)
    {
        var il=method.Body.GetILProcessor();
        foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Ldarg_2),Instruction.Create(OpCodes.Ldarga_S,method.Parameters[2]),Instruction.Create(OpCodes.Call,length),Instruction.Create(OpCodes.Call,helper)})il.InsertBefore(before,instruction);
    }
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach(var type in types){yield return type;foreach(var nested in AllTypes(type.NestedTypes))yield return nested;}
    }
    private static Dictionary<string,string> MethodStates(AssemblyDefinition assembly)=>AllTypes(assembly.MainModule.Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.MetadataToken.ToString(),MethodState);
    private static string MethodState(MethodDefinition method)
    {
        var metadata=$"{method.FullName}|{method.Attributes}|{method.ImplAttributes}|{method.CallingConvention}";
        if(!method.HasBody)return metadata;
        var body=method.Body;
        string Operand(object? operand)=>operand switch
        {
            null=>"",Instruction i=>"@"+body.Instructions.IndexOf(i),Instruction[] a=>string.Join(",",a.Select(i=>body.Instructions.IndexOf(i))),
            MethodReference m=>m.FullName,FieldReference f=>f.FullName,TypeReference t=>t.FullName,
            VariableDefinition v=>"local:"+v.Index,ParameterDefinition p=>"arg:"+p.Index,_=>operand.ToString()??""
        };
        return metadata+"|"+body.MaxStackSize+"|"+body.InitLocals+"|"+string.Join(",",body.Variables.Select(v=>v.VariableType.FullName))+"|"+string.Join(";",body.Instructions.Select(i=>i.OpCode.Code+":"+Operand(i.Operand)))+"|"+string.Join(";",body.ExceptionHandlers.Select(e=>$"{e.HandlerType}:{Operand(e.TryStart)}:{Operand(e.TryEnd)}:{Operand(e.HandlerStart)}:{Operand(e.HandlerEnd)}:{Operand(e.FilterStart)}:{e.CatchType?.FullName}"));
    }
    private static string References(AssemblyDefinition assembly)=>string.Join("\n",assembly.MainModule.AssemblyReferences.Select(r=>r.FullName));
    private static string Resources(AssemblyDefinition assembly)=>string.Join("\n",assembly.MainModule.Resources.Select(r=>r.Name+":"+r.Attributes+":"+(r is EmbeddedResource e?Sha(e.GetResourceData()):r.ResourceType.ToString())));
    private static string RawIlSha(byte[] bytes)
    {
        using var pe=new PEReader(new MemoryStream(bytes,false));var md=pe.GetMetadataReader();
        var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Namespace)+"."+md.GetString(t.Name)==LinuxType);
        var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)=="PatchData");return Sha(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!);
    }
    private static string Sha(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
}
