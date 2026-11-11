using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Emby.Web.GenericEdit.Elements;
using StrmAssistantRebuild.Options;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.Common;

/// <summary>Plugin-owned outbound HTTP only. No claim to replace Emby's already-created HTTP transports.</summary>
public sealed class NetworkService
{
    public const string CheckCommand = "CheckProxyServer";
    public const int ProxyCheckDeadlineMilliseconds = 999;
    public const string ProxyCheckUrl = "http://www.google.com/generate_204";
    public const string ServerProxyGap = "Plugin-owned HTTP only; Emby-wide outgoing proxy has no verified public configuration hook.";
    public const string LocalDiscoveryGap = "LocalDiscoveryUrl is not applied: this host exposes IP-only LocalNetworkAddresses, not a public discovery URL setting.";
    public const string HttpsProxyGap = ".NET 6/7 HttpClientHandler does not support TLS-to-proxy (https:// proxy endpoints); HTTP CONNECT to HTTPS destinations remains supported.";
    public string NativeRoutingDetail { get; set; } = ServerProxyGap;
    public string DiscoveryDetail { get; set; } = LocalDiscoveryGap;
    private readonly HttpClientFactory outbound;
    private readonly Func<string, Task<IPAddress[]>>? resolve;
    private readonly Func<HttpMessageHandler>? probeHandler;
    private readonly object gate = new();
    private (bool Enabled, string Url, NetworkOptions.ProxyModeOption Mode, bool Decompress, bool Passed)? applied;
    private long revision;
    private string resultText = "Proxy check has not run in this session.";
    private ItemStatus resultStatus = ItemStatus.Unknown;
    private string? checkedUrl;
    private bool checkSucceeded;
    private bool applicationPending;

    public NetworkService(HttpClientFactory outbound, Func<string, Task<IPAddress[]>>? resolver = null,
        Func<HttpMessageHandler>? probeHandlerFactory = null)
    { this.outbound = outbound; resolve = resolver; probeHandler = probeHandlerFactory; }

    /// <summary>No outbound self-test on loading/saving options. LastProxyCheckSucceeded gates activation, as in v57.</summary>
    public void Configure(NetworkOptions options)
    {
        lock (gate)
        {
            var url = (options.ProxyServerUrl ?? "").Trim();
            var state = (options.EnableProxyServer, url, options.ProxyMode, options.EnableAutomaticDecompression, options.LastProxyCheckSucceeded);
            if (applied == state)
            {
                if (applicationPending)
                { applicationPending = false; resultStatus = ItemStatus.Unknown; resultText = "Previously applied network configuration remains active."; }
                return;
            }
            var changed = applied.HasValue && (applied.Value.Url != url || applied.Value.Enabled != options.EnableProxyServer) &&
                !(checkedUrl == url && options.EnableProxyServer);
            var passed = options.LastProxyCheckSucceeded && !changed;
            var nextStatus = changed || applicationPending ? ItemStatus.Unknown : resultStatus;
            var nextText = changed ? "Proxy settings changed; run the Google check again." :
                applicationPending ? "Requested network settings are now applied." : resultText;
            ProxyRouting? proxy = null;
            if (options.EnableProxyServer && url.Length > 0)
            {
                if (!ProxyRouting.TryCreate(url, options.ProxyMode, out var parsed, resolve))
                {
                    passed = false; nextStatus = ItemStatus.Unavailable;
                    nextText = "Invalid HTTP(S) proxy endpoint or proxy mode.";
                }
                else if (!parsed!.TransportSupported)
                { passed = false; nextStatus = ItemStatus.Unavailable; nextText = HttpsProxyGap; }
                else if (passed) proxy = parsed;
            }
            else passed = false;
            try { outbound.ConfigureProxy(proxy, options.EnableAutomaticDecompression); }
            catch (InvalidOperationException)
            {
                applicationPending = true;
                resultStatus = ItemStatus.Warning;
                resultText = "Requested network settings are NOT applied: previous transport remains active while responses drain. Retry after completing/disposal of active responses.";
                Prepare(options);
                throw;
            }
            applicationPending = false;
            if (changed) { checkSucceeded = false; checkedUrl = null; }
            options.LastProxyCheckSucceeded = passed;
            resultStatus = nextStatus; resultText = nextText;
            applied = (options.EnableProxyServer, url, options.ProxyMode, options.EnableAutomaticDecompression, passed);
            revision++;
        }
    }

