using CombatSolver;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace OfflineSearchHarness;

internal static class BlindNativeChoices
{
    internal static IDisposable Bind(ObservedCardSelector selector, CombatState live, PlanAction command,
        HarnessOptions options, MainLoopContext loop, CancellationToken cancellationToken)
    {
        var previous = selector.CombatSelector;
        if (selector.UsesBlindPolicy)
        {
            var player = LocalContext.GetMe(live)!;
            PotionModel? potion = command.Kind == PlanActionKind.UsePotion ? player.PotionSlots[command.PotionSlot] : null;
            CombatRootSnapshot before = CombatRootSnapshot.Capture(live);
            SolverDisplayNames names = SolverDisplayNames.Capture(live);
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null);
            policy = policy with { FixedBudget = true, PredictPotionReward = false, Interaction = null,
                RequestWorkTotals = null, PortfolioTelemetry = null, PortfolioExperiment = null };
            CardChoiceSpec? template = null;
            if (command.Kind == PlanActionKind.PlayCard)
            {
                var simulator = before.ForkForSample(options.BlindSeed).ForkSimulator();
                var card = CombatBeamSolver.FindCardForReplay(simulator.State.GetPlayerCombatState(player).Hand.Cards, command)
                    ?? throw new InvalidOperationException("Native blind choice source card is missing.");
                template = CardChoiceSupport.GetSpec(simulator, card);
                if (template == null && card.Preview is Abundance or Discovery or Quasar or Splash)
                    template = new(PlanChoiceEffect.GenerateToHand, PileType.None, 0, 1, [], [], 0);
            }
            int calls = 0;
            selector.CombatSelector = (cards, min, max) =>
            {
                if (++calls > 1)
                    throw new NotSupportedException("Blind native selection needs a source for this nested choice.");
                if (potion != null && PotionChoiceContinuation.Supports(potion))
                {
                    if (PotionChoiceSupport.GeneratesCardChoice(potion) && (min > 1 || max != 1))
                        throw new NotSupportedException("Generated blind potion selection bounds changed.");
                    CombatRootSnapshot observation = CombatRootSnapshot.Capture(live);
                    BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
                    BlindSearchOptions blind = new(BlindSearchOptions.GenerateSampleSeeds(options.BlindSeed, options.BlindSamples),
                        PerSampleBudgetMilliseconds: options.BudgetMilliseconds,
                        PolicySamples: options.BlindPolicySamples, MaxRolloutActions: options.BlindRolloutActions,
                        MaxRolloutTurns: options.BlindRolloutTurns, MaxPolicyExpandedNodes: options.BlindPolicyNodes);
                    using CancellationTokenSource choiceCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    choiceCancellation.CancelAfter(TimeSpan.FromSeconds(30));
                    BlindContinuationPolicy chooser = new(observation, names, damage, policy, blind,
                        Math.Max(1, options.BudgetMilliseconds / blind.MaxRolloutActions), choiceCancellation.Token, _ => { });
                    CombatBeamSolver driver = new(observation, names, damage, policy, cancellationToken, searchProfile: policy.Profile);
                    BlindPotionObservation prefix = driver.ObserveNativePotion(potion, cards);
                    Task<BlindPotionChoiceResult> task = Task.Run(
                        () => chooser.ChoosePotion(observation, command, prefix), choiceCancellation.Token);
                    try
                    {
                        loop.RunUntilCompleted(task, TimeSpan.FromSeconds(30), "observed blind potion selection");
                        BlindPotionChoiceResult result = task.GetAwaiter().GetResult();
                        selector.LastBlindEvidence = result;
                        return Match(cards, result.Selected);
                    }
                    finally
                    {
                        choiceCancellation.Cancel();
                        try
                        {
                            // A timed-out worker must leave the prefix before its simulator is released.
                            if (!task.IsCompleted)
                            {
                                try { task.GetAwaiter().GetResult(); }
                                catch (OperationCanceledException) when (choiceCancellation.IsCancellationRequested) { }
                            }
                        }
                        finally { prefix.Snapshot.ReleaseSimulator(); }
                    }
                }
                if (template == null)
                    throw new NotSupportedException("Blind native choice has no observed source definition.");
                // Draw-pile selection sources (Seeker Strike family) present the vanilla screen
                // order; the prediction's automatic policy picks that same first candidate, so the
                // deployment path takes it directly instead of running the priority branching.
                if (template.Effect == PlanChoiceEffect.MoveToHand && template.SourcePile == PileType.Draw)
                {
                    selector.LastBlindEvidence = new { source = command.CardId, policy = "first_candidate",
                        options = cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray() };
                    return cards.Take(min).ToArray();
                }
                PredictedCard[] observed = cards.Select(card => new PredictedCard((CardModel)card.MutableClone())).ToArray();
                CardChoiceSpec spec = template with { Options = observed, SourceCards = observed, MinCount = min, MaxCount = max,
                    IsImplicitAllSelection = cards.Length == min };
                TurnStartChoiceRequest request = new(command.CardId, spec.Effect, spec.SourcePile, min, spec);
                PlanCardChoice choice = BlindObservedChoices.Resolve(request, names, policy.Profile)
                    ?? throw new NotSupportedException("Blind native selection has no legal visible choice.");
                selector.LastBlindEvidence = new { source = command.CardId, policy = "visible_card_priority", choice };
                return Match(cards, choice);
            };
        }
        return new RestoreScope(() => { selector.CombatSelector = previous; selector.LastBlindEvidence = null; });
    }

    private static CardModel[] Match(CardModel[] candidates, PlanCardChoice choice)
        => choice.Cards.Select(token => candidates.Where(card => CardChoiceSupport.MatchesToken(card, token))
            .Skip(token.OptionOccurrence).FirstOrDefault()
            ?? throw new InvalidDataException("Blind choice does not match the observed native options.")).ToArray();

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
