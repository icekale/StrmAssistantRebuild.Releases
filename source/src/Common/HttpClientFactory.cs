using System.Net;

namespace StrmAssistantRebuild.Common;

/// <summary>Stable client shared by every plugin-owned HTTP caller, including callers retaining Client.</summary>
public sealed class HttpClientFactory : IDisposable
{
    private readonly SwitchingHandler transport;
    private readonly HttpClient client;
    public HttpClientFactory(HttpMessageHandler? handler = null)
    {
        IsInjected = handler != null;
        transport = new SwitchingHandler(handler ?? CreateHandler(null, true));
        client = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StrmAssistantRebuild/0.1.0");
    }
    public HttpClient Client => client;
    public bool IsInjected { get; }
    public int PendingDrains => transport.PendingDrains;

    /// <summary>Injected handlers keep their original identity and ownership; production swaps only the transport.</summary>
    public void ConfigureProxy(IWebProxy? proxy, bool automaticDecompression = true)
    {
        if (!IsInjected) transport.Replace(CreateHandler(proxy, automaticDecompression));
    }
    private static HttpClientHandler CreateHandler(IWebProxy? proxy, bool decompress) => new()
    {
        AutomaticDecompression = decompress ? DecompressionMethods.GZip | DecompressionMethods.Deflate : DecompressionMethods.None,
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
        // Explicit false ignores process/environment proxies when this plugin's proxy is disabled.
        UseProxy = proxy != null, Proxy = proxy, MaxResponseHeadersLength = 64
    };

    /// <summary>Stop new work, preserving active requests/streaming bodies until their responses are disposed.</summary>
    public void Dispose()
    {
        var drained = transport.Stop();
        if (drained.IsCompleted) client.Dispose();
        else _ = drained.ContinueWith(_ => client.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    /// <summary>Bounded wait, not forced destruction. A false result leaves at most four retired transports owned by their response leases.</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken token = default)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Dispose();
        try { await transport.Drained.WaitAsync(timeout, token).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    private sealed class SwitchingHandler : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly List<Generation> generations = new();
        private readonly TaskCompletionSource<bool> drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Generation current;
        private bool stopped;
        public SwitchingHandler(HttpMessageHandler handler) { current = new Generation(handler); generations.Add(current); }
        public Task Drained => drained.Task;
        public int PendingDrains { get { lock (gate) return generations.Count(g => g.Retired); } }
        public void Replace(HttpMessageHandler handler)
        {
            lock (gate)
            {
                if (stopped) { handler.Dispose(); throw new ObjectDisposedException(nameof(HttpClientFactory)); }
                // ponytail: bounded generations instead of timers that destroy an in-flight response.
                // A fifth busy replacement is rejected; caller retries after disposing its responses.
                if (generations.Count >= 4 && current.Leases > 0)
                { handler.Dispose(); throw new InvalidOperationException("HTTP transports are draining; retry after active responses complete."); }
                current.Retired = true;
                Collect(current);
                current = new Generation(handler);
                generations.Add(current);
            }
        }
        public Task Stop()
        {
            lock (gate)
            {
                stopped = true;
                foreach (var generation in generations.ToArray()) { generation.Retired = true; Collect(generation); }
                if (generations.Count == 0) drained.TrySetResult(true);
                return drained.Task;
            }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Generation generation;
            lock (gate)
            {
                if (stopped) throw new ObjectDisposedException(nameof(HttpClientFactory));
                generation = current;
                generation.Leases++;
            }
            try
            {
                var response = await generation.Invoker.SendAsync(request, token).ConfigureAwait(false);
                // Headers-only completion is NOT completion of a streaming response. Its content owns the lease.
                response.Content = new LeasedContent(response.Content, () => Release(generation));
                return response;
            }
            catch { Release(generation); throw; }
        }
        private void Release(Generation generation)
        {
            lock (gate) { generation.Leases--; Collect(generation); }
        }
        private void Collect(Generation generation)
        {
            if (!generation.Retired || generation.Leases != 0) return;
            generations.Remove(generation);
            generation.Invoker.Dispose();
            if (stopped && generations.Count == 0) drained.TrySetResult(true);
        }
        protected override void Dispose(bool disposing) { if (disposing) Stop(); base.Dispose(disposing); }
        private sealed class Generation
        {
            public readonly HttpMessageInvoker Invoker;
            public int Leases;
            public bool Retired;
            public Generation(HttpMessageHandler handler) => Invoker = new HttpMessageInvoker(handler, true);
        }
    }
    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent inner;
        private Action? release;
        public LeasedContent(HttpContent inner, Action release)
        {
            this.inner = inner; this.release = release;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
        private void Release() => Interlocked.Exchange(ref release, null)?.Invoke();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            try { await inner.CopyToAsync(stream, token).ConfigureAwait(false); }
            finally { Release(); }
        }
        protected override async Task<Stream> CreateContentReadStreamAsync() => new LeasedStream(await inner.ReadAsStreamAsync().ConfigureAwait(false), Release);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => new LeasedStream(await inner.ReadAsStreamAsync(token).ConfigureAwait(false), Release);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { inner.Dispose(); }
                finally { Release(); }
            }
            base.Dispose(disposing);
        }
    }
    private sealed class LeasedStream : Stream
    {
        private readonly Stream inner;
        private Action? release;
        public LeasedStream(Stream inner, Action release) { this.inner = inner; this.release = release; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        private int ReadComplete(int count, int requested) { if (count == 0 && requested > 0) Interlocked.Exchange(ref release, null)?.Invoke(); return count; }
        public override int Read(byte[] buffer, int offset, int count) => ReadComplete(inner.Read(buffer, offset, count), count);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadComplete(await inner.ReadAsync(buffer, offset, count, token).ConfigureAwait(false), count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            ReadComplete(await inner.ReadAsync(buffer, token).ConfigureAwait(false), buffer.Length);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { inner.Dispose(); }
                finally { Interlocked.Exchange(ref release, null)?.Invoke(); }
            }
            base.Dispose(disposing);
        }
    }
}