    /// <summary>Populate the existing exact Options/StatusItem, without inventing a new button/route or contacting Google.</summary>
    public void Prepare(NetworkOptions options)
    {
        lock (gate)
        {
            if (checkedUrl == (options.ProxyServerUrl ?? "").Trim()) options.LastProxyCheckSucceeded = checkSucceeded;
            options.ShowProxyServerStatus = options.EnableProxyServer || !string.IsNullOrWhiteSpace(options.LocalDiscoveryUrl);
            options.ProxyServerStatus ??= new StatusItem();
            options.ProxyServerStatus.Status = resultStatus;
            options.ProxyServerStatus.Caption = resultStatus == ItemStatus.Succeeded ? Resources.ProxyServer_Available :
                resultStatus == ItemStatus.Unavailable ? Resources.ProxyServer_Unavailable : UiLanguage.Text("Network · plugin HTTP", "网络 · 插件 HTTP", "網路 · 外掛 HTTP");
            // Chinese UI shows a translated summary; the technical routing/discovery details remain in Runtime diagnostics.
            var discovery = !string.IsNullOrWhiteSpace(options.LocalDiscoveryUrl);
            options.ProxyServerStatus.StatusText = UiLanguage.IsChinese
                ? ChineseResult(resultText) + ChineseRoutingNote(NativeRoutingDetail) + (discovery ? ChineseDiscoveryNote(DiscoveryDetail) : "")
                : resultText + " " + NativeRoutingDetail + (discovery ? " " + DiscoveryDetail : "");
        }
    }

    // Known limitations stay visible in Chinese too; other technical detail is in Runtime diagnostics.
    private static string ChineseRoutingNote(string detail) => detail.Contains(ServerProxyGap, StringComparison.Ordinal)
        ? UiLanguage.Text("", " 仅对插件自身的 HTTP 请求生效；Emby 全局出站代理没有可用的公开设置入口。", " 僅對外掛自身的 HTTP 請求生效；Emby 全域出站代理沒有可用的公開設定入口。")
        : detail.StartsWith("Inactive", StringComparison.Ordinal) ? UiLanguage.Text("", " Emby 原生请求的代理路由未生效，详见运行时能力详情。", " Emby 原生請求的代理路由未生效，詳見執行期能力詳情。") : "";
    private static string ChineseDiscoveryNote(string detail) => detail.Contains(LocalDiscoveryGap, StringComparison.Ordinal)
        ? UiLanguage.Text("", " 本地发现地址未生效：当前 Emby 只公开 IP 形式的本地地址，没有发现地址设置入口。", " 本機探索位址未生效：目前 Emby 只公開 IP 形式的本機位址，沒有探索位址設定入口。")
        : detail.StartsWith("Inactive", StringComparison.Ordinal) ? UiLanguage.Text("", " 本地发现地址未生效，详见运行时能力详情。", " 本機探索位址未生效，詳見執行期能力詳情。") : "";

    private static string ChineseResult(string text)
    {
        string T(string hans, string hant) => UiLanguage.Text(text, hans, hant);
        if (text.StartsWith("Google 204: ", StringComparison.Ordinal)) return T("Google 测试通过：", "Google 測試通過：") + text["Google 204: ".Length..];
        return text switch
        {
            "Proxy check has not run in this session." => T("本次运行尚未测试代理。", "本次執行尚未測試代理。"),
            "Previously applied network configuration remains active." => T("沿用之前已生效的网络设置。", "沿用之前已生效的網路設定。"),
            "Proxy settings changed; run the Google check again." => T("代理设置已更改，请重新测试。", "代理設定已變更，請重新測試。"),
            "Requested network settings are now applied." => T("新的网络设置已生效。", "新的網路設定已生效。"),
            "Invalid HTTP(S) proxy endpoint or proxy mode." => T("代理地址或代理模式无效。", "代理位址或代理模式無效。"),
            HttpsProxyGap => T("当前 .NET 不支持 https:// 代理地址，请改用 http:// 代理。", "目前 .NET 不支援 https:// 代理位址，請改用 http:// 代理。"),
            "Checking Google via the selected proxy…" => T("正在通过代理测试 Google…", "正在透過代理測試 Google…"),
            "Proxy is disabled or its HTTP(S) endpoint is invalid." => T("代理未启用或地址无效。", "代理未啟用或位址無效。"),
            "Proxy check cancelled." => T("代理测试已取消。", "代理測試已取消。"),
            "Google check exceeded the 999 ms deadline." => T("Google 测试超过 999 毫秒。", "Google 測試超過 999 毫秒。"),
            _ when text.StartsWith("Requested network settings are NOT applied", StringComparison.Ordinal) =>
                T("新的网络设置暂未生效：仍有请求在使用旧连接，请稍后再试。", "新的網路設定暫未生效：仍有請求在使用舊連線，請稍後再試。"),
            _ when text.StartsWith("Google proxy check failed", StringComparison.Ordinal) || text.StartsWith("Google check did not return", StringComparison.Ordinal) =>
                T("Google 测试失败。", "Google 測試失敗。"),
            _ => text
        };
    }

