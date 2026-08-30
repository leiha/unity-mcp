using System;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Explicitly refreshes Unity's asset database and optionally requests a script compilation.
    /// This is side-effectful and should be treated as a tool.
    /// </summary>
    [McpForUnityTool("refresh_unity", AutoRegister = false)]
    public static class RefreshUnity
    {
        private const int DefaultWaitTimeoutSeconds = 60;

        /// <summary>
        /// PONT-18 — what an import in play mode actually costs, said in the reply rather than
        /// left for the caller to discover from an empty world. Null when there is nothing to warn
        /// about: a caveat that fires on every call stops being read.
        /// </summary>
        private static string PlayModeWarningOrNull(bool wasPlaying)
            => wasPlaying
                ? "⛔ THE EDITOR WAS IN PLAY MODE WHEN THIS REFRESH WAS ASKED FOR. If it triggers a "
                + "domain reload, YOUR RUNNING WORLD IS DESTROYED — the DI container is torn down — "
                + "AND UNITY STAYS IN PLAY MODE. Application.isPlaying will still be true, "
                + "is_compiling will go back to false, and the screen will still be painted: all "
                + "three read as 'it is fine' over a world that is gone. The only probe that bites "
                + "is di_state -> built. If you needed that world, stop and re-enter play mode "
                + "(~50 s) rather than measure anything on what is left. ⭐ And the ordering that "
                + "avoids this entirely: IMPORT FIRST, BUILD THE WORLD SECOND — never the reverse."
                : null;

        public static async Task<object> HandleCommand(JObject @params)
        {
            string mode = @params?["mode"]?.ToString() ?? "if_dirty";
            string scope = @params?["scope"]?.ToString() ?? "all";
            string compile = @params?["compile"]?.ToString() ?? "none";
            bool waitForReady = ParamCoercion.CoerceBool(@params?["wait_for_ready"], false);

            // ⛔⛔ PONT-18 — AN IMPORT IN PLAY MODE DESTROYS THE RUNNING WORLD, AND EVERY SIGNAL
            //   AFTERWARDS SAYS IT DID NOT. `[MEASURED 2026-08-30 02:17 by the `pont` seat and
            //   filed in PROBLEMES-DE-L-OUTIL.md, nature: RÉUSSITE QUI MENT.]`
            //   A refresh in play mode triggers a full domain reload — "Domain Reload Profiling:
            //   95962ms" in the editor log — which destroys the DI container, while Unity STAYS in
            //   play mode. What the caller then reads:
            //       Application.isPlaying     -> True      "the game is running"
            //       is_compiling              -> false     "it is done"
            //       the screen                -> painted   the whole HUD is still there
            //   and the world is gone: day 00, empty tree, empty stocks, a bare board. Nothing on
            //   screen says the world left. The only probe that bites is `di_state` -> built.
            // ⭐ READ BEFORE, NOT AFTER: by the time the reply is built, a reload may already have
            //   flipped this, and the caller would be told about a state that is no longer the one
            //   its own request walked into.
            // ⚠ This does NOT refuse the import — importing while playing is sometimes exactly what
            //   a seat wants. It refuses to stay SILENT about what the import costs.
            bool wasPlayingWhenAsked = EditorApplication.isPlaying;

            if (TestRunStatus.IsRunning)
            {
                return new ErrorResponse("tests_running", new
                {
                    reason = "tests_running",
                    retry_after_ms = 5000
                });
            }

            bool refreshTriggered = false;
            bool compileRequested = false;

            try
            {
                // Best-effort semantics: if_dirty currently behaves like force unless future dirty signals are added.
                bool shouldRefresh = string.Equals(mode, "force", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(mode, "if_dirty", StringComparison.OrdinalIgnoreCase);

                if (shouldRefresh)
                {
                    if (string.Equals(scope, "scripts", StringComparison.OrdinalIgnoreCase))
                    {
                        // For scripts, requesting compilation is usually the meaningful action.
                        // We avoid a heavyweight full refresh by default.
                    }
                    else
                    {
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                        refreshTriggered = true;
                    }
                }

                if (string.Equals(compile, "request", StringComparison.OrdinalIgnoreCase))
                {
                    CompilationPipeline.RequestScriptCompilation();
                    compileRequested = true;
                }

                if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) && !refreshTriggered)
                {
                    // If the caller asked for "all" and we skipped refresh above (e.g., scripts-only path),
                    // do a lightweight refresh now. Use ForceSynchronousImport to ensure the refresh
                    // completes before returning, preventing stalls when Unity is backgrounded.
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    refreshTriggered = true;
                }
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"refresh_failed: {ex.Message}");
            }

            // Unity 6+ fix: Skip wait_for_ready when compile was requested.
            // The EditorApplication.update polling in WaitForUnityReadyAsync doesn't survive
            // domain reloads properly in Unity 6+, causing infinite compilation loops.
            // When compilation is requested, return immediately and let client poll editor_state.
            // Earlier Unity versions retain the original behavior.
#if UNITY_6000_0_OR_NEWER
            bool shouldWaitForReady = waitForReady && !compileRequested;
#else
            bool shouldWaitForReady = waitForReady;
#endif
            if (shouldWaitForReady)
            {
                try
                {
                    await WaitForUnityReadyAsync(
                        TimeSpan.FromSeconds(DefaultWaitTimeoutSeconds)).ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    return new ErrorResponse("refresh_timeout_waiting_for_ready", new
                    {
                        refresh_triggered = refreshTriggered,
                        compile_requested = compileRequested,
                        resulting_state = "unknown",
                        play_mode_when_asked = wasPlayingWhenAsked,
                        play_mode_warning = PlayModeWarningOrNull(wasPlayingWhenAsked),
                    });
                }
                catch (Exception ex)
                {
                    return new ErrorResponse($"refresh_wait_failed: {ex.Message}");
                }
            }

            string resultingState = EditorApplication.isCompiling
                ? "compiling"
                : (EditorApplication.isUpdating ? "asset_import" : "idle");

            return new SuccessResponse(
                wasPlayingWhenAsked
                    ? "⛔ Refresh requested WHILE PLAYING — read play_mode_warning before trusting anything else."
                    : "Refresh requested.",
                new
                {
                    refresh_triggered = refreshTriggered,
                    compile_requested = compileRequested,
                    resulting_state = resultingState,
                    play_mode_when_asked = wasPlayingWhenAsked,
                    play_mode_warning = PlayModeWarningOrNull(wasPlayingWhenAsked),
                    hint = shouldWaitForReady
                        ? "Unity refresh completed; editor should be ready."
                        : "If Unity enters compilation/domain reload, poll editor_state until ready_for_tools is true."
                });
        }

        private static Task WaitForUnityReadyAsync(TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = DateTime.UtcNow;

            void Tick()
            {
                try
                {
                    if (tcs.Task.IsCompleted)
                    {
                        EditorApplication.update -= Tick;
                        return;
                    }

                    if ((DateTime.UtcNow - start) > timeout)
                    {
                        EditorApplication.update -= Tick;
                        tcs.TrySetException(new TimeoutException());
                        return;
                    }

                    if (!EditorApplication.isCompiling
                        && !EditorApplication.isUpdating
                        && !TestRunStatus.IsRunning
                        && !EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        EditorApplication.update -= Tick;
                        tcs.TrySetResult(true);
                    }
                }
                catch (Exception ex)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetException(ex);
                }
            }

            EditorApplication.update += Tick;
            // Nudge Unity to pump once in case update is throttled.
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
            return tcs.Task;
        }
    }
}
