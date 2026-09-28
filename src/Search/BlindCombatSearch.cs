using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal enum BlindAggregationMode
{
    Majority,
    MeanScore,
    RiskAverse,
}

/// <summary>Independent hypotheses about hidden draw order and future combat RNG.</summary>
internal sealed record BlindSearchOptions(
    IReadOnlyList<ulong> SampleSeeds,
    BlindAggregationMode Aggregation = BlindAggregationMode.MeanScore,
    int? PerSampleBudgetMilliseconds = null,
    int KnownTopDrawCards = 0,
    ulong DecisionSeed = 0xC6BC279692B5CC83UL,
    int PolicySamples = 3,
    int MaxRolloutActions = 12,
    int MaxRolloutTurns = 3,
    int MaxPolicyExpandedNodes = 128)
{
    internal static IReadOnlyList<ulong> GenerateSampleSeeds(ulong baseSeed, int count)
    {
        if (count is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(count));
        ulong state = baseSeed;
        ulong[] seeds = new ulong[count];
        for (int index = 0; index < seeds.Length; index++)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong value = state;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                seeds[index] = value ^ (value >> 31);
            }
        }
        return seeds;
    }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(SampleSeeds);
        if (SampleSeeds.Count is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(SampleSeeds));
        if (SampleSeeds.Distinct().Count() != SampleSeeds.Count)
            throw new ArgumentException("Blind sample seeds must be unique.", nameof(SampleSeeds));
        if (!Enum.IsDefined(Aggregation))
            throw new ArgumentOutOfRangeException(nameof(Aggregation));
        if (PerSampleBudgetMilliseconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(PerSampleBudgetMilliseconds));
        ArgumentOutOfRangeException.ThrowIfNegative(KnownTopDrawCards);
        if (PolicySamples is < 1 or > 16 || MaxRolloutActions is < 1 or > 64
            || MaxRolloutTurns is < 1 or > 8 || MaxPolicyExpandedNodes is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(PolicySamples), "Invalid blind rollout bounds.");
    }
}

/// <summary>
/// A command to initiate now. Choices inside an action or at the next turn require a new
/// observation; a determinized sample's choice tokens must never be deployed to the live game.
/// </summary>
internal static class BlindCombatCommand
{
    internal static PlanAction From(PlanAction action) => action with
    {
        Choice = null,
        NestedChoices = null,
        NestedChoicesBeforePrimary = 0,
        TurnStartChoices = null,
        RelicEffects = null,
        EndsPlayerTurn = false,
    };

    internal static string Key(PlanAction action) => JsonSerializer.Serialize(new
    {
        kind = action.Kind.ToString(),
        action.Turn,
        action.CardId,
        action.CardOccurrence,
        action.CardStateKey,
        action.CardStateOccurrence,
        action.CardUpgradeLevel,
        action.CardEnchantmentId,
        action.PotionId,
        action.PotionSlot,
        action.TargetIndex,
        action.TargetCombatId,
    });

    internal static bool Matches(PlanAction left, PlanAction right)
        => left.Kind == right.Kind && left.Turn == right.Turn
            && left.CardId == right.CardId && left.CardOccurrence == right.CardOccurrence
            && left.CardStateKey == right.CardStateKey
            && left.CardStateOccurrence == right.CardStateOccurrence
            && left.CardUpgradeLevel == right.CardUpgradeLevel
            && left.CardEnchantmentId == right.CardEnchantmentId
            && left.PotionId == right.PotionId && left.PotionSlot == right.PotionSlot
            && left.TargetIndex == right.TargetIndex && left.TargetCombatId == right.TargetCombatId;
}

internal sealed record BlindSearchSampleSummary(
    ulong SampleSeed,
    string FirstActionKey,
    PlanAction? FirstAction,
    double Score,
    int ProjectedBattleHpLost,
    bool CompleteVictory,
    SearchBoundaryReason BoundaryReason,
    TimeSpan Elapsed);

internal sealed record BlindSearchEvaluation(
    ulong SampleSeed,
    string ActionKey,
    double Score,
    int ProjectedBattleHpLost,
    bool Survives,
    bool CompleteVictory,
    SearchBoundaryReason BoundaryReason,
    TimeSpan Elapsed)
{
    public int Decisions { get; init; }
    public string ValueScope { get; init; } = "determinized_plan";
    public string StopReason { get; init; } = "search_boundary";
    public IReadOnlyList<PlanAction> ObservedActions { get; init; } = [];
    public IReadOnlyList<BlindPotionChoiceResult> PotionChoices { get; init; } = [];
    public IReadOnlyList<BlindCardChoiceResult> CardChoices { get; init; } = [];
}

