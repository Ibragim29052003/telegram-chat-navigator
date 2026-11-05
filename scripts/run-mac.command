#!/bin/zsh
set -euo pipefail
cd "${0:A:h}/.."
dotnet run --project src/TelegramChatNavigator.Desktop -- "$@"
