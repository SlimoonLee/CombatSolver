using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;

namespace OfflineSearchHarness;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] rawArgs)
    {
        if (rawArgs.Length == 3 && rawArgs[0] == "--compare-quality-batch")
            return QualityComparison.Run(rawArgs[1], rawArgs[2]);
        if (rawArgs.Length == 2 && rawArgs[0] == "--ranking-schema")
            return RankingChecks.WriteSchema(rawArgs[1]);
        if (rawArgs.Length == 3 && rawArgs[0] == "--check-ranking")
            return RankingChecks.Run(rawArgs[1], rawArgs[2]);
        HarnessOptions options;
        try
        {
            options = HarnessOptions.Parse(rawArgs);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(HarnessOptions.Usage);
            return 2;
        }

        HarnessLog.VerboseGameLog = options.VerboseGameLog;
        HarnessLog.Language = options.Language;
        Directory.CreateDirectory(options.OutputDirectory);

        List<StepRecord> steps = [];
        Dictionary<string, object?> payload = new()
        {
            ["label"] = options.Label,
            ["scenario"] = options.Scenario,
            ["requestPath"] = options.RequestPath,
            ["milestone"] = options.Milestone,
        };

        string reached = "none";
        int exitCode = 0;
        IDisposable? choiceScope = null;
        IDisposable? observedChoiceScope = null;
        Stopwatch requestWatch = Stopwatch.StartNew();
        TimeSpan RequestTimeout(int phaseLimitSeconds)
        {
            TimeSpan phaseLimit = TimeSpan.FromSeconds(phaseLimitSeconds);
            if (!options.PlayCombat) return phaseLimit;
            TimeSpan remaining = TimeSpan.FromSeconds(options.CombatTimeoutSeconds) - requestWatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Native rollout request deadline exhausted.");
            return remaining < phaseLimit ? remaining : phaseLimit;
        }
        try
        {
            MainLoopContext loop = new();
            SynchronizationContext.SetSynchronizationContext(loop);

            Step(steps, "M0.1 装 Godot 绕过补丁", () =>
            {
                GameBootstrap.ApplyGodotBypasses();
                int nodeCctors = GameBootstrap.SkipGodotNodeStaticConstructors();
                return $"{GameBootstrap.Bypasses.Count} 处绕过（含 {nodeCctors} 个节点静态构造）";
            });
            Step(steps, "M0.2 初始化游戏静态状态", GameBootstrap.InitializeStaticState);
            Step(steps, "M0.3 初始化模组运行期状态", () => ModRuntime.Initialize(options));
            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_PROBE_STATICS") is { Length: > 0 } filter)
                Step(steps, "P 静态构造探针", () => $"types={GameBootstrap.ProbeStaticConstructors(filter)}");
            GeneratedScenarioSetup? generated = null;
            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_MEMORY_DISPLAY_CHECKS") == "1")
                MemoryDisplayChecks.Run();
            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_MEMORY_DISPLAY_CHECKS") == "baseline")
                MemoryDisplayChecks.ReproduceOldCapacity();
            UnattendedTestRunner.OfflineScenarioSession? session = null;
            CombatState? combat = null;
            ObservedCardSelector? observedSelector = null;
            HistoryCombatSetup? history = null;
            if (options.PlayCombat || options.HistoryRunPath != null)
            {
                observedSelector = new ObservedCardSelector(options.NativeChoicePolicy);
                observedChoiceScope = CardSelectCmd.PushSelector(observedSelector, localOnly: true);
            }

            if (options.RunSavePath != null || options.CombatReplayPath != null || options.HistoryRunPath != null)
            {
                session = UnattendedTestRunner.OfflineScenarioSession.Create(new UnattendedTestRequest());
                Step(steps, "S1.1 从玩家原生材料进入战斗", () =>
                {
                    TimeSpan importTimeout = RequestTimeout(30);
                    if (options.HistoryRunPath != null)
                    {
                        history = new HistoryCombatSetup(options.HistoryRunPath, observedSelector!);
                        loop.RunUntilCompleted(history.EnterFirstAsync(), importTimeout, "native history prefix");
                        payload["historyGuidance"] = history.Guidance;
                    }
                    else
                    {
                        Task<object> entering = SavedCombatSetup.EnterAsync(options);
                        loop.RunUntilCompleted(entering, importTimeout, "native save import");
                        payload["saveImport"] = entering.GetAwaiter().GetResult();
                    }
                    combat = OfflineCombat.WaitForPlayableCombat(loop, RequestTimeout(120));
                    return OfflineCombat.DescribeRoot(combat);
                });
            }
            else if (options.RequestPath != null)
            {
                Step(steps, "G1.1 解析生成场景请求", () =>
                {
                    UnattendedTestRequest request = GeneratedScenarioSetup.ReadRequest(options.RequestPath);
                    generated = GeneratedScenarioSetup.Prepare(
                        request, Path.Combine(options.OutputDirectory, "evidence"));
                    session = generated.Session;
                    return $"character={generated.Request.CharacterId} encounter={generated.Request.EncounterId} "
                        + $"generated={generated.Resolved != null}";
                });
                payload["scenario"] = new HarnessScenario(
                    generated!.Request.CharacterId, generated.Request.EncounterId,
                    generated.Request.Seed, generated.Request.Ascension,
                    generated.Request.ActIndexForTest);
                payload["resolvedScenario"] = generated!.Resolved?.Options;
                payload["catalogFingerprint"] = generated.Resolved?.CatalogFingerprint;

                Step(steps, "G1.2 建跑局、注入装备、进遭遇战房间", () =>
                {
                    TimeSpan enterTimeout = RequestTimeout(300);
                    Task<IDisposable?> enter = generated!.EnterCombatRoomAsync();
                    loop.RunUntilCompleted(enter, enterTimeout, "生成场景进房");
                    choiceScope = enter.GetAwaiter().GetResult();
                    return $"pumped={loop.PumpedCallbacks}";
                });
                Step(steps, "G1.3 推进到玩家第一回合", () =>
                {
                    combat = OfflineCombat.WaitForPlayableCombat(loop, RequestTimeout(120));
                    generated!.CaptureOpening(combat);
                    if (!generated.Request.PreserveNativeCombatStateForTest)
                    {
                        TimeSpan injectTimeout = RequestTimeout(30);
                        loop.RunUntilCompleted(generated.Session.InjectInitialStateAsync(
                            combat, MegaCrit.Sts2.Core.Context.LocalContext.GetMe(combat)!),
                            injectTimeout, "固定夹具状态注入");
                    }
                    return OfflineCombat.DescribeRoot(combat);
                });
                payload["setupChoices"] = generated.SetupChoices;
                if (options.PlayCombat)
                {
                    choiceScope?.Dispose();
                    choiceScope = null;
                }
            }
            else
            {
                session = UnattendedTestRunner.OfflineScenarioSession.Create(new UnattendedTestRequest());
                Step(steps, "M1.1 建跑局并进入遭遇战房间", () =>
                {
                    TimeSpan enterTimeout = RequestTimeout(180);
                    Task enter = OfflineCombat.EnterCombatRoomAsync(options.Scenario);
                    loop.RunUntilCompleted(enter, enterTimeout, "EnterRoomDebug");
                    return $"pumped={loop.PumpedCallbacks}";
                });
                Step(steps, "M1.2 推进到玩家第一回合", () =>
                {
                    combat = OfflineCombat.WaitForPlayableCombat(loop, RequestTimeout(120));
                    return OfflineCombat.DescribeRoot(combat);
                });
            }

            reached = "M1";
            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_HISTORY_CHECKS") == "1")
                HistoryCounterChecks.Run(combat!, options.OutputDirectory);
            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_INFUSED_CORE_CHECKS") == "1")
                InfusedCoreChecks.Run(combat!, options.OutputDirectory);
            payload["budget"] = DescribeBudget(options);
            payload["root"] = OfflineCombat.DescribeRoot(combat!);
            string diagnostics = options.BlindSamples > 0
                ? OfflineCombat.DescribeRoot(combat!)
                : ModRuntime.DescribeStart(combat!);
            payload["rootDiagnostics"] = diagnostics;
            File.WriteAllText(Path.Combine(options.OutputDirectory, "root-diagnostics.txt"), diagnostics);
            WriteProgress(options, "M1", "ok", "已到达玩家第一回合");

            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FIXED_PREFIX_CONTINUATIONS") == "1")
            {
                Step(steps, "M2 固定长前缀续用测量", () =>
                {
                    payload["fixedPrefixContinuations"] = FixedPrefixContinuationBenchmark.Run(combat!, options, loop);
                    return "N4/N8/N17; fixed-prefix-continuations.json";
                });
                reached = "M2";
            }
            else if (options.Milestone != "M1" && !options.PlayCombat)
            {
                ModRuntime.SearchOutcome? outcome = null;
                using MemorySampler memory = new(TimeSpan.FromMilliseconds(100));
                Step(steps, $"M2.1 跑一次固定预算搜索（{options.SearchMode}）", () =>
                {
                    outcome = ModRuntime.RunSearchDetailed(combat!, options, loop, session);
                    var solver = outcome.LegacyMetrics;
                    return $"boundary={solver["boundary"]} total_expanded={solver["totalExpanded"]} "
                        + $"total_transitions={solver["totalTransitions"]} score={solver["score"]} "
                        + $"projected_battle_hp_lost={solver["projectedBattleHpLost"]} "
                        + $"wall_s={outcome.WallSeconds:0.00}";
                });
                payload["search"] = new Dictionary<string, object?>
                {
                    ["rootContinuationStamp"] = outcome!.RootContinuationStamp,
                    ["rootLiveStamp"] = outcome.RootLiveStamp,
                    ["rootUnchanged"] = outcome.RootUnchanged,
                    ["rootCapture"] = outcome.RootCapture,
                    ["solverMetrics"] = outcome.LegacyMetrics,
                    ["planActions"] = outcome.PlanActions,
                    ["blindPolicy"] = outcome.BlindPolicy,
                    ["cachedContinuations"] = outcome.BlindPolicy != null ? [] : outcome.Result.Continuations
                        .Select(item => new
                        {
                            item.StartTurnNumber,
                            item.ForecastOffset,
                            state = item.ExpectedState.StateText,
                        })
                        .ToArray(),
                    ["continuations"] = outcome.Continuations,
                    ["patchLog"] = ModRuntime.PatchLog.ToArray(),
                };
                // 与游戏内 result.json 同名同形的那一份（游戏自己的 Writer 造的）。
                payload["solverMetrics"] = outcome.SolverMetrics;
                payload["blindPolicy"] = outcome.BlindPolicy;
                // 宿主自己从 SolverResult 读的剪枝/复用计数（游戏内 result.json 没有这些字段）。
                payload["pruneCounters"] = outcome.LegacyMetrics;
                payload["searchPolicy"] = outcome.Policy;
                payload["comparisonQuality"] = outcome.Result.ComparisonQuality;
                payload["phasePerformance"] = ModRuntime.LastPhasePerformance;
                File.WriteAllText(
                    Path.Combine(options.OutputDirectory, "search-policy.json"),
                    JsonSerializer.Serialize(outcome.Policy, UnattendedTestFiles.JsonOptions));
                payload["timeBoundaryObserved"] = outcome.TimeBoundaryObserved;
                payload["wallSeconds"] = outcome.WallSeconds;
                memory.Dispose();
                payload["peakManagedHeapBytes"] = memory.PeakManagedHeapBytes;
                payload["peakManagedLiveBytes"] = memory.PeakManagedLiveBytes;
                payload["peakWorkingSetBytes"] = memory.PeakWorkingSetBytes;
                payload["memorySamples"] = memory.Samples;
                payload["totalAllocatedBytes"] = GC.GetTotalAllocatedBytes(precise: false);

                File.WriteAllText(
                    Path.Combine(options.OutputDirectory, "route.json"),
                    JsonSerializer.Serialize(outcome.RouteActions, UnattendedTestFiles.JsonOptions));

                reached = "M2";
                if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_ANCILLARY_CHECKS") == "1")
                    AncillaryFailureChecks.Run(outcome.Result, options.OutputDirectory);
                WriteProgress(options, "M2", "ok", "搜索完成并产出指标");
            }
            if (options.PlayCombat)
            {
                List<CombatRolloutResult> rollouts = [];
                payload["rollouts"] = rollouts;
                Step(steps, "M3 原生命令逐步执行与未知 RNG 重观察", () =>
                {
                    int count = history == null ? 1 : options.HistoryCombatLimit;
                    for (int index = 0; index < count; index++)
                    {
                        TimeSpan combatTimeout = RequestTimeout(options.CombatTimeoutSeconds);
                        string directory = Path.Combine(options.OutputDirectory, $"combat-{index:D2}");
                        Directory.CreateDirectory(directory);
                        HarnessOptions combatOptions = options with { OutputDirectory = directory };
                        CombatRolloutResult rollout = CombatRollout.Run(combat!, combatOptions, loop, session!, observedSelector!, combatTimeout);
                        rollouts.Add(rollout);
                        File.WriteAllText(Path.Combine(options.OutputDirectory, "rollout-summary.json"),
                            JsonSerializer.Serialize(new { nativeChoicePolicy = options.NativeChoicePolicy,
                                reobserveAfterEveryCommand = true, rollouts, historyGuidance = history?.Guidance }, Json));
                        if (rollout.Status != "Victory")
                        {
                            payload["rolloutStopReason"] = rollout.Status;
                            exitCode = rollout.Status == "Defeat" ? 1 : 3;
                            break;
                        }
                        if (history != null)
                        {
                            TimeSpan rewardTimeout = RequestTimeout(10);
                            loop.RunUntilCompleted(history.TakeRewardsAsync(), rewardTimeout, "historical native rewards");
                            File.WriteAllText(Path.Combine(options.OutputDirectory, "history-guidance.json"),
                                JsonSerializer.Serialize(history.Guidance, Json));
                            if (index + 1 < count)
                            {
                                TimeSpan nextTimeout = RequestTimeout(15);
                                Task<bool> next = history.EnterNextAsync();
                                loop.RunUntilCompleted(next, nextTimeout, "historical next combat");
                                if (!next.Result) { payload["historyStopReason"] = history.StopReason; exitCode = 3; break; }
                                combat = OfflineCombat.WaitForPlayableCombat(loop, RequestTimeout(120));
                            }
                        }
                    }
                    payload["nativeChoices"] = observedSelector!.Choices;
                    return string.Join(", ", rollouts.Select(rollout => $"{rollout.Encounter}:{rollout.Status} decisions={rollout.Decisions} hp={rollout.StartHp}->{rollout.EndHp}"));
                });
                reached = "M3";
            }
        }
        catch (TimeoutException error) when (options.PlayCombat
            && requestWatch.Elapsed.TotalSeconds >= options.CombatTimeoutSeconds)
        {
            payload["rolloutStopReason"] = "TimeLimit";
            payload["error"] = new { type = error.GetType().FullName, message = error.Message };
            WriteProgress(options, NextMilestone(reached), "held", error.Message);
            Console.Error.WriteLine($"[HELD] {error.Message}");
            exitCode = 3;
        }
        catch (Exception error)
        {
            Exception root = Unwrap(error);
            payload["error"] = new
            {
                type = root.GetType().FullName,
                message = root.Message,
                stack = root.StackTrace,
            };
            WriteProgress(options, NextMilestone(reached), "blocked", $"{root.GetType().Name}: {root.Message}");
            Console.Error.WriteLine($"[FAIL] {root.GetType().Name}: {root.Message}");
            // 包装异常（如 SearchTransitionException）的真正原因在 InnerException 上，必须打出来。
            for (Exception? inner = root.InnerException; inner != null; inner = inner.InnerException)
                Console.Error.WriteLine($"[FAIL:inner] {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(root.StackTrace);
            exitCode = 1;
        }
        finally
        {
            choiceScope?.Dispose();
            observedChoiceScope?.Dispose();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        payload["reachedMilestone"] = reached;
        payload["bypasses"] = GameBootstrap.Bypasses;
        payload["steps"] = steps;
        string resultPath = Path.Combine(options.OutputDirectory, "harness-result.json");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(payload, Json));
        // run_plan.py 直接消费的一份：solverMetrics 与游戏内 result.json 同名同形。
        File.WriteAllText(
            Path.Combine(options.OutputDirectory, "result.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["label"] = options.Label,
                ["status"] = exitCode == 0 ? "Passed" : exitCode == 3 ? "Held" : "Failed",
                ["reachedMilestone"] = reached,
                ["requestPath"] = options.RequestPath,
                ["profile"] = options.Profile,
                ["searchMode"] = options.SearchMode,
                ["budget"] = payload.GetValueOrDefault("budget"),
                ["solverMetrics"] = payload.GetValueOrDefault("solverMetrics"),
                ["pruneCounters"] = payload.GetValueOrDefault("pruneCounters"),
                ["blindPolicy"] = payload.GetValueOrDefault("blindPolicy"),
                ["timeBoundaryObserved"] = payload.GetValueOrDefault("timeBoundaryObserved"),
                ["wallSeconds"] = payload.GetValueOrDefault("wallSeconds"),
                ["peakManagedHeapBytes"] = payload.GetValueOrDefault("peakManagedHeapBytes"),
                ["peakManagedLiveBytes"] = payload.GetValueOrDefault("peakManagedLiveBytes"),
                ["peakWorkingSetBytes"] = payload.GetValueOrDefault("peakWorkingSetBytes"),
                ["totalAllocatedBytes"] = payload.GetValueOrDefault("totalAllocatedBytes"),
                ["rootContinuationStamp"] = payload.GetValueOrDefault("search") is Dictionary<string, object?> s
                    ? s.GetValueOrDefault("rootContinuationStamp")
                    : null,
                ["comparisonQuality"] = payload.GetValueOrDefault("comparisonQuality"),
                ["continuations"] = payload.GetValueOrDefault("search") is Dictionary<string, object?> search
                    ? search.GetValueOrDefault("continuations")
                    : null,
                ["catalogFingerprint"] = payload.GetValueOrDefault("catalogFingerprint"),
                ["saveImport"] = payload.GetValueOrDefault("saveImport"),
                ["rollouts"] = payload.GetValueOrDefault("rollouts"),
                ["historyStopReason"] = payload.GetValueOrDefault("historyStopReason"),
                ["rolloutStopReason"] = payload.GetValueOrDefault("rolloutStopReason"),

                ["error"] = payload.GetValueOrDefault("error"),
            }, UnattendedTestFiles.JsonOptions));

        Console.WriteLine();
        Console.WriteLine("| 步骤 | 结果 | 详情 | 耗时 ms |");
        Console.WriteLine("|---|---|---|---:|");
        foreach (StepRecord step in steps)
            Console.WriteLine($"| {step.Name} | {step.Status} | {step.Detail.Replace("|", "\\|")} | {step.Milliseconds} |");
        Console.WriteLine();
        Console.WriteLine($"reached={reached} result={resultPath}");
        return exitCode;
    }

    private static object DescribeBudget(HarnessOptions options)
    {
        SolverSearchProfile profile = ModRuntime.ResolveProfile(options);
        return new
        {
            options.Profile,
            profile.BeamWidth,
            profile.MaxExpandedNodes,
            profile.MaxCardBranchesPerNode,
            profile.MaxPileChoiceBranchesPerAction,
            profile.MaxHandChoiceBranchesPerAction,
            options.MaxDegreeOfParallelism,
            options.BudgetMilliseconds,
            options.PotionPolicy,
            options.SearchMode,
            options.BlindSamples,
            options.BlindSeed,
            options.BlindAggregation,
            options.BlindKnownTopDrawCards,
            options.UsePortfolio,
            fixedSearchBudget = !options.ProductionBudget,
            enableNoGcRegion = options.EnableNoGcRegion,
            noGcRegionBudgetGigabytes = options.EnableNoGcRegion
                ? options.NoGcRegionBudgetGigabytes
                : (double?)null,
        };
    }

    private static string NextMilestone(string reached) => reached switch
    {
        "none" => "M1",
        "M1" => "M2",
        _ => "M3",
    };

    private static void Step(List<StepRecord> steps, string name, Func<string> body)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            string detail = body();
            steps.Add(new StepRecord(name, "ok", detail, watch.ElapsedMilliseconds));
            Console.WriteLine($"[ok]   {name}: {detail} ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception error)
        {
            Exception root = Unwrap(error);
            steps.Add(new StepRecord(name, "FAIL", $"{root.GetType().Name}: {root.Message}", watch.ElapsedMilliseconds));
            Console.WriteLine($"[FAIL] {name}: {root.GetType().Name}: {root.Message} ({watch.ElapsedMilliseconds} ms)");
            throw;
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException or AggregateException && error.InnerException != null)
            error = error.InnerException;
        return error;
    }

    private static void WriteProgress(HarnessOptions options, string milestone, string status, string note)
    {
        if (options.RequestPath != null)
            return;
        string path = Path.Combine(options.WorkspaceDirectory, "progress.json");
        Directory.CreateDirectory(options.WorkspaceDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(new { milestone, status, note }, Json));
    }
}

