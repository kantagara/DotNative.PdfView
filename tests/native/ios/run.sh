#!/bin/sh
set -eu
ROOT=$(CDPATH= cd -- "$(dirname "$0")/../../.." && pwd)
OUT=$(mktemp -d)
mkdir -p "$OUT/PdfSmoke.app"
cp "$ROOT/tests/native/ios/Info.plist" "$OUT/PdfSmoke.app/Info.plist"
xcrun swiftc -target arm64-apple-ios15.0-simulator -sdk "$(xcrun --sdk iphonesimulator --show-sdk-path)" "$ROOT/tests/native/ios/Harness.swift" "$ROOT/Platform/iOS/PdfViewPlugin.swift" -o "$OUT/PdfSmoke.app/PdfSmoke"
codesign --force --sign - "$OUT/PdfSmoke.app"
xcrun simctl install booted "$OUT/PdfSmoke.app"
xcrun simctl terminate booted com.dotnative.pdfsmoke >/dev/null 2>&1 || true
CONTAINER=$(xcrun simctl get_app_container booted com.dotnative.pdfsmoke data)
rm -f "$CONTAINER/Documents/result.txt"
xcrun simctl launch booted com.dotnative.pdfsmoke
for i in 1 2 3 4 5 6 7 8 9 10; do
  if test -f "$CONTAINER/Documents/result.txt"; then
    cat "$CONTAINER/Documents/result.txt"
    grep -q '^PASS' "$CONTAINER/Documents/result.txt"
    exit $?
  fi
  sleep 1
done
exit 1
