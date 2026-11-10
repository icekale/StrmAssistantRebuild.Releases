using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Common;

/// <summary>One owned ffprobe process; no shell, host mutation, log payloads or output files.</summary>
public sealed class CustomProbeRunner
{
    public const int MaxStdoutBytes = 16 * 1024 * 1024;
    public const int MaxStderrBytes = 256 * 1024;
    public const int DrainTimeoutMs = 2000;

    public async Task<MediaInfoBundle> ProbeAsync(string executable, MediaSourceInfo resolvedSource,
        bool video, int timeoutMs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        var watch = Stopwatch.StartNew();
        var bytes = await RunProcessAsync(CreateStartInfo(executable, resolvedSource), timeoutMs, token, "probe").ConfigureAwait(false);
        string json;
        try { json = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Custom probe returned invalid UTF-8."); }
        var bundle = Parse(json, resolvedSource, video);
        token.ThrowIfCancellationRequested();
        if (watch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"Custom probe exceeded {timeoutMs} ms.");
        return bundle;
    }

    // Shared only by the owned probe and encoder: same bounded pipes, deadline and tree cleanup.
    internal static async Task<byte[]> RunProcessAsync(ProcessStartInfo start, int timeoutMs, CancellationToken token, string operation)
    {
        token.ThrowIfCancellationRequested();
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        using var process = new Process { StartInfo = start };
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(token);
        abort.CancelAfter(timeoutMs);
        try
        {
            if (!process.Start()) throw new IOException($"Custom {operation} did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            // Native start errors can contain the executable path or arguments. Never retain them.
            throw new IOException($"Custom {operation} start failed; check executable permissions and format.");
        }
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxStdoutBytes, true, abort);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, MaxStderrBytes, false, abort);
        var drains = Task.WhenAll(stdout, stderr);
        try
        {
            await process.WaitForExitAsync(abort.Token).ConfigureAwait(false);
            await drains.WaitAsync(abort.Token).ConfigureAwait(false);
        }
        catch
        {
            // Only the Process instance started above is a kill target. Never kill by executable name,
            // global PID scan, or a PID read from output. The parent must still exist for tree discovery.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (Win32Exception)
            {
                throw new IOException($"Custom {operation} process-tree termination failed.");
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // A misbehaving detached descendant may retain a pipe after its parent has exited.
            // Bound that drain too; stdlib tree kill does not claim containment of detached processes.
            try { await drains.WaitAsync(TimeSpan.FromMilliseconds(DrainTimeoutMs)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                process.StandardOutput.Close();
                process.StandardError.Close();
                try { await drains.ConfigureAwait(false); } catch { }
            }
            catch { /* Observe both reader failures; report only redacted categories below. */ }
            token.ThrowIfCancellationRequested();
            if (stdout.IsFaulted || stderr.IsFaulted)
            {
                if (stdout.Exception?.GetBaseException() is InvalidDataException ||
                    stderr.Exception?.GetBaseException() is InvalidDataException)
                    throw new InvalidDataException($"Custom {operation} output exceeded the stdout 16 MiB or stderr 256 KiB limit.");
                throw new IOException($"Custom {operation} output pipe failed.");
            }
            throw new TimeoutException($"Custom {operation} exceeded {timeoutMs} ms.");
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Custom {operation} exited with code {process.ExitCode}; diagnostic output withheld.");
        if (abort.IsCancellationRequested) throw new TimeoutException($"Custom {operation} exceeded {timeoutMs} ms.");
        return await stdout.ConfigureAwait(false);
    }

    public static MediaInfoBundle Parse(string json, MediaSourceInfo resolvedSource, bool video) =>
        CustomProbeJson.Parse(json, resolvedSource, video);

    internal static ProcessStartInfo CreateStartInfo(string executable, MediaSourceInfo source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var path = string.IsNullOrWhiteSpace(source.ProbePath) ? source.Path : source.ProbePath;
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            throw new ArgumentException("Custom probe requires an already resolved media path.");
        var start = CreateProcessStartInfo(executable, "probe");
        foreach (var arg in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters" })
            start.ArgumentList.Add(arg);
        AddHttpHeaders(start, source, source.ProbeProtocol ?? source.Protocol);
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(path);
        return start;
    }

    internal static ProcessStartInfo CreateProcessStartInfo(string executable, string operation)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException($"Custom {operation} requires an absolute executable file path.");
        // Read/execute is not a write: the configured executable need not live inside Emby data roots.
        if (!File.Exists(executable)) throw new FileNotFoundException($"Custom {operation} executable was not found.");
        var start = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // ffprobe/ffmpeg otherwise honor inherited FFREPORT and can write reports outside the workspace.
        start.Environment.Remove("FFREPORT");
        return start;
    }

    internal static void AddHttpHeaders(ProcessStartInfo start, MediaSourceInfo source, MediaProtocol protocol)
    {
        if (protocol == MediaProtocol.Http && source.RequiredHttpHeaders?.Count > 0)
        {
            var headers = new StringBuilder();
            foreach (var header in source.RequiredHttpHeaders)
            {
                if (string.IsNullOrEmpty(header.Key) || header.Key.Any(c => !IsHeaderToken(c)) ||
                    header.Value == null || header.Value.Any(c => c == '\r' || c == '\n' || c == '\0'))
                    throw new ArgumentException("Resolved HTTP headers contain invalid characters.");
                headers.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            if (headers.Length > 64 * 1024) throw new ArgumentException("Resolved HTTP headers exceed 64 KiB.");
            start.ArgumentList.Add("-headers");
            start.ArgumentList.Add(headers.ToString());
        }
    }

    private static bool IsHeaderToken(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ||
        "!#$%&'*+-.^_`|~".Contains(c);

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, bool retain, CancellationTokenSource abort)
    {
        using var output = retain ? new MemoryStream() : null;
        var buffer = new byte[8192];
        var count = 0;
        var exceeded = false;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                if (exceeded) continue; // Drain after signalling termination, without retaining more bytes.
                count += read;
                if (count > limit) { exceeded = true; abort.Cancel(); continue; }
                if (output != null) output.Write(buffer, 0, read);
            }
            if (exceeded) throw new InvalidDataException("Custom probe output limit exceeded.");
            return output?.ToArray() ?? Array.Empty<byte>();
        }
        catch
        {
            abort.Cancel();
            throw;
        }
    }
}
