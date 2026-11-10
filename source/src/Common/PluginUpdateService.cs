using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

public sealed record PluginUpdateResult(string State, string Detail, string Source, Version? Version = null, bool RestartPending = false);
public sealed record PluginCandidateIdentity(string AssemblyName, Version Version, Guid PluginId, string TargetFramework, string Architecture, string Sha256);
/// <summary>About-page GitHubToken/GitHubProxy, read fresh for each operation. Never logged or persisted by this service.</summary>
public sealed record PluginUpdateAccess(string? GitHubToken, string? GitHubProxy);

/// <summary>Updates only this rebuild from its own release feed. No target execution, ZIP extraction, redirect following, or server restart.</summary>
public sealed class PluginUpdateService
{
    /// <summary>M145/F7: the rebuild's own public release feed; original/pro feeds are never candidates.</summary>
    public static readonly Uri DefaultReleaseFeed = new("https://raw.githubusercontent.com/icekale/StrmAssistantRebuild.Releases/main/latest.json");
    private static readonly HashSet<string> GitHubHosts = new(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com", "api.github.com", "github.com" };
    public const string AssemblyName = "StrmAssistantRebuild";
    public const string FileName = AssemblyName + ".dll";
    public static readonly Guid PluginId = new("97d51f81-cd66-4e74-a2f3-e0c3687cf738");
    public const long MaxPackageBytes = 64 * 1024 * 1024;
    private const string Framework = ".NETCoreApp,Version=v6.0";
    private readonly HttpClientFactory http;
    private readonly string pluginsPath;
    private readonly Version currentVersion;
    private readonly Action notifyPendingRestart;
    private readonly ILogger logger;
    private readonly Uri? releaseFeed;
    private readonly Func<PluginUpdateAccess>? access;
    private readonly EmbyPathPolicy paths;
    // ponytail: one operation per service; the Plugin owns exactly one service instance.
    private readonly SemaphoreSlim gate = new(1, 1);
    public PluginUpdateResult LastResult { get; private set; }

    public PluginUpdateService(HttpClientFactory http, string pluginsPath, Version currentVersion,
        Action notifyPendingRestart, ILogger logger, Uri? releaseFeed = null, Func<PluginUpdateAccess>? access = null)
    {
        this.access = access;
        LastResult = releaseFeed == null
            ? new("unavailable", "No rebuild release feed is configured; latest version is unknown.", "none")
            : new("not-checked", "Update not checked yet; latest version is unknown.", "configured-feed");
        this.http = http;
        if (!Path.IsPathFullyQualified(pluginsPath)) throw new ArgumentException("PluginsPath must be an absolute Emby path.", nameof(pluginsPath));
        this.pluginsPath = Path.GetFullPath(pluginsPath);
        paths = new EmbyPathPolicy(this.pluginsPath); // Deliberately narrower than the plugin's system/data roots.
        this.currentVersion = currentVersion;
        this.notifyPendingRestart = notifyPendingRestart;
        this.logger = logger;
        if (releaseFeed != null) RequireHttps(releaseFeed);
        this.releaseFeed = releaseFeed;
    }

    public async Task<PluginUpdateResult> CheckAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (LastResult.RestartPending) return LastResult;
            await FindReleaseAsync(token).ConfigureAwait(false);
            return LastResult;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { SetResult("cancelled", "Update check cancelled.", "feed"); throw; }
        catch (Exception ex) { SetResult("failed", ex.Message, "feed"); throw; }
        finally { gate.Release(); }
    }