    /// <summary>User-command-only Google 204 check, always through the selected proxy (not the scraping whitelist).</summary>
    public async Task CheckProxyAsync(NetworkOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var url = (options.ProxyServerUrl ?? "").Trim();
        long captured;
        lock (gate) { captured = ++revision; resultStatus = ItemStatus.InProgress; resultText = "Checking Google via the selected proxy…"; }
        Prepare(options);
        bool success = false;
        var text = "Proxy is disabled or its HTTP(S) endpoint is invalid.";
        var cancelled = false;
        try
        {
            if (options.EnableProxyServer && ProxyRouting.TryCreate(url, options.ProxyMode, out var routing, resolve))
            {
                if (!routing!.TransportSupported) throw new NotSupportedException(HttpsProxyGap);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(ProxyCheckDeadlineMilliseconds);
                using var probe = new HttpClientFactory(probeHandler?.Invoke());
                // Force this single known endpoint through the proxy. It is deliberately not part of the scraping whitelist.
                probe.ConfigureProxy(new WebProxy(routing!.Address) { Credentials = routing.Credentials }, false);
                var watch = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get, ProxyCheckUrl);
                using var response = await probe.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength > 4096) throw new InvalidDataException("Proxy check response exceeds limit.");
                // A malicious/non-204 body is still bounded; successful 204 is expected to have an empty body.
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var buffer = new byte[1024];
                int bytes = 0, count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) != 0)
                    if ((bytes += count) > 4096) throw new InvalidDataException("Proxy check response exceeds limit.");
                token.ThrowIfCancellationRequested();
                success = response.StatusCode == HttpStatusCode.NoContent && bytes == 0 && watch.Elapsed.TotalMilliseconds < ProxyCheckDeadlineMilliseconds;
                text = success ? "Google 204: " + watch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms (<999 ms)." : "Google check did not return an empty 204 within 999 ms.";
            }
        }
        catch (OperationCanceledException) { cancelled = token.IsCancellationRequested; text = cancelled ? "Proxy check cancelled." : "Google check exceeded the 999 ms deadline."; }
        catch (NotSupportedException) { text = HttpsProxyGap; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or ArgumentException)
        { text = "Google proxy check failed (" + ex.GetType().Name + ")."; } // Never echo credentials or exception URLs.
        lock (gate)
        {
            if (captured == revision && url == (options.ProxyServerUrl ?? "").Trim())
            {
                checkedUrl = url; checkSucceeded = success;
                options.LastProxyCheckSucceeded = success;
                resultStatus = success ? ItemStatus.Succeeded : ItemStatus.Unavailable;
                resultText = text;
                // A successful explicit check can activate the endpoint immediately; no handler disposal mid-request.
                Configure(options);
            }
        }
        Prepare(options);
        if (cancelled) token.ThrowIfCancellationRequested();
    }
}

