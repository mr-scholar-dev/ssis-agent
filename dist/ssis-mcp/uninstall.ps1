<#
.SYNOPSIS
  Unregister the SSIS MCP server from Codex / Claude Code and remove generated config files.
.DESCRIPTION
  Removes the 'ssis' server registration where the CLIs are present, and deletes .\config.
  Does NOT delete the bin folder (delete this whole directory manually to fully remove).
#>
$ErrorActionPreference = 'SilentlyContinue'
$here = $PSScriptRoot
function Has($cmd) { return [bool](Get-Command $cmd -ErrorAction SilentlyContinue) }

Write-Host "== Unregister: Codex ==" -ForegroundColor Cyan
if (Has 'codex') { & codex mcp remove ssis; Write-Host "  removed (if present)" } else { Write-Host "  Codex CLI not found" -ForegroundColor Yellow }

Write-Host "== Unregister: Claude Code ==" -ForegroundColor Cyan
if (Has 'claude') { & claude mcp remove ssis --scope user; Write-Host "  removed (if present)" } else { Write-Host "  Claude CLI not found" -ForegroundColor Yellow }

$cfgDir = Join-Path $here 'config'
if (Test-Path $cfgDir) { Remove-Item -Recurse -Force $cfgDir; Write-Host "Removed $cfgDir" }
Write-Host "Done. Delete this folder to remove the server binaries." -ForegroundColor Green
