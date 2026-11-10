using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Emby.Media.Model.DiagnosticsModel;
using MediaBrowser.Common;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;


internal enum SignedFingerprintEvent { Event, ProviderBegin, ProviderComplete, Move, RunnerStart, RunnerExit, Failure, Output }

// Do not format or redact dynamic payloads: paths, stdout/stderr, exceptions and even object.ToString may contain secrets.
// Exact native format matches classify events; already-interpolated/unknown strings are intentionally generic.
internal sealed class SignedFingerprintLogger : ILogger
{
    private readonly ILogger sink;
    private readonly long itemId;
    private readonly Guid run;
    private readonly bool runner;
    private long count;
    private int failed;
    internal bool Failed => Volatile.Read(ref failed) != 0;
    internal SignedFingerprintLogger(ILogger sink, long itemId, Guid run, bool runner)
    { this.sink = sink; this.itemId = itemId; this.run = run; this.runner = runner; }
    internal void Record(SignedFingerprintEvent kind, LogSeverity severity = LogSeverity.Info, int? exitCode = null)
    {
        var sequence = Interlocked.Increment(ref count);
        // Pinned ILogger levels are 0..4. Never forward an arbitrary enum or unknown object to the sink.
        if ((int)severity < 0 || (int)severity > 4) severity = LogSeverity.Warn;
        try
        {
            sink.Log(severity, "[SignedFingerprint] item={0} run={1} stage={2} event={3} sequence={4} exitCode={5}",
                itemId, run, runner ? "Runner" : "Provider", (int)kind, sequence, exitCode.HasValue ? exitCode.Value : "none");
        }
        catch { Interlocked.Exchange(ref failed, 1); } // Sticky operation failure; never expose or retry the raw payload.
    }
    private void Write(LogSeverity severity, string message, object[]? args)
    {
        var kind = message switch {
            "Moving fingerprint file from {0} to {1}" => SignedFingerprintEvent.Move,
            "ProcessRun '{0}' Execute: {1}" or "ProcessRun '{0}' Started." => SignedFingerprintEvent.RunnerStart,
            "ProcessRun '{0}' Process exited with code {1}" or "ProcessRun '{0}' Process exited with code {1} - Failed" => SignedFingerprintEvent.RunnerExit,
            "Error running {0} {1}" or "ProcessRun '{0}' Process error:" or
            "ProcessRun '{0}' Extension error in {1}::{2} - " or
            "ProcessRun '{0}': Error starting {1}. WorkingFolder: {2}" => SignedFingerprintEvent.Failure,
            _ => SignedFingerprintEvent.Event
        };
        Record(kind, severity, kind == SignedFingerprintEvent.RunnerExit && args is { Length: > 1 } && args[1] is int code ? code : null);
    }
    public void Info(string message, params object[] paramList) => Write(LogSeverity.Info, message, paramList);
    public void Debug(string message, params object[] paramList) => Write(LogSeverity.Debug, message, paramList);
    public void Warn(string message, params object[] paramList) => Write(LogSeverity.Warn, message, paramList);
    public void Error(string message, params object[] paramList) => Write(LogSeverity.Error, message, paramList);
    public void Fatal(string message, params object[] paramList) => Write(LogSeverity.Fatal, message, paramList);
    public void ErrorException(string message, Exception exception, params object[] paramList) => Record(SignedFingerprintEvent.Failure, LogSeverity.Error);
    public void FatalException(string message, Exception exception, params object[] paramList) => Record(SignedFingerprintEvent.Failure, LogSeverity.Fatal);
    public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) => Record(SignedFingerprintEvent.Output, severity);
    public void Log(LogSeverity severity, string message, params object[] paramList) => Write(severity, message, paramList);
    public void Log(LogSeverity severity, ReadOnlyMemory<char> message) => Record(SignedFingerprintEvent.Event, severity);
    public void Info(ReadOnlyMemory<char> message) => Record(SignedFingerprintEvent.Event);
    public void Debug(ReadOnlyMemory<char> message) => Record(SignedFingerprintEvent.Event, LogSeverity.Debug);
    public void Warn(ReadOnlyMemory<char> message) => Record(SignedFingerprintEvent.Event, LogSeverity.Warn);
    public void Error(ReadOnlyMemory<char> message) => Record(SignedFingerprintEvent.Event, LogSeverity.Error);
}

