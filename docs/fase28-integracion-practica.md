# Fase 28 — Benchmark completo: IntegracionPractica (end-to-end)

Real classroom ETL practice (UTN — *Aplicación de Bases de Datos*), built from the **actual practice
files** as the source of truth, executed with the licensed `dtexec`, and verified with **business
data** (not just exit code 0). Nothing about the practice rules was invented; unspecified rules are
reported as gaps below.

Source files (`C:\Users\serra\Downloads\Archivos de practica`):
`Practica Integración Instrucciones.docx/.pdf`, `Base practica origen.sql`, `Base practica destino.sql`,
`Carga de tablas enfermedad y enfermedad mascota.xls`, `Carga de tablas emergentes.accdb`.
No previous `.dtsx/.dtproj` exists in the folder → **no golden package to compare against**.

Generator/executor: `src/SsisMcp.Practica` (build vehicle; drives the MCP builders, executes, and
verifies). Output package: `…/Archivos de practica/IntegracionPractica/IntegracionPractica.dtsx`.

## Requirements extracted (from the instructions)

1. Project `IntegracionPractica`.
2. A SQL task that **empties the five destination tables**.
3. Load destination `Vet`:
   - `Cliente` and `Mascota` from the **source SQL DB** (with field transformations).
   - `TipoCliente`, `Enfermedad`, `EnfermedadMascota` from the **Excel** file (with changes).
   - `EnfermedadMascota.impuesto = 13%` of the cost, via a **derived column**.
   - Add extra `Mascota` and `Enfermedad` rows from the **Access** DB; **do not load
     `EnfermedadMascota` until the Access step is done**.
4. Export two queries to an Excel workbook `reportes`:
   - Q1: client full name, phone, pet name, illness name.
   - Q2: client name, pet name, total money spent on that pet's illnesses.
5. Export `Cliente` and `Mascota` to XML files `ClienteXML` and `MascotaXML`.

## Connections (Connection Managers)

