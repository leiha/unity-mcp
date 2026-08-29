using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Microsoft.CSharp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("execute_code", AutoRegister = false, Group = "scripting_ext")]
    public static class ExecuteCode
    {
        private const int MaxCodeLength = 50000;
        private const int MaxHistoryEntries = 50;
        private const int MaxHistoryCodePreview = 500;
        internal const int WrapperLineOffset = 10;
        private const string WrapperClassName = "MCPDynamicCode";
        private const string WrapperMethodName = "Execute";

        private const string ActionExecute = "execute";
        private const string ActionGetHistory = "get_history";
        private const string ActionClearHistory = "clear_history";
        private const string ActionReplay = "replay";

        private static readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private static string[] _cachedAssemblyPaths;

        [UnityEditor.InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            _cachedAssemblyPaths = null;
            RoslynCompiler.ResetCache();
        }

        private static readonly HashSet<string> _blockedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.IO.File.Delete",
            "System.IO.Directory.Delete",
            "FileUtil.DeleteFileOrDirectory",
            "AssetDatabase.DeleteAsset",
            "AssetDatabase.MoveAssetToTrash",
            "EditorApplication.Exit",
            "Process.Start",
            "Process.Kill",
            "while(true)",
            "while (true)",
            "for(;;)",
            "for (;;)",
        };

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();

            switch (action)
            {
                case ActionExecute:
                    return HandleExecute(@params);
                case ActionGetHistory:
                    return HandleGetHistory(@params);
                case ActionClearHistory:
                    return HandleClearHistory();
                case ActionReplay:
                    return HandleReplay(@params);
                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions: {ActionExecute}, {ActionGetHistory}, {ActionClearHistory}, {ActionReplay}");
            }
        }

        private static object HandleExecute(JObject @params)
        {
            string code = @params["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
                return new ErrorResponse("Required parameter 'code' is missing or empty.");

            if (code.Length > MaxCodeLength)
                return new ErrorResponse($"Code exceeds maximum length of {MaxCodeLength} characters.");

            bool safetyChecks = @params["safety_checks"]?.Value<bool>() ?? true;
            string compiler = @params["compiler"]?.ToString()?.ToLowerInvariant() ?? "auto";
            // PONT-06 — caller may widen or disable the main-thread budget (0 = no guard).
            int budgetMs = @params["timeout_ms"]?.Value<int>() ?? DefaultBudgetMs;

            if (safetyChecks)
            {
                var violation = CheckBlockedPatterns(code);
                if (violation != null)
                    return new ErrorResponse($"Blocked pattern detected: {violation}");
            }

            try
            {
                var startTime = DateTime.UtcNow;
                var result = CompileAndExecute(code, compiler, budgetMs);
                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

                AddToHistory(code, result, elapsed, safetyChecks, compiler);
                return result;
            }
            catch (Exception e)
            {
                McpLog.Error($"[ExecuteCode] Execution failed: {e}");
                var errorResult = new ErrorResponse($"Execution failed: {e.Message}");
                AddToHistory(code, errorResult, 0, safetyChecks, compiler);
                return errorResult;
            }
        }

        private static object HandleGetHistory(JObject @params)
        {
            int limit = @params["limit"]?.Value<int>() ?? 10;
            limit = Math.Clamp(limit, 1, MaxHistoryEntries);

            if (_history.Count == 0)
                return new SuccessResponse("No execution history.", new { total = 0, entries = new object[0] });

            var entries = _history.Skip(Math.Max(0, _history.Count - limit)).ToList();
            return new SuccessResponse($"Returning {entries.Count} of {_history.Count} history entries.", new
            {
                total = _history.Count,
                entries = entries.Select((e, i) => new
                {
                    index = _history.Count - entries.Count + i,
                    codePreview = e.code.Length > MaxHistoryCodePreview
                        ? e.code.Substring(0, MaxHistoryCodePreview) + "..."
                        : e.code,
                    e.success,
                    e.resultPreview,
                    e.elapsedMs,
                    e.timestamp,
                    e.safetyChecksEnabled,
                    e.compiler,
                }).ToList(),
            });
        }

        private static object HandleClearHistory()
        {
            int count = _history.Count;
            _history.Clear();
            return new SuccessResponse($"Cleared {count} history entries.");
        }

        private static object HandleReplay(JObject @params)
        {
            if (_history.Count == 0)
                return new ErrorResponse("No execution history to replay.");

            int? index = @params["index"]?.Value<int>();
            if (index == null || index < 0 || index >= _history.Count)
                return new ErrorResponse($"Invalid history index. Valid range: 0-{_history.Count - 1}");

            var entry = _history[index.Value];
            var replayParams = JObject.FromObject(new
            {
                action = ActionExecute,
                code = entry.code,
                safety_checks = entry.safetyChecksEnabled,
                compiler = entry.compiler ?? "auto",
            });
            return HandleExecute(replayParams);
        }

        // ──────────────────── Compilation ────────────────────

        private static object CompileAndExecute(string code, string compiler, int budgetMs)
        {
            string wrappedSource = WrapUserCode(code);
            string[] assemblyPaths = GetAssemblyPaths();

            Assembly compiled;
            string usedCompiler;

            switch (compiler)
            {
                case "roslyn":
                    if (!RoslynCompiler.IsAvailable)
                        return new ErrorResponse("Roslyn (Microsoft.CodeAnalysis) is not available. Install it via NuGet or use compiler='codedom'.");
                    compiled = RoslynCompiler.Compile(wrappedSource, assemblyPaths, out var roslynErrors);
                    if (compiled == null)
                        return new ErrorResponse("Compilation failed", new { errors = OffsetErrors(roslynErrors), compiler = "roslyn" });
                    usedCompiler = "roslyn";
                    break;

                case "codedom":
                    compiled = CodeDomCompile(wrappedSource, assemblyPaths, out var codedomErrors);
                    if (compiled == null)
                        return new ErrorResponse("Compilation failed", new { errors = OffsetErrors(codedomErrors), compiler = "codedom" });
                    usedCompiler = "codedom";
                    break;

                default: // "auto"
                    if (RoslynCompiler.IsAvailable)
                    {
                        compiled = RoslynCompiler.Compile(wrappedSource, assemblyPaths, out var autoErrors);
                        if (compiled == null)
                            return new ErrorResponse("Compilation failed", new { errors = OffsetErrors(autoErrors), compiler = "roslyn" });
                        usedCompiler = "roslyn";
                    }
                    else
                    {
                        compiled = CodeDomCompile(wrappedSource, assemblyPaths, out var autoFallbackErrors);
                        if (compiled == null)
                            return new ErrorResponse("Compilation failed", new { errors = OffsetErrors(autoFallbackErrors), compiler = "codedom" });
                        usedCompiler = "codedom";
                    }
                    break;
            }

            return InvokeCompiled(compiled, usedCompiler, budgetMs);
        }

        /// <summary>
        /// PONT-06 — how long submitted code may hold the editor main thread before the budget
        /// guard fires. Deliberately BELOW the bridge's own 30 s frame budget, so the caller gets a
        /// real, named error instead of a bare "timed out" from a layer that knows nothing.
        /// Callers may override it with `timeout_ms`; 0 disables the guard.
        /// </summary>
        private const int DefaultBudgetMs = 20000;

        private static object InvokeCompiled(Assembly assembly, string compilerUsed, int budgetMs)
        {
            var type = assembly.GetType(WrapperClassName);
            if (type == null)
                return new ErrorResponse("Internal error: failed to find compiled type.");

            var method = type.GetMethod(WrapperMethodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                return new ErrorResponse("Internal error: failed to find Execute method.");

            object result = null;
            Exception executionError = null;

            // PONT-06 — submitted code runs on the editor MAIN thread, so code that blocks freezes
            // the editor for every session at once. It was believed this could not be interrupted.
            // MEASURED on the bench, both edges played:
            //   Thread.Interrupt on the main thread DOES break a Sleep/Wait  -> 5.007 s, editor survived
            //   it does NOT break a pure CPU loop                            -> ran the full 20.000 s
            //   an interrupt that misses its target STAYS PENDING and hits the NEXT command — the
            //   error then accuses an innocent caller, which is worse than the freeze it prevents.
            //   Draining it with a short Sleep in a catch clears it: measured, next command clean.
            var mainThread = System.Threading.Thread.CurrentThread;
            var finished = new System.Threading.ManualResetEventSlim(false);
            bool budgetFired = false;
            System.Threading.Thread watchdog = null;

            if (budgetMs > 0)
            {
                watchdog = new System.Threading.Thread(() =>
                {
                    if (finished.Wait(budgetMs)) return;
                    budgetFired = true;
                    try { mainThread.Interrupt(); } catch { /* the thread may have just ended */ }
                })
                {
                    IsBackground = true,
                    Name = "MCP execute_code budget",
                };
                watchdog.Start();
            }

            bool lateInterruptDrained = false;
            try
            {
                result = method.Invoke(null, null);
            }
            catch (TargetInvocationException tie)
            {
                executionError = tie.InnerException ?? tie;
            }
            catch (Exception e)
            {
                executionError = e;
            }
            finally
            {
                finished.Set();
                // Drain an interrupt that arrived too late to hit its target. Without this, it
                // detonates inside whatever the main thread waits on NEXT.
                try { System.Threading.Thread.Sleep(1); }
                catch (System.Threading.ThreadInterruptedException) { lateInterruptDrained = true; }
                finished.Dispose();
            }

            if (budgetFired && executionError is System.Threading.ThreadInterruptedException)
                return new ErrorResponse(
                    $"Code held the editor main thread past its {budgetMs} ms budget and was interrupted. "
                  + "The editor is free again. Raise the budget with `timeout_ms` if this was intended.",
                    new { budgetMs, interrupted = true, compiler = compilerUsed });

            if (budgetFired)
                return new ErrorResponse(
                    $"Code held the editor main thread past its {budgetMs} ms budget and COULD NOT be "
                  + "interrupted — it never waits, so it is a CPU loop. It ran to completion and the "
                  + "editor was frozen for every session for the whole time. Do not submit unbounded loops.",
                    new
                    {
                        budgetMs,
                        interrupted = false,
                        lateInterruptDrained,
                        ranToCompletion = executionError == null,
                        compiler = compilerUsed,
                    });

            if (executionError != null)
                return new ErrorResponse($"Runtime error: {executionError.Message}",
                    new { exceptionType = executionError.GetType().Name, stackTrace = executionError.StackTrace, compiler = compilerUsed });

            if (result != null)
                return new SuccessResponse("Code executed successfully.",
                    new { result = SerializeResult(result), compiler = compilerUsed });

            return new SuccessResponse("Code executed successfully.", new { compiler = compilerUsed });
        }

        private static List<string> OffsetErrors(List<string> errors)
        {
            // Errors already have line numbers adjusted by the compiler-specific code
            return errors;
        }

        // ──────────────────── CodeDom compiler ────────────────────

        private static Assembly CodeDomCompile(string source, string[] assemblyPaths, out List<string> errors)
        {
            errors = new List<string>();

            // CodeDom needs the netstandard-aware filtered paths
            var filtered = FilterAssemblyPathsForCodeDom(assemblyPaths);

            // CSharpCodeProvider turns every ReferencedAssemblies entry into a literal /r:"..." flag
            // on the csc command line. Projects with ~100+ asmdefs blow past Windows' 32 KB
            // CreateProcess argument limit and fail with "The filename or extension is too long."
            // Route references through a response file (@responsefile is supported by both mcs and
            // Roslyn csc) so we pass exactly one short argument regardless of reference count.
            string responseFilePath = Path.Combine(Path.GetTempPath(), $"mcp-codedom-{Guid.NewGuid():N}.rsp");

            try
            {
                using (var writer = new StreamWriter(responseFilePath, append: false, Encoding.UTF8))
                {
                    foreach (var path in filtered)
                    {
                        writer.Write("/r:\"");
                        writer.Write(path);
                        writer.WriteLine("\"");
                    }
                }

                using (var provider = new CSharpCodeProvider())
                {
                    var parameters = new CompilerParameters
                    {
                        GenerateInMemory = true,
                        GenerateExecutable = false,
                        TreatWarningsAsErrors = false,
                        CompilerOptions = "@\"" + responseFilePath + "\"",
                    };

                    var results = provider.CompileAssemblyFromSource(parameters, source);

                    if (results.Errors.HasErrors)
                    {
                        foreach (CompilerError error in results.Errors)
                        {
                            if (!error.IsWarning)
                            {
                                int userLine = Math.Max(1, error.Line - WrapperLineOffset);
                                errors.Add($"Line {userLine}: {error.ErrorText}");
                            }
                        }
                        return null;
                    }

                    return results.CompiledAssembly;
                }
            }
            finally
            {
                try { if (File.Exists(responseFilePath)) File.Delete(responseFilePath); }
                catch { /* best effort */ }
            }
        }

        // CSharpCodeProvider can't resolve type-forwarding, so when netstandard.dll is loaded
        // alongside mscorlib/System.Runtime/System.Collections, types like List<T> appear in
        // multiple assemblies causing "type defined multiple times" errors.
        private static readonly HashSet<string> _codedomDuplicateAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib",
            "System.Runtime",
            "System.Private.CoreLib",
            "System.Collections",
        };

        private static string[] FilterAssemblyPathsForCodeDom(string[] allPaths)
        {
            bool hasNetstandard = allPaths.Any(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), "netstandard", StringComparison.OrdinalIgnoreCase));

            if (!hasNetstandard)
                return allPaths;

            return allPaths.Where(p =>
                !_codedomDuplicateAssemblies.Contains(Path.GetFileNameWithoutExtension(p))).ToArray();
        }

        // ──────────────────── Shared helpers ────────────────────

        private static string WrapUserCode(string code)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine($"public static class {WrapperClassName}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static object {WrapperMethodName}()");
            sb.AppendLine("    {");
            sb.AppendLine(code);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string[] GetAssemblyPaths()
        {
            if (_cachedAssemblyPaths == null)
                _cachedAssemblyPaths = ResolveAssemblyPaths();
            return _cachedAssemblyPaths;
        }

        private static string[] ResolveAssemblyPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in UnityAssembliesCompat.GetLoadedAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic) continue;
                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    if (!File.Exists(location)) continue;
                    paths.Add(location);
                }
                catch (NotSupportedException)
                {
                    // Some assemblies don't support Location property
                }
            }

            var result = new string[paths.Count];
            paths.CopyTo(result);
            return result;
        }

        private static string CheckBlockedPatterns(string code)
        {
            foreach (var pattern in _blockedPatterns)
            {
                if (code.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Code contains blocked pattern: '{pattern}'. Disable safety checks with safety_checks=false if this is intentional.";
            }
            return null;
        }

        private static void AddToHistory(string code, object result, double elapsedMs, bool safetyChecks, string compiler = "auto")
        {
            string preview;
            if (result is SuccessResponse sr)
                preview = sr.Data?.ToString() ?? sr.Message;
            else if (result is ErrorResponse er)
                preview = er.Error;
            else
                preview = result?.ToString() ?? "null";

            if (preview != null && preview.Length > 200)
                preview = preview.Substring(0, 200) + "...";

            _history.Add(new HistoryEntry
            {
                code = code,
                success = result is SuccessResponse,
                resultPreview = preview,
                elapsedMs = Math.Round(elapsedMs, 1),
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                safetyChecksEnabled = safetyChecks,
                compiler = compiler,
            });

            while (_history.Count > MaxHistoryEntries)
                _history.RemoveAt(0);
        }

        private static object SerializeResult(object result)
        {
            if (result == null) return null;

            var type = result.GetType();
            if (type.IsPrimitive || result is string || result is decimal)
                return result;

            try
            {
                return JToken.FromObject(result);
            }
            catch
            {
                return result.ToString();
            }
        }

        private class HistoryEntry
        {
            public string code;
            public bool success;
            public string resultPreview;
            public double elapsedMs;
            public string timestamp;
            public bool safetyChecksEnabled;
            public string compiler;
        }
    }

    /// <summary>
    /// Roslyn compiler backend accessed entirely via reflection.
    /// No compile-time dependency on Microsoft.CodeAnalysis — works only if the package is installed.
    /// </summary>
    internal static class RoslynCompiler
    {
        private static bool? _isAvailable;
        private static Type _syntaxTreeType;
        private static Type _compilationType;
        private static Type _compilationOptionsType;
        private static Type _parseOptionsType;
        private static Type _metadataReferenceType;
        private static Type _outputKindEnum;
        private static Type _languageVersionEnum;
        private static MethodInfo _parseText;
        private static MethodInfo _createCompilation;
        private static MethodInfo _createFromFile;
        private static MethodInfo _emit;
        private static object _parseOptions;
        private static object _compilationOptions;

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable == null)
                    _isAvailable = Initialize();
                return _isAvailable.Value;
            }
        }

        public static void ResetCache()
        {
            _isAvailable = null;
        }

        // PONT-02 — assemblies preloaded from the editor installation, kept so type lookup does
        // not have to go through Type.GetType (which only searches the default load context).
        private static readonly Dictionary<string, Assembly> _preloaded =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// PONT-02 — Roslyn ships INSIDE the editor installation but is not loaded in the editor
        /// domain, so `Type.GetType` finds nothing and every execute_code call silently falls back
        /// to CodeDom. CodeDom shells out to Mono's `mcs`, which is an older C# and blocks the main
        /// thread for the whole compile: measured on an EMPTY project, `return 2+2;` took 12.720 s
        /// and a local function was rejected outright. Loading these four files turns that fallback
        /// off. Best effort: if anything is missing we leave Roslyn unavailable and CodeDom keeps
        /// working exactly as before.
        /// </summary>
        private static void PreloadRoslynFromEditorInstallation()
        {
            string contents;
            try { contents = UnityEditor.EditorApplication.applicationContentsPath; }
            catch { return; }
            if (string.IsNullOrEmpty(contents)) return;

            string directory = Path.Combine(contents, "MonoBleedingEdge", "lib", "mono", "4.5");
            if (!Directory.Exists(directory)) return;

            // Dependencies first: Roslyn will not load without them.
            string[] fileNames =
            {
                "System.Collections.Immutable.dll",
                "System.Reflection.Metadata.dll",
                "Microsoft.CodeAnalysis.dll",
                "Microsoft.CodeAnalysis.CSharp.dll",
            };

            foreach (var fileName in fileNames)
            {
                try
                {
                    string path = Path.Combine(directory, fileName);
                    if (!File.Exists(path)) continue;
                    var assembly = Assembly.LoadFrom(path);
                    if (assembly != null)
                        _preloaded[assembly.GetName().Name] = assembly;
                }
                catch (Exception e)
                {
                    McpLog.Warn($"[ExecuteCode] Could not preload {fileName}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// PONT-02 — resolve a type by full name, looking in the default context first, then in the
        /// assemblies we preloaded, then in whatever the domain already holds.
        /// </summary>
        private static Type ResolveType(string fullName, string assemblySimpleName)
        {
            var type = Type.GetType($"{fullName}, {assemblySimpleName}");
            if (type != null) return type;

            if (_preloaded.TryGetValue(assemblySimpleName, out var preloaded))
            {
                type = preloaded.GetType(fullName);
                if (type != null) return type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!string.Equals(assembly.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch { /* an assembly that cannot be inspected is not the one we want */ }
            }

            return null;
        }

        private static bool Initialize()
        {
            try
            {
                PreloadRoslynFromEditorInstallation();

                _syntaxTreeType = ResolveType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree", "Microsoft.CodeAnalysis.CSharp");
                _compilationType = ResolveType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation", "Microsoft.CodeAnalysis.CSharp");
                _compilationOptionsType = ResolveType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions", "Microsoft.CodeAnalysis.CSharp");
                _parseOptionsType = ResolveType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions", "Microsoft.CodeAnalysis.CSharp");
                _metadataReferenceType = ResolveType("Microsoft.CodeAnalysis.MetadataReference", "Microsoft.CodeAnalysis");
                _outputKindEnum = ResolveType("Microsoft.CodeAnalysis.OutputKind", "Microsoft.CodeAnalysis");
                _languageVersionEnum = ResolveType("Microsoft.CodeAnalysis.CSharp.LanguageVersion", "Microsoft.CodeAnalysis.CSharp");

                if (_syntaxTreeType == null || _compilationType == null || _compilationOptionsType == null ||
                    _parseOptionsType == null || _metadataReferenceType == null || _outputKindEnum == null ||
                    _languageVersionEnum == null)
                    return false;

                // CSharpSyntaxTree.ParseText(string, CSharpParseOptions, string, Encoding, CancellationToken)
                var syntaxTreeBase = ResolveType("Microsoft.CodeAnalysis.SyntaxTree", "Microsoft.CodeAnalysis");
                _parseText = _syntaxTreeType.GetMethod("ParseText", new[] { typeof(string), _parseOptionsType, typeof(string), typeof(Encoding), typeof(System.Threading.CancellationToken) });
                if (_parseText == null)
                    return false;

                // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
                var metadataRefBase = _metadataReferenceType;
                var syntaxTreeEnumerable = typeof(IEnumerable<>).MakeGenericType(syntaxTreeBase);
                var metadataRefEnumerable = typeof(IEnumerable<>).MakeGenericType(metadataRefBase);
                _createCompilation = _compilationType.GetMethod("Create", new[] { typeof(string), syntaxTreeEnumerable, metadataRefEnumerable, _compilationOptionsType });
                if (_createCompilation == null)
                    return false;

                // MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)
                _createFromFile = _metadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateFromFile");
                if (_createFromFile == null)
                    return false;

                // Emit has no single-param overload; the simplest is
                // Emit(Stream, Stream, Stream, Stream, IEnumerable<ResourceDescription>, EmitOptions, CancellationToken)
                var compilationBase = ResolveType("Microsoft.CodeAnalysis.Compilation", "Microsoft.CodeAnalysis");
                if (compilationBase == null) return false;
                _emit = compilationBase.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Emit")
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (_emit == null)
                    return false;

                // Build CSharpParseOptions — constructor has optional params, use reflection
                var latestValue = Enum.Parse(_languageVersionEnum, "Latest");
                var parseOptionsCtor = _parseOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0];
                var parseCtorParams = parseOptionsCtor.GetParameters();
                var parseArgs = new object[parseCtorParams.Length];
                for (int i = 0; i < parseCtorParams.Length; i++)
                {
                    if (parseCtorParams[i].Name == "languageVersion")
                        parseArgs[i] = latestValue;
                    else if (parseCtorParams[i].HasDefaultValue)
                        parseArgs[i] = parseCtorParams[i].DefaultValue;
                    else
                        parseArgs[i] = null;
                }
                _parseOptions = parseOptionsCtor.Invoke(parseArgs);

                // Build CSharpCompilationOptions — use the first constructor (has most defaults)
                var dllKind = Enum.Parse(_outputKindEnum, "DynamicallyLinkedLibrary");
                var compOptionsCtor = _compilationOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0];
                var compCtorParams = compOptionsCtor.GetParameters();
                var compArgs = new object[compCtorParams.Length];
                for (int i = 0; i < compCtorParams.Length; i++)
                {
                    if (compCtorParams[i].Name == "outputKind")
                        compArgs[i] = dllKind;
                    else if (compCtorParams[i].HasDefaultValue)
                        compArgs[i] = compCtorParams[i].DefaultValue;
                    else
                        compArgs[i] = null;
                }
                _compilationOptions = compOptionsCtor.Invoke(compArgs);

                return true;
            }
            catch (Exception e)
            {
                McpLog.Warn($"[ExecuteCode] Roslyn initialization failed: {e.Message}");
                return false;
            }
        }

        public static Assembly Compile(string source, string[] assemblyPaths, out List<string> errors)
        {
            errors = new List<string>();

            try
            {
                // Parse source
                var syntaxTree = _parseText.Invoke(null, new object[] { source, _parseOptions, null, null, default(System.Threading.CancellationToken) });

                // Build metadata references
                var metadataRefBase = _metadataReferenceType;
                var listType = typeof(List<>).MakeGenericType(metadataRefBase);
                var refs = (System.Collections.IList)Activator.CreateInstance(listType);

                foreach (var path in assemblyPaths)
                {
                    try
                    {
                        var cfParams = _createFromFile.GetParameters();
                        var cfArgs = new object[cfParams.Length];
                        cfArgs[0] = path; // string path
                        for (int i = 1; i < cfParams.Length; i++)
                            cfArgs[i] = cfParams[i].HasDefaultValue ? cfParams[i].DefaultValue : null;
                        var metaRef = _createFromFile.Invoke(null, cfArgs);
                        refs.Add(metaRef);
                    }
                    catch
                    {
                        // Skip assemblies that can't be loaded as metadata
                    }
                }

                // Build syntax tree array
                var syntaxTreeBase = ResolveType("Microsoft.CodeAnalysis.SyntaxTree", "Microsoft.CodeAnalysis");
                var treeArray = Array.CreateInstance(syntaxTreeBase, 1);
                treeArray.SetValue(syntaxTree, 0);

                // Create compilation
                var compilation = _createCompilation.Invoke(null, new object[] { "MCPDynamic", treeArray, refs, _compilationOptions });

                // Emit to memory
                using (var ms = new MemoryStream())
                {
                    // Build args for the Emit overload (fill non-stream params with defaults)
                    var emitParams = _emit.GetParameters();
                    var emitArgs = new object[emitParams.Length];
                    emitArgs[0] = ms; // peStream
                    for (int i = 1; i < emitParams.Length; i++)
                    {
                        if (emitParams[i].HasDefaultValue)
                            emitArgs[i] = emitParams[i].DefaultValue;
                        else
                            emitArgs[i] = null;
                    }
                    var emitResult = _emit.Invoke(compilation, emitArgs);

                    // Check emitResult.Success
                    var successProp = emitResult.GetType().GetProperty("Success");
                    bool success = (bool)successProp.GetValue(emitResult);

                    if (!success)
                    {
                        // Read emitResult.Diagnostics
                        var diagProp = emitResult.GetType().GetProperty("Diagnostics");
                        var diagnostics = (System.Collections.IEnumerable)diagProp.GetValue(emitResult);
                        var severityError = Enum.Parse(ResolveType("Microsoft.CodeAnalysis.DiagnosticSeverity", "Microsoft.CodeAnalysis"), "Error");

                        foreach (var diag in diagnostics)
                        {
                            var sevProp = diag.GetType().GetProperty("Severity");
                            var severity = sevProp.GetValue(diag);
                            if (!severity.Equals(severityError)) continue;

                            var locProp = diag.GetType().GetProperty("Location");
                            var loc = locProp.GetValue(diag);
                            var spanProp = loc.GetType().GetMethod("GetLineSpan");
                            var lineSpan = spanProp.Invoke(loc, null);
                            var startProp = lineSpan.GetType().GetProperty("StartLinePosition");
                            var startPos = startProp.GetValue(lineSpan);
                            var lineProp = startPos.GetType().GetProperty("Line");
                            int line = (int)lineProp.GetValue(startPos);

                            var msgProp = diag.GetType().GetMethod("GetMessage", new[] { typeof(System.Globalization.CultureInfo) });
                            string msg = (string)msgProp.Invoke(diag, new object[] { null });

                            int userLine = Math.Max(1, line + 1 - ExecuteCode.WrapperLineOffset);
                            errors.Add($"Line {userLine}: {msg}");
                        }
                        return null;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    return Assembly.Load(ms.ToArray());
                }
            }
            catch (Exception e)
            {
                // Walk to the deepest cause: TargetInvocationException (and friends) wrap the real
                // failure inside .InnerException, and reporting only e.Message hides everything
                // useful (e.g. a missing transitive dep manifests as the generic "Exception has been
                // thrown by the target of an invocation.").
                Exception root = e;
                while (root.InnerException != null) root = root.InnerException;
                errors.Add($"Roslyn compilation error: {root.GetType().Name}: {root.Message}");
                return null;
            }
        }
    }
}
