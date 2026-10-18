#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${ROOT_DIR}/dist/cli"

echo "==================================================="
echo "  Building AgentMesh CLI Cross-Platform Binaries"
echo "==================================================="

echo "[1/2] Publishing Linux x64 single-file binary..."
dotnet publish "${ROOT_DIR}/src/AgentMesh.Cli/AgentMesh.Cli.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "${OUT_DIR}/linux-x64"

echo "[2/2] Publishing Windows x64 single-file binary..."
dotnet publish "${ROOT_DIR}/src/AgentMesh.Cli/AgentMesh.Cli.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "${OUT_DIR}/win-x64"

echo "==================================================="
echo "  Build Complete!"
echo "  Linux binary:   ${OUT_DIR}/linux-x64/agentmesh"
echo "  Windows binary: ${OUT_DIR}/win-x64/agentmesh.exe"
echo "==================================================="
