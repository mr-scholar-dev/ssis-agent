# Troubleshooting

## `MSB1009: Project file does not exist. Switch: *.sln`
The .NET 10 SDK creates the new XML solution format `*.slnx`. Use `SSIS-Agent-MCP.slnx`.

## Probe shows OS build `9200`
A net48 exe without a `supportedOS` app manifest gets the Win32 compatibility-shim version.
Fixed by `src/SsisMcp.EnvProbe/app.manifest`.

## `ssis.runtime : FAIL`
`Microsoft.SqlServer.ManagedDTS` is not in the GAC → SSIS is not installed. Install SQL Server
Integration Services (or the matching shared feature). The core cannot run without it.

## `ssis.projects.extension : WARN`
The programmatic SSIS API still works without the VS design-time extension. The extension is only
needed for editing packages inside Visual Studio (VS bridge phase).

## `provider.ace.excel_access : FAIL`
Install the Microsoft Access Database Engine redistributable (ACE OLE DB). Match the **bitness**
of the SSIS host process (x64 here). Mixing x86 ACE with an x64 host is a common failure.

## `sqlserver.connectivity : WARN`
Best-effort connect to the local default instance with integrated security. A warning here is
non-fatal; it only means SQL tools can't be exercised against a local engine on this machine.