internal sealed record BlindSearchActionSummary(
    string FirstActionKey,
    PlanAction FirstAction,
    int VoteCount,
    int SampleCount,
    double MeanScore,
    double MeanProjectedBattleHpLost,
    double WorstQuarterBattleHpLost,
    double SurvivalRate,
    double VictoryRate);

/// <summary>The representative result is diagnostic only and cannot be cached as a live route.</summary>
internal sealed record BlindSearchResult(
    SolverResult RepresentativeResult,
    PlanAction? SelectedAction,
    string SelectedActionKey,
    BlindAggregationMode Aggregation,
    IReadOnlyList<BlindSearchSampleSummary> Samples,
    IReadOnlyList<BlindSearchEvaluation> Evaluations,
    IReadOnlyList<BlindSearchActionSummary> ActionSummaries,
    int SearchRuns,
    long TotalExpandedNodes,
    long TotalTransitionCount,
    TimeSpan Elapsed);

internal static partial class CombatSearchCoordinator
{
    internal static BlindSearchResult SolveBlind(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        BlindSearchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(displayNames);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (policy.IncludeTurnSetup || root.PlayerPhase != PlayerTurnPhase.Play)
            throw new NotSupportedException("Blind search requires a stable Play-phase observation.");
        ulong[] seeds = options.SampleSeeds.ToArray();
        Stopwatch watch = Stopwatch.StartNew();
        List<BlindSearchSampleSummary> samples = [];
        Dictionary<string, PlanAction> candidates = new(StringComparer.Ordinal);
        List<BlindSearchEvaluation> evaluations = [];
        SolverResult? representative = null;
        long expanded = 0, transitions = 0;
        int runs = 0;
        int budget = options.PerSampleBudgetMilliseconds
            ?? policy.BudgetOverrideMilliseconds ?? policy.Profile.SoftTimeBudgetMilliseconds;
        SearchPolicySnapshot samplePolicy = policy with
        {
            Interaction = null,
            RequestWorkTotals = null,
            PortfolioTelemetry = null,
            PortfolioExperiment = null,
            PredictPotionReward = false,
            FixedBudget = true,
            BudgetOverrideMilliseconds = budget,
            Profile = policy.Profile with { SoftTimeBudgetMilliseconds = budget },
        };

        void RecordWork(SolverResult result)
        {
            expanded += result.TotalExpandedNodes;
            transitions += result.TotalTransitionCount;
            runs++;
        }

        foreach (ulong seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CombatRootSnapshot sampleRoot = root.ForkForSample(seed, options.KnownTopDrawCards);
            SolverResult result;
            try
            {
                result = Solve(sampleRoot, displayNames, battleDamage,
                    samplePolicy, cancellationToken, progressCallback: null);
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                // Discovery only nominates first commands; potion uses are still evaluated on outer
                // executable trajectories even without discovery votes. Smart's use-or-save
                // tradeoff can be unsatisfiable in this budget when every completed route used the
                // potion, so discovery retries without the policy instead of failing the search.
                result = Solve(sampleRoot, displayNames, battleDamage,
                    samplePolicy with { PotionPolicy = SolverPotionPolicy.Disabled },
                    cancellationToken, progressCallback: null);
            }
            RecordWork(result);
            representative ??= result;
            PlanAction? first = BlindFirstAction(root.StartTurnNumber, result);
            string key = first == null ? "<none>" : BlindCombatCommand.Key(first);
            if (first != null)
                candidates.TryAdd(key, first);
            samples.Add(new(seed, key, first, result.BestNode.Score,
                result.ProjectedBattleHpLost, IsCompleteVictory(result),
                result.BoundaryReason, result.TotalSearchElapsed));
            policy.Diagnostics.Info($"[CombatSolver/Blind] discovery sample={seed} action={key} "
                + $"hp_lost={result.ProjectedBattleHpLost} boundary={result.BoundaryReason}");
        }

        if (candidates.Count == 0 && representative!.Snapshot.AllEnemiesDead)
            return new(representative, null, "<none>", options.Aggregation, samples, [], [],
                runs, expanded, transitions, watch.Elapsed);

        // Pool effects must remain eligible even if no clairvoyant discovery sample nominated them.
        CombatRootSnapshot observedRoot = root.ForkForSample(options.DecisionSeed, options.KnownTopDrawCards);
        CombatBeamSolver observer = new(observedRoot, displayNames, battleDamage, samplePolicy, cancellationToken,
            searchProfile: samplePolicy.Profile);
        SimulationSnapshot observation = observer.ObserveBlindState();
        try
        {
            foreach (PlanAction command in observer.EnumerateBlindCommands(observation))
                candidates.TryAdd(BlindCombatCommand.Key(command), command);
        }
        finally { observation.ReleaseSimulator(); }

        int candidateBudget = Math.Max(1, budget / candidates.Count);
        BlindContinuationPolicy continuationPolicy = new(root, displayNames, battleDamage, samplePolicy,
            options, Math.Max(1, candidateBudget / options.MaxRolloutActions), cancellationToken, RecordWork,
            count => transitions += count);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        foreach ((string key, PlanAction command) in candidates.OrderBy(item => item.Key, StringComparer.Ordinal))
        foreach (ulong seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlindSearchEvaluation evaluation = continuationPolicy.Evaluate(command, seed, candidateBudget);
            evaluations.Add(evaluation);
            transitions += evaluation.Decisions;
            policy.Diagnostics.Info($"[CombatSolver/Blind] policy sample={seed} action={key} "
                + $"hp_lost={evaluation.ProjectedBattleHpLost} scope={evaluation.ValueScope} stop={evaluation.StopReason}");
        }

        Dictionary<string, int> policyVotes = evaluations.GroupBy(value => value.SampleSeed)
            .Select(group => group.OrderByDescending(value => value.Score)
                .ThenBy(value => value.ActionKey, StringComparer.Ordinal).First().ActionKey)
            .GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        BlindSearchActionSummary[] summaries = candidates.Select(candidate =>
        {
            BlindSearchEvaluation[] values = evaluations.Where(run => run.ActionKey == candidate.Key).ToArray();
            return SummarizeBlindAction(candidate.Key, candidate.Value,
                policyVotes.GetValueOrDefault(candidate.Key), values);
        }).ToArray();
        BlindSearchActionSummary selected = SelectBlindAction(summaries, options.Aggregation);
        // This route is explicitly diagnostic. Deployment receives only SelectedAction.
        var representativePolicy = samplePolicy;
        CombatBeamSolver representativeSolver;
        while (true)
        {
            representativeSolver = new(root.ForkForSample(seeds[0], options.KnownTopDrawCards),
                displayNames, battleDamage, representativePolicy, cancellationToken,
                searchProfile: representativePolicy.Profile,
                initialActionConstraint: selected.FirstAction);
            try
            {
                representative = representativeSolver.Solve();
                break;
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                // The constrained first command may be a potion use whose every completed route
                // fails Smart's tradeoff at this budget; the diagnostic route drops the policy.
                representativePolicy = representativePolicy with { PotionPolicy = SolverPotionPolicy.Disabled };
            }
        }
        RecordWork(representative);
        return new(representative, selected.FirstAction, selected.FirstActionKey, options.Aggregation,
            samples, evaluations.ToArray(),
            summaries.OrderByDescending(item => item.VoteCount).ThenBy(item => item.FirstActionKey,
                StringComparer.Ordinal).ToArray(), runs, expanded, transitions, watch.Elapsed);
    }

