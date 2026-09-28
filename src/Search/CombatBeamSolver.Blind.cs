using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed record BlindPotionObservation(SimulationSnapshot Snapshot, PotionChoiceFrame Frame);

internal sealed partial class CombatBeamSolver
{
    internal SimulationSnapshot ObserveBlindState() => Replay([], allowExecutionCapture: false);

    internal IReadOnlyList<PlanAction> EnumerateBlindCommands(SimulationSnapshot snapshot)
    {
        SearchNode parent = new(null, 0, snapshot.PotionUseCount, snapshot.PotionStrategicCost,
            snapshot.Turn, SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason, false, null, snapshot,
            CombatProgressState.Capture(snapshot));
        ExpansionPlan plan = new(parent, CardNameFirst: true);
        return EnumeratePlannedCardActions(plan).Select(item => BlindCombatCommand.From(item.Action))
            .Concat(EnumeratePlannedPotionActions(plan).Select(item => BlindCombatCommand.From(item.Action)))
            .Append(new(PlanActionKind.EndTurn, snapshot.Turn))
            .DistinctBy(BlindCombatCommand.Key).ToArray();
    }

    internal SimulationSnapshot ReplayBlindCommand(SimulationSnapshot parent, PlanAction command,
        Func<TurnStartChoiceRequest, PlanCardChoice?> observedChoicePolicy)
        => Replay([BlindCombatCommand.From(command)], parent, parent.Turn,
            observedChoicePolicy: observedChoicePolicy, allowExecutionCapture: false);

    internal BlindPotionObservation PrepareBlindPotion(SimulationSnapshot parent, PlanAction command,
        Func<TurnStartChoiceRequest, PlanCardChoice?> observedChoicePolicy)
    {
        CombatPredictionSimulator simulator = parent.Simulator.Fork();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        PotionModel potion = combat.GetPotionAtSlot(_player, command.PotionSlot)
            ?? throw new InvalidOperationException("Blind potion observation has an empty slot.");
        PotionChoiceFrame frame = new(potion, simulator.History.Entries.Count, simulator.ShuffleEventCount);
        combat.BeginActionChoices(TurnStartChoiceCursor.ForAutomaticPolicy(observedChoicePolicy));
        bool complete;
        try
        {
            complete = PotionExecutionSupport.Prepare(simulator, combat, potion, command.PotionSlot,
                combat.GetCreature(command.TargetCombatId));
        }
        finally { combat.EndActionChoices(); }
        SimulationSnapshot snapshot = Snapshot(simulator, parent.Turn, 0, parent.ShufflesCrossed,
            complete ? SearchBoundaryReason.None : SearchBoundaryReason.PendingChoice,
            ((ForkableSet<uint>)parent.ProcessedEnemyDeaths).Fork());
        return new(snapshot, frame);
    }

    internal BlindPotionObservation ObserveNativePotion(PotionModel potion, IReadOnlyList<CardModel> options)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        // The native selector has revealed these cards. They replace hypotheses only at this boundary.
        if (PotionChoiceSupport.GeneratesCardChoice(potion))
            simulator.History.CardGenerationOptions(options.Select(card => new PredictedCard((CardModel)card.MutableClone())).ToArray());
        PotionChoiceFrame frame = new((PotionModel)potion.MutableClone(), 0, simulator.ShuffleEventCount);
        return new(Snapshot(simulator, _startTurnNumber, 0, 0, SearchBoundaryReason.None, new ForkableSet<uint>()), frame);
    }

    internal SimulationSnapshot CompleteBlindPotion(SimulationSnapshot prefix, PlanAction command,
        PotionChoiceFrame frame, PlanCardChoice choice,
        Func<TurnStartChoiceRequest, PlanCardChoice?> observedChoicePolicy)
        => Replay([command with { Choice = choice }], prefix, prefix.Turn,
            potionChoiceFrame: frame, observedChoicePolicy: observedChoicePolicy,
            allowExecutionCapture: false, allowChoiceContinuationFallback: false);
}

internal static class BlindObservedChoices
{
    internal static PlanCardChoice? Resolve(TurnStartChoiceRequest request, SolverDisplayNames names,
        SolverSearchProfile profile, IList<PlanCardChoice>? observed = null)
    {
        if (request.Spec == null) return null;
        PlanCardChoice? choice = CardChoiceSupport.BuildChoices(request.Spec, names,
            profile.MaxPileChoiceBranchesPerAction, profile.MaxHandChoiceBranchesPerAction).FirstOrDefault();
        if (choice == null) return null;
        choice = choice with { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
        observed?.Add(choice);
        return choice;
    }
}