/// <summary>Reconstruction whitelist is sourced from actual module URLs, not decrypted original constants.</summary>
public sealed class ProxyRouting : IWebProxy
{
    // Exact module API origins + image.tmdb.org from the shipped resource; doubanio's suffix
    // is already accepted by DoubanSubjectParser.ImageUrl. This is not the original encrypted list.
    public static IReadOnlyList<string> ExactHosts { get; } = Array.AsReadOnly(new[]
    { "api.themoviedb.org", "api.tmdb.org", "image.tmdb.org", "movie.douban.com", "img.douban.com", "api.theintrodb.org", "api.introdb.app" });
    private readonly NetworkOptions.ProxyModeOption mode;
    private readonly Func<string, Task<IPAddress[]>> resolver;
    private readonly object dnsGate = new();
    private readonly Dictionary<string, (long Expires, Task<bool> Public)> dns = new(StringComparer.OrdinalIgnoreCase);
    public Uri Address { get; }
    // HTTPS proxy endpoints were added to the standard handler in .NET 8; target framework remains net6.
    public bool TransportSupported => Address.Scheme == "http" || Environment.Version.Major >= 8;
    public ICredentials? Credentials { get; set; }
    private ProxyRouting(Uri address, NetworkOptions.ProxyModeOption mode, ICredentials? credentials, Func<string, Task<IPAddress[]>>? resolver)
    { Address = address; this.mode = mode; Credentials = credentials; this.resolver = resolver ?? Dns.GetHostAddressesAsync; }

    public static bool TryCreate(string? text, NetworkOptions.ProxyModeOption mode, out ProxyRouting? proxy,
        Func<string, Task<IPAddress[]>>? resolver = null)
    {
        proxy = null;
        if (mode is not (NetworkOptions.ProxyModeOption.Global or NetworkOptions.ProxyModeOption.Whitelist) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0 || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        var address = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        CredentialCache? credentials = null;
        if (uri.UserInfo.Length > 0)
        {
            var pair = uri.UserInfo.Split(':', 2);
            var user = Uri.UnescapeDataString(pair[0]);
            var password = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "";
            if (user.Length == 0 || user.Any(char.IsControl) || password.Any(char.IsControl)) return false;
            credentials = new CredentialCache();
            credentials.Add(address, "Basic", new NetworkCredential(user, password));
            credentials.Add(address, "Digest", new NetworkCredential(user, password));
        }
        proxy = new ProxyRouting(address, mode, credentials, resolver);
        return true;
    }
    public Uri GetProxy(Uri destination) => IsBypassed(destination) ? destination : Address;
    public bool IsBypassed(Uri destination)
    {
        if (!destination.IsAbsoluteUri || destination.Scheme is not ("http" or "https")) return true;
        var host = destination.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out var literal)) return mode == NetworkOptions.ProxyModeOption.Whitelist || IsPrivateAddress(literal);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !host.Contains('.')) return true;
        if (mode == NetworkOptions.ProxyModeOption.Whitelist && !IsWhitelisted(host)) return true;
        Task<bool> task;
        lock (dnsGate)
        {
            var now = Environment.TickCount64;
            if (dns.TryGetValue(host, out var cached) && (cached.Expires > now || !cached.Public.IsCompleted)) task = cached.Public;
            else
            {
                foreach (var stale in dns.Where(p => p.Value.Expires <= now && p.Value.Public.IsCompleted).Select(p => p.Key).ToArray()) dns.Remove(stale);
                if (dns.Count >= 128) return true; // bounded single-flight DNS; never queue unbounded unresolved lookups.
                task = ResolvePublic(host);
                dns[host] = (now + 30_000, task);
            }
        }
        // IWebProxy is synchronous. Bound its wait; timeout/failure/mixed-private results bypass, never assume public.
        try { return !task.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { return true; }
    }
    private async Task<bool> ResolvePublic(string host)
    {
        try { var addresses = await resolver(host).ConfigureAwait(false); return addresses.Length > 0 && addresses.All(a => !IsPrivateAddress(a)); }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException) { return false; }
    }
    public static bool IsWhitelisted(string host)
    {
        host = host.TrimEnd('.');
        return ExactHosts.Contains(host, StringComparer.OrdinalIgnoreCase) || host.EndsWith(".doubanio.com", StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is 0 or 10 or 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 198 && b[1] is 18 or 19 || b[0] >= 224;
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return true;
        if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
            (b[0] & 0xfe) == 0xfc || address.ScopeId != 0) return true;
        // IPv4-compatible, NAT64, Teredo and other non-global-unicast ranges remain direct.
        if ((b[0] & 0xe0) != 0x20 || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return true;
        return b[0] == 0x20 && b[1] == 0x02 && IsPrivateAddress(new IPAddress(new byte[] { b[2], b[3], b[4], b[5] }));
    }
}
