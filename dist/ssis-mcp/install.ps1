<#
.SYNOPSIS
  Install & register the redistributable SSIS MCP server on THIS machine.

.DESCRIPTION
  Self-contained: resolves everything relative to this folder ($PSScriptRoot). No source tree, no
  .NET SDK, and no absolute paths from the build machine. Steps:
    1. Locate bin\SsisMcp.Server.exe (shipped here).
    2. Run an Environment Probe through the server (environment.detect) and print readiness.
    3. Register the server with Codex and/or Claude Code if their CLIs are present.
    4. Write client config files under .\config (with THIS machine's absolute exe path).
  Requires: Windows x64 + .NET Framework 4.8. SQL Server Integration Services (licensed) is needed
  only for package.execute and ADO.NET; everything else works without it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
  powershell -ExecutionPolicy Bypass -File install.ps1 -LogFile C:\logs\ssis-mcp.log
#>
param([string] $LogFile = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$exe  = Join-Path $here 'bin\SsisMcp.Server.exe'
if (-not (Test-Path $exe)) { throw "Server exe not found: $exe (is the 'bin' folder present?)" }
Write-Host "SSIS MCP server: $exe" -ForegroundColor Green

# --- 1) Environment Probe via the shipped server (proves the exe runs on this PC) ---
# Drive the server over stdio using a temp input file + cmd redirection, so the server's stderr
# logging never trips PowerShell's "native stderr = error" behavior, and paths with spaces are safe.
Write-Host "`n== Environment Probe ==" -ForegroundColor Cyan
$inFile = Join-Path $env:TEMP ("ssismcp-probe-" + [guid]::NewGuid().ToString('N') + ".jsonl")
@(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}'
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"environment.detect","arguments":{}}}'
) | Set-Content -Path $inFile -Encoding utf8
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try {
  $cmdLine = '"{0}" < "{1}" 2>nul' -f $exe, $inFile
  $lines = & cmd /c $cmdLine
  $probe = $null
  foreach ($l in $lines) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    try { $o = $l | ConvertFrom-Json } catch { continue }
    if ($o.id -eq 2 -and $o.result) { $probe = ($o.result.content[0].text | ConvertFrom-Json) }
  }
  if ($probe) {
    $col = if ($probe.coreUsable) { 'Green' } else { 'Yellow' }
    Write-Host ("  coreUsable = {0}" -f $probe.coreUsable) -ForegroundColor $col
    if ($probe.checks) { foreach ($c in $probe.checks) { Write-Host ("  - {0}: {1} {2}" -f $c.name, $c.status, $c.detail) } }
  } else { Write-Host "  (probe returned no environment.detect result)" -ForegroundColor Yellow }
} catch {
  Write-Host ("  Probe failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
} finally {
  $ErrorActionPreference = $prev
  Remove-Item $inFile -ErrorAction SilentlyContinue
}

# --- 2) Client config files (this machine's absolute path; written locally, not committed) ---
$cfgDir = Join-Path $here 'config'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$envObj = @{}; if ($LogFile) { $envObj['SSIS_MCP_LOG'] = $LogFile }

$mcp = [ordered]@{ mcpServers = [ordered]@{ ssis = [ordered]@{ command = $exe; args = @(); env = $envObj } } }
$mcpPath = Join-Path $cfgDir '.mcp.json'
[System.IO.File]::WriteAllText($mcpPath, ($mcp | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

$envToml = ($envObj.GetEnumerator() | ForEach-Object { '"{0}" = "{1}"' -f $_.Key, $_.Value }) -join ', '
$toml = "[mcp_servers.ssis]`ncommand = `"$($exe -replace '\\','\\')`"`nargs = []`nenv = { $envToml }`n"
[System.IO.File]::WriteAllText((Join-Path $cfgDir 'codex-config.toml'), $toml, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "`nWrote client configs to: $cfgDir" -ForegroundColor Green

# --- 3) Register with CLIs if present ---
function Has($cmd) { return [bool](Get-Command $cmd -ErrorAction SilentlyContinue) }

Write-Host "`n== Register: Codex ==" -ForegroundColor Cyan
if (Has 'codex') {
  try { & codex mcp remove ssis 2>$null | Out-Null } catch {}
  & codex mcp add ssis -- "$exe"
  Write-Host "  Registered 'ssis' with Codex. Verify: codex mcp list" -ForegroundColor Green
} else {
  Write-Host "  Codex CLI not found. Paste $cfgDir\codex-config.toml into %USERPROFILE%\.codex\config.toml" -ForegroundColor Yellow
}

Write-Host "`n== Register: Claude Code ==" -ForegroundColor Cyan
if (Has 'claude') {
  try { & claude mcp remove ssis --scope user 2>$null | Out-Null } catch {}
  & claude mcp add ssis --scope user -- "$exe"
  Write-Host "  Registered 'ssis' with Claude Code (user scope). Verify: claude mcp list" -ForegroundColor Green
} else {
  Write-Host "  Claude CLI not found. Use $cfgDir\.mcp.json (project scope) or run:" -ForegroundColor Yellow
  Write-Host ('    claude mcp add ssis --scope user -- "{0}"' -f $exe)
}

Write-Host "`nDone. To remove: powershell -ExecutionPolicy Bypass -File uninstall.ps1" -ForegroundColor Green
