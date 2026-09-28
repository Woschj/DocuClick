#!/bin/zsh
# Builds a signed DocuClick.app (Apple Silicon) and zips it for release.
#
#   macos/packaging/build-app.sh [version]        e.g. 0.1.0 (default: from DocuClick.Mac.csproj)
#
# Signing uses the self-signed "DocuClick Signing" identity (see
# create-signing-identity.sh) — no paid Apple Developer ID, no notarization.
# In CI, set MAC_SIGN_KEYCHAIN / MAC_SIGN_KEYCHAIN_PASSWORD to a keychain the
# identity was imported into. Output: dist/DocuClick.app + dist/DocuClick-macos-arm64.zip
set -euo pipefail
cd "${0:A:h}/../.."
ROOT="$PWD"

# The macOS app has its own version: <Version> in DocuClick.Mac.csproj.
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' macos/DocuClick.Mac/DocuClick.Mac.csproj)}"
BUILD="$(git rev-list --count HEAD 2>/dev/null || echo 1)"
IDENTITY="DocuClick Signing"
KEYCHAIN="${MAC_SIGN_KEYCHAIN:-$HOME/.docuclick-signing/docuclick-signing.keychain-db}"
KEYCHAIN_PASSWORD="${MAC_SIGN_KEYCHAIN_PASSWORD:-$(cat "$HOME/.docuclick-signing/p12-password.txt" 2>/dev/null || true)}"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

DIST="$ROOT/dist"
APP="$DIST/DocuClick.app"
PUBLISH="$ROOT/macos/DocuClick.Mac/bin/publish-osx-arm64"

echo "→ DocuClick $VERSION (Build $BUILD)"
rm -rf "$APP" "$PUBLISH"
mkdir -p "$DIST"

echo "→ dotnet publish (baut auch die Swift-Bibliothek)"
"$DOTNET" publish macos/DocuClick.Mac/DocuClick.Mac.csproj -c Release -r osx-arm64 --self-contained true \
  -p:Version="$VERSION" -p:DebugType=none -o "$PUBLISH" --nologo -v quiet

echo "→ App-Bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD/" macos/packaging/Info.plist > "$APP/Contents/Info.plist"
print -n "APPL????" > "$APP/Contents/PkgInfo"

echo "→ Icon"
ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
sips -s format png macos/packaging/AppIcon.ico --out "$ICONSET/base.png" >/dev/null
for size in 16 32 128 256; do
  sips -z $size $size "$ICONSET/base.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$ICONSET/base.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
rm "$ICONSET/base.png"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

echo "→ Signieren mit \"$IDENTITY\""
if [[ -n "$KEYCHAIN_PASSWORD" ]]; then
  security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
fi
# Inside-out: every file in Contents/MacOS first (the .NET runtime keeps its
# managed .dlls and .json files next to the executable, and codesign treats
# all of them as nested code that must be signed), then the bundle.
find "$APP/Contents/MacOS" -type f ! -name DocuClick -print0 |
  xargs -0 codesign --force --keychain "$KEYCHAIN" --sign "$IDENTITY" --timestamp=none 2>&1 | grep -v "replacing existing signature" || true
codesign --force --keychain "$KEYCHAIN" --sign "$IDENTITY" --identifier de.klann.docuclick --timestamp=none "$APP"
codesign --verify --strict --verbose=1 "$APP"
codesign -d -r- "$APP" 2>&1 | grep designated

echo "→ ZIP"
rm -f "$DIST/DocuClick-macos-arm64.zip"
ditto -c -k --keepParent "$APP" "$DIST/DocuClick-macos-arm64.zip"
echo "✓ $APP"
echo "✓ $DIST/DocuClick-macos-arm64.zip"
