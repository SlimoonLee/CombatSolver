using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
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
        if (potion?.Id.Entry is "SKILL_POTION" or "OROBIC_ACID")
            Check(root.ForkSimulator().State.GetPlayerCombatState(root.PlayerIdentity).Energy == 0,
                "zero-energy fixture is actually applied");
        PlanAction? potionCommand = potion == null ? null : new(PlanActionKind.UsePotion, root.StartTurnNumber,
            PotionId: potion.Id.Entry, PotionSlot: root.PlayerIdentity.GetPotionSlotIndex(potion));
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
        else
        {
            var lethal = result.ActionSummaries.Single(summary => summary.FirstAction.CardId == "BLUDGEON");
            var stoke = result.ActionSummaries.Single(summary => summary.FirstAction.CardId == "STOKE");
            Check(result.SelectedAction!.CardId == "BLUDGEON" && lethal.VictoryRate == 1 && lethal.MeanProjectedBattleHpLost == 0,
                "certain immediate kill is retained instead of consuming that hand in Stoke");
            Check(lethal.MeanScore >= stoke.MeanScore, "Stoke includes the opportunity cost of the exhausted hand");
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
        File.WriteAllText(Path.Combine(options.OutputDirectory, "blind-policy-checks.json"),
            JsonSerializer.Serialize(new { status = "Passed", checks, independentPolicySearchRuns = workRuns, evidence }, Program.Json));
        return $"checks={checks} Passed";
    }
}
