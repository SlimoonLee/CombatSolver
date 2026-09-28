using System.Diagnostics;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace OfflineSearchHarness;

internal sealed record CombatRolloutResult(string Status, string Encounter, int StartHp, int EndHp,
    int DamageReceived, int Turns, int Decisions, int PotionCommands, int NativeChoices, double WallSeconds);

internal static class CombatRollout
{
    private static readonly JsonSerializerOptions JournalJson = new(UnattendedTestFiles.JsonOptions) { WriteIndented = false };

    internal static CombatRolloutResult Run(CombatState combat, HarnessOptions options, MainLoopContext loop,
        UnattendedTestRunner.OfflineScenarioSession session, ObservedCardSelector selector, TimeSpan timeout)
    {
        Player player = LocalContext.GetMe(combat)!;
        string encounter = (combat.Encounter ?? throw new InvalidDataException("Rollout encounter missing.")).Id.ToString();
        int startHp = player.Creature.CurrentHp, decisions = 0, turns = 1, potions = 0, damageReceived = 0;
        int initialChoiceCount = selector.Choices.Count;
        int choicesCursor = initialChoiceCount;
        bool ended = false, won = false;
        Stopwatch watch = Stopwatch.StartNew();
        using CancellationTokenSource deadline = new(timeout);
        void OnEnded(CombatRoom room) { if (ReferenceEquals(room.CombatState, combat)) ended = true; }
        void OnWon(CombatRoom room) { if (ReferenceEquals(room.CombatState, combat)) won = true; }
        CombatManager.Instance.CombatEnded += OnEnded;
        CombatManager.Instance.CombatWon += OnWon;
        using StreamWriter journal = new(Path.Combine(options.OutputDirectory, "decisions.jsonl"), append: false) { AutoFlush = true };
        try
        {
            string status;
            while (true)
            {
                if (ended) { status = won ? "Victory" : "Defeat"; break; }
                if (watch.Elapsed >= timeout) { status = "TimeLimit"; break; }
                if (decisions >= options.CombatDecisionLimit) { status = "DecisionLimit"; break; }
                turns = Math.Max(turns, player.PlayerCombatState!.TurnNumber);
                if (turns > options.CombatTurnLimit) { status = "TurnLimit"; break; }
                if (!CombatManager.Instance.IsInProgress || combat.CurrentSide != CombatSide.Player
                    || player.PlayerCombatState.Phase != PlayerTurnPhase.Play || RunManager.Instance.ActionExecutor.IsRunning)
                    throw new InvalidOperationException("Rollout did not reach a stable Play boundary.");

                string before = OfflineCombat.DescribeRoot(combat);
                string directory = Path.Combine(options.OutputDirectory, $"decision-{decisions:D4}");
                Directory.CreateDirectory(directory);
                // Seeds depend only on the experiment seed and observation number, never on live RNG.
                HarnessOptions decisionOptions = options with { OutputDirectory = directory,
                    BlindSeed = unchecked(options.BlindSeed + (ulong)decisions * 0x9E3779B97F4A7C15UL) };
                ModRuntime.SearchOutcome outcome;
                try { outcome = ModRuntime.RunSearchDetailed(combat, decisionOptions, loop, session, deadline.Token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    status = "TimeLimit";
                    break;
                }
                if (watch.Elapsed >= timeout) { status = "TimeLimit"; break; }
                PlanAction command = outcome.SelectedCommand ?? throw new InvalidOperationException("Blind search produced no command.");
                if (command.Choice != null || command.NestedChoices != null || command.TurnStartChoices != null
                    || outcome.Result.Continuations.Count > 0 && outcome.BlindPolicy == null)
                    throw new InvalidOperationException("A determinized continuation reached native deployment.");
                journal.WriteLine(JsonSerializer.Serialize(new { phase = "selected", decision = decisions + 1,
                    before, command, outcome.RootUnchanged, policy = outcome.BlindPolicy }, JournalJson));
                GameAction native = CreateAction(combat, player, command);
                int originalTurn = player.PlayerCombatState.TurnNumber;
                int actionDamage = 0;
                // History is cleared by EndCombatInternal; observe entries as they are added.
                int historyCursor = CombatManager.Instance.History.Entries.Count();
                void CountHistory()
                {
                    var entries = CombatManager.Instance.History.Entries.ToArray();
                    foreach (DamageReceivedEntry damage in entries.Skip(historyCursor).OfType<DamageReceivedEntry>())
                        if (ReferenceEquals(damage.Receiver, player.Creature))
                            actionDamage += Math.Max(0, damage.Result.UnblockedDamage);
                    historyCursor = entries.Length;
                }
                CombatManager.Instance.History.Changed += CountHistory;
                try
                {
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(native);
                    loop.RunUntilCompleted(native.CompletionTask, Remaining(watch, timeout), "native command completion");
                    if (native.Exception != null) throw native.Exception;
                    Stopwatch settling = Stopwatch.StartNew();
                    while (!ended && (RunManager.Instance.ActionExecutor.IsRunning
                        || combat.CurrentSide != CombatSide.Player || player.PlayerCombatState.Phase != PlayerTurnPhase.Play
                        || command.Kind == PlanActionKind.EndTurn && player.PlayerCombatState.TurnNumber == originalTurn))
                    {
                        if (watch.Elapsed >= timeout)
                            throw new TimeoutException("Native combat progression exceeded the rollout deadline.");
                        if (settling.Elapsed.TotalSeconds > 10)
                            throw new TimeoutException("Native command completed but combat failed to settle within 10 seconds.");
                        loop.Pump(TimeSpan.FromMilliseconds(5));
                    }
                }
                finally { CombatManager.Instance.History.Changed -= CountHistory; }
                damageReceived += actionDamage;
                decisions++;
                if (command.Kind == PlanActionKind.UsePotion) potions++;
                journal.WriteLine(JsonSerializer.Serialize(new { phase = "executed", decision = decisions, command, actionDamage,
                    after = ended ? $"combat_ended won={won} hp={player.Creature.CurrentHp}" : OfflineCombat.DescribeRoot(combat),
                    nativeChoices = selector.Choices.Skip(choicesCursor).ToArray(),
                    wallSeconds = watch.Elapsed.TotalSeconds }, JournalJson));
                choicesCursor = selector.Choices.Count;
                Console.WriteLine($"[rollout] decision={decisions} turn={originalTurn} command={command.Kind}:{command.CardId ?? command.PotionId} hp={player.Creature.CurrentHp} ended={ended}");
            }
            return new CombatRolloutResult(status, encounter, startHp, player.Creature.CurrentHp,
                damageReceived, turns, decisions, potions, selector.Choices.Count - initialChoiceCount, watch.Elapsed.TotalSeconds);
        }
        finally
        {
            CombatManager.Instance.CombatEnded -= OnEnded;
            CombatManager.Instance.CombatWon -= OnWon;
        }
    }

    private static TimeSpan Remaining(Stopwatch watch, TimeSpan timeout)
    {
        TimeSpan remaining = timeout - watch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Native rollout request deadline exhausted.");
        return remaining;
    }

    private static GameAction CreateAction(CombatState combat, Player player, PlanAction command)
    {
        if (command.Turn != player.PlayerCombatState!.TurnNumber)
            throw new InvalidOperationException("Blind command turn mismatch.");
        Creature? target = command.TargetCombatId is uint id
            ? combat.Creatures.Single(creature => creature.CombatId == id && creature.IsAlive) : null;
        switch (command.Kind)
        {
            case PlanActionKind.PlayCard:
                CardModel card = SolverController.FindCardForDeployment(player.PlayerCombatState.Hand.Cards, command);
                if (!card.CanPlay(out var reason, out _))
                    throw new InvalidOperationException($"Blind command card is not playable: {card.Id}: {reason}.");
                return new PlayCardAction(card, target);
            case PlanActionKind.UsePotion:
                if ((uint)command.PotionSlot >= (uint)player.PotionSlots.Count)
                    throw new InvalidDataException("Blind command potion slot is out of range.");
                PotionModel potion = player.PotionSlots[command.PotionSlot]
                    ?? throw new InvalidDataException("Blind command potion slot is empty.");
                if (potion.Id.Entry != command.PotionId) throw new InvalidDataException("Blind command potion identity mismatch.");
                return new UsePotionAction(potion, target, isCombatInProgress: true);
            case PlanActionKind.EndTurn:
                return new EndPlayerTurnAction(player, player.PlayerCombatState.TurnNumber);
            default:
                throw new NotSupportedException($"Unsupported blind command {command.Kind}.");
        }
    }
}

internal sealed class ObservedCardSelector(string policy) : ICardSelector
{
    internal List<object> Choices { get; } = [];
    internal Func<IReadOnlyList<CardCreationResult>, CardRewardSelection>? RewardSelector { get; set; }
    internal Func<CardModel[], int, int, CardModel[]>? NonCombatSelector { get; set; }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        CardModel[] candidates = options.ToArray();
        CardModel[] selected;
        if (!CombatManager.Instance.IsInProgress && NonCombatSelector != null)
            selected = NonCombatSelector(candidates, minSelect, maxSelect);
        else if (candidates.Length == minSelect)
            selected = candidates;
        else if (policy == "FirstLegal")
            selected = candidates.Take(minSelect).ToArray();
        else
            throw new NotSupportedException($"Observed native card choice requires a policy: {candidates.Length} candidates, {minSelect}..{maxSelect} selections.");
        if (selected.Length < minSelect || selected.Length > maxSelect || selected.Distinct().Count() != selected.Length
            || selected.Any(card => !candidates.Contains(card)))
            throw new InvalidDataException("Observed card selection violates native bounds or identity.");
        Choices.Add(new { policy = CombatManager.Instance.IsInProgress ? policy : "history", minSelect, maxSelect,
            options = candidates.Select(CardChoiceSupport.ChoiceCardKey).ToArray(),
            selected = selected.Select(CardChoiceSupport.ChoiceCardKey).ToArray() });
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => RewardSelector?.Invoke(options) ?? default;
}
