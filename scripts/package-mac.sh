#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ "$(uname -s)" != Darwin ]]; then
  echo 'Упаковка и ad-hoc подпись выполняются на macOS.' >&2
  exit 1
fi
release_root="$PWD/artifacts"
bundle="$release_root/TelegramChatNavigator.app"
mkdir -p "$release_root"
# Удаляем только результаты предыдущей упаковки, чтобы не оставить старые демофайлы.
rm -rf "$release_root/publish" "$bundle"
dotnet publish src/TelegramChatNavigator.Desktop -c Release -r osx-arm64 --self-contained true -o "$release_root/publish" -p:PublishTrimmed=false -p:NuGetLockFilePath=obj/packages.osx-arm64.lock.json
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp -R "$release_root/publish/." "$bundle/Contents/MacOS/"
cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>TelegramChatNavigator.Desktop</string>
<key>CFBundleIdentifier</key><string>io.github.ibragim29052003.telegramchatnavigator</string>
<key>CFBundleName</key><string>TelegramChatNavigator</string>
<key>CFBundleDisplayName</key><string>Telegram Chat Navigator</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1.0.0</string>
<key>CFBundleShortVersionString</key><string>1.0.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
</dict></plist>
PLIST
chmod +x "$bundle/Contents/MacOS/TelegramChatNavigator.Desktop"
codesign --force --deep --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
ditto -c -k --sequesterRsrc --keepParent "$bundle" "$release_root/TelegramChatNavigator-osx-arm64.zip"
echo "Готово: $bundle"
echo 'Сборка содержит .NET runtime. Подпись ad-hoc; notarization Apple не выполнялась.'
