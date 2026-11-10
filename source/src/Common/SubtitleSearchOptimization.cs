using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace StrmAssistantRebuild.Common;

/// <summary>Perfect-match OpenSubtitles search over a registered STRM's local target; no remote resolution.</summary>
public static class SubtitleSearchOptimization
{
    private const string Owner="StrmAssistantRebuild.PerfectSubtitle.1.0.71";
    private const string Hash="0214AA7E918DE6B61071553F122B6FA322FD8DAC099E6DD5945006DD600AD1F9";
    private static readonly Guid Mvid=new("9748e51c-a9ed-4618-8ae7-644ab2bdcf3c");
    private static readonly object Gate=new();
    private static readonly Func<object,object> Copy=typeof(object).GetMethod("MemberwiseClone",BindingFlags.NonPublic|BindingFlags.Instance)!.CreateDelegate<Func<object,object>>();
    private sealed record State(ILibraryManager Library,MethodInfo Search,MethodInfo[] Contracts);
    private sealed record Invocation(object Instance,SubtitleSearchRequest Request);
    [ThreadStatic] private static Invocation? bypass;
    private static State? current;
    private static Harmony? patcher;
    private static MethodInfo? installed, filenameTarget;
    private static readonly ConditionalWeakTable<SubtitleSearchRequest,string> Names=new();
    private static int activeNames;
    public static bool IsEnabled=>Volatile.Read(ref current)!=null;
    public static string Detail{get;private set;}="Disabled; native subtitle search retained.";

