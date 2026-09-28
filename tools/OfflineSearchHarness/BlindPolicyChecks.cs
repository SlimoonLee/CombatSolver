using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

internal static class BlindPolicyChecks
{
    internal static string Run(CombatState live, HarnessOptions options, MainLoopContext loop)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null);
        policy = policy with { FixedBudget = true, PredictPotionReward = false,
            BudgetOverrideMilliseconds = options.BudgetMilliseconds,
            Profile = policy.Profile with { SoftTimeBudgetMilliseconds = options.BudgetMilliseconds } };
        BlindSearchOptions blindOptions = new(BlindSearchOptions.GenerateSampleSeeds(options.BlindSeed, 3),
            PerSampleBudgetMilliseconds: options.BudgetMilliseconds, MaxPolicyExpandedNodes: 128);
        int checks = 0, workRuns = 0;
        List<object> evidence = [];
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException("Blind policy check failed: " + message);
            checks++;
        }
        BlindContinuationPolicy Chooser() => new(root, names, damage, policy, blindOptions,
            options.BudgetMilliseconds, CancellationToken.None, _ => workRuns++);
        string initialLive = ContinuationStamp.CaptureLive(live).StateText;
        CombatRootSnapshot left = root.ForkForSample(17), right = root.ForkForSample(41);
        Check(left.ContinuationStamp != right.ContinuationStamp, "outer hidden futures really differ");
        BlindContinuationPolicy a = Chooser(), b = Chooser();
        PlanAction leftCommand = a.ChooseCommand(left), rightCommand = b.ChooseCommand(right);
        Check(BlindCombatCommand.Matches(leftCommand, rightCommand),
            "fresh policies choose the same command under the same observation and different hidden futures");
        evidence.Add(new { stage = "command_nonanticipation", leftCommand, rightCommand });

        PotionModel? potion = root.PlayerIdentity.PotionSlots.FirstOrDefault(p => p != null);
        if (potion?.Id.Entry is "SKILL_POTION" or "POWER_POTION" or "COLORLESS_POTION" or "OROBIC_ACID")
            Check(root.ForkSimulator().State.GetPlayerCombatState(root.PlayerIdentity).Energy == 0,
                "zero-energy fixture is actually applied");
        PlanAction? potionCommand = potion == null ? null : new(PlanActionKind.UsePotion, root.StartTurnNumber,
            PotionId: potion.Id.Entry, PotionSlot: root.PlayerIdentity.GetPotionSlotIndex(potion));
        PlanAction? poolCardCommand = potion == null ? FindHandPoolCardCommand(root) : null;
        HashSet<string> openingHandIds = root.ForkSimulator().State.GetPlayerCombatState(root.PlayerIdentity)
            .Hand.Cards.Select(card => card.Preview.Id.Entry).ToHashSet(StringComparer.Ordinal);
        if (potion != null && PotionChoiceSupport.GeneratesCardChoice(potion))
        {
            CombatBeamSolver driver = new(left, names, damage, policy, searchProfile: policy.Profile);
            SimulationSnapshot state = driver.ObserveBlindState();
            BlindPotionObservation prefix = driver.PrepareBlindPotion(state, potionCommand!, request => a.ObserveChoice(request));
            try
            {
                Check(prefix.Snapshot.BoundaryReason == SearchBoundaryReason.None, "stable generated potion prefix");
                CombatRootSnapshot observed = left.ObservePrediction(prefix.Snapshot.Simulator);
                CombatRootSnapshot futureA = observed.ForkForSample(71), futureB = observed.ForkForSample(97);
                SimulationSnapshot stateA = new CombatBeamSolver(futureA, names, damage, policy).ObserveBlindState();
                SimulationSnapshot stateB = new CombatBeamSolver(futureB, names, damage, policy).ObserveBlindState();
                try
                {
                    BlindPotionChoiceResult choiceA = Chooser().ChoosePotion(futureA, potionCommand!, new(stateA, prefix.Frame));
                    BlindPotionChoiceResult choiceB = Chooser().ChoosePotion(futureB, potionCommand!, new(stateB, prefix.Frame));
                    Check(JsonSerializer.Serialize(choiceA, Program.Json) == JsonSerializer.Serialize(choiceB, Program.Json),
                        "observed option values and selected card ignore the outer hidden future");
                    Check(choiceA.Options.Count == 4 && choiceA.Options.Any(option => option.Choice.Cards.Count == 0),
                        "all three observed cards and skip receive equal independent samples");
                    evidence.Add(new { stage = "choice_nonanticipation", choiceA, choiceB });
                }
                finally { stateA.ReleaseSimulator(); stateB.ReleaseSimulator(); }
            }
            finally { state.ReleaseSimulator(); prefix.Snapshot.ReleaseSimulator(); }
        }

        BlindSearchResult result = CombatSearchCoordinator.SolveBlind(root, names, damage, policy, blindOptions, CancellationToken.None);
        Check(result.Evaluations.Count == result.ActionSummaries.Count * blindOptions.SampleSeeds.Count,
            "complete equal-sample matrix");
        Check(result.Evaluations.All(value => value.ValueScope != "determinized_plan"
            && value.ObservedActions.Count == value.Decisions && value.Decisions is > 0 and <= 12),
            "candidate values come from bounded observed action trajectories");
        Check(result.Evaluations.All(value => value.ActionKey == BlindCombatCommand.Key(value.ObservedActions[0])),
            "every candidate executes its constrained first command");
        Check(result.SelectedAction != null && result.SelectedAction.Choice == null
            && result.SelectedAction.NestedChoices == null && result.SelectedAction.TurnStartChoices == null,
            "hypothesis choices are not exported");
        if (potionCommand != null)
            Check(result.Evaluations.Any(value => value.ActionKey == BlindCombatCommand.Key(potionCommand)),
                "random pool action is evaluated even without discovery votes");
        else if (poolCardCommand != null && poolCardCommand.CardId == "STOKE")
        {
            var lethal = result.ActionSummaries.Single(summary => summary.FirstAction.CardId == "BLUDGEON");
            var stoke = result.ActionSummaries.Single(summary => summary.FirstAction.CardId == "STOKE");
            Check(result.SelectedAction!.CardId == "BLUDGEON" && lethal.VictoryRate == 1 && lethal.MeanProjectedBattleHpLost == 0,
                "certain immediate kill is retained instead of consuming that hand in Stoke");
            Check(lethal.MeanScore >= stoke.MeanScore, "Stoke includes the opportunity cost of the exhausted hand");
        }
        else if (poolCardCommand != null)
        {
            Check(result.Evaluations.Any(value => value.ActionKey == BlindCombatCommand.Key(poolCardCommand)),
                "random pool action is evaluated even without discovery votes");
            string poolCardId = poolCardCommand.CardId!;
            string? killerId = poolCardId switch
            {
                "INFERNAL_BLADE" or "CINDER" or "DRAIN_POWER" or "HIDDEN_GEM"
                    or "DISCOVERY" or "ABUNDANCE" or "QUASAR" or "SPLASH" or "METAMORPHOSIS"
                    or "MANIFEST_AUTHORITY" or "MAD_SCIENCE" or "SEEKER_STRIKE"
                    or "CATASTROPHE" or "REBOOT" => "BLUDGEON",
                "WHITE_NOISE" or "CHAOS" => "STRIKE_DEFECT",
                "DISTRACTION" => "STRIKE_SILENT",
                // THRASH/JACKPOT/BALL_LIGHTNING: the pool card itself deals damage, so no
                // certain-kill counter-example is asserted; BALL_LIGHTNING also needs a two-enemy
                // fixture for its end-of-turn random target, where a one-command kill cannot complete.
                "STOKE" or "THRASH" or "BALL_LIGHTNING" or "JACKPOT" or "UPROAR" => null,
                _ => "STRIKE_IRONCLAD",
            };
            if (killerId != null)
            {
                var lethal = result.ActionSummaries.First(summary => summary.FirstAction.CardId == killerId);
                var pool = result.ActionSummaries.First(summary => summary.FirstAction.CardId == poolCardId);
                Check(lethal.VictoryRate == 1 && lethal.MeanProjectedBattleHpLost == 0,
                    "the available certain kill completes at zero loss");
                Check(lethal.MeanScore >= pool.MeanScore,
                    poolCardId + " carries the opportunity cost of the available kill");
                if (poolCardId != "ALCHEMIZE")
                    Check(result.SelectedAction!.CardId == killerId,
                        "certain immediate kill is retained instead of spending the turn on the random pool");
            }
            var poolTrajectories = result.Evaluations
                .Where(value => value.ActionKey == BlindCombatCommand.Key(poolCardCommand)).ToArray();
            Check(poolTrajectories.Length > 0 && poolTrajectories.All(value => value.ObservedActions.Count >= 2),
                "post-generation observation makes a fresh follow-up decision");
            if (poolCardId is "INFERNAL_BLADE" or "DISTRACTION")
                Check(poolTrajectories.Any(value => value.ObservedActions.Skip(1).Any(
                        action => action.Kind == PlanActionKind.PlayCard && !openingHandIds.Contains(action.CardId))),
                    "post-generation observation plays the revealed card");
        }
        else if (EndTurnRevealedFixtureApplies(live))
        {
            // Slither/Confused style draw-time cost randomization: EndTurn reveals the effect and the
            // next-turn decision must react to the sampled costs.
            var endTurn = result.ActionSummaries.First(summary => summary.FirstAction.Kind == PlanActionKind.EndTurn);
            var endTrajectories = result.Evaluations
                .Where(value => value.ActionKey == endTurn.FirstActionKey).ToArray();
            Check(endTrajectories.Length > 0
                    && endTrajectories.All(value => value.ObservedActions.Count >= 2),
                "end-turn revealed randomness leads to a fresh follow-up decision");
        }
        else
        {
            throw new InvalidOperationException("Policy fixture has no known random-pool source.");
        }
        Check(ContinuationStamp.CaptureLive(live).StateText == initialLive, "policy search leaves native state unchanged");
        evidence.Add(new { stage = "pool_matrix", result.SelectedAction, result.ActionSummaries, result.Evaluations,
            result.SearchRuns, result.TotalExpandedNodes, result.TotalTransitionCount });

        if (potion != null && PotionChoiceContinuation.Supports(potion))
        {
            ObservedCardSelector selector = new("Blind");
            using var selectScope = CardSelectCmd.PushSelector(selector, localOnly: true);
            using var commandScope = BlindNativeChoices.Bind(selector, live, potionCommand!, options with { BlindSamples = 3 },
                loop, CancellationToken.None);
            GameAction action = CombatRollout.CreateAction(live, root.PlayerIdentity, potionCommand!);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
            loop.RunUntilCompleted(action.CompletionTask, TimeSpan.FromSeconds(30), "blind native potion completion");
            if (action.Exception != null) throw action.Exception;
            JsonElement selected = JsonSerializer.SerializeToElement(selector.Choices.Single(), Program.Json);
            Check(selected.GetProperty("selected").GetArrayLength() > 0,
                "native optional potion choice selects useful cards rather than minSelect=0");
            PlanCardChoice nativeChoice = JsonSerializer.Deserialize<BlindPotionChoiceResult>(
                selected.GetProperty("valuation").GetRawText(), Program.Json)!.Selected;
            SimulationSnapshot expected = new CombatBeamSolver(root, names, damage, policy)
                .ReplayBlindAuditForTesting([potionCommand! with { Choice = nativeChoice }]);
            try
            {
                ContinuationStamp predicted = ContinuationStamp.CapturePredicted(root.PlayerIdentity,
                    expected.Simulator, expected.Turn, root.Forecast, root.StartTurnNumber);
                Check(predicted == ContinuationStamp.CaptureLive(live),
                    "selected native card, free cost, completion hooks, piles and RNG match full prediction");
            }
            finally { expected.ReleaseSimulator(); }
            evidence.Add(new { stage = "native_observed_selection", selected });
        }
        else if (poolCardCommand != null && poolCardCommand.CardId == "SEEKER_STRIKE")
        {
            // Seeker Strike presents the real draw-pile candidate screen. The blind deployment path
            // must pick the same candidate the prediction's automatic policy picks, and the native
            // candidate list must match the predicted candidates instance by instance.
            List<IReadOnlyList<string>> predictedChoiceOptions = [];
            PlanCardChoice? PredictionChoicePolicy(TurnStartChoiceRequest request)
            {
                if (request.Spec == null) return null;
                predictedChoiceOptions.Add(request.Spec.Options
                    .Select(CardChoiceSupport.ChoiceCardKey).ToArray());
                return CardChoiceSupport.BuildRequestedChoice(request.Spec, ["__FIRST__"]) with
                {
                    SourceId = request.SourceId,
                    ContextId = request.ContextId,
                    Timing = request.Timing,
                };
            }
            SimulationSnapshot predicted;
            try
            {
                predicted = new CombatBeamSolver(root, names, damage, policy)
                    .ReplayBlindAuditForTesting([poolCardCommand], PredictionChoicePolicy);
                Check(predictedChoiceOptions.Count == 1 && predictedChoiceOptions[0].Count >= 2,
                    "prediction presents multiple distinct draw-pile candidates");
            }
            finally { }
            ContinuationStamp predictedStamp;
            try
            {
                predictedStamp = ContinuationStamp.CapturePredicted(root.PlayerIdentity,
                    predicted.Simulator, predicted.Turn, root.Forecast, root.StartTurnNumber);
            }
            finally { predicted.ReleaseSimulator(); }

            ObservedCardSelector selector = new("Blind");
            using var selectScope = CardSelectCmd.PushSelector(selector, localOnly: true);
            using var commandScope = BlindNativeChoices.Bind(selector, live, poolCardCommand, options with { BlindSamples = 3 },
                loop, CancellationToken.None);
            GameAction action = CombatRollout.CreateAction(live, root.PlayerIdentity, poolCardCommand);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
            loop.RunUntilCompleted(action.CompletionTask, TimeSpan.FromSeconds(30), "blind native seeker strike completion");
            if (action.Exception != null) throw action.Exception;
            JsonElement selected = JsonSerializer.SerializeToElement(selector.Choices.Single(), Program.Json);
            Check(selected.GetProperty("selected").GetArrayLength() == 1,
                "native seeker strike selects exactly one draw-pile candidate");
            string[] nativeOptions = selected.GetProperty("options").EnumerateArray()
                .Select(item => item.GetString() ?? "").ToArray();
            string nativeSelected = selected.GetProperty("selected").EnumerateArray().First().GetString() ?? "";
            Check(nativeOptions.SequenceEqual(predictedChoiceOptions[0]),
                "native candidates match predicted candidates instance by instance in vanilla screen order");
            SimulationSnapshot expected = new CombatBeamSolver(root, names, damage, policy)
                .ReplayBlindAuditForTesting([poolCardCommand], PredictionChoicePolicy);
            try
            {
                ContinuationStamp expectedStamp = ContinuationStamp.CapturePredicted(root.PlayerIdentity,
                    expected.Simulator, expected.Turn, root.Forecast, root.StartTurnNumber);
                Check(expectedStamp == predictedStamp,
                    "the automatic prediction choice is deterministic across replays");
            }
            finally { expected.ReleaseSimulator(); }
            Check(nativeSelected == predictedChoiceOptions[0][0],
                "the native screen picks the same first candidate the prediction picked");
            Check(predictedStamp == ContinuationStamp.CaptureLive(live),
                "native seeker selection, attack, piles and RNG match full prediction");
            evidence.Add(new { stage = "native_observed_selection", nativeOptions, nativeSelected,
                predictedOptions = predictedChoiceOptions[0] });
        }
        File.WriteAllText(Path.Combine(options.OutputDirectory, "blind-policy-checks.json"),
            JsonSerializer.Serialize(new { status = "Passed", checks, independentPolicySearchRuns = workRuns, evidence }, Program.Json));
        return $"checks={checks} Passed";
    }

    private static PlanAction? FindHandPoolCardCommand(CombatRootSnapshot root)
    {
        var hand = root.ForkSimulator().State.GetPlayerCombatState(root.PlayerIdentity).Hand.Cards;
        string? poolCardId = hand.Select(card => card.Preview.Id.Entry)
            .FirstOrDefault(id => id is "STOKE" or "INFERNAL_BLADE" or "WHITE_NOISE" or "DISTRACTION"
                or "TRUE_GRIT" or "THRASH" or "CINDER" or "ANOINTED" or "HIDDEN_GEM" or "DRAIN_POWER"
                or "ALCHEMIZE" or "CHAOS" or "BALL_LIGHTNING"
                or "BUNDLE_OF_JOY" or "METAMORPHOSIS"
                or "DISCOVERY" or "ABUNDANCE" or "QUASAR" or "SPLASH"
                or "JACKPOT" or "MANIFEST_AUTHORITY" or "MAD_SCIENCE"
                or "SEEKER_STRIKE" or "UPROAR" or "CATASTROPHE" or "REBOOT");
        if (poolCardId == null) return null;
        var card = hand.Single(c => c.Preview.Id.Entry == poolCardId);
        var target = card.Preview.TargetType == TargetType.AnyEnemy ? root.Enemies[0] : null;
        return new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: poolCardId,
            TargetIndex: target == null ? -1 : 0, TargetCombatId: target?.CombatId,
            CardStateKey: CardChoiceSupport.ChoiceCardKey(card),
            CardUpgradeLevel: card.Preview.CurrentUpgradeLevel,
            CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "");
    }

    private static bool EndTurnRevealedFixtureApplies(CombatState live)
    {
        var player = LocalContext.GetMe(live)!;
        var combat = player.PlayerCombatState!;
        bool slither = combat.DrawPile.Cards.Concat(combat.Hand.Cards)
            .Any(card => card.Enchantment?.Id.Entry == "SLITHER");
        bool confused = player.Creature.Powers.Any(power => power.Id.Entry == "CONFUSED_POWER");
        bool generationPower = player.Creature.Powers.Any(power => power.Id.Entry is
            "CALL_OF_THE_VOID_POWER" or "CREATIVE_AI_POWER" or "HELLO_WORLD_POWER" or "SPECTRUM_SHIFT_POWER");
        return slither || confused || generationPower;
    }
}
