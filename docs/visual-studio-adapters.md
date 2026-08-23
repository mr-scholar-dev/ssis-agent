# Visual Studio adapters — official VS 2022 + VS 2026 support

## ⛔ Environment blocker on this host (Designer Layout phase)

The Designer/visual phase requires a full Visual Studio IDE **with the SQL Server Integration
Services Projects extension** (the SSIS Designer). Empirically confirmed on this host:

| Detected instance | IDE (`devenv.exe`) | SSIS Designer | Can open `.dtproj` |
|---|---|---|---|
| `2022\BuildTools` (17.14.35) | **no** (Build Tools only) | no | **no** |
| `18\Community` (VS 2026, 18.7.3) | yes | **no** (extension absent) | **no** |
| SSMS 22 (22.9.0) | n/a | no | no |

`Microsoft.DataTransformationServices.Design.dll` is present in **no** VS/SQL/SSMS directory.
Also, Microsoft's SSIS Projects extension currently targets **VS 2019/2022 only** — there is no
VS 2026 build — and no full VS 2022 IDE is installed here to host it.

**Consequence:** the phase's Definition of Done (open a generated package in the VS 2022/2026 SSIS
Designer and see the diagram) is **`EnvironmentBlocked`** on this host. Per policy we do **not**
fake it: `DesignerLayoutVerified` is not claimed. What *is* delivered and verifiable:

- **Concrete detection** (`WindowsVisualStudioLocator`, `VisualStudio2022Adapter`,
  `VisualStudio2026Adapter`) reports each instance honestly, incl. `DesignerUnavailable`.
- Three independent status dimensions are tracked separately:
  `FunctionalStructureVerified` (✅ today) · `DesignerLayoutVerified` (⛔ EnvironmentBlocked) ·
  `ExecutionVerified` (⛔ EnvironmentBlocked).

## How SSIS persists designer layout (reference — not locally verifiable without the Designer)

> Documented from the SSIS package schema; **not** confirmed against a VS-created golden on this
> host (no Designer available). Writing this layout blind is deliberately **not** done yet, to honor
> the "don't invent properties / don't fake verification" rule.

- **Control Flow** layout lives at package level in a design-time element
  `DTS:Property[@DTS:Name="DesignTimeProperties"]` whose value is a CDATA XML blob:
  `<Objects><Package><LayoutInfo><GraphLayout>` containing `NodeLayout` (per executable: `Id`,
  `Size` = W,H, `Location`/`TopLeft` = X,Y) and `EdgeLayout` (per precedence constraint: connector
  routing between node ids).
- **Data Flow** layout is stored per Data Flow Task, again as a design-time `GraphLayout` CDATA,
  with a `NodeLayout` per pipeline component and `EdgeLayout`/paths between component input/output
  anchors.
- It is stored **outside** the runtime Object Model (a design-time annotation), so a layout engine
  would read/write only this blob, kept strictly separate from functional component configuration
  (which always goes through the SSIS OM).

A `DesignerLayoutEngine` (control-flow top→bottom, data-flow left→right, branch separation, overlap
avoidance, preserve-existing) is **designed but not implemented** pending a host where the output can
actually be opened in the Designer and verified — otherwise it would be theoretical, not proven.

---


**Both Visual Studio 2022 and Visual Studio 2026 are first-class, officially supported from the
initial design.** VS 2022 is *not* treated as future/best-effort compatibility.

## ✅ Control Flow DesignerLayout VERIFIED (VS 2022, 2026-08-23)

An MCP-generated package (`samples/VisualBenchmark_ControlFlow.dtsx`, laid out by
`SsisMcp.Designer.ControlFlowLayoutEngine`) was opened in the VS 2022 SSIS Designer and **visually
confirmed**: SqlBorrar → DFTTipoCliente → DFTCliente → DFTMascota → DFTEnfermedad rendered
**top→bottom with the MCP-computed positions**, the four Success precedence arrows drawn between the
boxes, no overlap, correct names, and the `Vet` connection manager present. So for Control Flow:

```
FunctionalStructureVerified = true
DesignerLayoutVerified      = true   (VS 2022 only; VS 2026 EnvironmentBlocked by policy)
ExecutionVerified           = pending (see note)
```

> **Execution note:** the same package additionally **executed green inside VS** (`DtsDebugHost` exit
> code 0). Execution is license-gated only for the *standalone* runtime — **inside the SSDT/VS context
> it runs**. This reframes the execution gate: a viable path to `ExecutionVerified` is executing
> through the SSDT/DtsDebugHost context rather than requiring a Standard+ standalone license.
> (Data Flow with transforms still to be confirmed this way.)

Data Flow designer layout (left→right, Match/No-Match branches) and its visual confirmation are the
remaining part of this gate (`DataFlowLayoutEngine` not yet implemented).

## Target roles (Designer verification vs bridge)

| Generation | MCP bridge | SSIS Designer verification |
|---|---|---|
| **VS 2022** | ✅ target | ✅ **Designer verification target** (once SSIS Projects extension installed) |
| **VS 2026** | ✅ target | ⛔ **EnvironmentBlocked** — Microsoft's SSIS Projects extension does not support VS 2026 yet; we will not fake visual compatibility |

The probe reports **four distinct facts per instance** (never one boolean):
`VisualStudioInstalled`, `VisualStudioIdeAvailable`, `SsisProjectsExtensionInstalled`,
`SsisDesignerAvailable`, plus a per-instance `DesignerLayoutTesting` = `Ready | EnvironmentBlocked`
and an overall `designer.layout.testing` check.

### To enable Designer verification (resume the gate)

Install **Visual Studio 2022 Community (full IDE, not Build Tools)** + the **SQL Server Integration
Services Projects** extension, then re-run `dotnet run --project src/SsisMcp.EnvProbe`. Expected:

```
vs.2022                 : PASS  (... IdeAvailable=True; SsisProjectsExtensionInstalled=True; SsisDesignerAvailable=True; DesignerLayoutTesting=Ready)
designer.layout.testing : PASS  (READY ...)
```

When `DesignerLayoutTesting=Ready`, resume exactly at this gate: (1) create golden packages from the
Designer, (2) capture the real `DesignTimeProperties`/`GraphLayout`, (3) diff vs MCP output,
(4) implement `DesignerLayoutEngine` from observed metadata only, (5) open MCP packages in VS 2022 &
verify visually, (6) turn everything possible into regressions. Do **not** implement blind layout
before this.

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