    internal static BlindSearchActionSummary SelectBlindAction(
        IReadOnlyList<BlindSearchActionSummary> actions, BlindAggregationMode mode)
        => (mode switch
        {
            BlindAggregationMode.Majority => actions.OrderByDescending(action => action.VoteCount)
                .ThenByDescending(action => action.MeanScore),
            BlindAggregationMode.MeanScore => actions.OrderByDescending(action => action.MeanScore)
                .ThenByDescending(action => action.VoteCount),
            BlindAggregationMode.RiskAverse => actions.OrderByDescending(action => action.SurvivalRate)
                .ThenBy(action => action.WorstQuarterBattleHpLost)
                .ThenByDescending(action => action.VictoryRate).ThenByDescending(action => action.MeanScore),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        }).ThenBy(action => action.FirstActionKey, StringComparer.Ordinal).First();

    internal static BlindSearchActionSummary SummarizeBlindAction(
        string key, PlanAction command, int votes, IReadOnlyList<BlindSearchEvaluation> values)
        => new(key, command, votes, values.Count,
            values.Average(value => value.Score), values.Average(value => value.ProjectedBattleHpLost),
            values.OrderByDescending(value => value.ProjectedBattleHpLost)
                .Take(Math.Max(1, (values.Count + 3) / 4)).Average(value => value.ProjectedBattleHpLost),
            values.Count(value => value.Survives) / (double)values.Count,
            values.Count(value => value.CompleteVictory) / (double)values.Count);

    internal static PlanAction? BlindFirstAction(int turn, SolverResult result)
    {
        PlanAction? action = result.BestNode.Actions.FirstOrDefault(action => action.Turn == turn);
        return action == null ? null : BlindCombatCommand.From(action);
    }
}
