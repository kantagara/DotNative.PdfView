using System;
using Microsoft.Extensions.DependencyInjection;

namespace DotNative.PdfView;

public static class PdfViewerServiceProviderExtensions
{
#if NET10_0_OR_GREATER
    extension(IServiceProvider services)
    {
        /// <summary>Resolves the registered plugin using the provider's DI lifetime.</summary>
        public IPdfViewer PdfViewer => services.GetRequiredService<IPdfViewer>();
    }
#else
    /// <summary>Resolves the registered plugin using the provider's DI lifetime.</summary>
    public static IPdfViewer PdfViewer(this IServiceProvider services) =>
        services.GetRequiredService<IPdfViewer>();
#endif
}
