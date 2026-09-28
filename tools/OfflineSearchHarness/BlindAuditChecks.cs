using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace OfflineSearchHarness;

internal static class BlindAuditChecks
{
    private static readonly RunRngType[] CombatStreams =
    [RunRngType.Shuffle, RunRngType.CombatCardGeneration, RunRngType.CombatPotionGeneration,
        RunRngType.CombatCardSelection, RunRngType.CombatEnergyCosts, RunRngType.CombatTargets,
        RunRngType.CombatOrbs, RunRngType.MonsterAi, RunRngType.Niche];

    internal static string Run(CombatState live, HarnessOptions options, MainLoopContext loop, string mode)
    {
        var player = LocalContext.GetMe(live)!;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), live, false, null);
        policy = policy with
        {
            FixedBudget = true,
            PredictPotionReward = false,
            BudgetOverrideMilliseconds = options.BudgetMilliseconds,
            Profile = policy.Profile with { SoftTimeBudgetMilliseconds = options.BudgetMilliseconds },
        };
        int checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException("Blind audit failed: " + message);
            checks++;
        }
        string Stamp(CombatRootSnapshot source, SimulationSnapshot state)
            => ContinuationStamp.CapturePredicted(player, (CombatPredictionSimulator)state.Simulator,
                state.Turn, source.Forecast, source.StartTurnNumber).StateText;
        SimulationSnapshot Replay(CombatRootSnapshot source, params PlanAction[] prefix)
            => new CombatBeamSolver(source, names, damage, policy)
                .ReplayBlindAuditForTesting(prefix, AuditChoicePolicy);
        // The audit's semantic oracle picks the first candidate in the request's own option order,
        // matching the native FirstVisibleCardSelector, so generated-choice cards (Discovery
        // family) and hand discards (Prepared under Tingsha) reconcile.
        PlanCardChoice? AuditChoicePolicy(TurnStartChoiceRequest request)
        {
            if (request.Spec == null) return null;
            if (request.Spec.Options.Count > 0)
                return CardChoiceSupport.BuildRequestedChoice(request.Spec, ["__FIRST__"]) with
                {
                    SourceId = request.SourceId,
                    ContextId = request.ContextId,
                    Timing = request.Timing,
                };
            var choices = CardChoiceSupport.BuildChoices(request.Spec, names,
                policy.Profile.MaxPileChoiceBranchesPerAction, policy.Profile.MaxHandChoiceBranchesPerAction);
            var choice = choices.FirstOrDefault(item => item.Cards.Count > 0) ?? choices.FirstOrDefault();
            if (choice == null) return null;
            return choice with { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
        }
        PlanAction Play(string id)
        {
            var card = root.ForkSimulator().State.GetPlayerCombatState(player).Hand.Cards
                .Single(c => c.Preview.Id.Entry == id);
            var target = card.Preview.TargetType == TargetType.AnyEnemy ? root.Enemies[0] : null;
            return new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: id,
                TargetIndex: target == null ? -1 : 0, TargetCombatId: target?.CombatId,
                CardStateKey: CardChoiceSupport.ChoiceCardKey(card),
                CardUpgradeLevel: card.Preview.CurrentUpgradeLevel,
                CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "");
        }
        PlanAction end = new(PlanActionKind.EndTurn, root.StartTurnNumber);
        List<object> evidence = [];
        ulong[] seeds = BlindSearchOptions.GenerateSampleSeeds(options.BlindSeed, 4).ToArray();

        PlanAction command;
        if (mode == "effects")
        {
            PotionModel? potion = player.PotionSlots.FirstOrDefault(p => p != null);
            if (potion == null)
            {
                // Audited effects either place exactly one pool card in the opening hand, or (for
                // draw-time cost randomization like Slither/Confused) let EndTurn reveal the effect.
                var handIds = root.ForkSimulator().State.GetPlayerCombatState(root.PlayerIdentity).Hand.Cards
                    .Select(card => card.Preview.Id.Entry).ToHashSet(StringComparer.Ordinal);
                string[] auditedCards =
                    ["STOKE", "INFERNAL_BLADE", "DISTRACTION", "WHITE_NOISE",
                     "TRUE_GRIT", "THRASH", "CINDER", "ANOINTED", "HIDDEN_GEM", "DRAIN_POWER",
                     "ALCHEMIZE", "CHAOS", "BALL_LIGHTNING", "PREPARED"];
                string? auditedId = auditedCards.FirstOrDefault(handIds.Contains);
                command = auditedId == null
                    ? new(PlanActionKind.EndTurn, root.StartTurnNumber)
                    : Play(auditedId);
            }
            else
            {
                command = new(PlanActionKind.UsePotion, root.StartTurnNumber,
                    PotionId: potion.Id.Entry, PotionSlot: player.GetPotionSlotIndex(potion));
            }
            Check(command.CardId is "STOKE" or "INFERNAL_BLADE" or "DISTRACTION" or "WHITE_NOISE"
                or "TRUE_GRIT" or "THRASH" or "CINDER" or "ANOINTED" or "HIDDEN_GEM" or "DRAIN_POWER"
                or "ALCHEMIZE" or "CHAOS" or "BALL_LIGHTNING" or "PREPARED"
                || command.PotionId is "ATTACK_POTION" or "SKILL_POTION" or "POWER_POTION" or "COLORLESS_POTION"
                    or "OROBIC_ACID" or "SNECKO_OIL" or "ENTROPIC_BREW" or "COSMIC_CONCOCTION"
                || (command.Kind == PlanActionKind.EndTurn && string.IsNullOrEmpty(command.CardId)),
                "fixture contains one audited effect");
            PlanAction ResolveChoice(CombatRootSnapshot source)
            {
                if (potion == null || !PotionChoiceSupport.GeneratesCardChoice(potion)) return command;
                var beforeUse = (SimulatedCombatState)source.ForkSimulator().State.CombatState;
                PotionModel ownedPotion = beforeUse.GetPotionAtSlot(player, command.PotionSlot)
                    ?? throw new InvalidOperationException("Missing branch potion before use.");
                SimulationSnapshot probe = Replay(source, command);
                try
                {
                    var simulated = (CombatPredictionSimulator)probe.Simulator;
                    CardChoiceSpec spec = PotionChoiceSupport.GetSpec(simulated, ownedPotion);
                    Check(spec.Options.Count == 3 && spec.MinCount == 0 && spec.MaxCount == 1,
                        "generated options and optional choice bounds");
                    return command with { Choice = CardChoiceSupport.BuildRequestedChoice(spec, ["__FIRST__"]) };
                }
                finally { probe.ReleaseSimulator(); }
            }

            var savedRng = live.RunState.Rng.ToSerializable();
            var draw = player.PlayerCombatState!.DrawPile;
            CardModel[] savedDraw = draw.Cards.ToArray();
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            // Channel-then-turn-end effects (lightning orb passive strikes a random enemy at end of
            // turn) need the EndTurn suffix to reach the random part of the audited effect.
            bool channelsThenTurnEnd = command.Kind == PlanActionKind.PlayCard && command.CardId == "BALL_LIGHTNING";
            PlanAction endTurnCommand = new(PlanActionKind.EndTurn, root.StartTurnNumber);
            PlanAction[] Prefix(PlanAction first) => channelsThenTurnEnd ? [first, endTurnCommand] : [first];
            void AlterHiddenInputs()
            {
                foreach (RunRngType stream in CombatStreams)
                    for (int i = 0; i < 17; i++) live.RunState.Rng.GetRng(stream).NextUnsignedLong();
                draw._cards.Reverse();
            }
            void RestoreHiddenInputs()
            {
                live.RunState.Rng.LoadFromSerializable(savedRng);
                draw._cards.Clear();
                draw._cards.AddRange(savedDraw);
            }
            CombatRootSnapshot altered;
            try
            {
                AlterHiddenInputs();
                altered = CombatRootSnapshot.Capture(live);
                Check(altered.ContinuationStamp.StateText != root.ContinuationStamp.StateText,
                    "test really changes hidden native RNG and draw order");
            }
            finally { RestoreHiddenInputs(); }
            Check(ContinuationStamp.CaptureLive(live).StateText == liveBefore, "hidden-input perturbation restored");
            HashSet<string> sampledOutcomes = new(StringComparer.Ordinal);
            foreach (ulong seed in seeds)
            {
                CombatRootSnapshot left = root.ForkForSample(seed);
                CombatRootSnapshot right = altered.ForkForSample(seed);
                Check(left.ContinuationStamp.StateText == right.ContinuationStamp.StateText,
                    "sample root ignores real hidden RNG and draw order");
                PlanAction action = ResolveChoice(left);
                SimulationSnapshot a = Replay(left, Prefix(action));
                SimulationSnapshot? b = null;
                try
                {
                    AlterHiddenInputs();
                    PlanAction other = ResolveChoice(right);
                    Check(JsonSerializer.Serialize(action, Program.Json) == JsonSerializer.Serialize(other, Program.Json),
                        "sample option choice ignores real hidden RNG");
                    b = Replay(right, Prefix(other));
                    Check(Stamp(left, a) == Stamp(right, b), "full effect and RNG ignore real hidden inputs");
                    Check(a.Score == b.Score, "effect valuation ignores real hidden inputs");
                    var sim = (CombatPredictionSimulator)a.Simulator;
                    var simState = sim.State.GetPlayerCombatState(player);
                    var simCombat = (SimulatedCombatState)sim.State.CombatState;
                    string[] hand = simState.Hand.Cards
                        .Select(CardChoiceSupport.ChoiceCardKey).ToArray();
                    // Randomness can land outside the hand: pile contents (upgrade/replay marks),
                    // potion slots and orb types all differ per sample for the respective sources.
                    string[] discard = simState.DiscardPile.Cards.Select(CardChoiceSupport.ChoiceCardKey)
                        .OrderBy(key => key, StringComparer.Ordinal).ToArray();
                    string[] drawPile = simState.DrawPile.Cards.Select(CardChoiceSupport.ChoiceCardKey)
                        .OrderBy(key => key, StringComparer.Ordinal).ToArray();
                    string[] slots = Enumerable.Range(0, player.PotionSlots.Count)
                        .Select(slot => simCombat.GetPotionAtSlot(player, slot)?.Id.Entry ?? "-").ToArray();
                    string[] orbs = simState.OrbQueue.Orbs.Select(orb => orb.Id.Entry).ToArray();
                    string[] enemyHps = sim.State.Enemies
                        .Select(creature => sim.State.GetCreature(creature).CurrentHp.ToString()).ToArray();
                    string outcome = string.Join(";", hand) + "|" + string.Join(";", discard)
                        + "|" + string.Join(";", drawPile) + "|" + string.Join(";", slots)
                        + "|" + string.Join(";", orbs) + "|" + string.Join(";", enemyHps)
                        + ":" + action.Choice?.Cards.FirstOrDefault()?.CardId;
                    sampledOutcomes.Add(outcome);
                    evidence.Add(new { sampleSeed = seed, hand, discard, drawPile, slots, orbs, enemyHps,
                        selected = action.Choice?.Cards.Select(c => c.CardId).ToArray(),
                        boundary = a.BoundaryReason.ToString(), a.Score });
                    PlanAction exported = BlindCombatCommand.From(action);
                    Check(exported.Choice == null && exported.NestedChoices == null && exported.TurnStartChoices == null,
                        "sample choice is removed from native command");
                }
                finally { RestoreHiddenInputs(); a.ReleaseSimulator(); b?.ReleaseSimulator(); }
            }
            Check(sampledOutcomes.Count > 1,
                "independent samples explore different generated outcomes (count=" + sampledOutcomes.Count + ")");
            Check(ContinuationStamp.CaptureLive(live).StateText == liveBefore, "effect sampling leaves native state unchanged");

            // Deterministic native equality is a semantic oracle, separate from the blind decision.
            PlanAction nativePlan = ResolveChoice(root);
            SimulationSnapshot expected = Replay(root, Prefix(nativePlan));
            try
            {
                string projected = Stamp(root, expected);
                var selector = new FirstVisibleCardSelector();
                using (CardSelectCmd.PushSelector(selector, localOnly: true))
                {
                    ExecuteNative(live, BlindCombatCommand.From(nativePlan), loop);
                    if (channelsThenTurnEnd) ExecuteNative(live, endTurnCommand, loop);
                }
                ContinuationStamp actual = ContinuationStamp.CaptureLive(live);
                Check(projected == actual.StateText, "native effect matches full predicted continuation including RNG");
                evidence.Add(new { native = true, selector.Options, selector.Selected,
                    hand = player.PlayerCombatState!.Hand.Cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray() });
            }
            finally { expected.ReleaseSimulator(); }
        }
        else if (mode is "delayed" or "immediate")
        {
            command = Play("OUTMANEUVER");
            int energyBefore = player.PlayerCombatState!.Energy;
            SimulationSnapshot now = Replay(root, command), next = Replay(root, command, end);
            try
            {
                var nowSim = (CombatPredictionSimulator)now.Simulator;
                var nextSim = (CombatPredictionSimulator)next.Simulator;
                int energyNow = nowSim.State.GetPlayerCombatState(player).Energy;
                int energyNext = nextSim.State.GetPlayerCombatState(player).Energy;
                Check(energyNow == energyBefore - 1, "setup has an immediate energy cost");
                Check(next.Turn == root.StartTurnNumber + 1 && energyNext == 5,
                    "next-turn energy gain is actually realized");
                evidence.Add(new { stage = "realized-energy", energyBefore, energyNow, energyNext });
            }
            finally { now.ReleaseSimulator(); next.ReleaseSimulator(); }
            var commands = mode == "delayed" ? new[] { command, end }
                : new[] { command, Play("STRIKE_SILENT"), end };
            List<BlindSearchActionSummary> summaries = [];
            foreach (PlanAction action in commands)
            {
                List<BlindSearchEvaluation> values = [];
                foreach (ulong seed in seeds.Take(3))
                {
                    SolverResult result = new CombatBeamSolver(root.ForkForSample(seed), names, damage, policy,
                        initialActionConstraint: action).Solve();
                    PlanAction? first = CombatSearchCoordinator.BlindFirstAction(root.StartTurnNumber, result);
                    Check(first != null && BlindCombatCommand.Matches(action, first), "forced initial action is honored");
                    Check(result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead,
                        "comparison reaches a complete victory");
                    values.Add(new(seed, BlindCombatCommand.Key(action), result.BestNode.Score,
                        result.ProjectedBattleHpLost, !result.Snapshot.PlayerDead, result.Snapshot.AllEnemiesDead,
                        result.BoundaryReason, result.TotalSearchElapsed));
                }
                var summary = CombatSearchCoordinator.SummarizeBlindAction(BlindCombatCommand.Key(action), action, 0, values);
                summaries.Add(summary);
                evidence.Add(new { action = action.CardId.Length > 0 ? action.CardId : action.Kind.ToString(),
                    summary.MeanProjectedBattleHpLost, summary.MeanScore, values });
            }
            var winner = CombatSearchCoordinator.SelectBlindAction(summaries, BlindAggregationMode.MeanScore);
            Check(winner.FirstAction.CardId == (mode == "delayed" ? "OUTMANEUVER" : "STRIKE_SILENT"),
                "delayed setup wins only when its later benefit repays the cost");
            var blind = CombatSearchCoordinator.SolveBlind(root, names, damage, policy,
                new(BlindSearchOptions.GenerateSampleSeeds(options.BlindSeed, 3), BlindAggregationMode.MeanScore,
                    options.BudgetMilliseconds), CancellationToken.None);
            Check(blind.SelectedAction?.CardId == winner.FirstAction.CardId, "production blind decision agrees with the comparison");
            evidence.Add(new { stage = "production-blind", blind.SelectedAction, blind.ActionSummaries });
            if (mode == "delayed")
            {
                SimulationSnapshot expected = Replay(root, command, end);
                try
                {
                    string projected = Stamp(root, expected);
                    ExecuteNative(live, command, loop);
                    ExecuteNative(live, end, loop);
                    Check(projected == ContinuationStamp.CaptureLive(live).StateText,
                        "two-turn native lifecycle matches full predicted continuation");
                }
                finally { expected.ReleaseSimulator(); }
            }
        }
        else throw new ArgumentException("Unknown blind audit mode: " + mode);

        File.WriteAllText(Path.Combine(options.OutputDirectory, "blind-audit.json"), JsonSerializer.Serialize(
            new { status = "Passed", mode, checks, evidence }, Program.Json));
        return $"mode={mode} checks={checks} Passed";
    }

    private static void ExecuteNative(CombatState state, PlanAction command, MainLoopContext loop)
    {
        var player = LocalContext.GetMe(state)!;
        GameAction action = CombatRollout.CreateAction(state, player, command);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        loop.RunUntilCompleted(action.CompletionTask, TimeSpan.FromSeconds(12), "audit native action");
        if (action.Exception != null) throw action.Exception;
        if (command.Kind == PlanActionKind.EndTurn)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (player.PlayerCombatState!.Phase != PlayerTurnPhase.Play
                || player.PlayerCombatState.TurnNumber == command.Turn)
            {
                if (watch.Elapsed.TotalSeconds > 12) throw new TimeoutException("Audit next turn did not settle.");
                loop.Pump(TimeSpan.FromMilliseconds(5));
            }
        }
    }

    private sealed class FirstVisibleCardSelector : ICardSelector
    {
        internal string[] Options { get; private set; } = [];
        internal string[] Selected { get; private set; } = [];
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            CardModel[] cards = options.ToArray();
            // Potion reconciliations present exactly three cards; discard triggers (e.g. Prepared
            // under Tingsha) present an arbitrary hand with min=max=1. Both pick the first card.
            if (maxSelect != 1 || minSelect > 1 || (cards.Length != 3 && minSelect != 1))
                throw new NotSupportedException("Audit expects a single-pick visible choice.");
            Options = cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray();
            Selected = [CardChoiceSupport.ChoiceCardKey(cards[0])];
            return Task.FromResult<IEnumerable<CardModel>>([cards[0]]);
        }
        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new NotSupportedException("Audit does not enter rewards.");
    }
}
