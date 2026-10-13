using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DotNative.PdfView;

/// <summary>Public HTTPS PDFs only. No cookies, credentials or authorization headers.
/// A private HTTP handler disables automatic redirects so each target is validated.</summary>
public sealed class PdfCache : IDisposable
{
    private readonly HttpClient http;
    private readonly string directory;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly TimeProvider clock;

    public PdfCache()
        : this(
            Path.Combine(Path.GetTempPath(), "dotnative-pdf-v1"),
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
            TimeProvider.System
        ) { }

    public PdfCache(string directory, HttpMessageHandler handler, TimeProvider? clock = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.clock = clock ?? TimeProvider.System;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public static void Validate(Uri source, PdfViewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateUri(source);
        if (
            options.Labels is { } labels
            && new[] { labels.Close, labels.PreviousPage, labels.NextPage }.Any(label =>
                string.IsNullOrWhiteSpace(label) || label.Length > 100 || label.Any(char.IsControl)
            )
        )
            throw new ArgumentException("Invalid PDF viewer labels.");
        if (
            string.IsNullOrWhiteSpace(options.Title)
            || options.Title.Length > 200
            || options.Title.Any(char.IsControl)
            || options.InitialPage < 0
            || options.MaxBytes is < 1024 or > 128 * 1024 * 1024
            || options.CacheDays is < 0 or > 365
            || options.MaxCachedFiles is < 1 or > 100
        )
            throw new ArgumentException("Invalid PDF viewer options.");
    }

    private static void ValidateUri(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (
            !source.IsAbsoluteUri
            || source.Scheme != "https"
            || source.UserInfo.Length != 0
            || source.IsLoopback
            || source.HostNameType != UriHostNameType.Dns
            || !source.IsDefaultPort
        )
            throw new ArgumentException(
                "PDF sources must be public HTTPS URLs without credentials."
            );
    }

    public async Task<string> GetAsync(
        Uri source,
        PdfViewOptions options,
        IProgress<PdfDownloadProgress>? progress,
        CancellationToken token
    )
    {
        Validate(source, options);
        await serial.WaitAsync(token);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri)))
                    + ".pdf"
            );
            var info = new FileInfo(path);
            if (
                info.Exists
                && info.Length <= options.MaxBytes
                && options.CacheDays > 0
                && clock.GetUtcNow() - info.LastWriteTimeUtc < TimeSpan.FromDays(options.CacheDays)
                && await IsPdfAsync(path, token)
            )
                return path;
            var uri = source;
            HttpResponseMessage? response = null;
            try
            {
                for (var redirects = 0; ; redirects++)
                {
                    ValidateUri(uri);
                    response = await http.GetAsync(
                        uri,
                        HttpCompletionOption.ResponseHeadersRead,
                        token
                    );
                    if (
                        response.StatusCode
                        is not (
                            HttpStatusCode.MovedPermanently
                            or HttpStatusCode.Redirect
                            or HttpStatusCode.SeeOther
                            or HttpStatusCode.TemporaryRedirect
                            or HttpStatusCode.PermanentRedirect
                        )
                    )
                        break;
                    var next = response.Headers.Location;
                    response.Dispose();
                    response = null;
                    if (redirects >= 4 || next is null)
                        throw new HttpRequestException("Invalid PDF redirect chain.");
                    uri = new Uri(uri, next);
                }
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                if (total > options.MaxBytes)
                    throw new InvalidDataException("PDF exceeds the configured byte limit.");
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (
                    var output = new FileStream(
                        temporary,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        32768,
                        true
                    )
                )
                {
                    var buffer = new byte[32768];
                    long count = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) != 0)
                    {
                        count += read;
                        if (count > options.MaxBytes)
                            throw new InvalidDataException(
                                "PDF exceeds the configured byte limit."
                            );
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        progress?.Report(new(count, total));
                    }
                }
                if (!await IsPdfAsync(temporary, token))
                    throw new InvalidDataException("The downloaded document is not a PDF.");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
                temporary = null;
                File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
                foreach (
                    var old in new DirectoryInfo(directory)
                        .GetFiles("*.pdf")
                        .Where(f => f.FullName != path)
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Skip(options.MaxCachedFiles - 1)
                )
                    old.Delete();
                return path;
            }
            finally
            {
                response?.Dispose();
            }
        }
        finally
        {
            if (temporary is not null)
                File.Delete(temporary);
            serial.Release();
        }
    }

    private static async Task<bool> IsPdfAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        var signature = new byte[5];
        return await input.ReadAsync(signature, token) == 5
            && signature.AsSpan().SequenceEqual("%PDF-"u8);
    }

    public void Dispose() => http.Dispose();
}