internal sealed record StepRecord(string Name, string Status, string Detail, long Milliseconds);

internal sealed record HarnessOptions
{
    public const string Usage = """
        用法：OfflineSearchHarness [选项]
          --request <path>       固定夹具或含 generatedScenarioPath 的无人测试请求 JSON
          --run-save <path>      原生当前版本战前存档；内存恢复并进入已保存坐标
          --combat-replay <path> 原生 .mcr 的战前存档（先核对模型编号表）
          --history-run <path>   .run 指导的新局前缀；使用完整解锁，按已录类型取最低列相邻节点
          --play-combat          执行未知 RNG 首命令并在每个稳定根重新求解
          --history-combats <n>  历史前缀最多战斗数（1..4，默认 1）；遇非战斗/不符记录时停止
          --combat-decisions <n> 整场决策上限（默认 200）
          --combat-turns <n>     整场回合上限（默认 20）
          --combat-timeout-seconds <n> 请求总时限（1..120，默认 120）
          --native-choice-policy <p> Stop（默认）|FirstLegal（只按实际可见候选选最少张）
          --label <name>         本根标签（写进 result.json，默认 offline）
          --character <id>       角色（无 --request 时用，默认 IRONCLAD）
          --encounter <id>       遭遇（无 --request 时用，默认 FUZZY_WURM_CRAWLER_WEAK）
          --seed <string>        跑局种子（无 --request 时用，默认 OFFLINEHARNESS1）
          --ascension <int>      飞升层数（默认 0）
          --act-index <int>      测试幕索引（默认 0）
          --profile <p>          Low|Medium|High|VeryHigh|Custom（默认 Custom）
          --beam <int>           Beam 宽度（覆盖预设）
          --nodes <int>          最大展开节点（覆盖预设）
          --card-branches <int>  每节点卡牌分支上限（覆盖预设）
          --pile-branches <int>  每动作牌堆选择分支上限（覆盖预设）
          --hand-branches <int>  每动作手牌选择分支上限（覆盖预设）
          --dop <int>            搜索并行度（默认 1）
          --budget-ms <int>      搜索预算毫秒（默认 600000）
          --potion-policy <p>    药水政策（默认 Smart）
          --search-mode <m>      Evaluate（单次求解，不经协调器，默认）| Coordinator（生产协调器）
          --blind-samples <n>    未知 RNG 采样数；启用后复用 Coordinator，只输出聚合首动作
          --blind-seed <ulong>   采样 seed 生成器的根值（默认 1）
          --blind-aggregation <m> Majority|MeanScore|RiskAverse（默认 Majority）
          --blind-known-top <n>  观察者已知的抽牌堆顶张数（默认 0）；其余牌序独立采样
          --use-portfolio        开宽度组合（只对 --search-mode Coordinator 有效）
          --no-plain-baseline    消融：丢掉普通基线成员（需 --use-portfolio）
          --unordered-pile-mask <0..15>  实验：状态键里顺序无关的牌堆（1手牌/2抽牌堆/4弃牌堆/8消耗堆）
          --state-key-salt <int> 实验：给状态指纹异或一个常量（双射，只改数值不改相等关系）
          --measure-phases       开按阶段的耗时/分配统计（SEARCH_PHASE 行进运行日志）
          --disable-transposition-prune <0..3>  实验：关掉转置支配剪枝（1=候选准入/2=展开准入）
          --memory-no-progress-limit <int>  实验：连续多少次无进展回收后提前收手（0=关闭）
          --transposition-entry-limit <int>  实验：转置支配表合并条目上限（0=不设上限；缺省=生产默认 1000000）
          --stop-at-zero-loss    启用生产零战损达标停止政策
          --stop-portfolio-at-hp-target  Coordinator：显式开启组合达标早停（缺省沿用生产配置）
          --disable-portfolio-hp-target-stop  Coordinator 消融：关闭组合达标早停
          --verify-incremental   逐动作完整回放核验（不得用于性能数字）
          --production-budget    使用生产预算流程，允许预算内的无胜利升级；不用于固定节点逐位对照
          --enable-no-gc-region   开 Runtime 的搜索内 No-GC 生命周期（默认关闭）
          --no-gc-region-budget-gigabytes <double>  No-GC 区域预算，单位十进制 GB（默认 1）
          --signal-ballast-mb <int>  进 No-GC scope 后先持有 N MiB 活对象，制造回收腾不出余量的压力
          --observe-ordering <N> 最多导出 N 个真实剪枝候选；仅供采集，不能用于性能数据
          --observe-ordering-states <p>  追加观察给定状态键的生成/准入/回合筛选事件
          --ordering <mode>     Evaluate 实验：baseline|base|band，复用现有排序成员
          --ranking-model <p>   实验：有界上下文排序修正；只与 baseline 排序同时使用
          --bounded-offensive-refinement  组合实验：保留原成员，有界追加进攻成员
          --disable-bounded-offensive-refinement  显式关闭有界追加
          --offensive-refinement  组合实验：以更窄的进攻排序成员替换宽成员
          --beam-weight <term:scale>  单项排序敏感度：CurrentEnergy|PersistentBuffDelta|EnemyHp，scale 0..2
          --continuous-threat  实验：EndTurn 后尚能出牌的新回合起点，连续计价致死意图
          --base-score-tactical-ties  实验：基础分成员的单进展值同分截线使用既有战术顺序
          --reallocated-refinement / --disable-reallocated-refinement  显式覆盖默认组合再分配
          --adaptive-novelty  实验：原Beam组合完成后，以实际工作量的至多1/8追加结构探索
          --observe-portfolio    导出追加搜索的特征与实际政策标签
          --portfolio-model <p>  加载可选选择器 JSON；不匹配的版本回退原组合
          --milestone <M1|M2>    跑到哪个里程碑（默认 M2）
          --out <dir>            产物目录（默认 <workspace>/offline）
          --workspace <dir>      工作区目录（默认 .local/offline-harness）
          --language <code>      本地化语言码（默认 eng）
          --verbose-game-log     把游戏 info/debug 日志也打到标准输出
        环境变量 OFFLINE_HARNESS_COMBATSOLVER_DLL 可以换掉运行时加载的 CombatSolver.dll。
        """;

