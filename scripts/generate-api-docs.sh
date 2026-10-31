#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dll="$root/server/bin/Release/net10.0-windows8.0/Vestigo.dll"
out="$root/docs/api"
level="$out/vestigo.etws.tracelevelselection.md"
result="$out/vestigo.etws.operationresult-1.md"

if [[ ! -f "$dll" ]]; then
  echo "Build Vestigo.Server in Release before generating API docs." >&2
  exit 1
fi

export PATH="${PATH}:${HOME}/.dotnet/tools"
if ! command -v xmldoc2md >/dev/null 2>&1 || ! xmldoc2md -v 2>/dev/null | grep -q '1.10.0'; then
  dotnet tool update -g Nefarius.Tools.XMLDoc2Markdown --version 1.10.0 \
    || dotnet tool install -g Nefarius.Tools.XMLDoc2Markdown --version 1.10.0
fi

mkdir -p "$out"
find "$out" -maxdepth 1 -name '*.md' -delete

xmldoc2md "$dll" "$out/"

if [[ ! -f "$out/index.md" || ! -f "$level" || ! -f "$out/vestigo.etws.capturemanager.md" || ! -f "$result" ]]; then
  echo "xmldoc2md did not write a complete docs/api tree." >&2
  exit 1
fi

if ! grep -q '| None | 0 |' "$level" || ! grep -q '| Verbose | 5 |' "$level"; then
  echo "TraceLevelSelection fields were not generated." >&2
  exit 1
fi

if ! grep -q 'public static OperationResult<T> Ok' "$result"; then
  echo "OperationResult<T> was not generated." >&2
  exit 1
fi
