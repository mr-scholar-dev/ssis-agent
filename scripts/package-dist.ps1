<#
.SYNOPSIS
  Assemble the redistributable MCP package into dist\ssis-mcp\bin.

.DESCRIPTION
  Builds SsisMcp.Server (Release) and copies its full output — the server exe, all managed
  dependencies, and the Microsoft.Data.SqlClient closure that build\AdoNet.SqlClient.targets drops
  into the build folder — into dist\ssis-mcp\bin. The Microsoft.SqlServer.* SSIS assemblies are NOT
  copied: they are resolved from the target machine's GAC (Integration Services install).

  The committed dist\ssis-mcp\ carries only install.ps1 / uninstall.ps1 / README.md; the bin folder
  and any config are produced here and are git-ignored (no binaries, no machine paths, in source).
#>
param([ValidateSet('Release','Debug')] [string] $Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\SsisMcp.Server\SsisMcp.Server.csproj'
$src  = Join-Path $repo "src\SsisMcp.Server\bin\$Configuration\net48"
$dist = Join-Path $repo 'dist\ssis-mcp\bin'

Write-Host "Building $proj ($Configuration)..."
dotnet build $proj -c $Configuration -v q -nologo
if (-not (Test-Path (Join-Path $src 'SsisMcp.Server.exe'))) { throw "build output not found: $src" }

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -Path (Join-Path $src '*') -Destination $dist -Recurse -Force
Get-ChildItem $dist -Filter *.pdb | Remove-Item -Force   # not needed for redistribution

$count = (Get-ChildItem $dist -File).Count
Write-Host "Packaged $count files into $dist" -ForegroundColor Green
Write-Host "Next: copy dist\ssis-mcp to the target PC and run install.ps1 there."