    public HarnessScenario Scenario { get; init; } = new("IRONCLAD", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS1", 0, 0);
    public string? RequestPath { get; init; }
    public string? RunSavePath { get; init; }
    public string? CombatReplayPath { get; init; }
    public string? HistoryRunPath { get; init; }
    public bool PlayCombat { get; init; }
    public int HistoryCombatLimit { get; init; } = 1;
    public int CombatDecisionLimit { get; init; } = 200;
    public int CombatTurnLimit { get; init; } = 20;
    public int CombatTimeoutSeconds { get; init; } = 120;
    public string NativeChoicePolicy { get; init; } = "Stop";
    public string Label { get; init; } = "offline";
    public string Profile { get; init; } = "Custom";
    public int? Beam { get; init; }
    public int? Nodes { get; init; }
    public int? MaxCardBranchesPerNode { get; init; }
    public int? MaxPileChoiceBranchesPerAction { get; init; }
    public int? MaxHandChoiceBranchesPerAction { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = 1;
    public int BudgetMilliseconds { get; init; } = 600_000;
    public string PotionPolicy { get; init; } = "Smart";
    public string SearchMode { get; init; } = "Evaluate";
    public int BlindSamples { get; init; }
    public ulong BlindSeed { get; init; } = 1;
    public string BlindAggregation { get; init; } = "Majority";
    public int BlindKnownTopDrawCards { get; init; }
    /// <summary>开宽度组合（协调器的组合成员通道）；Evaluate 模式下没有意义。</summary>
    public bool UsePortfolio { get; init; }
    /// <summary>消融：丢掉只带基线宽度、不带排序修饰的组合成员，少跑一次真实搜索。</summary>
    public bool NoPlainBaselineMember { get; init; }
    /// <summary>实验：状态键里哪些牌堆改成顺序无关哈希（手牌=1/抽牌堆=2/弃牌堆=4/消耗堆=8）；0 即生产口径。</summary>
    public int UnorderedPileMask { get; init; }
    /// <summary>实验：给状态指纹异或一个由该值导出的常量；双射，只改数值不改相等关系。0 即生产口径。</summary>
    public int StateKeySalt { get; init; }
    /// <summary>开按阶段统计：每个阶段的耗时与分配字节，落到运行日志的 SEARCH_PHASE 行。</summary>
    public bool MeasureSearchPhases { get; init; }
    /// <summary>实验：关掉转置支配剪枝的位（1=候选准入/2=展开准入）；0 即生产口径。</summary>
    public int TranspositionPruningDisabledMask { get; init; }
    /// <summary>实验：连续多少次无进展回收后提前收手；0 即关闭（生产口径）。</summary>
    public int MemoryNoProgressRecoveryLimit { get; init; }
    /// <summary>实验：转置支配表合并条目上限；0 = 不设上限，缺省 = 生产默认。</summary>
    public int? TranspositionEntryLimit { get; init; }
    public bool ProductionBudget { get; init; }
    public bool StopAtZeroLoss { get; init; }
    public bool VerifyIncremental { get; init; }
    /// <summary>实验：走 Runtime 的搜索内 No-GC 生命周期，供无头宿主复现内存回收与截断。</summary>
    public bool EnableNoGcRegion { get; init; }
    /// <summary>No-GC 区域预算；只在 <see cref="EnableNoGcRegion" /> 开启时生效。</summary>
    public double NoGcRegionBudgetGigabytes { get; init; } = 1d;
    /// <summary>进入 No-GC scope 后先持有的活对象 MiB，用于制造“回收腾不出余量”的受控压力。</summary>
    public int SignalBallastMegabytes { get; init; }
    public int OrderingObservationLimit { get; init; }
    public string? OrderingWatchedStatesPath { get; init; }
    public string Ordering { get; init; } = "baseline";
    public string? RankingModelPath { get; init; }
    public bool ContinuousThreatRanking { get; init; }
    public bool BaseScoreTacticalTies { get; init; }
    public bool AdaptiveNoveltyRefinement { get; init; }
    public bool? ReallocatedRefinementPortfolio { get; init; }
    public BeamWeightPerturbation? BeamWeightPerturbation { get; init; }
    public bool OffensiveRefinementPortfolio { get; init; }
    public bool? BoundedOffensiveRefinementPortfolio { get; init; }
    public bool? StopPortfolioAtHpTarget { get; init; }
    public bool ObservePortfolio { get; init; }
    public string? PortfolioModelPath { get; init; }
    public string Milestone { get; init; } = "M2";
    public string WorkspaceDirectory { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public bool VerboseGameLog { get; init; }
    public string Language { get; init; } = "eng";

    public string LogDirectory => Path.Combine(OutputDirectory, "logs");

    public static HarnessOptions Parse(string[] args)
    {
        string character = "IRONCLAD", encounter = "FUZZY_WURM_CRAWLER_WEAK", seed = "OFFLINEHARNESS1";
        int ascension = 0, actIndex = 0, dop = 1, budget = 600_000, unorderedPileMask = 0, stateKeySalt = 0;
        int blindSamples = 0;
        int blindKnownTop = 0;
        ulong blindSeed = 1;
        int transpositionPruneOff = 0, memoryNoProgressLimit = 0;
        int? transpositionEntryLimit = null;
        bool measurePhases = false, enableNoGcRegion = false, productionBudget = false;
        bool stopAtZeroLoss = false, verifyIncremental = false;
        double noGcRegionBudgetGigabytes = 1d;
        int orderingObservationLimit = 0;
        string? orderingWatchedStatesPath = null;
        string ordering = "baseline";
        string? rankingModelPath = null;
        bool continuousThreatRanking = false;
        bool baseScoreTacticalTies = false;
        bool adaptiveNoveltyRefinement = false;
        bool? reallocatedRefinementPortfolio = null;
        BeamWeightPerturbation? beamWeightPerturbation = null;
        bool offensiveRefinementPortfolio = false;
        bool? boundedOffensiveRefinementPortfolio = null;
        bool? stopPortfolioAtHpTarget = null;
        int signalBallastMegabytes = 0;
        int? beam = null, nodes = null, cardBranches = null, pileBranches = null, handBranches = null;
        bool usePortfolio = false, observePortfolio = false, noPlainBaseline = false;
        string? portfolioModelPath = null;
        string potionPolicy = "Smart", milestone = "M2", language = "eng";
        string blindAggregation = "Majority";
        string profile = "Custom", searchMode = "Evaluate", label = "offline";
        string? output = null, requestPath = null;
        string? runSavePath = null, combatReplayPath = null, historyRunPath = null;
        bool playCombat = false;
        int historyCombats = 1, combatDecisions = 200, combatTurns = 20, combatTimeout = 120;
        string nativeChoicePolicy = "Stop";
        string workspace = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../.local/offline-harness"));
        bool verbose = false;

        for (int index = 0; index < args.Length; index++)
        {
            string key = args[index];
            string Value()
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException($"选项 {key} 缺少取值。");
                return args[++index];
            }
            switch (key)
            {
                case "--request": requestPath = Path.GetFullPath(Value()); break;
                case "--run-save": runSavePath = Path.GetFullPath(Value()); break;
                case "--combat-replay": combatReplayPath = Path.GetFullPath(Value()); break;
                case "--history-run": historyRunPath = Path.GetFullPath(Value()); break;
                case "--play-combat": playCombat = true; break;
                case "--history-combats": historyCombats = int.Parse(Value()); break;
                case "--combat-decisions": combatDecisions = int.Parse(Value()); break;
                case "--combat-turns": combatTurns = int.Parse(Value()); break;
                case "--combat-timeout-seconds": combatTimeout = int.Parse(Value()); break;
                case "--native-choice-policy": nativeChoicePolicy = Value(); break;
                case "--label": label = Value(); break;
                case "--character": character = Value(); break;
                case "--encounter": encounter = Value(); break;
                case "--seed": seed = Value(); break;
                case "--ascension": ascension = int.Parse(Value()); break;
                case "--act-index": actIndex = int.Parse(Value()); break;
                case "--profile": profile = Value(); break;
                case "--beam": beam = int.Parse(Value()); break;
                case "--nodes": nodes = int.Parse(Value()); break;
                case "--card-branches": cardBranches = int.Parse(Value()); break;
                case "--pile-branches": pileBranches = int.Parse(Value()); break;
                case "--hand-branches": handBranches = int.Parse(Value()); break;
                case "--dop": dop = int.Parse(Value()); break;
                case "--budget-ms": budget = int.Parse(Value()); break;
                case "--potion-policy": potionPolicy = Value(); break;
                case "--search-mode": searchMode = Value(); break;
                case "--blind-samples": blindSamples = int.Parse(Value()); break;
                case "--blind-seed": blindSeed = ulong.Parse(Value()); break;
                case "--blind-aggregation": blindAggregation = Value(); break;
                case "--blind-known-top": blindKnownTop = int.Parse(Value()); break;
                case "--use-portfolio": usePortfolio = true; break;
                case "--no-plain-baseline": noPlainBaseline = true; break;
                case "--unordered-pile-mask": unorderedPileMask = int.Parse(Value()); break;
                case "--state-key-salt": stateKeySalt = int.Parse(Value()); break;
                case "--measure-phases": measurePhases = true; break;
                case "--stop-at-zero-loss": stopAtZeroLoss = true; break;
                case "--verify-incremental": verifyIncremental = true; break;
                case "--production-budget": productionBudget = true; break;
                case "--disable-transposition-prune": transpositionPruneOff = int.Parse(Value()); break;
                case "--memory-no-progress-limit": memoryNoProgressLimit = int.Parse(Value()); break;
                case "--transposition-entry-limit": transpositionEntryLimit = int.Parse(Value()); break;
                case "--enable-no-gc-region": enableNoGcRegion = true; break;
                case "--no-gc-region-budget-gigabytes": noGcRegionBudgetGigabytes = double.Parse(Value()); break;
                case "--signal-ballast-mb": signalBallastMegabytes = int.Parse(Value()); break;
                case "--observe-ordering": orderingObservationLimit = int.Parse(Value()); break;
                case "--observe-ordering-states": orderingWatchedStatesPath = Path.GetFullPath(Value()); break;
                case "--ordering": ordering = Value(); break;
                case "--ranking-model": rankingModelPath = Path.GetFullPath(Value()); break;
                case "--bounded-offensive-refinement": boundedOffensiveRefinementPortfolio = true; break;
                case "--disable-bounded-offensive-refinement": boundedOffensiveRefinementPortfolio = false; break;
                case "--offensive-refinement": offensiveRefinementPortfolio = true; break;
                case "--beam-weight":
                    string[] termScale = Value().Split(':');
                    if (termScale.Length != 2 || !Enum.TryParse(termScale[0], out BeamWeightTerm term))
                        throw new ArgumentException("--beam-weight 需要 Term:Scale。");
                    beamWeightPerturbation = new BeamWeightPerturbation(term,
                        double.Parse(termScale[1], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "--continuous-threat": continuousThreatRanking = true; break;
                case "--base-score-tactical-ties": baseScoreTacticalTies = true; break;
                case "--reallocated-refinement": reallocatedRefinementPortfolio = true; break;
                case "--disable-reallocated-refinement": reallocatedRefinementPortfolio = false; break;
                case "--adaptive-novelty": adaptiveNoveltyRefinement = true; break;
                case "--stop-portfolio-at-hp-target": stopPortfolioAtHpTarget = true; break;
                case "--disable-portfolio-hp-target-stop": stopPortfolioAtHpTarget = false; break;
                case "--observe-portfolio": observePortfolio = true; break;
                case "--portfolio-model": portfolioModelPath = Path.GetFullPath(Value()); break;
                case "--milestone": milestone = Value(); break;
                case "--out": output = Value(); break;
                case "--workspace": workspace = Value(); break;
                case "--language": language = Value(); break;
                case "--verbose-game-log": verbose = true; break;
                default: throw new ArgumentException($"未知选项 {key}。");
            }
        }
        if (milestone is not ("M1" or "M2"))
            throw new ArgumentException("--milestone 只接受 M1 或 M2。");
        if (new[] { requestPath, runSavePath, combatReplayPath, historyRunPath }.Count(path => path != null) > 1)
            throw new ArgumentException("--request、--run-save、--combat-replay、--history-run 只能选一个。");
        if (playCombat && (blindSamples == 0 || blindKnownTop != 0 || milestone == "M1"))
            throw new ArgumentException("--play-combat 需要 blind 模式、未知堆顶（0）及 M2；不接受固定堆顶知识跨动作传播。");
        if (nativeChoicePolicy is not ("Stop" or "FirstLegal") || historyCombats is < 1 or > 4
            || combatDecisions is < 1 or > 1000 || combatTurns is < 1 or > 100 || combatTimeout is < 1 or > 120)
            throw new ArgumentException("无效的原生 rollout 选择政策或停止预算。");
        if (profile is not ("Low" or "Medium" or "High" or "VeryHigh" or "Custom"))
            throw new ArgumentException("--profile 只接受 Low|Medium|High|VeryHigh|Custom。");
        if (searchMode is not ("Evaluate" or "Coordinator"))
            throw new ArgumentException("--search-mode 只接受 Evaluate 或 Coordinator。");
        if (blindSamples is < 0 or > 64)
            throw new ArgumentException("--blind-samples 只接受 0..64。");
        if (blindSamples > 0 && searchMode != "Coordinator")
            throw new ArgumentException("--blind-samples 需要 --search-mode Coordinator。");
        if (blindAggregation is not ("Majority" or "MeanScore" or "RiskAverse"))
            throw new ArgumentException("--blind-aggregation 只接受 Majority|MeanScore|RiskAverse。");
        if (blindKnownTop < 0 || blindKnownTop > 0 && blindSamples == 0)
            throw new ArgumentException("--blind-known-top 必须非负并在 blind 模式下使用。");
        if (blindSamples > 0 && (productionBudget || observePortfolio || portfolioModelPath != null))
            throw new ArgumentException("blind 模式使用独立固定预算，不接受 production-budget 或 portfolio observation/model。");
        if (blindSamples > 0 && budget <= 0)
            throw new ArgumentException("blind 模式需要正的 --budget-ms。");
        if (orderingObservationLimit is < 0 or > 100000 || orderingObservationLimit > 0 && searchMode != "Evaluate")
            throw new ArgumentException("--observe-ordering 仅支持 Evaluate，范围 0..100000。");
        if (orderingWatchedStatesPath != null && orderingObservationLimit == 0)
            throw new ArgumentException("--observe-ordering-states 需要有限的 --observe-ordering。");
        if (ordering is not ("baseline" or "base" or "band") || ordering != "baseline" && searchMode != "Evaluate")
            throw new ArgumentException("--ordering 只接受 baseline|base|band，非基线仅支持 Evaluate。");
        if (rankingModelPath != null && ordering != "baseline")
            throw new ArgumentException("--ranking-model 不能同时叠加其他 --ordering 实验。");
        if (continuousThreatRanking && (rankingModelPath != null || ordering != "baseline"))
            throw new ArgumentException("--continuous-threat 必须单独测量，不叠加排序模型或其他成员。");
        if (baseScoreTacticalTies && (searchMode == "Evaluate" ? ordering != "base" : !usePortfolio))
            throw new ArgumentException("--base-score-tactical-ties 需要 Evaluate --ordering base 或 Coordinator --use-portfolio。");
        if (reallocatedRefinementPortfolio.HasValue && (searchMode != "Coordinator" || !usePortfolio))
            throw new ArgumentException("组合再分配覆盖需要 Coordinator --use-portfolio。");
        if (reallocatedRefinementPortfolio == true && (noPlainBaseline || adaptiveNoveltyRefinement
            || offensiveRefinementPortfolio || boundedOffensiveRefinementPortfolio == true))
            throw new ArgumentException("--reallocated-refinement 不能叠加显式成员列表消融或精炼实验。");
        if (adaptiveNoveltyRefinement && (searchMode != "Coordinator" || !usePortfolio))
            throw new ArgumentException("--adaptive-novelty 需要 Coordinator --use-portfolio。");
        if (beamWeightPerturbation != null && (continuousThreatRanking || rankingModelPath != null || ordering != "baseline"))
            throw new ArgumentException("--beam-weight 必须单独测量，不叠加其他排序实验。");
        if (offensiveRefinementPortfolio && (!usePortfolio || searchMode != "Coordinator"
            || beamWeightPerturbation != null || continuousThreatRanking || rankingModelPath != null || ordering != "baseline"))
            throw new ArgumentException("--offensive-refinement 需要Coordinator组合且不能叠加其他排序实验。");
        if (boundedOffensiveRefinementPortfolio.HasValue && (!usePortfolio || searchMode != "Coordinator"))
            throw new ArgumentException("有界追加参数需要Coordinator组合。");
        if (boundedOffensiveRefinementPortfolio == true && (offensiveRefinementPortfolio
            || beamWeightPerturbation != null || continuousThreatRanking || rankingModelPath != null || ordering != "baseline"))
            throw new ArgumentException("有界追加不能叠加其他排序实验。");
        if (stopPortfolioAtHpTarget.HasValue && searchMode != "Coordinator")
            throw new ArgumentException("组合达标早停参数仅用于Coordinator。");
        if (usePortfolio && searchMode != "Coordinator")
            throw new ArgumentException("--use-portfolio 只对 --search-mode Coordinator 有效。");
        if ((observePortfolio || portfolioModelPath != null) && (!usePortfolio || searchMode != "Coordinator"))
            throw new ArgumentException("选择器实验需要 --search-mode Coordinator --use-portfolio。");
        if (noPlainBaseline && (!usePortfolio || searchMode != "Coordinator"))
            throw new ArgumentException("--no-plain-baseline 需要 --search-mode Coordinator --use-portfolio。");
        if (unorderedPileMask is < 0 or > 15)
            throw new ArgumentException("--unordered-pile-mask 只接受 0..15（手牌=1/抽牌堆=2/弃牌堆=4/消耗堆=8）。");
        if (transpositionPruneOff is < 0 or > 3)
            throw new ArgumentException("--disable-transposition-prune 只接受 0..3（1=候选准入/2=展开准入）。");
        if (memoryNoProgressLimit < 0)
            throw new ArgumentException("--memory-no-progress-limit 只接受非负数（0=关闭）。");
        if (transpositionEntryLimit is < 0)
            throw new ArgumentException("--transposition-entry-limit 只接受非负数（0=不设上限）。");
        if (noGcRegionBudgetGigabytes < 1d || noGcRegionBudgetGigabytes > 256d)
            throw new ArgumentException("--no-gc-region-budget-gigabytes 只接受 1..256。");
        if (signalBallastMegabytes is < 0 or > 4096)
            throw new ArgumentException("--signal-ballast-mb 只接受 0..4096。");
        if (signalBallastMegabytes > 0 && !enableNoGcRegion)
            throw new ArgumentException("--signal-ballast-mb 需要 --enable-no-gc-region。");
        if (profile == "Custom" && requestPath == null)
        {
            beam ??= 24;
            nodes ??= 2000;
        }

        return new HarnessOptions
        {
            Scenario = new HarnessScenario(character, encounter, seed, ascension, actIndex),
            RequestPath = requestPath,
            RunSavePath = runSavePath,
            CombatReplayPath = combatReplayPath,
            HistoryRunPath = historyRunPath,
            PlayCombat = playCombat,
            HistoryCombatLimit = historyCombats,
            CombatDecisionLimit = combatDecisions,
            CombatTurnLimit = combatTurns,
            CombatTimeoutSeconds = combatTimeout,
            NativeChoicePolicy = nativeChoicePolicy,
            Label = label,
            Profile = profile,
            Beam = beam,
            Nodes = nodes,
            MaxCardBranchesPerNode = cardBranches,
            MaxPileChoiceBranchesPerAction = pileBranches,
            MaxHandChoiceBranchesPerAction = handBranches,
            MaxDegreeOfParallelism = dop,
            BudgetMilliseconds = budget,
            PotionPolicy = potionPolicy,
            SearchMode = searchMode,
            BlindSamples = blindSamples,
            BlindSeed = blindSeed,
            BlindAggregation = blindAggregation,
            BlindKnownTopDrawCards = blindKnownTop,
            UsePortfolio = usePortfolio,
            NoPlainBaselineMember = noPlainBaseline,
            UnorderedPileMask = unorderedPileMask,
            StateKeySalt = stateKeySalt,
            MeasureSearchPhases = measurePhases,
            TranspositionPruningDisabledMask = transpositionPruneOff,
            TranspositionEntryLimit = transpositionEntryLimit,
            ProductionBudget = productionBudget,
            StopAtZeroLoss = stopAtZeroLoss,
            VerifyIncremental = verifyIncremental,
            MemoryNoProgressRecoveryLimit = memoryNoProgressLimit,
            EnableNoGcRegion = enableNoGcRegion,
            NoGcRegionBudgetGigabytes = noGcRegionBudgetGigabytes,
            SignalBallastMegabytes = signalBallastMegabytes,
            OrderingObservationLimit = orderingObservationLimit,
            OrderingWatchedStatesPath = orderingWatchedStatesPath,
            Ordering = ordering,
            RankingModelPath = rankingModelPath,
            ContinuousThreatRanking = continuousThreatRanking,
            BaseScoreTacticalTies = baseScoreTacticalTies,
            AdaptiveNoveltyRefinement = adaptiveNoveltyRefinement,
            ReallocatedRefinementPortfolio = reallocatedRefinementPortfolio,
            BeamWeightPerturbation = beamWeightPerturbation,
            OffensiveRefinementPortfolio = offensiveRefinementPortfolio,
            BoundedOffensiveRefinementPortfolio = boundedOffensiveRefinementPortfolio,
            StopPortfolioAtHpTarget = stopPortfolioAtHpTarget,
            ObservePortfolio = observePortfolio,
            PortfolioModelPath = portfolioModelPath,
            Milestone = milestone,
            WorkspaceDirectory = Path.GetFullPath(workspace),
            OutputDirectory = Path.GetFullPath(output ?? Path.Combine(workspace, "offline")),
            VerboseGameLog = verbose,
            Language = language,
        };
    }
}
