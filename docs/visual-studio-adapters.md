# Visual Studio adapters — official VS 2022 + VS 2026 support

**Both Visual Studio 2022 and Visual Studio 2026 are first-class, officially supported from the
initial design.** VS 2022 is *not* treated as future/best-effort compatibility.

> Status: **design contracts locked** (`SsisMcp.Core/Ide/VisualStudioContracts.cs`).
> Concrete detection + adapters + fixtures are scheduled after the Data Flow builder, per the
> agreed implementation order (Safety → read-only inspection → Control Flow → Data Flow → VS adapters).

## Two independent responsibilities

| Interface | Concern | Namespace |
|---|---|---|
| `IVisualStudioAdapter` | Visual Studio IDE compatibility (open project, show flows, build, bridge/VSIX) | `SsisMcp.Core.Ide` |
| `ISsisVersionAdapter` | SSIS runtime generation & `TargetServerVersion` (2016…2025) | `SsisMcp.Core.Versioning` |

These are **deliberately separate**. A VS version being installed does **not** imply any particular
SSIS target version is supported — the runtime must be verified independently (see
[ssis-versioning.md](ssis-versioning.md)).

## Detection contract (`IVisualStudioLocator.DetectAll`)

Must enumerate **every** installed instance (via `vswhere -all -prerelease -products *`) and report,
per instance:

- Visual Studio version (e.g. `17.14.x` → VS2022, `18.x` → VS2026)
- edition (Community/Professional/Enterprise)
- installation path
- SSIS Projects extension installed? + its version
- available capabilities
- can open `.dtproj`?
- bridge/VSIX compatibility

**Never hard-code VS 2026 just because it is the active or newest instance.** The mapping from
version → generation is explicit and data-driven.

## Selection (`Select(TargetIde)`)

- `targetIde = vs2022` → resolve a VS 2022 (v17) instance, or null if none.
- `targetIde = vs2026` → resolve a VS 2026 (v18) instance, or null if none.
- `targetIde = auto` → documented policy: prefer an instance that **can open `.dtproj`** (SSIS
  Projects extension present) and is bridge-compatible; among those, prefer the newest generation;
  fall back to the newest instance otherwise. Ties and absence are reported explicitly, never guessed.

## Core independence

The SSIS MCP core runs with **no Visual Studio installed and none open**. All VS-specific behavior
sits behind `IVisualStudioAdapter` / `IVisualStudioLocator`. The bridge (Fase 23) is a thin VSIX per
generation; it contains no SSIS logic.

## Planned tests & fixtures (VS adapter phase)

- Fixtures describing synthetic `vswhere` outputs for: VS2022-only, VS2026-only, both installed,
  neither, and instances with/without the SSIS Projects extension.
- Tests asserting `DetectAll` maps versions → generations correctly across both generations, and
  that `Select(auto/vs2022/vs2026)` picks the right instance (or null) for each fixture.
