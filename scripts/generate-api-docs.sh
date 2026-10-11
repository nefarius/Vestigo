#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dll="$root/src/Vestigo.Server/bin/Release/net10.0-windows8.0/Vestigo.dll"
out="$root/docs/api"
stub="$out/vestigo.etws.operationresult-1.md"
level="$out/vestigo.etws.tracelevelselection.md"

if [[ ! -f "$dll" ]]; then
  echo "Build Vestigo.Server in Release before generating API docs." >&2
  exit 1
fi

if [[ ! -f "$stub" ]]; then
  echo "Missing OperationResult<T> page: $stub" >&2
  exit 1
fi

export PATH="${PATH}:${HOME}/.dotnet/tools"
if ! command -v xmldoc2md >/dev/null 2>&1; then
  dotnet tool install -g Nefarius.Tools.XMLDoc2Markdown --version 1.8.0
fi

mkdir -p "$out"
# The generator crashes on OperationResult<T>. Keep that page and regenerate the rest.
cp "$stub" "${stub}.keep"
find "$out" -maxdepth 1 -name '*.md' -delete

log="$(mktemp)"
set +e
xmldoc2md "$dll" "$out/" >"$log" 2>&1
status=$?
set -e
cat "$log"

known_failure=0
if [[ $status -ne 0 ]]; then
  if grep -q "The given key 'T' was not present in the dictionary." "$log" \
    && grep -Eq 'Generation: [0-9]+ succeeded, 1 failed' "$log"; then
    known_failure=1
    echo "xmldoc2md failed only on OperationResult<T>." >&2
  fi
fi
rm -f "$log"
mv "${stub}.keep" "$stub"
if [[ $status -ne 0 && $known_failure -ne 1 ]]; then
  echo "xmldoc2md failed." >&2
  exit 1
fi

if [[ ! -f "$out/index.md" || ! -f "$level" || ! -f "$out/vestigo.etws.capturemanager.md" ]]; then
  echo "xmldoc2md did not write a complete docs/api tree." >&2
  exit 1
fi

if ! grep -q '| None | 0 |' "$level" || ! grep -q '| Verbose | 5 |' "$level"; then
  echo "TraceLevelSelection fields were not generated." >&2
  exit 1
fi
