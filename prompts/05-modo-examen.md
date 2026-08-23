# 05 · Modo examen (prompt principal)

Usa el **SSIS MCP**. Prioridad absoluta: **exactitud**. Esto es un examen; no improvises.

Reglas:
- **Lee absolutamente todo primero** (instrucciones + todas las fuentes) antes de construir.
- **No inventes** nada (mappings, reglas, fechas, defaults, Lookups/Derived/Splits). Sin evidencia → pregunta.
- Ante ambigüedad: **preguntas cortas y concretas**, luego continúa.
- Construye la estructura SSIS normal:
```
Package → Control Flow → Data Flow Tasks → pipelines internos
```
- **Mappings automáticos solo si son inequívocos**; **detecta y aplica conversiones** de tipo necesarias.
- Respeta **ADO.NET** si el profesor lo pide (y el resto de tecnologías/componentes exactos requeridos).
- **Layout limpio**, **metadata/lineage** correctos.
- **Execute + verify** con datos reales (row counts / outputs), no solo `Package.Validate`.
- Ante fallos: **reparación segura** vía Safety (`preview`→`apply`, backup/undo). **Nunca destruyas trabajo existente.**
- **No te saltes requisitos** para simplificar.

Al terminar, responde **breve**:
- **Qué hice** ·  **Qué verifiqué** ·  **Qué falta** ·  **Qué debo revisar visualmente en Visual Studio**.
