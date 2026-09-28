using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record BlindPotionOptionValue(PlanCardChoice Choice, double MeanScore,
    double VictoryRate, double MeanBattleHpLost)
{
    public string ValueScope { get; init; } = "independent_determinized_choice_proposal";
}

internal sealed record BlindPotionChoiceResult(string SourceId, PlanCardChoice Selected,
    IReadOnlyList<BlindPotionOptionValue> Options);

/// <summary>
/// A bounded executable continuation policy. Its decision samples never inherit the trajectory's
/// hidden RNG or draw order. Determinized plans propose commands; only observed commands are executed.
/// </summary>
internal sealed class BlindContinuationPolicy(
    CombatRootSnapshot originalRoot,
    SolverDisplayNames names,
    BattleDamageSnapshot damage,
    SearchPolicySnapshot policy,
    BlindSearchOptions options,
    int decisionBudgetMilliseconds,
    CancellationToken cancellationToken,
    Action<SolverResult> recordWork,
    Action<int>? recordTransitions = null)
{
    private readonly ulong[] _decisionSeeds = BlindSearchOptions.GenerateSampleSeeds(
        options.DecisionSeed, options.PolicySamples).ToArray();
    private readonly Dictionary<string, PlanAction> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlindPotionChoiceResult> _choices = new(StringComparer.Ordinal);

    private SearchPolicySnapshot DecisionPolicy => policy with
    {
        BudgetOverrideMilliseconds = Math.Max(25, decisionBudgetMilliseconds / options.PolicySamples),
        RequestWorkTotals = null,
        UseBeamWidthPortfolio = false,
        UseNoveltyPortfolio = false,
        ZeroInheritedPotionUse = true,
        Profile = policy.Profile with
        {
            MaxExpandedNodes = Math.Min(policy.Profile.MaxExpandedNodes, options.MaxPolicyExpandedNodes),
            SoftTimeBudgetMilliseconds = Math.Max(25, decisionBudgetMilliseconds / options.PolicySamples),
        },
    };

    /// <summary>
    /// Smart's use-or-save tradeoff can still be unsatisfiable inside this tiny budget (for
    /// example when no route can improve on the potion-free baseline), so an unsatisfied potion
    /// policy retries without it instead of failing the evaluation.
    /// </summary>
    private SolverResult SolveDecision(CombatRootSnapshot sample, SearchPolicySnapshot searchPolicy)
    {
        try
        {
            return CombatSearchCoordinator.Solve(sample, names, damage, searchPolicy,
                cancellationToken, progressCallback: null);
        }
        catch (PotionPolicyUnsatisfiedException)
        {
            return CombatSearchCoordinator.Solve(sample, names, damage,
                searchPolicy with { PotionPolicy = SolverPotionPolicy.Disabled },
                cancellationToken, progressCallback: null);
        }
    }

    internal PlanCardChoice? ObserveChoice(TurnStartChoiceRequest request, IList<PlanCardChoice>? observed = null)
        => BlindObservedChoices.Resolve(request, names, policy.Profile, observed);

    internal PlanAction ChooseCommand(CombatRootSnapshot observation)
    {
        CombatRootSnapshot normalized = observation.ForkForSample(_decisionSeeds[0]);
        string key = normalized.ContinuationStamp.StateText;
        if (_commands.TryGetValue(key, out PlanAction? cached)) return cached;
        List<(PlanAction Action, double Score)> proposals = [];
        foreach (ulong seed in _decisionSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CombatRootSnapshot sample = seed == _decisionSeeds[0] ? normalized : observation.ForkForSample(seed);
            SearchPolicySnapshot searchPolicy = DecisionPolicy;
            SolverResult result = SolveDecision(sample, searchPolicy);
            recordWork(result);
            PlanAction command = CombatSearchCoordinator.BlindFirstAction(observation.StartTurnNumber, result)
                ?? throw new InvalidOperationException("Independent blind policy search produced no command: " + result.BoundaryReason);
            proposals.Add((command, result.BestNode.Score));
        }
        PlanAction selected = proposals.GroupBy(item => BlindCombatCommand.Key(item.Action))
            .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Average(item => item.Score))
            .ThenBy(group => group.Key, StringComparer.Ordinal).First().First().Action;
        _commands.Add(key, selected);
        return selected;
    }

    internal BlindPotionChoiceResult ChoosePotion(CombatRootSnapshot observation, PlanAction command,
        BlindPotionObservation potionObservation)
    {
        CombatRootSnapshot prefixRoot = observation.ObservePrediction(potionObservation.Snapshot.Simulator);
        CombatRootSnapshot normalized = prefixRoot.ForkForSample(_decisionSeeds[0]);
        CardChoiceSpec spec = PotionChoiceSupport.GetSpec(potionObservation.Snapshot.Simulator, potionObservation.Frame.Potion);
        string key = normalized.ContinuationStamp.StateText + JsonSerializer.Serialize(command)
            + JsonSerializer.Serialize(spec.Options.Select(CardChoiceSupport.ChoiceCardKey));
        if (_choices.TryGetValue(key, out BlindPotionChoiceResult? cached)) return cached;
        PlanCardChoice[] choices = CardChoiceSupport.BuildChoices(spec, names,
            policy.Profile.MaxPileChoiceBranchesPerAction, policy.Profile.MaxHandChoiceBranchesPerAction)
            .Select(choice => choice with { SourceId = command.PotionId! }).ToArray();
        List<BlindPotionOptionValue> values = [];
        foreach (PlanCardChoice choice in choices)
        {
            double score = 0, losses = 0;
            int victories = 0;
            foreach (ulong seed in _decisionSeeds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CombatRootSnapshot sample = seed == _decisionSeeds[0] ? normalized : prefixRoot.ForkForSample(seed);
                SearchPolicySnapshot searchPolicy = DecisionPolicy;
                CombatBeamSolver driver = new(sample, names, damage, searchPolicy, cancellationToken,
                    searchProfile: searchPolicy.Profile);
                SimulationSnapshot before = driver.ObserveBlindState();
                SimulationSnapshot? after = null;
                try
                {
                    after = driver.CompleteBlindPotion(before, command, potionObservation.Frame, choice,
                        request => ObserveChoice(request));
                    recordTransitions?.Invoke(1);
                    if (after.BoundaryReason != SearchBoundaryReason.None)
                        throw new NotSupportedException("Blind potion continuation reached " + after.BoundaryReason);
                    CombatRootSnapshot continuation = sample.ObservePrediction(after.Simulator);
                    SolverResult result = SolveDecision(continuation, searchPolicy);
                    recordWork(result);
                    score += result.BestNode.Score;
                    losses += result.ProjectedBattleHpLost;
                    if (result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead
                        && result.BoundaryReason == SearchBoundaryReason.None && !result.Snapshot.HasRisk) victories++;
                }
                finally { before.ReleaseSimulator(); after?.ReleaseSimulator(); }
            }
            values.Add(new(choice, score / _decisionSeeds.Length, victories / (double)_decisionSeeds.Length,
                losses / _decisionSeeds.Length));
        }
        if (values.Count == 0) throw new NotSupportedException("Blind potion has no legal observed selection.");
        BlindPotionOptionValue selected = values.OrderByDescending(value => value.MeanScore)
            .ThenBy(value => value.Choice.Cards.Count)
            .ThenBy(value => JsonSerializer.Serialize(value.Choice), StringComparer.Ordinal).First();
        BlindPotionChoiceResult resultChoice = new(command.PotionId!, selected.Choice, values);
        _choices.Add(key, resultChoice);
        return resultChoice;
    }

    internal BlindSearchEvaluation Evaluate(PlanAction first, ulong trajectorySeed, int budgetMilliseconds)
    {
        Stopwatch watch = Stopwatch.StartNew();
        CombatRootSnapshot trajectory = originalRoot.ForkForSample(trajectorySeed, options.KnownTopDrawCards);
        CombatBeamSolver driver = new(trajectory, names, damage, policy, cancellationToken, searchProfile: policy.Profile);
        SimulationSnapshot state = driver.ObserveBlindState();
        int initialLoss = state.CumulativePlayerHpLost;
        List<PlanAction> actions = [];
        List<BlindPotionChoiceResult> potionChoices = [];
        string stop = "action_limit";
        try
        {
            for (int step = 0; step < options.MaxRolloutActions; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlanAction command = step == 0 ? first : ChooseCommand(trajectory.ObservePrediction(state.Simulator));
                SimulationSnapshot next;
                if (command.Kind == PlanActionKind.UsePotion
                    && PotionChoiceSupport.RequiresChoice(((SimulatedCombatState)state.Simulator.State.CombatState)
                        .GetPotionAtSlot(trajectory.PlayerIdentity, command.PotionSlot)
                        ?? throw new InvalidOperationException("Blind rollout potion disappeared.")))
                {
                    BlindPotionObservation prefix = driver.PrepareBlindPotion(state, command, request => ObserveChoice(request));
                    if (prefix.Snapshot.BoundaryReason != SearchBoundaryReason.None)
                        next = prefix.Snapshot;
                    else
                    {
                        try
                        {
                            BlindPotionChoiceResult choice = ChoosePotion(trajectory, command, prefix);
                            potionChoices.Add(choice);
                            next = driver.CompleteBlindPotion(prefix.Snapshot, command, prefix.Frame, choice.Selected,
                                request => ObserveChoice(request));
                        }
                        finally { prefix.Snapshot.ReleaseSimulator(); }
                    }
                }
                else
                    next = driver.ReplayBlindCommand(state, command, request => ObserveChoice(request));
                state.ReleaseSimulator();
                state = next;
                actions.Add(command);
                if (state.PlayerDead || state.AllEnemiesDead) { stop = "terminal"; break; }
                if (state.BoundaryReason != SearchBoundaryReason.None) { stop = "search_boundary"; break; }
                if (state.Turn - originalRoot.StartTurnNumber >= options.MaxRolloutTurns) { stop = "turn_limit"; break; }
                if (watch.ElapsedMilliseconds >= budgetMilliseconds) { stop = "time_limit"; break; }
            }
            bool terminal = state.PlayerDead || state.AllEnemiesDead;
            double value = terminal || state.BoundaryReason != SearchBoundaryReason.None
                ? ObservedValue(state) : EvaluateLeaf(trajectory.ObservePrediction(state.Simulator));
            return new(trajectorySeed, BlindCombatCommand.Key(first), value,
                damage.HpLostSoFar + Math.Max(0, state.CumulativePlayerHpLost - initialLoss),
                !state.PlayerDead && state.PlayerHp > 0,
                state.AllEnemiesDead && !state.PlayerDead && !state.HasRisk && state.BoundaryReason == SearchBoundaryReason.None,
                state.BoundaryReason, watch.Elapsed)
            {
                Decisions = actions.Count,
                ValueScope = terminal ? "completed_policy_trajectory" : "observed_prefix_with_state_heuristic",
                StopReason = stop,
                ObservedActions = actions.ToArray(),
                PotionChoices = potionChoices.ToArray(),
            };
        }
        finally { state.ReleaseSimulator(); }
    }

    private double EvaluateLeaf(CombatRootSnapshot observation)
    {
        double total = 0;
        foreach (ulong seed in _decisionSeeds)
        {
            CombatRootSnapshot sample = observation.ForkForSample(seed);
            SimulationSnapshot state = new CombatBeamSolver(sample, names, damage, policy, cancellationToken,
                searchProfile: policy.Profile).ObserveBlindState();
            try
            {
                // Value only the freshly sampled current observation, never a clairvoyant tail plan.
                total += state.Score
                    + (state.PlayerMaxHp - originalRoot.InitialPlayerMaxHp - state.PotionStrategicCost) * SolverWeights.Hp
                    + state.FutureResourceValue * SolverWeights.FutureResourceBeamValue
                    + Math.Min(originalRoot.IsActEndingBoss ? SolverWeights.PersistentBuffDeltaBeamCap
                        : SolverWeights.StandardPersistentBuffDeltaBeamCap, Math.Max(0, state.PersistentBuffValue))
                        * (originalRoot.IsActEndingBoss ? SolverWeights.PersistentBuffDeltaBeamValue
                            : SolverWeights.StandardPersistentBuffDeltaBeamValue)
                    + state.ReachableHandValue * -SolverWeights.EnemyHp;
            }
            finally { state.ReleaseSimulator(); }
        }
        return total / _decisionSeeds.Length;
    }

    private double ObservedValue(SimulationSnapshot state)
        => (state.PlayerDead ? SolverWeights.DeathPenalty : 0)
            + (state.AllEnemiesDead && !state.HasRisk ? SolverWeights.VictoryBonus : 0)
            + (state.PlayerHp + state.PlayerMaxHp - originalRoot.InitialPlayerMaxHp
                - state.CumulativePlayerHpLost + state.GrowthHpCredit - state.PotionStrategicCost) * SolverWeights.Hp
            + state.EnemyHp * SolverWeights.EnemyHp;
}
