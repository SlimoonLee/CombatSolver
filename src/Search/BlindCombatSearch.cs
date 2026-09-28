using System.Diagnostics;
using System.Text.Json;
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
    BlindAggregationMode Aggregation = BlindAggregationMode.Majority,
    int? PerSampleBudgetMilliseconds = null,
    int KnownTopDrawCards = 0)
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
    TimeSpan Elapsed);

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
        List<(BlindSearchEvaluation Evidence, SolverResult Result)> evaluations = [];
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
            SolverResult result = Solve(sampleRoot, displayNames, battleDamage,
                samplePolicy, cancellationToken, progressCallback: null);
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
        PlanAction endTurn = new(PlanActionKind.EndTurn, root.StartTurnNumber);
        candidates.TryAdd(BlindCombatCommand.Key(endTurn), endTurn);

        // Every candidate sees every sample, including samples where it did not win discovery.
        // The second stage shares one additional per-sample budget among the candidate searches.
        int candidateBudget = Math.Max(1, budget / candidates.Count);
        foreach ((string key, PlanAction command) in candidates.OrderBy(item => item.Key, StringComparer.Ordinal))
        foreach (ulong seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CombatRootSnapshot sampleRoot = root.ForkForSample(seed, options.KnownTopDrawCards);
            SearchRequestWorkTotals totals = new();
            SearchPolicySnapshot evaluationPolicy = samplePolicy with
            {
                RequestWorkTotals = totals,
                PortfolioTelemetry = null,
                Profile = samplePolicy.Profile with { SoftTimeBudgetMilliseconds = candidateBudget },
            };
            SolverResult result = new CombatBeamSolver(sampleRoot, displayNames, battleDamage,
                evaluationPolicy, cancellationToken, searchProfile: evaluationPolicy.Profile,
                initialActionConstraint: command).Solve();
            SearchRequestWorkSnapshot work = totals.Snapshot();
            result.TotalExpandedNodes = work.ExpandedNodes;
            result.TotalTransitionCount = work.TransitionCount;
            result.TotalChoiceBranchesEvaluated = work.ChoiceBranchesEvaluated;
            result.TotalSearchElapsed = work.Elapsed;
            RecordWork(result);
            PlanAction? actual = BlindFirstAction(root.StartTurnNumber, result);
            if (actual == null || !BlindCombatCommand.Matches(command, actual))
                throw new InvalidOperationException("Blind candidate search did not evaluate its initial command. "
                    + $"action={key} boundary={result.BoundaryReason}");
            evaluations.Add((new(seed, key, result.BestNode.Score, result.ProjectedBattleHpLost,
                !result.Snapshot.PlayerDead && result.Snapshot.ProjectedPlayerHp > 0,
                IsCompleteVictory(result), result.BoundaryReason, result.TotalSearchElapsed), result));
        }

        BlindSearchActionSummary[] summaries = candidates.Select(candidate =>
        {
            BlindSearchEvaluation[] values = evaluations.Where(run => run.Evidence.ActionKey == candidate.Key)
                .Select(run => run.Evidence).ToArray();
            return SummarizeBlindAction(candidate.Key, candidate.Value,
                samples.Count(sample => sample.FirstActionKey == candidate.Key), values);
        }).ToArray();
        BlindSearchActionSummary selected = SelectBlindAction(summaries, options.Aggregation);
        representative = evaluations.First(run => run.Evidence.ActionKey == selected.FirstActionKey).Result;
        return new(representative, selected.FirstAction, selected.FirstActionKey, options.Aggregation,
            samples, evaluations.Select(run => run.Evidence).ToArray(),
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
