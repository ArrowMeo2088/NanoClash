#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(pwd)"

SLN=NanoClash.slnx
CSPROJ=Src/NanoClash.csproj
CONFIG=Release
STAGEDIR="$ROOT/Build/pub"
OUTDIR="$ROOT/Publish"

detect_rid() {
  case "$(uname -s)" in
    Linux*)  echo linux-x64 ;;
    Darwin*) echo osx-arm64 ;;
    *)
      echo "Unsupported host OS for build.sh (use build.bat on Windows)." >&2
      exit 1
      ;;
  esac
}

RID="$(detect_rid)"
CMD=publish

usage() {
  cat <<EOF
Usage: ./build.sh [publish|build|restore|clean|help] [RID]
  (no args) / publish  - NativeAOT -> Build/pub, sync finals -> Publish/
  build                - restore + managed Release build only
  restore              - restore NuGet packages into Packages/
  clean                - clean Build/ and Publish/ app binaries
  RID                  - optional (default: linux-x64 on Linux, osx-arm64 on macOS)
EOF
}

sync_final() {
  echo "== sync final AOT artifacts -> Publish/ =="
  mkdir -p "$OUTDIR"
  # Drop framework leftovers; keep user config/log/data/README.
  find "$OUTDIR" -maxdepth 1 -type f \( \
      -name '*.dll' -o -name '*.so' -o -name '*.dylib' -o -name 'icon.ico' \
      -o -name 'NanoClash.deps.json' -o -name 'NanoClash.runtimeconfig.json' \
      -o -name 'createdump' -o -name 'createdump.exe' \
    \) -delete 2>/dev/null || true

  if [[ -f "$STAGEDIR/NanoClash.exe" ]]; then
    cp -f "$STAGEDIR/NanoClash.exe" "$OUTDIR/"
  elif [[ -f "$STAGEDIR/NanoClash" ]]; then
    cp -f "$STAGEDIR/NanoClash" "$OUTDIR/"
    chmod +x "$OUTDIR/NanoClash"
  else
    echo "ERROR: NativeAOT binary not found in Build/pub" >&2
    exit 1
  fi

  # Rules.bin.gz / wintun.dll are embedded; runtime extract under ApplicationData/ArrorMeo/NanoClash.
}

try_upx() {
  case "$(uname -s)" in
    Darwin*)
      echo "== upx: skipped on macOS =="
      return 0
      ;;
  esac
  if ! command -v upx >/dev/null 2>&1; then
    echo "== upx: not in PATH, skip =="
    return 0
  fi
  local bin=""
  if [[ -f "$OUTDIR/NanoClash.exe" ]]; then
    bin="$OUTDIR/NanoClash.exe"
  elif [[ -f "$OUTDIR/NanoClash" ]]; then
    bin="$OUTDIR/NanoClash"
  else
    echo "== upx: binary not found, skip =="
    return 0
  fi
  echo "== upx --best --lzma -f =="
  if upx --best --lzma -f -q "$bin"; then
    echo "== upx: ok =="
    return 0
  fi
  echo "== upx: failed, keeping uncompressed binary =="
  return 0
}

if [[ $# -ge 1 ]]; then
  case "$1" in
    publish|build|restore|clean|help)
      CMD=$1
      shift
      ;;
    *-*)
      RID=$1
      shift
      ;;
    *)
      echo "Unknown command: $1" >&2
      usage
      exit 1
      ;;
  esac
fi

if [[ $# -ge 1 ]]; then
  RID=$1
fi

case "$CMD" in
  help)
    usage
    exit 0
    ;;
  restore)
    echo "== restore =="
    dotnet restore "$SLN"
    ;;
  build)
    echo "== restore =="
    dotnet restore "$SLN"
    echo "== build ($CONFIG, managed) =="
    dotnet build "$SLN" -c "$CONFIG" --no-restore
    ;;
  clean)
    echo "== clean =="
    dotnet clean "$SLN" -c "$CONFIG"
    rm -rf Build Src/Publish
    rm -f "$OUTDIR/NanoClash" "$OUTDIR/NanoClash.exe" \
      "$OUTDIR"/*.dll "$OUTDIR"/*.so "$OUTDIR"/*.dylib \
      "$OUTDIR/NanoClash.deps.json" "$OUTDIR/NanoClash.runtimeconfig.json" \
      "$OUTDIR/createdump" "$OUTDIR/createdump.exe" 2>/dev/null || true
    ;;
  publish)
    echo "== restore =="
    dotnet restore "$SLN"
    mkdir -p "$STAGEDIR" "$OUTDIR"
    EXTRA=()
    case "$RID" in
      win-*) EXTRA+=(-p:MewUIBackend=Direct2D) ;;
    esac
    echo "== publish NativeAOT + full Trim ($CONFIG $RID) -> Build/pub/ =="
    dotnet publish "$CSPROJ" -c "$CONFIG" -r "$RID" -o "$STAGEDIR" --self-contained true \
      -p:PublishAot=true \
      -p:TrimMode=full \
      -p:OptimizationPreference=Size \
      -p:IlcFoldIdenticalMethodBodies=true \
      -p:InvariantGlobalization=true \
      -p:StripSymbols=true \
      -p:DebuggerSupport=false \
      -p:StackTraceSupport=false \
      -p:EventSourceSupport=false \
      -p:HttpActivityPropagationSupport=false \
      -p:MetadataUpdaterSupport=false \
      -p:UseSystemResourceKeys=true \
      -p:NullabilityInfoContextSupport=false \
      -p:DebugType=None -p:DebugSymbols=false \
      -p:AppendRuntimeIdentifierToOutputPath=false \
      ${EXTRA[@]+"${EXTRA[@]}"}
    sync_final
    try_upx
    echo "== done: $OUTDIR/NanoClash =="
    ;;
esac
