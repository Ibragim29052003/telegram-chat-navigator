#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ "$(uname -s)" != Darwin ]]; then
  echo 'Упаковка и ad-hoc подпись выполняются на macOS.' >&2
  exit 1
fi
release_root="$PWD/artifacts"
mkdir -p "$release_root"
# Временная папка вне iCloud не получает посторонние метаданные во время подписи.
staging_root="$(mktemp -d "${TMPDIR:-/private/tmp}/telegram-navigator-package.XXXXXX")"
trap 'rm -rf "$staging_root"' EXIT
bundle="$staging_root/TelegramChatNavigator.app"
dotnet publish src/TelegramChatNavigator.Desktop -c Release -r osx-arm64 --self-contained true -o "$staging_root/publish" -p:PublishTrimmed=false -p:NuGetLockFilePath=obj/packages.osx-arm64.lock.json
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp -R "$staging_root/publish/." "$bundle/Contents/MacOS/"
cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>TelegramChatNavigator.Desktop</string>
<key>CFBundleIdentifier</key><string>io.github.ibragim29052003.telegramchatnavigator</string>
<key>CFBundleName</key><string>TelegramChatNavigator</string>
<key>CFBundleDisplayName</key><string>Telegram Chat Navigator</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1.1.1</string>
<key>CFBundleShortVersionString</key><string>1.1.1</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
</dict></plist>
PLIST
chmod +x "$bundle/Contents/MacOS/TelegramChatNavigator.Desktop"
# Метаданные Finder не относятся к приложению и мешают строгой проверке подписи.
for attribute in com.apple.FinderInfo com.apple.ResourceFork; do
  /usr/bin/xattr -dr "$attribute" "$bundle" 2>/dev/null || true
done
codesign --force --deep --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
# Сохраняем атрибуты подписи DLL; отбрасывать все xattr при архивации нельзя.
ditto -c -k --sequesterRsrc --keepParent "$bundle" "$release_root/TelegramChatNavigator-osx-arm64.zip"
ditto -x -k "$release_root/TelegramChatNavigator-osx-arm64.zip" "$staging_root/check"
codesign --verify --deep --strict "$staging_root/check/TelegramChatNavigator.app"
# Заменяем только результаты предыдущей упаковки, сохраняя исходники проекта.
rm -rf "$release_root/publish" "$release_root/TelegramChatNavigator.app"
ditto "$bundle" "$release_root/TelegramChatNavigator.app"
echo "Готово: $release_root/TelegramChatNavigator.app"
echo 'Сборка содержит .NET runtime. Подпись ad-hoc; notarization Apple не выполнялась.'
