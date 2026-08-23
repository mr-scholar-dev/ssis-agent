<#
.SYNOPSIS
  Build the SSIS MCP server and generate ready-to-use client configs (Claude Code + Codex).

.DESCRIPTION
  1. Builds src/SsisMcp.Server in the chosen configuration.
  2. Resolves the absolute path of SsisMcp.Server.exe (handles spaces).
  3. Writes .mcp.json (Claude Code, project-scoped) at the repo root.
  4. Writes mcp/codex-config.toml (a snippet to paste into ~/.codex/config.toml).
  5. Prints the exact commands to register the server on THIS or another PC.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\setup-mcp.ps1
  powershell -ExecutionPolicy Bypass -File scripts\setup-mcp.ps1 -Configuration Release -LogFile C:\logs\ssis-mcp.log
#>
param(
  [ValidateSet('Debug','Release')] [string] $Configuration = 'Debug',
  [string] $LogFile = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$serverProj = Join-Path $repo 'src\SsisMcp.Server\SsisMcp.Server.csproj'
$exe = Join-Path $repo "src\SsisMcp.Server\bin\$Configuration\net48\SsisMcp.Server.exe"

Write-Host "Repo:   $repo"
Write-Host "Build:  $serverProj ($Configuration)"
dotnet build $serverProj -c $Configuration -v q -nologo
if (-not (Test-Path $exe)) { throw "Server exe not found after build: $exe" }
Write-Host "Server: $exe" -ForegroundColor Green

# --- env block (optional stderr->file logging) ---
$envBlock = @{}
if ($LogFile) { $envBlock['SSIS_MCP_LOG'] = $LogFile }

# --- Claude Code: .mcp.json (project-scoped) ---
$mcp = [ordered]@{
  mcpServers = [ordered]@{
    ssis = [ordered]@{
      command = $exe
      args    = @()
      env     = $envBlock
    }
  }
}
$mcpPath = Join-Path $repo '.mcp.json'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($mcpPath, ($mcp | ConvertTo-Json -Depth 6), $utf8NoBom)
Write-Host "Wrote Claude Code config: $mcpPath" -ForegroundColor Green

# --- Codex: config.toml snippet ---
$envToml = ($envBlock.GetEnumerator() | ForEach-Object { "`"$($_.Key)`" = `"$($_.Value)`"" }) -join ', '
$toml = @"
# Paste into ~/.codex/config.toml  (Windows: %USERPROFILE%\.codex\config.toml)
[mcp_servers.ssis]
command = "$($exe -replace '\\','\\')"
args = []
env = { $envToml }
"@
$tomlDir = Join-Path $repo 'mcp'
New-Item -ItemType Directory -Force -Path $tomlDir | Out-Null
$tomlPath = Join-Path $tomlDir 'codex-config.toml'
[System.IO.File]::WriteAllText($tomlPath, $toml, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote Codex snippet:      $tomlPath" -ForegroundColor Green

Write-Host ""
Write-Host "== Register in Claude Code ==" -ForegroundColor Cyan
Write-Host "  Project scope: .mcp.json is already at the repo root - open Claude Code in this folder."
Write-Host ('  Or user scope: claude mcp add ssis --scope user -- "{0}"' -f $exe)
Write-Host ""
Write-Host "== Register in Codex ==" -ForegroundColor Cyan
Write-Host ('  codex mcp add ssis -- "{0}"' -f $exe)
Write-Host ('  (or paste {0} into %USERPROFILE%\.codex\config.toml)' -f $tomlPath)
