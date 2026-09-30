using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver;

internal sealed record BlindPotionOptionValue(PlanCardChoice Choice, double MeanScore,
    double VictoryRate, double MeanBattleHpLost)
{
    public string ValueScope { get; init; } = "independent_determinized_choice_proposal";
}

internal sealed record BlindPotionChoiceResult(string SourceId, PlanCardChoice Selected,
    IReadOnlyList<BlindPotionOptionValue> Options);

internal sealed record BlindCardChoiceResult(string SourceId, PlanCardChoice Selected,
    IReadOnlyList<BlindPotionOptionValue> Options)
{
    /// <summary>1-based scoring order among this command's choice layers; nested commands record one entry per layer.</summary>
    public int Layer { get; init; } = 1;
}

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
        List<BlindCardChoiceResult> cardChoices = [];
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
                    next = ReplayCardChoiceAware(driver, trajectory, state, command, cardChoices);
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
                CardChoices = cardChoices.ToArray(),
            };
        }
        finally { state.ReleaseSimulator(); }
    }

    /// <summary>
    /// Replays one command; when its execution surfaces card choices with multiple recorded
    /// candidates (Discovery family, Seeker Strike), the same command is re-replayed from the same
    /// pre-command state once per candidate — deterministic apart from the choice — and each
    /// continuation is scored with independent inner searches. Commands that raise several such
    /// choices (nested generations, auto-played choices) are scored depth-first: every layer is
    /// evaluated under the already-pinned outer layers while deeper requests resolve through the
    /// automatic policy, so a layer's score isolates its own effect. The best candidate's post-state
    /// continues the trajectory; per-option scores are recorded as
    /// independent_determinized_choice_proposal, same as potion options.
    /// </summary>
    private SimulationSnapshot ReplayCardChoiceAware(CombatBeamSolver driver, CombatRootSnapshot trajectory,
        SimulationSnapshot state, PlanAction command, List<BlindCardChoiceResult> cardChoices)
    {
        // The first pass observes every request in arrival order; deterministic replays raise them
        // in the same order, so scored layers are addressed by ordinal. 概率分支模式下穿过怪物
        // 随机分支（最高权重钉扎续行），每次重跑重收集请求列表。
        List<TurnStartChoiceRequest> requests = [];
        SimulationSnapshot firstPass = ReplayThroughMonsterBranches(driver, state, command,
            attemptCommand =>
            {
                requests.Clear();
                return driver.ReplayBlindCommandResolved(state, attemptCommand, request =>
                {
                    requests.Add(request);
                    return ObserveChoice(request);
                });
            });
        List<int> scoringLayers = Enumerable.Range(0, requests.Count)
            .Where(ordinal => requests[ordinal].Spec is { Options.Count: > 1 })
            .ToList();
        if (scoringLayers.Count == 0)
            return firstPass;

        CombatRootSnapshot preCommandRoot = trajectory.ObservePrediction(state.Simulator);
        Dictionary<int, int> pinned = [];
        foreach (int layer in scoringLayers)
        {
            CardChoiceSpec spec = requests[layer].Spec!;
            var evaluated = new List<(PlanCardChoice Choice, double Score, int Victories)>();
            int bestIndex = 0;
            double bestScore = double.NegativeInfinity;
            for (int index = 0; index < spec.Options.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlanCardChoice forced = CardChoiceSupport.BuildRequestedChoiceAt(spec, index);
                double score = 0;
                int victories = 0;
                Dictionary<int, int> layerPins = new(pinned) { [layer] = index };
                foreach (ulong seed in _decisionSeeds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CombatRootSnapshot sample = preCommandRoot.ForkForSample(seed);
                    CombatBeamSolver seedDriver = new(sample, names, damage, DecisionPolicy, cancellationToken,
                        searchProfile: DecisionPolicy.Profile);
                    SimulationSnapshot before = seedDriver.ObserveBlindState();
                    SimulationSnapshot? after = null;
                    try
                    {
                        after = ReplayPinnedCommand(seedDriver, before, command, layerPins);
                        CombatRootSnapshot continuation = sample.ObservePrediction(after.Simulator);
                        SolverResult result = SolveDecision(continuation, DecisionPolicy);
                        recordWork(result);
                        recordTransitions?.Invoke(1);
                        score += result.BestNode.Score;
                        if (result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead
                            && result.BoundaryReason == SearchBoundaryReason.None && !result.Snapshot.HasRisk)
                            victories++;
                    }
                    finally
                    {
                        before.ReleaseSimulator();
                        after?.ReleaseSimulator();
                    }
                }
                double mean = score / _decisionSeeds.Length;
                evaluated.Add((forced, mean, victories));
                if (mean > bestScore)
                {
                    bestScore = mean;
                    bestIndex = index;
                }
            }
            pinned[layer] = bestIndex;
            cardChoices.Add(new BlindCardChoiceResult(requests[layer].SourceId,
                evaluated[bestIndex].Choice with
                {
                    SourceId = requests[layer].SourceId,
                    ContextId = requests[layer].ContextId,
                    Timing = requests[layer].Timing,
                },
                evaluated.Select(item => new BlindPotionOptionValue(item.Choice, item.Score,
                    item.Victories / (double)_decisionSeeds.Length, 0)).ToArray())
            { Layer = cardChoices.Count + 1 });
        }
        return ReplayPinnedCommand(driver, state, command, pinned);
    }

    /// <summary>
    /// 概率分支模式：重放穿过怪物随机分支——每次挂起按最高权重候选钉扎后续行，非怪物分支
    /// 挂起原样交回调用方。确定性模式该 helper 直通（无挂起时零额外重放）。
    /// </summary>
    private SimulationSnapshot ReplayThroughMonsterBranches(CombatBeamSolver driver, SimulationSnapshot before,
        PlanAction command, Func<PlanAction, SimulationSnapshot> replayOnce)
    {
        List<PlanCardChoice> branchPins = [];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            PlanAction attemptCommand = branchPins.Count == 0
                ? command
                : command with { TurnStartChoices = branchPins.ToArray() };
            SimulationSnapshot next = replayOnce(attemptCommand);
            if (next.BoundaryReason != SearchBoundaryReason.PendingChoice)
                return next;
            var simState = (SimulatedCombatState)next.Simulator.State.CombatState;
            if (simState.PendingMonsterBranchDemand is not { } demand)
                return next;
            (string bestId, _) = demand.Candidates.MaxBy(candidate => candidate.Weight);
            branchPins.Add(new PlanCardChoice(
                PlanChoiceEffect.MonsterBranch,
                PileType.None,
                [new PlanCardToken(bestId, 0, string.Empty, 0, 0, string.Empty)],
                MonsterBranchResolutionSupport.SourceId(demand.Owner, demand.BranchId),
                Timing: PlanChoiceTiming.EnemyTurn));
            next.ReleaseSimulator();
        }
        throw new InvalidOperationException("概率分支模式：怪物分支钉扎重放超出上限。");
    }

    /// <summary>
    /// Pins scored layers by request ordinal; every other request keeps the automatic policy.
    /// A pin is a position proposal: per-seed draws can change how many candidates a later
    /// request presents, so the index clamps to the options this replay actually raised.
    /// 概率分支模式下重放穿过怪物随机分支：每次挂起按最高权重候选钉扎后续行（最可能怪物
    /// 响应下的确定性延续；完整期望聚合属后续批次），非怪物分支挂起原样交回调用方。
    /// </summary>
    private SimulationSnapshot ReplayPinnedCommand(CombatBeamSolver driver, SimulationSnapshot before,
        PlanAction command, IReadOnlyDictionary<int, int> pins)
    {
        return ReplayThroughMonsterBranches(driver, before, command, attemptCommand =>
        {
            int ordinal = 0;
            return driver.ReplayBlindCommandResolved(before, attemptCommand, request =>
            {
                int current = ordinal++;
                if (pins.TryGetValue(current, out int index) && request.Spec is { Options.Count: > 0 } spec)
                    return CardChoiceSupport.BuildRequestedChoiceAt(spec, Math.Min(index, spec.Options.Count - 1));
                return ObserveChoice(request);
            });
        });
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