    internal static MethodInfo[] FindTargets(Assembly assembly)
    {
        var type=assembly.GetType("OpenSubtitles.OpenSubtitleComDownloader");
        var search=type?.GetMethod("Search",BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly);
        var inner=type?.GetMethod("SearchInternal",BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);
        var hash=assembly.GetType("OpenSubtitles.Hasher")?.GetMethod("ComputeMovieHash",BindingFlags.Public|BindingFlags.Static);
        var move=search?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance);
        var innerMove=inner?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance);
        return new[]{search,move,inner,innerMove,hash}.Where(m=>m!=null).Cast<MethodInfo>().ToArray();
    }
    internal static bool Verify(Assembly assembly,MethodInfo[] targets)
    {
        if(assembly.GetName().Name!="OpenSubtitles"||assembly.GetName().Version!=new Version(1,0,71,0)||assembly.ManifestModule.ModuleVersionId!=Mvid||targets.Length!=5)return false;
        var search=targets[0];var p=search.GetParameters();
        if(search.DeclaringType?.FullName!="OpenSubtitles.OpenSubtitleComDownloader"||!search.DeclaringType.IsPublic||!search.DeclaringType.IsSealed||!search.IsPublic||search.IsStatic||
            search.ReturnType!=typeof(Task<IEnumerable<RemoteSubtitleInfo>>)||p.Length!=3||p[0].ParameterType!=typeof(SubtitleSearchRequest)||p[1].ParameterType.FullName!="OpenSubtitles.OpenSubtitleOptions"||p[2].ParameterType!=typeof(CancellationToken))return false;
        var tokens=new[]{0x06000026,0x06000126,0x06000028,0x06000128,0x0600000B};
        var lengths=new[]{79,339,88,1679,155};
        var hashes=new[]{"0FE862A4753DACD1FDA6438F7B6558439CB8A02FEB6022B56D19527E55DB8DF1","B5A12A8A5BFABA26CAA672FF61AD9224ED2210AE4A3F65267EBCDF645CB926CD","95AF9CEB3DB0C819823281055D76192441BEAC1CF1B8F95F0F3441D78C1F3A4A","4996A0C820458B1D5D79AD5A50C64D7A70293CA91C6B7AE8F12BC3B08BFFCEC6","781F69D31DC321F0828271A4391B30E719A621CE40CE50BC551AAEC9431F3D5B"};
        return targets.Select((m,i)=>m.Module==search.Module&&m.MetadataToken==tokens[i]&&m.GetMethodBody()?.GetILAsByteArray() is {} il&&il.Length==lengths[i]&&Convert.ToHexString(SHA256.HashData(il))==hashes[i]).All(ok=>ok);
    }
    private static bool Artifact(IPlugin plugin)
    {
        var assembly=plugin.GetType().Assembly;var path=string.IsNullOrEmpty(assembly.Location)?plugin.AssemblyFilePath:assembly.Location;
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))return false;
        using var file=File.OpenRead(path);
        using(var pe=new PEReader(file,PEStreamOptions.LeaveOpen))
        {
            if(!pe.HasMetadata)return false;var metadata=pe.GetMetadataReader();if(!metadata.IsAssembly)return false;
            var definition=metadata.GetAssemblyDefinition();if(metadata.GetString(definition.Name)!=assembly.GetName().Name||definition.Version!=assembly.GetName().Version||metadata.GetGuid(metadata.GetModuleDefinition().Mvid)!=Mvid)return false;
        }
        file.Position=0;using var sha=SHA256.Create();return Convert.ToHexString(sha.ComputeHash(file))==Hash;
    }
    public static bool Configure(Version version,bool enabled,IEnumerable<IPlugin> plugins,ILibraryManager library,ILogger logger)
    {
        lock(Gate)
        {
            try
            {
                Disable();if(!enabled)return true;
                var found=plugins.Where(p=>p.GetType().FullName=="OpenSubtitles.Plugin"&&p.GetType().Assembly.GetName().Name=="OpenSubtitles").ToArray();
                if(version!=new Version(4,10,2,0)||found.Length!=1||!Artifact(found[0])){Detail="Perfect STRM subtitle adapter inactive: exact loaded plugin artifact differs.";return false;}
                var targets=FindTargets(found[0].GetType().Assembly);
                if(!Verify(found[0].GetType().Assembly,targets)||targets.Any(m=>Harmony.GetPatchInfo(m)?.Owners.Any(owner=>owner!=Owner)==true)||
                    targets.Where(m=>m!=filenameTarget).Any(m=>Harmony.GetPatchInfo(m)?.Owners.Any()==true)||
                    (filenameTarget!=null&&(filenameTarget!=targets[3]||!ExpectedFilenamePatch(filenameTarget))))
                {Detail="STRM subtitle adapter inactive: search/hash contract or ownership differs.";return false;}
                patcher??=new Harmony(Owner);
                if(filenameTarget==null){filenameTarget=targets[3];patcher.Patch(filenameTarget,transpiler:new HarmonyMethod(typeof(SubtitleSearchOptimization),nameof(ReplaceFilename)));}
                installed=targets[0];patcher.Patch(installed,prefix:new HarmonyMethod(typeof(SubtitleSearchOptimization),nameof(Before)));
                Volatile.Write(ref current,new(library,installed,targets));Detail="Request-perfect or library-perfect local STRM search: target hash with original STRM basename in native filename query; caller/options unchanged. Remote targets and full online provider behavior remain unverified.";return true;
            }
            catch(Exception error){Volatile.Write(ref current,null);try{Disable();}catch{}Detail="Perfect STRM subtitle adapter inactive: "+error.GetType().Name;return false;}
            finally{try{logger.Info("[StrmAssistantRebuild] {0}",Detail);}catch{}}
        }
    }
    public static void Disable()
    {
        lock(Gate)
        {
            Volatile.Write(ref current,null);
            if(installed!=null){patcher?.Unpatch(installed,HarmonyPatchType.All,Owner);installed=null;}
            RemoveFilenameIfIdle();
            Detail=activeNames==0?"Disabled; native subtitle search retained.":"Disabled for new searches; in-flight filename mapping retained until completion.";
        }
    }
    private static void RemoveFilenameIfIdle()
    {
        if(activeNames==0&&filenameTarget!=null){patcher?.Unpatch(filenameTarget,HarmonyPatchType.All,Owner);filenameTarget=null;}
    }
    private static bool ExpectedFilenamePatch(MethodInfo method)
    {
        var info=Harmony.GetPatchInfo(method);
        return info!=null&&info.Transpilers.Count==1&&info.Transpilers[0].owner==Owner&&info.Transpilers[0].PatchMethod==typeof(SubtitleSearchOptimization).GetMethod(nameof(ReplaceFilename),BindingFlags.NonPublic|BindingFlags.Static)&&
            info.Prefixes.Count==0&&info.Postfixes.Count==0&&info.Finalizers.Count==0&&info.InnerPrefixes.Count==0&&info.InnerPostfixes.Count==0;
    }
    private static string Filename(SubtitleSearchRequest request) => Names.TryGetValue(request,out var name)?name:Path.GetFileName(request.MediaPath);
    private static IEnumerable<CodeInstruction> ReplaceFilename(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.Select(i=>new CodeInstruction(i)).ToList();
        var getter=typeof(SubtitleSearchRequest).GetProperty(nameof(SubtitleSearchRequest.MediaPath))!.GetMethod!;
        var filename=typeof(Path).GetMethod(nameof(Path.GetFileName),new[]{typeof(string)})!;
        var matches=Enumerable.Range(0,Math.Max(0,code.Count-1)).Where(i=>code[i].Calls(getter)&&code[i+1].Calls(filename)).ToArray();
        if(matches.Length!=1)throw new InvalidOperationException("Native subtitle filename callsite differs.");
        // Preserve labels/EH positions and the preceding request load; replace string-producing
        // getter with nop and consume the request directly at the original Path.GetFileName call.
        var index=matches[0];code[index].opcode=OpCodes.Nop;code[index].operand=null;
        code[index+1].opcode=OpCodes.Call;code[index+1].operand=typeof(SubtitleSearchOptimization).GetMethod(nameof(Filename),BindingFlags.NonPublic|BindingFlags.Static)!;
        return code;
    }
    private static bool IsCurrent(State state)
    {
        if(!ReferenceEquals(state,Volatile.Read(ref current)))return false;
        try
        {
            if(!state.Contracts.Any(m=>Harmony.GetPatchInfo(m)?.Owners.Any(owner=>owner!=Owner)==true))return true;
        }
        catch { }
        Interlocked.CompareExchange(ref current,null,state);
        Detail="Perfect STRM subtitle adapter inactive: native search/hash ownership changed.";
        return false;
    }
    private static bool Before(object __instance,SubtitleSearchRequest __0,object __1,CancellationToken __2,ref Task<IEnumerable<RemoteSubtitleInfo>> __result)
    {
        if(bypass is {} pass&&ReferenceEquals(pass.Instance,__instance)&&ReferenceEquals(pass.Request,__0))return true;
        var state=Volatile.Read(ref current);
        if(state==null||__0==null)return true;
        // Capture before any library callback or await: a reused mutable caller must not change
        // the admitted path, perfect-match mode or basename after their checks have run.
        var snapshot=(SubtitleSearchRequest)Copy(__0);
        if(string.IsNullOrEmpty(snapshot.MediaPath)||!Path.IsPathFullyQualified(snapshot.MediaPath)||!Path.GetExtension(snapshot.MediaPath).Equals(".strm",StringComparison.OrdinalIgnoreCase))return true;
        try
        {
            if(!IsCurrent(state))return true;
            var item=state.Library.FindByPath(snapshot.MediaPath,false);
            if(item is not Video || item.InternalId<=0 || !item.IsShortcut || !string.Equals(item.Path,snapshot.MediaPath,StringComparison.Ordinal))return true;
            if(!snapshot.IsPerfectMatch&&state.Library.GetLibraryOptions(item).RequirePerfectSubtitleMatch!=true)return true;
        }
        catch{return true;}
        __result=ResolveAndSearch(state,__instance,__0,snapshot,__1,__2);return false;
    }
    internal static async Task<string?> ResolveLocalTarget(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous|FileOptions.SequentialScan);
            if(file.Length>16384)return null;
            var buffer=new byte[16385];var count=0;
            while(count<buffer.Length){var read=await file.ReadAsync(buffer.AsMemory(count),token).ConfigureAwait(false);if(read==0)break;count+=read;}
            if(count>16384)return null;
            var text=new UTF8Encoding(false,true).GetString(buffer,0,count).TrimStart('\uFEFF');
            var lines=text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            if(lines.Length!=1)return null;var target=lines[0];if(target.Any(char.IsControl))return null;
            if(Uri.TryCreate(target,UriKind.Absolute,out var uri)&&!string.IsNullOrEmpty(uri.Scheme))
            {if(!uri.IsFile||uri.IsUnc||uri.Query.Length!=0||uri.Fragment.Length!=0)return null;target=uri.LocalPath;}
            if(target.Any(char.IsControl)||!Path.IsPathFullyQualified(target)||Path.GetExtension(target).Equals(".strm",StringComparison.OrdinalIgnoreCase)||!File.Exists(target))return null;
            token.ThrowIfCancellationRequested();return target;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException){return null;}
    }
    private static async Task<IEnumerable<RemoteSubtitleInfo>> ResolveAndSearch(State state,object instance,SubtitleSearchRequest request,SubtitleSearchRequest snapshot,object options,CancellationToken token)
    {
        var originalPath=snapshot.MediaPath;
        var target=await ResolveLocalTarget(originalPath,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var actual=request;var mapped=false;
        lock(Gate)
        {
            if(target!=null&&IsCurrent(state))
            {
                actual=snapshot;actual.MediaPath=target;
                if(!actual.IsPerfectMatch){Names.Add(actual,Path.GetFileNameWithoutExtension(originalPath));activeNames++;mapped=true;}
            }
        }
        try
        {
            Task<IEnumerable<RemoteSubtitleInfo>> native;
            var previous=bypass;bypass=new(instance,actual);
            try{native=(Task<IEnumerable<RemoteSubtitleInfo>>)state.Search.Invoke(instance,new object?[]{actual,options,token})!;}
            catch(TargetInvocationException error) when(error.InnerException!=null){ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
            finally{bypass=previous;}
            return await native.ConfigureAwait(false);
        }
        finally
        {
            if(mapped)lock(Gate)
            {
                Names.Remove(actual);activeNames--;
                if(Volatile.Read(ref current)==null)try{RemoveFilenameIfIdle();}catch{Detail="Native subtitle adapter disabled; filename cleanup pending.";}
            }
        }
    }
}
