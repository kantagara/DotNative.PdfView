using System.Buffers.Binary;
using System.Net;
using System.Text;
using DotNative;
using DotNative.PdfView;
using DotNative.Plugins;

var root = Path.Combine(Path.GetTempPath(), "pdf-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var source = new Uri("https://documents.example.com/policy.pdf");
    var options = new PdfViewOptions("Policy", MaxBytes: 1024, MaxCachedFiles: 2);
    foreach (
        var uri in new[]
        {
            "http://example.com/a",
            "file:///etc/passwd",
            "https://u:p@example.com/a",
            "https://localhost/a",
        }
    )
        await Throws<ArgumentException>(() =>
        {
            PdfCache.Validate(new(uri), options);
            return Task.CompletedTask;
        });
    var handler = new HttpStub();
    var clock = new Clock();
    using var cache = new PdfCache(root, handler, clock);
    var path = await cache.GetAsync(source, options, null, default);
    Check(File.Exists(path) && handler.Calls == 1, "download failed");
    var hits = await Task.WhenAll(
        Enumerable.Range(0, 10).Select(_ => cache.GetAsync(source, options, null, default))
    );
    Check(hits.All(x => x == path) && handler.Calls == 1, "cache/concurrent dedupe failed");
    clock.Now = clock.Now.AddDays(8);
    await cache.GetAsync(source, options, null, default);
    Check(handler.Calls == 2, "expired cache not refreshed");
    File.WriteAllText(path, "html");
    await cache.GetAsync(source, options, null, default);
    Check(handler.Calls == 3, "corrupt cache reused");
    handler.Bytes = Encoding.ASCII.GetBytes("not a pdf");
    await Throws<InvalidDataException>(() =>
        cache.GetAsync(new("https://documents.example.com/bad.pdf"), options, null, default)
    );
    handler.Bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 2048));
    handler.Chunked = true;
    await Throws<InvalidDataException>(() =>
        cache.GetAsync(new("https://documents.example.com/large.pdf"), options, null, default)
    );
    Check(Directory.GetFiles(root, "*.partial").Length == 0, "partial file leaked");
    handler.Redirect = new("http://documents.example.com/downgrade.pdf");
    await Throws<ArgumentException>(() =>
        cache.GetAsync(new("https://documents.example.com/redirect.pdf"), options, null, default)
    );
    handler.Redirect = source;
    await Throws<HttpRequestException>(() =>
        cache.GetAsync(new("https://documents.example.com/loop.pdf"), options, null, default)
    );
    handler.Redirect = null;
    handler.Bytes = "%PDF-1.7\nbody"u8.ToArray();
    handler.Chunked = false;
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    await Throws<OperationCanceledException>(() =>
        cache.GetAsync(source, options, null, cancel.Token)
    );
    await cache.GetAsync(new("https://documents.example.com/2.pdf"), options, null, default);
    await cache.GetAsync(new("https://documents.example.com/3.pdf"), options, null, default);
    Check(Directory.GetFiles(root, "*.pdf").Length <= 2, "cache bound ignored");
    var transport = new Transport();
    using var channels = new PlatformChannels(new Dispatcher(), transport);
    transport.Channels = channels;
    var viewer = new PdfViewer(channels, new(NativePlatform.Android), cache);
    using var presentCancel = new CancellationTokenSource();
    var present = viewer.PresentAsync(source, options, cancellationToken: presentCancel.Token);
    await Wait(() => transport.Requests == 1);
    await Throws<InvalidOperationException>(() => viewer.PresentAsync(source, options));
    presentCancel.Cancel();
    await Throws<OperationCanceledException>(() => present);
    Check(transport.Cancels == 1, "native cancellation missing");
    transport.Fail = true;
    await Throws<PluginException>(() => viewer.PresentAsync(source, options));
    Check(!File.Exists(path), "native corrupt PDF retained in cache");
    await Throws<PlatformNotSupportedException>(() =>
        new PdfViewer(channels, new(NativePlatform.Linux), cache).PresentAsync(source, options)
    );
    Console.WriteLine(
        "PASS PDF: validation, download, concurrent cache, expiry, corrupt cache, bounded chunked stream, redirect validation/limit, cancellation, eviction, exclusive native presentation, native failure invalidation, unsupported platform."
    );
}
finally
{
    Directory.Delete(root, true);
}
static void Check(bool ok, string message)
{
    if (!ok)
        throw new Exception(message);
}
static async Task Throws<T>(Func<Task> run)
    where T : Exception
{
    try
    {
        await run();
    }
    catch (T)
    {
        return;
    }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task Wait(Func<bool> condition)
{
    for (var i = 0; i < 1000 && !condition(); i++)
        await Task.Delay(1);
    Check(condition(), "timeout");
}

sealed class Clock : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}

sealed class HttpStub : HttpMessageHandler
{
    public int Calls;
    public byte[] Bytes = "%PDF-1.7\nbody"u8.ToArray();
    public bool Chunked;
    public Uri? Redirect;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        Calls++;
        var response = new HttpResponseMessage(
            Redirect is null ? HttpStatusCode.OK : HttpStatusCode.Redirect
        );
        if (Redirect is not null)
            response.Headers.Location = Redirect;
        response.Content = Chunked ? new Chunked(Bytes) : new ByteArrayContent(Bytes);
        return Task.FromResult(response);
    }
}

sealed class Chunked(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(bytes).AsTask();
}

sealed class Dispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}

sealed class Transport : IPluginTransport
{
    public PlatformChannels Channels = null!;
    public int Requests;
    public int Cancels;
    public bool Fail;

    public void SendPlugin(ReadOnlySpan<byte> bytes)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) == 2)
        {
            Cancels++;
            return;
        }
        Requests++;
        if (!Fail)
            return;
        var payload = PluginCodec.Encode(
            new Dictionary<string, object?>
            {
                ["code"] = "invalid_pdf",
                ["message"] = "Corrupt PDF",
            }
        );
        var reply = new byte[12 + payload.Length];
        bytes[..8].CopyTo(reply);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), 1);
        payload.CopyTo(reply, 12);
        Channels.AcceptReply(reply);
    }
}