    public async Task<PluginUpdateResult> UpdateAsync(IProgress<double> progress, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        string? stage = null;
        try
        {
            if (LastResult.RestartPending) return LastResult;
            progress.Report(0);
            var release = await FindReleaseAsync(token).ConfigureAwait(false);
            if (release == null) { progress.Report(100); return LastResult; }
            RequireInstalled();
            stage = NewStage();
            var (download, downloadUri) = await SendAsync(new Uri(release.DownloadUrl), token).ConfigureAwait(false);
            using var response = download;
            response.EnsureSuccessStatusCode();
            RequireResponseOrigin(response, downloadUri);
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("Package Content-Length differs from the release manifest.");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await CopyStageAsync(source, stage, release.Size, progress, token).ConfigureAwait(false);
            var identity = InspectCandidate(stage, new Version(release.Version), release.Sha256);
            progress.Report(90);
            return Publish(stage, identity, "configured-feed", progress, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { SetResult("cancelled", "Update cancelled before publication; installed DLL unchanged.", "configured-feed"); throw; }
        catch (Exception ex) { SetResult("failed", ex.Message, "configured-feed"); throw; }
        finally { DeleteStage(stage); gate.Release(); }
    }

    /// <summary>Explicit local candidate path, also used by offline synthetic fixtures. Never invoked by the scheduled task.</summary>
    public async Task<PluginUpdateResult> InstallLocalAsync(string candidatePath, Version expectedVersion, string expectedSha256,
        IProgress<double> progress, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        string? stage = null;
        try
        {
            if (LastResult.RestartPending) return LastResult;
            progress.Report(0);
            if (expectedVersion <= currentVersion) throw new InvalidDataException("Candidate version must be newer than the running rebuild.");
            RequireInstalled();
            await using var source = new FileStream(candidatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length is <= 0 or > MaxPackageBytes) throw new InvalidDataException("Package is empty or exceeds the 64 MiB limit.");
            stage = NewStage();
            await CopyStageAsync(source, stage, source.Length, progress, token).ConfigureAwait(false);
            var identity = InspectCandidate(stage, expectedVersion, expectedSha256);
            progress.Report(90);
            return Publish(stage, identity, "explicit-local-candidate", progress, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { SetResult("cancelled", "Local update cancelled before publication; installed DLL unchanged.", "explicit-local-candidate"); throw; }
        catch (Exception ex) { SetResult("failed", ex.Message, "explicit-local-candidate"); throw; }
        finally { DeleteStage(stage); gate.Release(); }
    }

    public async Task<PluginUpdateResult> RollbackAsync(IProgress<double> progress, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        string? stage = null;
        try
        {
            progress.Report(0);
            var destination = OwnedPath(FileName);
            InspectCandidate(destination); // Never replace an unrelated plugin, even on rollback.
            var backup = OwnedPath("." + AssemblyName + ".previous.bak");
            await using (var source = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length is <= 0 or > MaxPackageBytes) throw new InvalidDataException("Backup is empty or oversized.");
                stage = NewStage();
                await CopyStageAsync(source, stage, source.Length, progress, token).ConfigureAwait(false);
            }
            var identity = InspectCandidate(stage);
            progress.Report(95);
            token.ThrowIfCancellationRequested();
            OwnedPath(Path.GetFileName(stage));
            OwnedPath(FileName);
            File.Replace(stage, destination, null); // Backup remains available; rollback itself is an atomic swap.
            return Published("rolled-back", identity, "local-backup", progress);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { SetResult("cancelled", "Rollback cancelled before publication.", "local-backup"); throw; }
        catch (Exception ex) { SetResult("failed", ex.Message, "local-backup"); throw; }
        finally { DeleteStage(stage); gate.Release(); }
    }

    private async Task<ReleaseManifest?> FindReleaseAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (releaseFeed == null)
        {
            SetResult("unavailable", "No rebuild release feed is configured; latest version is unknown. Original/pro packages are not candidates.", "none");
            return null;
        }
        var (feedResponse, feedUri) = await SendAsync(releaseFeed, token).ConfigureAwait(false);
        using var response = feedResponse;
        RequireResponseOrigin(response, feedUri);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotFound)
        {
            SetResult("no-package", "The configured rebuild feed has no package; latest version is unknown.", "configured-feed");
            return null;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 65536) throw new InvalidDataException("Release manifest exceeds 64 KiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (json.Length + read > 65536) throw new InvalidDataException("Release manifest exceeds 64 KiB.");
            json.Write(buffer, 0, read);
        }
        var release = JsonSerializer.Deserialize<ReleaseManifest>(json.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Release manifest is null.");
        if (release.PluginId != PluginId || release.AssemblyName != AssemblyName || release.FileName != FileName ||
            release.TargetFramework != Framework || release.Architecture != "AnyCPU" || release.Channel != "stable")
            throw new InvalidDataException("Release identity/framework/architecture/channel is not this stable net6 AnyCPU rebuild.");
        if (!Version.TryParse(release.Version, out var version) || version.Revision < 0)
            throw new InvalidDataException("Release version must be a four-component numeric assembly version.");
        RequireSha256(release.Sha256);
        if (release.Size is <= 0 or > MaxPackageBytes) throw new InvalidDataException("Declared package size is empty or exceeds 64 MiB.");
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var download)) throw new InvalidDataException("Package URL is not absolute.");
        RequireHttps(download);
        if (download.GetLeftPart(UriPartial.Authority) != releaseFeed.GetLeftPart(UriPartial.Authority) ||
            !download.AbsolutePath.EndsWith("/" + FileName, StringComparison.Ordinal))
            throw new InvalidDataException("Package must be a same-origin rebuild DLL, not a ZIP or original/pro binary.");
        if (version <= currentVersion)
        {
            SetResult("no-update", "The configured feed has no newer stable rebuild package.", "configured-feed", version);
            return null;
        }
        SetResult("update-available", "A configured rebuild candidate is available; its DLL has not yet been verified.", "configured-feed", version);
        return release;
    }

    private async Task CopyStageAsync(Stream source, string stage, long expectedSize, IProgress<double> progress, CancellationToken token)
    {
        await using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long count = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            count += read;
            if (count > MaxPackageBytes || count > expectedSize) throw new InvalidDataException("Package exceeded its declared size or the 64 MiB limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            progress.Report(5 + 75d * count / expectedSize);
        }
        if (count != expectedSize) throw new InvalidDataException("Package is truncated.");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    private PluginUpdateResult Publish(string stage, PluginCandidateIdentity identity, string source, IProgress<double> progress, CancellationToken token)
    {
        RequireInstalled();
        var destination = OwnedPath(FileName);
        var backup = OwnedPath("." + AssemblyName + ".previous.bak");
        progress.Report(95);
        token.ThrowIfCancellationRequested(); // Commit point: no cancellation or awaits after the atomic replacement.
        OwnedPath(Path.GetFileName(stage));
        OwnedPath(FileName);
        OwnedPath(Path.GetFileName(backup));
        File.Replace(stage, destination, backup);
        return Published("installed", identity, source, progress);
    }

    private PluginUpdateResult Published(string state, PluginCandidateIdentity identity, string source, IProgress<double> progress)
    {
        var detail = "Rebuild DLL published atomically; restart pending. The server was not restarted.";
        try { notifyPendingRestart(); }
        catch (Exception ex) { detail += " Host restart notification failed: " + ex.Message; }
        // Publication already succeeded: reporting/notification errors must not pretend that the old DLL remains installed.
        LastResult = new(state, detail, source, identity.Version, true);
        try { logger.Info("[StrmAssistantRebuild] Update {0}: {1}", state, detail); } catch { }
        try { progress.Report(100); } catch { }
        return LastResult;
    }

    private void RequireInstalled()
    {
        if (!Directory.Exists(pluginsPath)) throw new IOException("Emby plugins directory does not exist.");
        var installed = InspectCandidate(OwnedPath(FileName));
        if (installed.Version != currentVersion) throw new InvalidDataException("Installed rebuild differs from the running version; restart or reconcile it before updating.");
    }

    private string NewStage() => OwnedPath("." + AssemblyName + ".update-" + Guid.NewGuid().ToString("N") + ".stage");
    private string OwnedPath(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName) throw new IOException("Update path is not a direct plugins-root file.");
        var full = paths.RequireWritablePath(Path.Combine(pluginsPath, fileName));
        // LinkTarget also catches dangling symlinks that File.Exists deliberately reports as absent.
        for (FileSystemInfo? item = new FileInfo(full); item != null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.LinkTarget != null) throw new IOException("Update path traverses a symbolic link.");
        return full;
    }
    private void DeleteStage(string? stage)
    {
        if (stage == null) return;
        try { File.Delete(OwnedPath(Path.GetFileName(stage))); }
        catch (Exception ex) { try { logger.Warn("[StrmAssistantRebuild] Update staging cleanup failed: {0}", ex.Message); } catch { } }
    }
    private void SetResult(string state, string detail, string source, Version? version = null)
    {
        LastResult = new(state, detail, source, version, LastResult.RestartPending);
        logger.Info("[StrmAssistantRebuild] Update {0}: {1}", state, detail);
    }
    /// <summary>
    /// Original UpdatePluginTask contract: GitHubProxy is prefixed as proxy + "/" + URL; GitHubToken is sent as
    /// "Authorization: token ..." only when no proxy is used (the original removes the header for proxied requests).
    /// The token is additionally restricted to GitHub hosts. Same-origin checks use the unproxied URLs.
    /// </summary>
    private async Task<(HttpResponseMessage Response, Uri Effective)> SendAsync(Uri target, CancellationToken token)
    {
        var (proxy, githubToken) = ReadAccess();
        var effective = proxy == null ? target : new Uri(proxy.AbsoluteUri.TrimEnd('/') + "/" + target.AbsoluteUri);
        var request = new HttpRequestMessage(HttpMethod.Get, effective);
        if (proxy == null && githubToken != null && GitHubHosts.Contains(target.Host))
            request.Headers.TryAddWithoutValidation("Authorization", "token " + githubToken);
        var response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        return (response, effective);
    }
    private (Uri? Proxy, string? Token) ReadAccess()
    {
        var current = access?.Invoke();
        var proxyText = current?.GitHubProxy?.Trim();
        var tokenText = current?.GitHubToken?.Trim();
        Uri? proxy = null;
        if (!string.IsNullOrEmpty(proxyText) && (!Uri.TryCreate(proxyText, UriKind.Absolute, out proxy) || proxy.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(proxy.UserInfo) || !string.IsNullOrEmpty(proxy.Query) || !string.IsNullOrEmpty(proxy.Fragment)))
            throw new InvalidDataException("GitHubProxy must be an absolute HTTPS URL without credentials, query or fragment.");
        if (!string.IsNullOrEmpty(tokenText) && tokenText.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new InvalidDataException("GitHubToken contains whitespace or control characters.");
        return (proxy, string.IsNullOrEmpty(tokenText) ? null : tokenText);
    }
    private static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Update URLs require HTTPS without embedded credentials or fragments.");
    }
    private static void RequireResponseOrigin(HttpResponseMessage response, Uri requested)
    {
        if (response.RequestMessage?.RequestUri is Uri actual && actual != requested)
            throw new InvalidDataException("Update HTTP redirect was not accepted.");
    }
    private static void RequireSha256(string sha256)
    {
        if (string.IsNullOrEmpty(sha256) || sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Expected SHA-256 must contain exactly 64 hexadecimal characters.");
    }

    /// <summary>Offline structural validation. Reads PE/CLI metadata and a constant Id getter, never Assembly.Load/Invoke.</summary>
    public static PluginCandidateIdentity InspectCandidate(string path, Version? expectedVersion = null, string? expectedSha256 = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxPackageBytes) throw new InvalidDataException("Candidate is empty or exceeds 64 MiB.");
        using var hash = SHA256.Create();
        var sha256 = Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
        if (expectedSha256 != null)
        {
            RequireSha256(expectedSha256);
            if (!sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Candidate SHA-256 mismatch.");
        }
        stream.Position = 0;
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        var clr = headers.CorHeader;
        if (!pe.HasMetadata || clr == null || headers.PEHeader?.Magic != PEMagic.PE32 || headers.CoffHeader.Machine != Machine.I386 ||
            (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0 ||
            (clr.Flags & CorFlags.ILOnly) == 0 || (clr.Flags & (CorFlags.Requires32Bit | CorFlags.Prefers32Bit | CorFlags.NativeEntryPoint)) != 0 ||
            clr.EntryPointTokenOrRelativeVirtualAddress != 0 || clr.ManagedNativeHeaderDirectory.Size != 0)
            throw new InvalidDataException("Candidate must be an IL-only AnyCPU PE32 CLR DLL without a native entry point.");
        var metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly || metadata.AssemblyFiles.Count != 0) throw new InvalidDataException("Candidate must be a single manifest assembly without linked files.");
        var assembly = metadata.GetAssemblyDefinition();
        var name = metadata.GetString(assembly.Name);
        if (name != AssemblyName || !assembly.PublicKey.IsNil || !assembly.Culture.IsNil && metadata.GetString(assembly.Culture).Length != 0 ||
            expectedVersion != null && assembly.Version != expectedVersion)
            throw new InvalidDataException("Candidate assembly name/version/culture/key differs from this rebuild.");
        foreach (var resource in metadata.ManifestResources)
            if (!metadata.GetManifestResource(resource).Implementation.IsNil) throw new InvalidDataException("Candidate references an external resource.");
        foreach (var reference in metadata.AssemblyReferences)
        {
            var referenceName = metadata.GetString(metadata.GetAssemblyReference(reference).Name);
            if (!referenceName.StartsWith("System.", StringComparison.Ordinal) && !referenceName.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) &&
                referenceName is not ("mscorlib" or "netstandard" or "System" or "MediaBrowser.Common" or "MediaBrowser.Controller" or "MediaBrowser.Model" or "Emby.Web.GenericEdit" or "Emby.Web.GenericUI" or "Emby.Media.Model"))
                throw new InvalidDataException("Candidate requires an unbundled dependency: " + referenceName);
        }
        var frameworks = new List<string?>();
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (!IsType(metadata, constructor.Parent, "System.Runtime.Versioning", "TargetFrameworkAttribute", "System.Runtime")) continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new InvalidDataException("Invalid TargetFrameworkAttribute.");
            frameworks.Add(blob.ReadSerializedString());
        }
        if (frameworks.Count != 1 || frameworks[0] != Framework) throw new InvalidDataException("Candidate target framework is not net6.0.");
        var plugins = metadata.TypeDefinitions.Where(h =>
        {
            var type = metadata.GetTypeDefinition(h);
            return metadata.GetString(type.Namespace) == AssemblyName && metadata.GetString(type.Name) == "Plugin";
        }).ToArray();
        if (plugins.Length != 1) throw new InvalidDataException("Candidate has no unique rebuild Plugin type.");
        var plugin = metadata.GetTypeDefinition(plugins[0]);
        if ((plugin.Attributes & TypeAttributes.Public) == 0 || (plugin.Attributes & TypeAttributes.Sealed) == 0 ||
            !IsType(metadata, plugin.BaseType, "MediaBrowser.Common.Plugins", "BasePlugin", "MediaBrowser.Common"))
            throw new InvalidDataException("Candidate Plugin is not the public sealed Emby BasePlugin rebuild.");
        var ids = plugin.GetMethods().Where(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "get_Id").ToArray();
        if (ids.Length != 1) throw new InvalidDataException("Candidate has no unique Plugin.Id getter.");
        var getter = metadata.GetMethodDefinition(ids[0]);
        if ((getter.Attributes & (MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName)) !=
            (MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName) || (getter.Attributes & (MethodAttributes.Static | MethodAttributes.NewSlot)) != 0 || getter.RelativeVirtualAddress == 0)
            throw new InvalidDataException("Candidate Plugin.Id is not a public virtual override getter.");
        var signature = metadata.GetBlobReader(getter.Signature);
        if (signature.ReadByte() != 0x20 || signature.ReadCompressedInteger() != 0 || signature.ReadByte() != 0x11 ||
            !IsType(metadata, signature.ReadTypeHandle(), "System", "Guid", "System.Runtime") || signature.RemainingBytes != 0 ||
            !plugin.GetProperties().Any(h => metadata.GetString(metadata.GetPropertyDefinition(h).Name) == "Id" && metadata.GetPropertyDefinition(h).GetAccessors().Getter == ids[0]) ||
            plugin.GetMethodImplementations().Count != 0)
            throw new InvalidDataException("Candidate Id override/property/signature is not statically supported.");
        var body = pe.GetMethodBody(getter.RelativeVirtualAddress);
        var il = body.GetILBytes()!;
        // Deliberately narrow accepted form: ldstr GUID; newobj System.Guid(string); ret. No evaluator or target execution.
        if (body.ExceptionRegions.Length != 0 || il.Length != 11 || il[0] != 0x72 || il[5] != 0x73 || il[10] != 0x2a)
            throw new InvalidDataException("Plugin.Id is not a statically provable constant GUID getter.");
        var stringToken = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(1, 4));
        var constructorToken = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(6, 4));
        if ((stringToken & unchecked((int)0xff000000)) != 0x70000000 || (constructorToken & unchecked((int)0xff000000)) != 0x0a000000)
            throw new InvalidDataException("Plugin.Id getter contains unexpected metadata tokens.");
        var guidCtor = metadata.GetMemberReference(MetadataTokens.MemberReferenceHandle(constructorToken & 0xffffff));
        if (metadata.GetString(guidCtor.Name) != ".ctor" || !IsType(metadata, guidCtor.Parent, "System", "Guid", "System.Runtime") ||
            !metadata.GetBlobBytes(guidCtor.Signature).SequenceEqual(new byte[] { 0x20, 0x01, 0x01, 0x0e }) ||
            !Guid.TryParse(metadata.GetUserString(MetadataTokens.UserStringHandle(stringToken & 0xffffff)), out var id) || id != PluginId)
            throw new InvalidDataException("Candidate Plugin.Id differs from the independent rebuild ID.");
        return new(name, assembly.Version, id, Framework, "AnyCPU", sha256);
    }

    private static bool IsType(MetadataReader metadata, EntityHandle handle, string ns, string name, string assembly)
    {
        if (handle.Kind != HandleKind.TypeReference) return false;
        var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
        return metadata.GetString(type.Namespace) == ns && metadata.GetString(type.Name) == name &&
            type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
            metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == assembly;
    }

    // An explicit future rebuild feed contract; this project does not supply or invent a live endpoint.
    private sealed class ReleaseManifest
    {
        public Guid PluginId { get; set; }
        public string AssemblyName { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Version { get; set; } = "";
        public string TargetFramework { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public string DownloadUrl { get; set; } = "";
    }
}
