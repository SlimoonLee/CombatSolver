using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Random;

namespace CombatSolver;

internal static class BlindSearchContracts
{
    internal static object Run(CombatRootSnapshot root)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value)
                throw new InvalidOperationException("Blind contract failed: " + name);
            checks++;
        }
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (ArgumentException) { checks++; return; }
            throw new InvalidOperationException("Blind contract accepted: " + name);
        }
        (Func<CombatPredictionRngSet, Rng> Mutable,
            Func<CombatPredictionRngSet, PredictionRngState> Read)[] streams =
        [
            (s => s.Shuffle, s => s.ShuffleState),
            (s => s.CombatCardGeneration, s => s.CombatCardGenerationState),
            (s => s.CombatPotionGeneration, s => s.CombatPotionGenerationState),
            (s => s.CombatCardSelection, s => s.CombatCardSelectionState),
            (s => s.CombatEnergyCosts, s => s.CombatEnergyCostsState),
            (s => s.CombatTargets, s => s.CombatTargetsState),
            (s => s.CombatOrbGeneration, s => s.CombatOrbGenerationState),
            (s => s.MonsterAi, s => s.MonsterAiState),
            (s => s.Niche, s => s.NicheState),
        ];
        ulong[] seeds = BlindSearchOptions.GenerateSampleSeeds(ulong.MaxValue, 64).ToArray();
        Check(seeds.Distinct().Count() == 64, "unique sample seeds with overflow");
        Reject(() => new BlindSearchOptions([]).Validate(), "empty samples");
        Reject(() => new BlindSearchOptions([1, 1]).Validate(), "duplicate samples");
        Reject(() => new BlindSearchOptions([1], (BlindAggregationMode)99).Validate(), "invalid mode");
        Reject(() => new BlindSearchOptions([1], PerSampleBudgetMilliseconds: 0).Validate(), "zero budget");
        Reject(() => root.ForkForSample(1, int.MaxValue), "invalid known top");

        CombatPredictionSimulator original = root.ForkSimulator();
        ContinuationStamp Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            root.PlayerIdentity, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
        ContinuationStamp before = Stamp(original);
        CombatRootSnapshot firstRoot = root.ForkForSample(seeds[0]);
        CombatRootSnapshot secondRoot = root.ForkForSample(seeds[1]);
        CombatPredictionSimulator first = firstRoot.ForkSimulator();
        CombatPredictionSimulator sibling = firstRoot.ForkSimulator();
        CombatPredictionSimulator different = secondRoot.ForkSimulator();
        Check(Stamp(root.ForkSimulator()) == before, "source root unchanged by sampling");
        Check(Stamp(firstRoot.ForkForSample(seeds[2]).ForkSimulator())
            == Stamp(secondRoot.ForkForSample(seeds[2]).ForkSimulator()),
            "same observation and sample ignore source RNG and draw order");

        string[] Hand(CombatPredictionSimulator sim) => sim.State.GetPlayerCombatState(root.PlayerIdentity)
            .Hand.Cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray();
        string[] Draw(CombatPredictionSimulator sim) => sim.State.GetPlayerCombatState(root.PlayerIdentity)
            .DrawPile.Cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray();
        string[] drawBefore = Draw(original);
        Check(Hand(original).SequenceEqual(Hand(first)), "visible hand preserved");
        Check(drawBefore.Order(StringComparer.Ordinal).SequenceEqual(Draw(first).Order(StringComparer.Ordinal)),
            "draw composition preserved");
        Check(!Draw(first).SequenceEqual(Draw(different)) || drawBefore.Distinct().Count() <= 1,
            "samples explore hidden draw order");
        Check(Draw(root.ForkForSample(seeds[0], drawBefore.Length).ForkSimulator()).SequenceEqual(drawBefore),
            "explicitly known draw order preserved");
        if (drawBefore.Length > 0)
            Check(Draw(root.ForkForSample(seeds[1], 1).ForkSimulator())[0] == drawBefore[0],
                "known top preserved");

        for (int index = 0; index < streams.Length; index++)
        {
            var stream = streams[index];
            PredictionRngState originalState = stream.Read(original.Rng);
            PredictionRngState siblingState = stream.Read(sibling.Rng);
            Check(stream.Read(first.Rng) != stream.Read(different.Rng), "distinct stream " + index);
            Rng retained = stream.Mutable(first.Rng);
            CombatPredictionSimulator child = first.Fork();
            PredictionRngState childBefore = stream.Read(child.Rng);
            Rng native = childBefore.ToRng();
            retained.NextUnsignedLong();
            Check(stream.Read(child.Rng) == childBefore, "retained parent alias " + index);
            Rng childRng = stream.Mutable(child.Rng);
            for (int draw = 0; draw < 8; draw++)
                Check(childRng.NextInt(17) == native.NextInt(17)
                    && childRng.NextBool() == native.NextBool(), "native sample sequence " + index);
            Check(stream.Read(sibling.Rng) == siblingState
                && stream.Read(root.ForkSimulator().Rng) == originalState,
                "source and sibling isolated " + index);
        }
        first.Draw(root.PlayerIdentity, 1);
        Check(Draw(sibling).SequenceEqual(Draw(firstRoot.ForkSimulator())), "draw mutation isolated");
        Check(Stamp(root.ForkSimulator()) == before, "all sampling mutations leave source unchanged");

        PlanCardChoice choice = new(PlanChoiceEffect.GenerateToHand, PileType.None,
            [new("SAMPLE_ONLY", 0, "sample", 0, 0, "sample")]);
        PlanAction withChoice = new(PlanActionKind.PlayCard, root.StartTurnNumber,
            CardId: "DRAW", Choice: choice, TurnStartChoices: [choice]);
        PlanAction command = BlindCombatCommand.From(withChoice);
        Check(command.Choice == null && command.NestedChoices == null && command.TurnStartChoices == null,
            "sample choices never exported");
        Check(BlindCombatCommand.Matches(command, withChoice), "initial command does not fix future choices");
        PlanAction endTurn = new(PlanActionKind.EndTurn, root.StartTurnNumber);
        Check(BlindCombatCommand.Matches(endTurn, BlindCombatCommand.From(endTurn)), "end turn command");

        BlindSearchEvaluation[] risky =
        [
            new(1, "A", 100, 0, true, true, SearchBoundaryReason.None, default),
            new(2, "A", -50, 20, true, true, SearchBoundaryReason.None, default),
            new(3, "A", -50, 20, true, true, SearchBoundaryReason.None, default),
        ];
        BlindSearchEvaluation[] steady = [.. Enumerable.Range(1, 3).Select(seed =>
            new BlindSearchEvaluation((ulong)seed, "B", 20, 7, true, true, SearchBoundaryReason.None, default))];
        BlindSearchActionSummary a = CombatSearchCoordinator.SummarizeBlindAction("A", command, 2, risky);
        BlindSearchActionSummary b = CombatSearchCoordinator.SummarizeBlindAction("B", endTurn, 1, steady);
        Check(a.SampleCount == 3 && a.MeanScore == 0 && a.WorstQuarterBattleHpLost == 20,
            "all samples contribute, including losing samples");
        Check(CombatSearchCoordinator.SelectBlindAction([a, b], BlindAggregationMode.Majority) == a,
            "majority uses votes");
        Check(CombatSearchCoordinator.SelectBlindAction([a, b], BlindAggregationMode.MeanScore) == b,
            "mean rejects single lucky outcome");
        Check(CombatSearchCoordinator.SelectBlindAction([a, b], BlindAggregationMode.RiskAverse) == b,
            "risk mode prefers lower tail loss");
        return new { status = "Passed", checks, streams = streams.Length,
            hiddenInputIndependence = true, sourceUnchanged = true };
    }
}

internal sealed partial class CombatBeamSolver
{
    internal SimulationSnapshot ReplayBlindAuditForTesting(IReadOnlyList<PlanAction> actions,
        Func<TurnStartChoiceRequest, PlanCardChoice?>? observedChoicePolicy = null)
    {
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        return Replay(actions, allowExecutionCapture: false, observedChoicePolicy: observedChoicePolicy);
    }
}