| CM | Provider | Why |
|---|---|---|
| `Origen` | **ADO.NET** (SqlClient) → `PracticaOrigen` | profesor requires ADO.NET for SQL Server |
| `Vet` | **ADO.NET** (SqlClient) → `Vet` | ADO.NET destination + Execute SQL |
| `VetOleDb` | OLE DB (MSOLEDBSQL) → `Vet` | **only** for `Mascota` keep-identity (see gap #2) |
| `ExcelH` | ACE OLE DB, `HDR=YES`, Excel 8.0 | sheets `Enfermedad$`, `EnfermedadMascota$` |
| `ExcelNoH` | ACE OLE DB, `HDR=NO`, Excel 8.0 | sheet `Tipo Cliente$` has **no header row** |
| `Access` | ACE OLE DB (.accdb) | Access source |
| `Reportes` | ACE OLE DB, Excel 12.0 Xml | output `reportes.xlsx` |

ADO.NET is used for every SQL Server read/write **except** the identity-preserving `Mascota` load,
which is technically impossible over ADO.NET (see gap #2). Excel/Access have no ADO.NET provider, so
ACE OLE DB is the correct (not "convenience") choice there.

## Architecture (Control Flow) — linear precedence chain

```
SqlBorrar (Execute SQL)
  → DFTTipoCliente → DFTCliente → DFTMascota → DFTEnfermedad
  → DFTEnfermedadAccess → DFTMascotaAccess → DFTEnfermedadMascota
  → DFTReporte1 → DFTReporte2
```

The chain enforces the FK load order (TipoCliente before Cliente before Mascota; Enfermedad + Mascota
before EnfermedadMascota) **and** the instruction "no `EnfermedadMascota` until the Access step is
done" (EnfermedadMascota runs after both Access DFTs).

`SqlBorrar` = `DELETE FROM EnfermedadMascota; DELETE FROM Mascota; DELETE FROM Cliente; DELETE FROM
Enfermedad; DELETE FROM TipoCliente; DBCC CHECKIDENT('Mascota', RESEED, 0);` (see gap #1 for
DELETE-vs-DROP; the reseed makes re-runs idempotent for the identity column).

## Data Flows, transformations & mappings

Observed source types drive every cast (ADO.NET emits `DT_WSTR`/`DT_DBDATE`; Excel emits
`DT_R8`/`DT_WSTR`/`DT_DATE`; Access emits `DT_I2`). Destinations are `varchar` ⇒ **Data Conversion**
is required for every string column (`DT_WSTR/DT_STR`).

| DFT | Source | Transformations | Destination (map) |
|---|---|---|---|
| **DFTTipoCliente** | `ExcelNoH` `Tipo Cliente$` (F1,F2) | Data Conversion F1→`id` (I4), F2→`nombre` (STR50) | ADO.NET `TipoCliente` (id,nombre) |
| **DFTCliente** | ADO.NET `Origen.Cliente` | Data Conversion `direccion`→STR100, `telefono`→STR8; Derived `nombreCompleto=(DT_STR,150)(nombre+" "+apellido1+" "+apellido2)`, `annio=YEAR(nacimiento)`, `mes=MONTH`, `dia=DAY`, `idTipoCliente = idTipo=="F" ? 1 : 2` | ADO.NET `Cliente` (9 cols, explicit map) |
| **DFTMascota** | ADO.NET `Origen.Mascota` (`ORDER BY id`) | Data Conversion `nombre`→STR50 | **OLE DB** `Mascota` *keep-identity* (id,nombre,idCliente) |
| **DFTEnfermedad** | `ExcelH` `Enfermedad$` | Data Conversion `Codigo`→`id`(I4), `Nombre`→STR100 | ADO.NET `Enfermedad` (id,nombre) |
| **DFTEnfermedadAccess** | OLE DB `Access.Enfermedad` | Data Conversion `id`(I2→I4), `nombre`→STR100 | ADO.NET `Enfermedad` (append) |
| **DFTMascotaAccess** | OLE DB `Access.Mascota` | Data Conversion `id`(I2→I4), `nombre`→STR50, `idCliente`(I2→I4) | **OLE DB** `Mascota` *keep-identity* (append) |
| **DFTEnfermedadMascota** | `ExcelH` `EnfermedadMascota$` | Derived `impuesto=(DT_CY)(Costo*0.13)`; Data Conversion `Mascota`→`idMascota`(I4), `Enfermedad`→`idEnfermedad`(I4), `Costo`→`costo`(CY), `Fecha`→`fecha`(DBTIMESTAMP) | ADO.NET `EnfermedadMascota` (5 cols) |
| **DFTReporte1** | ADO.NET `Vet` (Q1 join) | — | Excel `Reporte1$` (autoMap) |
| **DFTReporte2** | ADO.NET `Vet` (Q2 join+`SUM`) | — | Excel `Reporte2$` (autoMap) |

Layout: unified Control Flow + per-DFT Data Flow via `PackageLayoutEngine` (top→bottom).

## Execution & business verification (licensed `dtexec` 17.0.1000.7)

`SsdtDebugExecutionHost.Execute` → **`Success`**. Destination data verified against the practice's
real numbers:

```
TipoCliente  = 2      Cliente = 5      Mascota = 10 (5 origen + 5 access)
Enfermedad   = 16 (10 excel + 6 access)          EnfermedadMascota = 15
TipoCliente:  1=FRECUENTE, 2=OCASIONAL
Cliente id=1: nombreCompleto="Cliente 1 Apellido 1-1 Apellido 2-1"  annio/mes/dia=1981/5/21  idTipoCliente=1
idTipo→id map: 1:F→1, 2:F→1, 3:O→2, 4:O→2, 5:F→1
Mascota id=1 = Duke   (id preserved 1..5 — semantic link intact)
impuesto = costo*0.13  → 0 mismatches (e.g. 2000→260.00, 3000→390.00, 3250→422.50)
reportes.xlsx: Reporte1=15 rows, Reporte2=5 rows (written by the package at runtime)
Q1/Q2 joins resolve correctly (Duke→Moquillo,Gripe,Rabia; Duke total=14500)
```

Three verification levels for this package:

```
FunctionalStructureVerified = true   (built + validated + reload-inspected, all DFTs)
DesignerLayoutVerified      = true   (unified layout persisted; opens laid-out in VS 2022)
ExecutionVerified           = true   (executed via licensed dtexec + destination business data verified)
```

## Gaps / ambiguities (reported, NOT invented)

1. **"Borre las cinco tablas"** — interpreted as **empty the rows** (`DELETE`), not `DROP TABLE`,
   because the subsequent loads require the tables (and their schema) to exist. If the professor means
   `DROP`+recreate, that changes `SqlBorrar`.
2. **`Mascota.id` is `IDENTITY`** but `EnfermedadMascota` (Excel) references pet ids 1..5 from the
   source. Preserving those ids requires `SET IDENTITY_INSERT`, which **only OLE DB fast-load
   (keep-identity)** can do — ADO.NET/row-by-row cannot. So `Mascota` uses OLE DB keep-identity while
   the rest use ADO.NET. Verified: `Mascota id=1 = Duke`. Access pets keep their ids 21..25.
3. **`Mascota.nacimiento` (smalldatetime)** — the source only has `edad` (integer years). The practice
   **does not specify** how to derive a birth date (no reference date given), so `nacimiento` is left
   **NULL** (all 10 rows) rather than inventing a formula. *If* the intended rule is
   `DATEADD(YEAR, -edad, <fecha>)`, the reference date must be provided. **← needs confirmation.**
4. **`idTipo` 'F'/'O' → `idTipoCliente` 1/2** — the letter→id mapping (F=FRECUENTE=1, O=OCASIONAL=2) is
   an **inference** from the Excel `Tipo Cliente` sheet; the instructions don't state it explicitly.
   High confidence, but flagged.
5. **XML export mechanism unspecified + SSIS/tooling gap** — SSIS has **no native XML destination**,
   and the MCP builder exposes none; the instructions don't say how to produce the XML. So the package
   does **not** produce the XML. `ClienteXML.xml` / `MascotaXML.xml` were generated **out-of-band via
   `SELECT … FOR XML`** and are clearly labeled as such. A pure-SSIS route would be a Script Task
   (now possible since VSTA is installed) + `FOR XML`, but injecting Script Task code is not yet a
   builder capability. **← reported, not faked.**

## What this exercised in the toolkit

- Multi-DFT package with internal Data Flows (7 load DFTs + 2 report DFTs) + Execute SQL + precedence.
- ADO.NET + OLE DB + Excel(×2 HDR modes) + Access connection managers in one package.
- Data Conversion (multi-column), Derived Column, explicit + auto mappings, OLE DB keep-identity.
- Fix landed: **multi-column Data Conversion positional lineage rebind** (`DataConversionLineageHandler`)
  — without it, any DFT converting 2+ columns failed validation after the save→reload cycle.

## Automatic-resolution summary

Of the 5 instruction groups: (1) project, (2) SqlBorrar, (3) full destination load with all
transforms + impuesto + Access append + ordering, (4) Excel reports — **built, executed and verified
automatically**. (5) XML export — artifacts produced out-of-band and the SSIS/tooling gap reported.
**≈ 90–95%** solved automatically end-to-end; the remainder is the XML-mechanism gap (#5) plus two
rules needing the professor's confirmation (#3 `edad→nacimiento`, and #1 DELETE-vs-DROP).