// The actual native runner remains the runner. Only its logger differs from the pinned factory's construction.
internal sealed class SignedFingerprintFfmpegManager : IFfmpegManager
{
    private readonly IFfmpegManager original;
    private readonly ConstructorInfo constructor;
    private readonly ILogger logger;
    private readonly IApplicationHost host;
    internal SignedFingerprintFfmpegManager(IFfmpegManager original, ConstructorInfo constructor, ILogger logger, IApplicationHost host)
    { this.original = original; this.constructor = constructor; this.logger = logger; this.host = host; }
    internal static ConstructorInfo? ProbeConstructor()
    {
        try
        {
            var assembly = Assembly.Load("Emby.Server.MediaEncoding");
            if (assembly.GetName().Name != "Emby.Server.MediaEncoding" || assembly.GetName().Version != FingerprintApi.VerifiedVersion ||
                assembly.ManifestModule.ModuleVersionId != new Guid("1a6398de-16b2-4e89-9e0c-2ed7d3859103") || string.IsNullOrEmpty(assembly.Location)) return null;
            using var stream = File.OpenRead(assembly.Location);
            using var hash = SHA256.Create();
            if (Convert.ToHexString(hash.ComputeHash(stream)) != "D8AA6ECF116C09C88E08478B7D3AEBDB0B0E05101A1CB669A23D299665955837") return null;
            var type = assembly.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunnerBase", true)!;
            var runType = assembly.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunType", true)!;
            if (!runType.IsEnum || !Enum.IsDefined(runType, "Ffmpeg") || !typeof(IFfmpegRunner).IsAssignableFrom(type)) return null;
            var ctor = type.GetConstructor(new[] { typeof(string), typeof(string), typeof(ILogger), typeof(IApplicationHost), runType, typeof(bool) });
            return ctor != null && FingerprintApi.VerifyMethod(ctor, "DA3033D12A2BB8AA01F04DCD0D25965FE9E6CB573191D1E33791664DC31DBBA5") ? ctor : null;
        }
        catch { return null; } // A missing runner disables query input only, never the plain/local provider.
    }
    public IFfmpegRunner CreateFfMpegRunner(string name, string logFilePath)
    {
        // Native fingerprint has no per-process file log. Do not open an unprotected ProcessLogWriter surface.
        if (name != "CreateTitleFingerprint" || logFilePath != null) throw new NotSupportedException("Signed fingerprint runner contract changed.");
        return (IFfmpegRunner)constructor.Invoke(new object?[] {
            name, logFilePath, logger, host, Enum.Parse(constructor.GetParameters()[4].ParameterType, "Ffmpeg"), false });
    }
    public IFfmpegConfiguration FfmpegConfiguration => original.FfmpegConfiguration;
    public FfmpegCapabilities FfmpegCapabilities { get => original.FfmpegCapabilities; set => original.FfmpegCapabilities = value; }
    public bool CanEncodeToAudioCodec(ReadOnlySpan<char> codec) => original.CanEncodeToAudioCodec(codec);
    public bool CanEncodeToSubtitleCodec(ReadOnlySpan<char> codec) => original.CanEncodeToSubtitleCodec(codec);
    public bool CanExtractSubtitles(string codec, EncodingContext encodingContext) => original.CanExtractSubtitles(codec, encodingContext);
    public bool SupportsSubtitleConversionTo(MediaSourceInfo mediaSource, MediaStream mediaStream, ReadOnlySpan<char> toCodec, EncodingContext context, SubtitleDeliveryMethod deliveryMethod) =>
        original.SupportsSubtitleConversionTo(mediaSource, mediaStream, toCodec, context, deliveryMethod);
    public bool RequiresSubtitleProcessing(MediaSourceInfo mediaSource, MediaStream mediaStream, EncodingContext context, SubtitleDeliveryMethod deliveryMethod) =>
        original.RequiresSubtitleProcessing(mediaSource, mediaStream, context, deliveryMethod);
}
