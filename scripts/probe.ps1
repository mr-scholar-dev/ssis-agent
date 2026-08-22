# Builds and runs the Phase 0 environment probe. Exit code is non-zero on a critical failure.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build "$root\SSIS-Agent-MCP.slnx" -c Debug
    dotnet run --project "$root\src\SsisMcp.EnvProbe" -c Debug --no-build
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
