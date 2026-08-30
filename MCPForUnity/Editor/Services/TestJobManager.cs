using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    internal enum TestJobStatus
    {
        Running,
        Succeeded,
        Failed
    }

    internal sealed class TestJobFailure
    {
        public string FullName { get; set; }
        public string Message { get; set; }
    }

    internal sealed class TestJob
    {
        public string JobId { get; set; }
        public TestJobStatus Status { get; set; }
        public string Mode { get; set; }
        public long StartedUnixMs { get; set; }
        public long? FinishedUnixMs { get; set; }
        public long LastUpdateUnixMs { get; set; }
        public int? TotalTests { get; set; }
        public int CompletedTests { get; set; }
        public string CurrentTestFullName { get; set; }
        public long? CurrentTestStartedUnixMs { get; set; }
        public string LastFinishedTestFullName { get; set; }
        public long? LastFinishedUnixMs { get; set; }
        public List<TestJobFailure> FailuresSoFar { get; set; }
        public string Error { get; set; }
        public TestRunResult Result { get; set; }
        public long InitTimeoutMs { get; set; }

        /// PONT-20. Whether the editor was in PLAY MODE at the moment this job was ASKED FOR.
        /// Read at StartJob and never again: by the time an initialization timeout fires, a
        /// domain reload may have flipped the editor's state, and the answer would then describe
        /// a world that is not the one the caller asked in. Same reason PONT-18 reads its play
        /// mode flag before the refresh rather than while building the reply.
        public bool StartedInPlayMode { get; set; }
    }

    /// <summary>
    /// The sentence a caller is told when a test job never started -- and NOTHING else.
    ///
    /// ⛔⛔ WHY IT IS NOT A METHOD ON TestJobManager, AND THE REASON WAS MEASURED, NOT REASONED.
    /// `[MEASURED 2026-08-30 09:2x: as a member of TestJobManager, its judge returned NO VERDICT
    ///  nine times -- "the Unity runtime cannot initialise UnityEngine.DebugLogHandler". Touching
    ///  ANY member of TestJobManager runs that type's static initialiser, which reaches into the
    ///  editor's logging; outside the editor it throws before a single assertion is read.]`
    /// ⇒ a pure sentence carried by a stateful type is NOT judgeable anywhere: it drags the whole
    ///   type in with it. Living alone, it is judged in any runtime, at any time, for nothing.
    /// ⚠ And note WHAT that cost looked like: not a red, a NO VERDICT -- a judge that never ran.
    ///   Had the runner reported it as a failure, somebody would have repaired a correct message.
    /// </summary>
    internal static class TestJobInitTimeoutMessage
    {
        /// <summary>
        /// Builds what a caller is told when a test job never started. It is a pure function of
        /// four values on purpose: the sentence it produces is the whole point of PONT-20/21, and
        /// a sentence buried inside TestJobManager.GetJob could only be judged by a test able to
        /// drive the editor's compile/update state. This one is judged anywhere.
        ///
        /// ⛔ PONT-20 -- WHY IT NAMES PLAY MODE, AND WHAT THAT CLAIM IS WORTH.
        ///   "failed to initialize within 15000ms" named nothing a caller could act on, and the
        ///   single most common circumstance around it in this workshop -- somebody holding play
        ///   mode -- appeared in no field of the reply and in no word of the message. The tool does
        ///   not refuse (asking for tests during play mode is legitimate); it refuses to stay
        ///   silent. ⚠ It reports the editor's state at ASK time. It does NOT claim to have proven
        ///   that play mode caused this particular timeout -- that correlation was observed on
        ///   2026-08-30 and explicitly recorded as NOT established. Naming a suspect is useful;
        ///   dressing it as a cause would send somebody to repair the wrong thing.
        ///
        /// ⛔ PONT-21 -- THE TWO THINGS THE MESSAGE STILL HID, AND THAT NO CALLER COULD DERIVE.
        ///   `[MEASURED 2026-08-30 08:0x: the `plateau` seat hit this exact refusal, read `status:
        ///    failed` and asked whether its tests were red. They had never run.]`
        ///
        ///   ① `status: failed` names the JOB, never your assertions. A caller who reads `failed`
        ///      as a red verdict goes and repairs code that is fine. Same shape as this workshop's
        ///      `total: 0 + Passed` trap -- a NULL verdict wearing a red coat.
        ///      ⛔⛔ AND THE FIRST DRAFT OF THIS VERY MESSAGE GOT IT WRONG IN THE OTHER DIRECTION.
        ///      It said "ZERO test cases ran". `TotalTests == null` does NOT say that: it says
        ///      THIS JOB SAW NOTHING, and those two are indistinguishable from in here.
        ///      `[MEASURED 2026-08-30 by the `plateau` seat, who retracted his own premise to us:
        ///       his job was reported `failed · completed: 0 · failures_so_far: []` while
        ///       TestResults.xml, written 3 minutes later, carried his five cases, all PASSED.]`
        ///      ⇒ a message that asserts "nothing ran" sends a caller to re-run a green suite and
        ///      spend a screen slot for nothing. The tool must report what it OBSERVED and name
        ///      the artefact that settles it -- never conclude for the caller.
        ///
        ///   ② `init_timeout` EXISTS. It is a first-class parameter of run_tests
        ///      (Server/src/services/tools/run_tests.py) that lands in InitTimeoutMs and is read by
        ///      the caller of this method -- and no failure path had ever named it.
        ///      ⭐ A capability that a refusal does not name does not exist: the caller cannot
        ///      discover from the refusal the one knob that answers it, so he retries the identical
        ///      call, or gives up on the tool.
        ///
        ///   The waited value AND its origin are both reported, because "I waited 15000ms" and
        ///   "I waited the 15000ms you asked for" call for opposite next moves.
        ///
        /// HOW TO PROVE THESE ASSERTIONS CAN REDDEN
        ///   The judge is Energeia.Pont.Tests, ATestJobThatNeverRanSaysSoAndNamesTheKnobTests.
        ///   PREDICTION -- not yet played (this method has never returned through the real bridge).
        ///   MUT-1  drop the `timeoutWasAskedFor` branch (always emit the default wording)
        ///          ⇒ expected RED: TheOriginOfTheWait_IsNotTheSameSentenceInBothCases,
        ///                          AnAskedForWait_SaysTheCallerAskedForIt
        ///          ⇒ expected GREEN: the observed-not-concluded case and the knob-naming case
        ///   MUT-2  drop the word `init_timeout` from the sentence
        ///          ⇒ expected RED: TheRefusalNamesTheKnobThatAnswersIt, and it ALONE
        ///   MUT-3  always emit the "not been written since" wording, whatever the mtime
        ///          ⇒ expected RED: AResultFileWrittenAfterTheJobStarted_IsPointedAt,
        ///                          TheResultFileNote_IsNotTheSameSentenceInBothCases
        /// </summary>
        /// <param name="resultsPath">
        /// Where the test runner writes its result file, or null when we cannot name it. This is
        /// the ONLY artefact that separates "nothing ran" from "the start notification was lost",
        /// and it costs nothing to point at.
        /// </param>
        /// <param name="resultsWrittenUnixMs">Last-write time of that file, 0 when absent.</param>
        /// <param name="jobStartedUnixMs">When this job was asked for.</param>
        internal static string BuildInitializationTimeoutError(
            long waitedMs,
            bool timeoutWasAskedFor,
            bool startedInPlayMode,
            string mode,
            string resultsPath,
            long resultsWrittenUnixMs,
            long jobStartedUnixMs)
        {
            string waitedNote = timeoutWasAskedFor
                ? $"Waited {waitedMs} ms -- the value you passed as init_timeout."
                : $"Waited {waitedMs} ms -- the default; nobody asked for a longer one.";

            string playModeNote = startedInPlayMode
                ? $" The editor was in PLAY MODE when this job was asked for, and this job asked for {mode} tests. That is the first thing to check -- not a proven cause."
                : string.Empty;

            string resultsNote;
            if (string.IsNullOrEmpty(resultsPath))
            {
                resultsNote = string.Empty;
            }
            else if (resultsWrittenUnixMs > jobStartedUnixMs)
            {
                resultsNote = $" ⭐ READ THIS BEFORE RE-RUNNING ANYTHING: {resultsPath} was written "
                            + $"{resultsWrittenUnixMs - jobStartedUnixMs} ms AFTER this job was asked for. "
                            + "Cases may well have run and finished while this job saw nothing. It carries the "
                            + "case names and the outcome -- that file settles it, this message does not. "
                            + "(A newer file is not proof that it is YOUR run: confront the case names.)";
            }
            else
            {
                resultsNote = $" The runner's result file ({resultsPath}) has not been written since this job "
                            + "was asked for -- consistent with nothing having run, though not proof of it.";
            }

            return "Test job failed to INITIALIZE: this bridge never saw the test runner start. "
                 + "⚠ That is NOT the same as \"no test ran\": `completed: 0`, a null `total` and an empty "
                 + "`failures_so_far` say what THIS JOB OBSERVED. They are not a verdict on your assertions, "
                 + "and they do not separate \"nothing ran\" from \"cases ran and the start notification was lost\"."
                 + resultsNote
                 + " " + waitedNote
                 + " Raise the wait with the `init_timeout` parameter of run_tests (milliseconds, e.g. init_timeout: 120000) "
                 + "when a domain reload, an import or a busy editor may be eating the first seconds."
                 + playModeNote;
        }

    }

    /// <summary>
    /// Tracks async test jobs started via MCP tools. This is not intended to capture manual Test Runner UI runs.
    /// </summary>
    internal static class TestJobManager
    {
        // Keep this small to avoid ballooning payloads during polling.
        private const int FailureCap = 25;
        private const long StuckThresholdMs = 60_000;
        private const long DefaultInitializationTimeoutMs = 15_000; // 15 seconds default; override per-job via run_tests init_timeout param
        private const long MaxInitializationTimeoutMs = 600_000; // 10 minutes hard cap
        private const int MaxJobsToKeep = 10;
        private const long MinPersistIntervalMs = 1000; // Throttle persistence to reduce overhead

        // SessionState survives domain reloads within the same Unity Editor session.
        private const string SessionKeyJobs = "MCPForUnity.TestJobsV1";
        private const string SessionKeyCurrentJobId = "MCPForUnity.CurrentTestJobIdV1";

        private static readonly object LockObj = new();
        private static readonly Dictionary<string, TestJob> Jobs = new();
        private static string _currentJobId;
        private static long _lastPersistUnixMs;

        static TestJobManager()
        {
            // Restore after domain reloads (e.g., compilation while a job is running).
            TryRestoreFromSessionState();
        }

        public static string CurrentJobId
        {
            get { lock (LockObj) return _currentJobId; }
        }

        public static bool HasRunningJob
        {
            get
            {
                lock (LockObj)
                {
                    return !string.IsNullOrEmpty(_currentJobId);
                }
            }
        }

        /// <summary>
        /// Force-clears any stuck or orphaned test job. Call this when tests get stuck due to
        /// assembly reloads or other interruptions.
        /// </summary>
        /// <returns>True if a job was cleared, false if no running job exists.</returns>
        public static bool ClearStuckJob()
        {
            bool cleared = false;
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId))
                {
                    return false;
                }

                if (Jobs.TryGetValue(_currentJobId, out var job) && job.Status == TestJobStatus.Running)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    job.Status = TestJobStatus.Failed;
                    job.Error = "Job cleared manually (stuck or orphaned)";
                    job.FinishedUnixMs = now;
                    job.LastUpdateUnixMs = now;
                    McpLog.Warn($"[TestJobManager] Manually cleared stuck job {_currentJobId}");
                    cleared = true;
                }

                _currentJobId = null;
            }
            PersistToSessionState(force: true);
            return cleared;
        }

        private sealed class PersistedState
        {
            public string current_job_id { get; set; }
            public List<PersistedJob> jobs { get; set; }
        }

        private sealed class PersistedJob
        {
            public string job_id { get; set; }
            public string status { get; set; }
            public string mode { get; set; }
            public long started_unix_ms { get; set; }
            public long? finished_unix_ms { get; set; }
            public long last_update_unix_ms { get; set; }
            public int? total_tests { get; set; }
            public int completed_tests { get; set; }
            public string current_test_full_name { get; set; }
            public long? current_test_started_unix_ms { get; set; }
            public string last_finished_test_full_name { get; set; }
            public long? last_finished_unix_ms { get; set; }
            public List<TestJobFailure> failures_so_far { get; set; }
            public string error { get; set; }
            public long init_timeout_ms { get; set; }
            // PONT-20. Persisted on purpose: a job asked for during play mode is exactly the
            // job most likely to cross a domain reload, and losing the flag there would drop it
            // from the only message that ever mentions it.
            public bool started_in_play_mode { get; set; }
        }

        private static TestJobStatus ParseStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return TestJobStatus.Running;
            }

            string s = status.Trim().ToLowerInvariant();
            return s switch
            {
                "succeeded" => TestJobStatus.Succeeded,
                "failed" => TestJobStatus.Failed,
                _ => TestJobStatus.Running
            };
        }

        private static void TryRestoreFromSessionState()
        {
            try
            {
                string json = SessionState.GetString(SessionKeyJobs, string.Empty);
                if (string.IsNullOrWhiteSpace(json))
                {
                    var legacy = SessionState.GetString(SessionKeyCurrentJobId, string.Empty);
                    _currentJobId = string.IsNullOrWhiteSpace(legacy) ? null : legacy;
                    return;
                }

                var state = JsonConvert.DeserializeObject<PersistedState>(json);
                if (state?.jobs == null)
                {
                    return;
                }

                lock (LockObj)
                {
                    Jobs.Clear();
                    foreach (var pj in state.jobs)
                    {
                        if (pj == null || string.IsNullOrWhiteSpace(pj.job_id))
                        {
                            continue;
                        }

                        Jobs[pj.job_id] = new TestJob
                        {
                            JobId = pj.job_id,
                            Status = ParseStatus(pj.status),
                            Mode = pj.mode,
                            StartedUnixMs = pj.started_unix_ms,
                            FinishedUnixMs = pj.finished_unix_ms,
                            LastUpdateUnixMs = pj.last_update_unix_ms,
                            TotalTests = pj.total_tests,
                            CompletedTests = pj.completed_tests,
                            CurrentTestFullName = pj.current_test_full_name,
                            CurrentTestStartedUnixMs = pj.current_test_started_unix_ms,
                            LastFinishedTestFullName = pj.last_finished_test_full_name,
                            LastFinishedUnixMs = pj.last_finished_unix_ms,
                            FailuresSoFar = pj.failures_so_far ?? new List<TestJobFailure>(),
                            Error = pj.error,
                            InitTimeoutMs = pj.init_timeout_ms,
                            StartedInPlayMode = pj.started_in_play_mode,
                            // Intentionally not persisted to avoid ballooning SessionState.
                            Result = null
                        };
                    }

                    _currentJobId = string.IsNullOrWhiteSpace(state.current_job_id) ? null : state.current_job_id;
                    if (!string.IsNullOrEmpty(_currentJobId) && !Jobs.ContainsKey(_currentJobId))
                    {
                        _currentJobId = null;
                    }

                    // Detect and clean up stale "running" jobs that were orphaned by domain reload.
                    // After a domain reload, TestRunStatus resets to not-running, but _currentJobId
                    // may still be set. If the job hasn't been updated recently, it's likely orphaned.
                    if (!string.IsNullOrEmpty(_currentJobId) && Jobs.TryGetValue(_currentJobId, out var currentJob))
                    {
                        if (currentJob.Status == TestJobStatus.Running)
                        {
                            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            long staleCutoffMs = 5 * 60 * 1000; // 5 minutes
                            if (now - currentJob.LastUpdateUnixMs > staleCutoffMs)
                            {
                                McpLog.Warn($"[TestJobManager] Clearing stale job {_currentJobId} (last update {(now - currentJob.LastUpdateUnixMs) / 1000}s ago)");
                                currentJob.Status = TestJobStatus.Failed;
                                currentJob.Error = "Job orphaned after domain reload";
                                currentJob.FinishedUnixMs = now;
                                _currentJobId = null;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Restoration is best-effort; never block editor load.
                McpLog.Warn($"[TestJobManager] Failed to restore SessionState: {ex.Message}");
            }
        }

        private static void PersistToSessionState(bool force = false)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            // Throttle non-critical updates to reduce overhead during large test runs
            if (!force && (now - _lastPersistUnixMs) < MinPersistIntervalMs)
            {
                return;
            }
            
            try
            {
                PersistedState snapshot;
                lock (LockObj)
                {
                    var jobs = Jobs.Values
                        .OrderByDescending(j => j.LastUpdateUnixMs)
                        .Take(MaxJobsToKeep)
                        .Select(j => new PersistedJob
                        {
                            job_id = j.JobId,
                            status = j.Status.ToString().ToLowerInvariant(),
                            mode = j.Mode,
                            started_unix_ms = j.StartedUnixMs,
                            finished_unix_ms = j.FinishedUnixMs,
                            last_update_unix_ms = j.LastUpdateUnixMs,
                            total_tests = j.TotalTests,
                            completed_tests = j.CompletedTests,
                            current_test_full_name = j.CurrentTestFullName,
                            current_test_started_unix_ms = j.CurrentTestStartedUnixMs,
                            last_finished_test_full_name = j.LastFinishedTestFullName,
                            last_finished_unix_ms = j.LastFinishedUnixMs,
                            failures_so_far = (j.FailuresSoFar ?? new List<TestJobFailure>()).Take(FailureCap).ToList(),
                            error = j.Error,
                            init_timeout_ms = j.InitTimeoutMs,
                            started_in_play_mode = j.StartedInPlayMode
                        })
                        .ToList();

                    snapshot = new PersistedState
                    {
                        current_job_id = _currentJobId,
                        jobs = jobs
                    };
                }

                SessionState.SetString(SessionKeyCurrentJobId, snapshot.current_job_id ?? string.Empty);
                SessionState.SetString(SessionKeyJobs, JsonConvert.SerializeObject(snapshot));
                _lastPersistUnixMs = now;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestJobManager] Failed to persist SessionState: {ex.Message}");
            }
        }

        public static string StartJob(TestMode mode, TestFilterOptions filterOptions = null, long initTimeoutMs = 0)
        {
            // Clamp to valid range: non-positive values mean "use default", cap at 10 minutes
            if (initTimeoutMs < 0) initTimeoutMs = 0;
            if (initTimeoutMs > MaxInitializationTimeoutMs) initTimeoutMs = MaxInitializationTimeoutMs;

            string jobId = Guid.NewGuid().ToString("N");
            long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string modeStr = mode.ToString();

            var job = new TestJob
            {
                JobId = jobId,
                Status = TestJobStatus.Running,
                Mode = modeStr,
                StartedUnixMs = started,
                FinishedUnixMs = null,
                LastUpdateUnixMs = started,
                TotalTests = null,
                CompletedTests = 0,
                CurrentTestFullName = null,
                CurrentTestStartedUnixMs = null,
                LastFinishedTestFullName = null,
                LastFinishedUnixMs = null,
                FailuresSoFar = new List<TestJobFailure>(),
                Error = null,
                Result = null,
                InitTimeoutMs = initTimeoutMs,
                StartedInPlayMode = EditorApplication.isPlaying
            };

            // Single lock scope for check-and-set to avoid TOCTOU race
            lock (LockObj)
            {
                if (!string.IsNullOrEmpty(_currentJobId))
                {
                    throw new InvalidOperationException("A Unity test run is already in progress.");
                }
                Jobs[jobId] = job;
                _currentJobId = jobId;
            }
            PersistToSessionState(force: true);

            // Kick the run (must be called on main thread; our command handlers already run there).
            Task<TestRunResult> task = MCPServiceLocator.Tests.RunTestsAsync(mode, filterOptions);

            void FinalizeJob(Action finalize)
            {
                // Ensure state mutation happens on main thread to avoid Unity API surprises.
                EditorApplication.delayCall += () =>
                {
                    try { finalize(); }
                    catch (Exception ex) { McpLog.Error($"[TestJobManager] Finalize failed: {ex.Message}\n{ex.StackTrace}"); }
                };
            }

            task.ContinueWith(t =>
            {
                // NOTE: We now finalize jobs deterministically from the TestRunnerService RunFinished callback.
                // This continuation is retained as a safety net in case RunFinished is not delivered.
                FinalizeJob(() => FinalizeFromTask(jobId, t));
            }, TaskScheduler.Default);

            return jobId;
        }

        public static void FinalizeCurrentJobFromRunFinished(TestRunResult resultPayload)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.FinishedUnixMs = now;
                job.Status = resultPayload != null && resultPayload.Failed > 0
                    ? TestJobStatus.Failed
                    : TestJobStatus.Succeeded;
                job.Error = null;
                job.Result = resultPayload;
                job.CurrentTestFullName = null;
                _currentJobId = null;
            }
            PersistToSessionState(force: true);
        }

        public static void OnRunStarted(int? totalTests)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.TotalTests = totalTests;
                job.CompletedTests = 0;
                job.CurrentTestFullName = null;
                job.CurrentTestStartedUnixMs = null;
                job.LastFinishedTestFullName = null;
                job.LastFinishedUnixMs = null;
                job.FailuresSoFar ??= new List<TestJobFailure>();
                job.FailuresSoFar.Clear();
            }
            PersistToSessionState(force: true);
        }

        public static void OnTestStarted(string testFullName)
        {
            if (string.IsNullOrWhiteSpace(testFullName))
            {
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CurrentTestFullName = testFullName;
                job.CurrentTestStartedUnixMs = now;
            }
            PersistToSessionState();
        }

        public static void OnLeafTestFinished(string testFullName, bool isFailure, string message)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CompletedTests = Math.Max(0, job.CompletedTests + 1);
                job.LastFinishedTestFullName = testFullName;
                job.LastFinishedUnixMs = now;

                if (isFailure)
                {
                    job.FailuresSoFar ??= new List<TestJobFailure>();
                    if (job.FailuresSoFar.Count < FailureCap)
                    {
                        job.FailuresSoFar.Add(new TestJobFailure
                        {
                            FullName = testFullName,
                            Message = string.IsNullOrWhiteSpace(message) ? "Test failed" : message
                        });
                    }
                }
            }
            PersistToSessionState();
        }

        public static void OnRunFinished()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CurrentTestFullName = null;
            }
            PersistToSessionState(force: true);
        }

        /// <summary>
        /// Where Unity's test runner drops its result file by default. Returned as a path plus a
        /// last-write time, never as a verdict: this method does not know whose run wrote it.
        /// </summary>
        private static void ReadRunnerResultFile(out string path, out long writtenUnixMs)
        {
            path = null;
            writtenUnixMs = 0;
            try
            {
                // ⚠ Fully qualified on purpose: this file does not `using UnityEngine`, and adding
                // it would drag UnityEngine.Debug/Object into a file that already lives in
                // UnityEditor. ⚠ persistentDataPath throws off the main thread; the catch below
                // then leaves the path null, and a hint we could not verify is simply not given.
                string candidate = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "TestResults.xml");
                path = candidate;
                var info = new System.IO.FileInfo(candidate);
                if (info.Exists)
                {
                    writtenUnixMs = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                }
            }
            catch (Exception)
            {
                // A path we cannot stat is a path we do not name: an unreadable hint is worse than
                // none, because a caller would go looking for a file we never checked.
                path = null;
                writtenUnixMs = 0;
            }
        }

        internal static TestJob GetJob(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            TestJob jobToReturn = null;
            bool shouldPersist = false;
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out var job))
                {
                    return null;
                }

                // Check if job is stuck in "running" state without having called OnRunStarted (TotalTests still null).
                // This happens when tests fail to initialize (e.g., unsaved scene, compilation issues).
                // After 15 seconds without initialization, auto-fail the job to prevent hanging.
                if (job.Status == TestJobStatus.Running && job.TotalTests == null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long initTimeout = job.InitTimeoutMs > 0 ? job.InitTimeoutMs : DefaultInitializationTimeoutMs;
                    if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && now - job.StartedUnixMs > initTimeout)
                    {
                        ReadRunnerResultFile(out string resultsPath, out long resultsWrittenUnixMs);
                        string initError = TestJobInitTimeoutMessage.BuildInitializationTimeoutError(
                            initTimeout,
                            timeoutWasAskedFor: job.InitTimeoutMs > 0,
                            startedInPlayMode: job.StartedInPlayMode,
                            mode: job.Mode,
                            resultsPath: resultsPath,
                            resultsWrittenUnixMs: resultsWrittenUnixMs,
                            jobStartedUnixMs: job.StartedUnixMs);
                        McpLog.Warn($"[TestJobManager] Job {jobId} failed to initialize within {initTimeout}ms, auto-failing. {initError}");
                        job.Status = TestJobStatus.Failed;
                        job.Error = initError;
                        job.FinishedUnixMs = now;
                        job.LastUpdateUnixMs = now;
                        if (_currentJobId == jobId)
                        {
                            _currentJobId = null;
                            // Keep TestRunStatus in sync: when initialization times out, neither
                            // RunStarted nor RunFinished fires, so the running flag would otherwise leak.
                            // Only clear it if this job is still the active one — a newer job may have taken over.
                            TestRunStatus.MarkFinished();
                        }
                        shouldPersist = true;
                    }
                }

                jobToReturn = job;
            }

            if (shouldPersist)
            {
                PersistToSessionState(force: true);
            }
            return jobToReturn;
        }

        internal static object ToSerializable(TestJob job, bool includeDetails, bool includeFailedTests)
        {
            if (job == null)
            {
                return null;
            }

            object resultPayload = null;
            if (job.Status == TestJobStatus.Succeeded && job.Result != null)
            {
                resultPayload = job.Result.ToSerializable(job.Mode, includeDetails, includeFailedTests);
            }

            return new
            {
                job_id = job.JobId,
                status = job.Status.ToString().ToLowerInvariant(),
                mode = job.Mode,
                started_unix_ms = job.StartedUnixMs,
                finished_unix_ms = job.FinishedUnixMs,
                last_update_unix_ms = job.LastUpdateUnixMs,
                progress = new
                {
                    completed = job.CompletedTests,
                    total = job.TotalTests,
                    current_test_full_name = job.CurrentTestFullName,
                    current_test_started_unix_ms = job.CurrentTestStartedUnixMs,
                    last_finished_test_full_name = job.LastFinishedTestFullName,
                    last_finished_unix_ms = job.LastFinishedUnixMs,
                    stuck_suspected = IsStuck(job),
                    editor_is_focused = InternalEditorUtility.isApplicationActive,
                    blocked_reason = GetBlockedReason(job),
                    failures_so_far = BuildFailuresPayload(job.FailuresSoFar),
                    failures_capped = (job.FailuresSoFar != null && job.FailuresSoFar.Count >= FailureCap)
                },
                error = job.Error,
                result = resultPayload
            };
        }

        private static string GetBlockedReason(TestJob job)
        {
            if (job == null || job.Status != TestJobStatus.Running)
            {
                return null;
            }

            if (!IsStuck(job))
            {
                return null;
            }

            // This matches the real-world symptom you observed: background Unity can get heavily throttled by OS/Editor.
            if (!InternalEditorUtility.isApplicationActive)
            {
                return "editor_unfocused";
            }

            if (EditorApplication.isCompiling)
            {
                return "compiling";
            }

            if (EditorApplication.isUpdating)
            {
                return "asset_import";
            }

            return "unknown";
        }

        private static bool IsStuck(TestJob job)
        {
            if (job == null || job.Status != TestJobStatus.Running)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(job.CurrentTestFullName) || !job.CurrentTestStartedUnixMs.HasValue)
            {
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return (now - job.CurrentTestStartedUnixMs.Value) > StuckThresholdMs;
        }

        private static object[] BuildFailuresPayload(List<TestJobFailure> failures)
        {
            if (failures == null || failures.Count == 0)
            {
                return Array.Empty<object>();
            }

            var list = new object[failures.Count];
            for (int i = 0; i < failures.Count; i++)
            {
                var f = failures[i];
                list[i] = new { full_name = f?.FullName, message = f?.Message };
            }
            return list;
        }

        private static void FinalizeFromTask(string jobId, Task<TestRunResult> task)
        {
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out var existing))
                {
                    if (_currentJobId == jobId) _currentJobId = null;
                    return;
                }

                // If RunFinished already finalized the job, do nothing.
                if (existing.Status != TestJobStatus.Running)
                {
                    if (_currentJobId == jobId) _currentJobId = null;
                    return;
                }

                existing.LastUpdateUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                existing.FinishedUnixMs = existing.LastUpdateUnixMs;

                if (task.IsFaulted)
                {
                    existing.Status = TestJobStatus.Failed;
                    existing.Error = task.Exception?.GetBaseException()?.Message ?? "Unknown test job failure";
                    existing.Result = null;
                }
                else if (task.IsCanceled)
                {
                    existing.Status = TestJobStatus.Failed;
                    existing.Error = "Test job canceled";
                    existing.Result = null;
                }
                else
                {
                    var result = task.Result;
                    existing.Status = result != null && result.Failed > 0
                        ? TestJobStatus.Failed
                        : TestJobStatus.Succeeded;
                    existing.Error = null;
                    existing.Result = result;
                }

                if (_currentJobId == jobId)
                {
                    _currentJobId = null;
                }
            }
            PersistToSessionState(force: true);
        }
    }
}

