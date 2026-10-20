# Agentmesh Development & CLI Rules

## AgentMesh CLI Rebuild Requirement
Whenever you modify, fix, or refactor any code in `src/AgentMesh.Cli/`:
1. **Always rebuild and publish the CLI binary** to `dist/cli/linux-x64/agentmesh` so changes immediately take effect:
   ```bash
   dotnet publish src/AgentMesh.Cli/AgentMesh.Cli.csproj \
     -c Release \
     -r linux-x64 \
     --self-contained true \
     -p:PublishSingleFile=true \
     -p:IncludeNativeLibrariesForSelfExtract=true \
     -o dist/cli/linux-x64
   ```
2. When performing full releases or cross-platform builds, run `./scripts/build-cli.sh`.
