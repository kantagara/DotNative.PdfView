# DotNative.PdfView

Native in-app PDF presentation for DotNative on Android and iOS, without MAUI or a WebView. Android uses the operating system `PdfRenderer`; iOS uses `PDFKit.PDFView`. This is a native viewer adapter, not a new PDF engine.

```csharp
services.AddPdfViewer();
await services.GetRequiredService<IPdfViewer>().PresentAsync(
    new Uri(configuredPublicDocumentUrl), new PdfViewOptions(localizedTitle),
    progress: downloadProgress, cancellationToken: screenLifetime);
```

`services.PdfViewer` is the net10/C#14 accessor; `services.PdfViewer()` is the net9 accessor. `IPdfViewer` works on both targets. `PresentAsync` downloads and presents a full-screen native viewer, and normally completes after closing and releasing the document. Cancellation stops download or requests native dismissal; the channel cancellation may complete before the native UI dismissal animation/cleanup finishes. A second presentation during cleanup returns `busy`, which callers may retry. Unsupported desktop platforms fail explicitly.

The application owns loading/error/retry UI, title, document URL and screen/account cancellation. Download bytes and optional content length are reported through `IProgress<PdfDownloadProgress>`. Native rendering has a loading indicator; parser/render errors fail the presentation task. There is no fake success or external-browser substitution. An external-open fallback is a separate explicit app action.

## Viewer and cache

- All pages are reachable. Android: previous/next buttons or vertical swipe, page count, pinch/pan/double-tap zoom, system Back/close. iOS: continuous vertical PDFKit scrolling, pinch zoom, page count and close.
- Public HTTPS URLs only; no credentials/cookies/auth headers. Every redirect is validated and the chain is bounded. This is not a DNS sandbox; callers must supply trusted public document URLs.
- Streaming download bounded by actual bytes, atomic private cache writes, PDF signature validation, TTL and bounded oldest-file eviction. Defaults: 32 MiB, 7 days, 20 files. Native invalid/corrupt/password-protected documents invalidate their cache entry so retry downloads again.
- Android renders one bounded bitmap per requested page and closes page/renderer/file resources on exit. iOS removes observers and releases PDFDocument on exit. Backgrounding closes the viewer.
- PDF links are inert: Android renders pixels, iOS strips link annotations from the in-memory document. Password entry, search, form editing and annotation editing are not part of this preview.

The native host supplies the app-private cache directory before downloading, so Android does not depend on a desktop-style managed temporary path. `PdfViewOptions.Labels` localizes native Close/Previous/Next labels.

## Native setup

Use the published release source tag in a DotNative dependency manifest. For source ProjectReference consumers, import `buildTransitive/DotNative.PdfView.targets`; NuGet consumers import it automatically. The target registers `PdfViewPlugin`, its Kotlin/Swift sources and the iOS `PDFKit` framework.

Android needs INTERNET permission for download and the DotNative host's platform-channel runtime. No location/storage permission, FileProvider or bundled PDFium `.so` is used. Supported source targets are android-arm64/android-x64/linux-bionic-arm64 and ios-arm64/iossimulator-arm64. iOS uses private temporary storage and HTTPS; no ATS exception is needed. Build with the supported DotNative host toolchain and deployment targets.

## Validation

```sh
dotnet run --project tests -f net10.0 --artifacts-path /tmp/pdf-tests
DOTNET_ROLL_FORWARD=Major dotnet run --project tests -f net9.0 --artifacts-path /tmp/pdf-tests
```

Managed tests cover URL/options, redirects, byte limits, corrupt cache, expiry/eviction, concurrent download, cancellation, exclusive presentation, native error invalidation and unavailable platforms. Native validation results and repeatable harness instructions are in `tests/native/README.md`. Emulator/simulator checks do not establish physical-device or whole-application readiness.
