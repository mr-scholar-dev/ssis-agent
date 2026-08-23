using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SsisMcp.Core.Building;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.Ssis.Building
{
    /// <summary>Outcome of configuring + precompiling a Script Task.</summary>
    public sealed class ScriptTaskBuildResult
    {
        public bool Compiled { get; set; }
        public int AssemblyBytes { get; set; }
        public string ProjectName { get; set; } = "";
        public string LanguageDisplayName { get; set; } = "";
        public List<string> ReadOnlyVariables { get; } = new List<string>();
        public List<string> ReadWriteVariables { get; } = new List<string>();
        public List<string> References { get; } = new List<string>();
    }

    /// <summary>
    /// Reusable MCP capability: configure a Script Task's C# source, variables and references, and
    /// **precompile** it through the VSTA design-time so a headless <c>dtexec</c> can run the binary.
    ///
    /// Entirely reflection-based over the VSTA object model (<c>IVstaHelper</c> /
    /// <c>VSTAScriptProjectStorage</c>), so this assembly takes no compile-time dependency on VSTA and
    /// **degrades to a structured error** where the design-time is absent:
    /// <list type="bullet">
    /// <item><see cref="BuilderErrorCode.UnsupportedEnvironment"/> — VSTA/script design-time not installed.</item>
    /// <item><see cref="BuilderErrorCode.ScriptCompileFailed"/> — the C# did not compile (messages included).</item>
    /// </list>
    ///
    /// Verified sequence (SQL 2025 / VSTA 2022): set language+entry+variables → <c>Initalize</c> →
    /// <c>LoadNewProject(template)</c> → seed <c>SaveProjectToStorage</c> → replace <c>ScriptMain.cs</c>
    /// (+ inject references into the .csproj) → <c>LoadProjectFromStorage</c> → <c>Build</c> →
    /// <c>GetBuildErrors</c> → <c>SaveProjectToStorage</c>. The compiled assembly is persisted into the
    /// package, so save/reload/execute needs no designer.
    /// </summary>
    public sealed class ScriptTaskConfigurator
    {
        private const string ScriptTaskTypeName = "Microsoft.SqlServer.Dts.Tasks.ScriptTask.ScriptTask";

        public ScriptTaskBuildResult Configure(
            Dts.TaskHost host, string source,
            IEnumerable<string>? readOnlyVariables = null,
            IEnumerable<string>? readWriteVariables = null,
            IEnumerable<string>? references = null,
            string entryPoint = "Main",
            string languageInternalName = "CSharp")
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (string.IsNullOrWhiteSpace(source))
                throw new BuilderException(BuilderErrorCode.InvalidExpression, "Script Task source is required");

            // The TaskHost returns InnerObject as the managed ScriptTask ONLY when its managed assembly
            // is loaded in the AppDomain; otherwise it comes back as a raw __ComObject (over which the
            // VSTA object model is not reachable). Force-load it from the GAC, then re-fetch InnerObject.
            EnsureScriptTaskAssemblyLoaded();
            var inner = host.InnerObject;
            if (inner == null)
                throw new BuilderException(BuilderErrorCode.Unsupported, $"'{host.Name}' has no task object");
            if (inner.GetType().FullName != ScriptTaskTypeName)
                throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment,
                    $"Script Task managed object not resolvable (inner={inner.GetType().FullName}); " +
                    "the Integration Services Script Task design-time is not available in this process");

            var result = new ScriptTaskBuildResult();
            var ro = (readOnlyVariables ?? Enumerable.Empty<string>()).ToList();
            var rw = (readWriteVariables ?? Enumerable.Empty<string>()).ToList();
            var refs = (references ?? Enumerable.Empty<string>()).ToList();
            result.ReadOnlyVariables.AddRange(ro);
            result.ReadWriteVariables.AddRange(rw);
            result.References.AddRange(refs);

            // ---- language display name (e.g. "Microsoft Visual C# 2022") ----
            var displayName = ResolveLanguageDisplayName(languageInternalName);
            result.LanguageDisplayName = displayName;

            SetProp(host, "ScriptLanguage", displayName);
            SetProp(host, "EntryPoint", entryPoint);
            SetProp(host, "ReadOnlyVariables", string.Join(",", ro));
            SetProp(host, "ReadWriteVariables", string.Join(",", rw));

            var projName = (string)GetProp(host, "ScriptProjectName");
            var templatePath = (string)GetProp(host, "ProjectTemplatePath");
            result.ProjectName = projName;

            var engine = inner.GetType().GetProperty("ScriptingEngine")?.GetValue(inner)
                ?? throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment, "Script Task scripting engine unavailable");
            var helper = engine.GetType().GetProperty("VstaHelper")?.GetValue(engine)
                ?? throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment,
                    "VSTA design-time not available (install the Integration Services script design-time)");
            var storage = inner.GetType().GetProperty("ScriptStorage")?.GetValue(inner)
                ?? throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment, "Script Task storage unavailable");

            object Inv(string m, params object[] a)
            {
                var mi = helper.GetType().GetMethod(m) ?? throw new BuilderException(
                    BuilderErrorCode.UnsupportedEnvironment, $"VSTA helper method '{m}' not found");
                try { return mi.Invoke(helper, a); }
                catch (TargetInvocationException tie)
                {
                    throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment,
                        $"VSTA helper '{m}' failed: {tie.InnerException?.Message ?? tie.Message}");
                }
            }

            try
            {
                Inv("Initalize", "SSIS", false);                       // note VSTA's spelling
                if (!TrueOf(Inv("LoadNewProject", templatePath, "", projName)))
                    throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment, "VSTA LoadNewProject failed");
                Inv("SaveProjectToStorage", storage);                  // seed ScriptFiles from template

                var scriptFiles = (Hashtable)storage.GetType().GetProperty("ScriptFiles").GetValue(storage);
                ReplaceFileData(scriptFiles, "ScriptMain.cs", source.Replace("__NAMESPACE__", projName));
                if (refs.Count > 0) InjectReferences(scriptFiles, projName + ".csproj", refs);

                if (!TrueOf(Inv("LoadProjectFromStorage", storage)))
                    throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment, "VSTA LoadProjectFromStorage failed");

                Inv("Build", "");
                var errors = CollectBuildErrors(Inv("GetBuildErrors", ""));
                if (errors.Count > 0)
                    throw new BuilderException(BuilderErrorCode.ScriptCompileFailed,
                        "Script Task did not compile:\n" + string.Join("\n", errors.Take(20)));

                Inv("SaveProjectToStorage", storage);

                var asmCode = storage.GetType().GetProperty("AssemblyCode").GetValue(storage) as byte[];
                result.AssemblyBytes = asmCode?.Length ?? 0;
                result.Compiled = result.AssemblyBytes > 0;
                if (!result.Compiled)
                    throw new BuilderException(BuilderErrorCode.ScriptCompileFailed,
                        "Script Task produced no assembly (precompile did not persist)");
            }
            finally
            {
                try { helper.GetType().GetMethod("CleanUp")?.Invoke(helper, null); } catch { /* best effort */ }
            }
            return result;
        }

        private static void EnsureScriptTaskAssemblyLoaded()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "Microsoft.SqlServer.ScriptTask") return;
            foreach (var full in new[]
            {
                "Microsoft.SqlServer.ScriptTask, Version=17.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91",
                "Microsoft.SqlServer.ScriptTask",
            })
            {
                try { Assembly.Load(full); return; } catch { /* try next */ }
            }
            // not fatal here — the InnerObject type check below produces the structured error.
        }

        private static bool TrueOf(object? o) => o is bool b && b;

        private static void ReplaceFileData(Hashtable scriptFiles, string endsWith, string data)
        {
            object? key = scriptFiles.Keys.Cast<object>()
                .FirstOrDefault(k => k.ToString().EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
            if (key == null)
                throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment, $"project file '{endsWith}' not found in template");
            var vf = scriptFiles[key];
            var dataField = vf.GetType().GetField("Data")
                ?? throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment, "VSTAScriptFile.Data field not found");
            dataField.SetValue(vf, data);
        }

        // Adds <Reference Include="..."/> entries into the project's .csproj so custom assemblies resolve.
        private static void InjectReferences(Hashtable scriptFiles, string csprojEndsWith, List<string> refs)
        {
            object? key = scriptFiles.Keys.Cast<object>()
                .FirstOrDefault(k => k.ToString().EndsWith(csprojEndsWith, StringComparison.OrdinalIgnoreCase)
                                  || k.ToString().EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            if (key == null) return; // no csproj found — skip (references optional)
            var vf = scriptFiles[key];
            var dataField = vf.GetType().GetField("Data");
            var csproj = (string)dataField.GetValue(vf);
            var sb = new StringBuilder();
            sb.Append("  <ItemGroup>\n");
            foreach (var r in refs) sb.Append($"    <Reference Include=\"{r}\" />\n");
            sb.Append("  </ItemGroup>\n");
            var idx = csproj.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
            csproj = idx >= 0 ? csproj.Insert(idx, sb.ToString()) : csproj + sb;
            dataField.SetValue(vf, csproj);
        }

        private static List<string> CollectBuildErrors(object? errorsObj)
        {
            var list = new List<string>();
            if (errorsObj is IEnumerable en)
                foreach (var e in en)
                {
                    var s = e?.ToString() ?? "";
                    // skip the decorative separators VSTA emits
                    if (!string.IsNullOrWhiteSpace(s) && s.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                        list.Add(s.Replace("\r", " ").Replace("\n", " ").Trim());
                }
            return list;
        }

        private static string ResolveLanguageDisplayName(string internalName)
        {
            var langType = FindType("Microsoft.SqlServer.VSTAHosting.VSTAScriptLanguages");
            if (langType == null)
                throw new BuilderException(BuilderErrorCode.UnsupportedEnvironment,
                    "VSTAScriptLanguages not available (script design-time not installed)");
            var m = langType.GetMethod("GetDisplayName", new[] { typeof(string) });
            try
            {
                var name = m?.Invoke(null, new object[] { internalName }) as string;
                if (string.IsNullOrEmpty(name))
                    throw new BuilderException(BuilderErrorCode.Unsupported, $"unknown script language '{internalName}'");
                return name!;
            }
            catch (TargetInvocationException tie)
            {
                throw new BuilderException(BuilderErrorCode.Unsupported,
                    $"script language '{internalName}' not recognized: {tie.InnerException?.Message ?? tie.Message}");
            }
        }

        private static Type? FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return Type.GetType(fullName);
        }

        private static void SetProp(Dts.TaskHost host, string name, object value) => host.Properties[name].SetValue(host, value);
        private static object GetProp(Dts.TaskHost host, string name) => host.Properties[name].GetValue(host);
    }

    /// <summary>Builds canonical Script Task C# source. Use the <c>__NAMESPACE__</c> token where the
    /// project namespace goes; <see cref="ScriptTaskConfigurator"/> substitutes the real one.</summary>
    public static class ScriptTaskSource
    {
        public static string CSharpMain(string mainBody, IEnumerable<string>? usings = null, string extraMembers = "")
        {
            var us = new List<string> { "System", "System.Data", "Microsoft.SqlServer.Dts.Runtime" };
            if (usings != null) foreach (var u in usings) if (!us.Contains(u)) us.Add(u);
            var sb = new StringBuilder();
            foreach (var u in us) sb.Append($"using {u};\n");
            sb.Append("namespace __NAMESPACE__\n{\n");
            sb.Append("    [Microsoft.SqlServer.Dts.Tasks.ScriptTask.SSISScriptTaskEntryPointAttribute]\n");
            sb.Append("    public partial class ScriptMain : Microsoft.SqlServer.Dts.Tasks.ScriptTask.VSTARTScriptObjectModelBase\n    {\n");
            sb.Append("        public void Main()\n        {\n");
            sb.Append(mainBody).Append('\n');
            sb.Append("            Dts.TaskResult = (int)ScriptResults.Success;\n");
            sb.Append("        }\n");
            if (!string.IsNullOrWhiteSpace(extraMembers)) sb.Append(extraMembers).Append('\n');
            sb.Append("        enum ScriptResults { Success = 0, Failure = 1 }\n");
            sb.Append("    }\n}\n");
            return sb.ToString();
        }
    }
}
