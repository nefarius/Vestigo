#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dll="$root/src/Vestigo.Server/bin/Release/net10.0-windows8.0/Vestigo.dll"
out="$root/docs/api"
stub="$out/vestigo.etws.operationresult-1.md"

if [[ ! -f "$dll" ]]; then
  echo "Build Vestigo.Server in Release before generating API docs." >&2
  exit 1
fi

export PATH="${PATH}:${HOME}/.dotnet/tools"
if ! command -v xmldoc2md >/dev/null 2>&1; then
  dotnet tool install -g Nefarius.Tools.XMLDoc2Markdown --version 1.8.0
fi

mkdir -p "$out"
# The generator crashes on OperationResult<T>. Keep that page and regenerate the rest.
cp "$stub" "${stub}.keep"
xmldoc2md "$dll" "$out/" || true
mv "${stub}.keep" "$stub"

if [[ ! -f "$out/index.md" ]]; then
  echo "xmldoc2md did not write docs/api/index.md." >&2
  exit 1
fi
