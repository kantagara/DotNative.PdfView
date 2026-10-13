using DotNative.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNative.PdfView;

public sealed record PdfViewOptions(
    string Title,
    int InitialPage = 0,
    int MaxBytes = 32 * 1024 * 1024,
    int CacheDays = 7,
    int MaxCachedFiles = 20,
    PdfViewLabels? Labels = null
);

public sealed record PdfViewLabels(string Close, string PreviousPage, string NextPage);

public sealed record PdfDownloadProgress(long Bytes, long? TotalBytes);

public interface IPdfViewer
{
    /// <summary>Downloads/cache-validates and presents a real native PDF viewer. Completes on close.
    /// Cancellation closes the viewer. Unsupported platforms throw; this never opens a WebView.</summary>
    Task PresentAsync(
        Uri source,
        PdfViewOptions options,
        IProgress<PdfDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}

public static class PdfViewServices
{
    public static IServiceCollection AddPdfViewer(this IServiceCollection services)
    {
        services.TryAddSingleton<PdfCache>();
        services.TryAddSingleton<IPdfViewer, PdfViewer>();
        return services;
    }
}

public sealed class PdfViewer(IPlatformChannels channels, PresentationTarget target, PdfCache cache)
    : IPdfViewer
{
    private readonly SemaphoreSlim presentation = new(1, 1);

    public async Task PresentAsync(
        Uri source,
        PdfViewOptions options,
        IProgress<PdfDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (target.Platform is not (NativePlatform.Android or NativePlatform.IOS))
            throw new PlatformNotSupportedException(
                "PDF native presentation supports Android and iOS."
            );
        PdfCache.Validate(source, options);
        if (!await presentation.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A PDF viewer is already open.");
        try
        {
            var nativeDirectory = await channels
                .Get("dotnative.pdf-view")
                .InvokeAsync("cacheDirectory", null, cancellationToken);
            if (nativeDirectory is not string directory || !Path.IsPathFullyQualified(directory))
                throw new InvalidDataException("Native PDF cache directory is unavailable.");
            var path = await cache.GetAsync(
                source,
                options,
                progress,
                cancellationToken,
                directory
            );
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await channels
                    .Get("dotnative.pdf-view")
                    .InvokeAsync(
                        "present",
                        new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["title"] = options.Title,
                            ["initialPage"] = options.InitialPage,
                            ["closeLabel"] = options.Labels?.Close ?? "Close",
                            ["previousLabel"] = options.Labels?.PreviousPage ?? "Previous page",
                            ["nextLabel"] = options.Labels?.NextPage ?? "Next page",
                        },
                        cancellationToken
                    );
            }
            catch (PluginException error) when (error.Code == "invalid_pdf")
            {
                File.Delete(path); // Retry must not reopen a corrupt or unsupported cached document.
                throw;
            }
        }
        finally
        {
            presentation.Release();
        }
    }
}
