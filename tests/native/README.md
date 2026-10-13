# Native validation harness

Android harness compiles the production plugin sources against the real OS/Google SDK. Its channel and NativeHost stubs only expose the entry points for instrumentation; it does not replace native rendering or Google consent gates. It is not a test of the complete DotNative engine's view registry/transport.

Requires JDK 17, Android SDK 36 and an emulator (tested API 36.1).

```sh
./tests/native/android/gradlew -p tests/native/android :app:connectedDebugAndroidTest
```

The runner selects the test class explicitly to avoid legacy runner discovery scanning Google SDK classes. Tests use a local generated PDF or reject ad loads before consent. The manifest uses Google's demo app ID in the ads harness. No ad requests, clicks, impressions or real accounts are used.

Run `tests/native/ios/run.sh` on macOS with an arm64 iOS simulator booted. It compiles the real production PDFKit wrapper with test-only channel stubs, presents a generated two-page document, navigates, cancels, checks invalid-page failure and reopens. It does not require a signing team or network. The 2026-10-13 iOS 26.5 / iPhone 17 Pro simulator run passed.

Android tests recreate the PDF fixture at each native-open boundary because the OS may evict cache under low storage. Managed PdfCache redownload-on-miss is tested separately. Navigation assertions wait for native rendering instead of assuming a fixed render time.

2026-10-13 combined Android API 36.1 harness: PDF navigation/cancel/reopen/invalid page plus Ads consent gate passed (2/2). No live ad fill was requested. The split per-plugin harnesses compile the same production sources.
