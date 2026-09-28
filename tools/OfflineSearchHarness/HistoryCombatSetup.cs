using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.TestSupport;

namespace OfflineSearchHarness;

/// <summary>Native run prefix guided by history, which does not contain exact map coordinates or unlock state.</summary>
internal sealed class HistoryCombatSetup
{
    private readonly JsonElement _history;
    private readonly JsonElement[] _nodes;
    private readonly ObservedCardSelector _selector;
    private int _nodeIndex;
    private int _rewardChoiceIndex;
    internal List<object> Guidance { get; } = [];
    internal string? StopReason { get; private set; }

    internal HistoryCombatSetup(string path, ObservedCardSelector selector)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        SavedCombatSetup.ValidateExtensions(document.RootElement, "$", new List<string>());
        _history = document.RootElement.Clone();
        if (_history.GetProperty("players").GetArrayLength() != 1)
            throw new NotSupportedException("History guidance requires a singleplayer run.");
        _nodes = _history.GetProperty("map_point_history")[0].EnumerateArray().Select(node => node.Clone()).ToArray();
        _selector = selector;
        selector.RewardSelector = SelectCardReward;
        selector.NonCombatSelector = SelectNonCombatCards;
    }

    internal async Task EnterFirstAsync()
    {
        using IDisposable setupSelector = CardSelectCmd.PushSelector(_selector, localOnly: false);
        CharacterModel character = OfflineCombat.ResolveUnique(ModelDb.AllCharacters,
            _history.GetProperty("players")[0].GetProperty("character").GetString()!, "character");
        List<ActModel> acts = _history.GetProperty("acts").EnumerateArray()
            .Select(act => OfflineCombat.ResolveUnique(ModelDb.All.OfType<ActModel>(), act.GetString()!, "act").ToMutable()).ToList();
        RunState state = RunState.CreateForNewRun([Player.CreateForNewRun(character, UnlockState.all, 1)],
            acts, [], GameMode.Standard, _history.GetProperty("ascension").GetInt32(), _history.GetProperty("seed").GetString()!);
        RunManager.Instance.SetUpNewSingleplayer(state, shouldSave: false);
        await RunManager.Instance.FinalizeStartingRelics();
        RunManager.Instance.Launch();
        await RunManager.Instance.EnterAct(0, doTransition: false);
        Guidance.Add(new { kind = "history_scope", seed = state.Rng.Seed,
            coordinates = "lowest_column_connected_node_matching_recorded_type", unlockState = "all",
            comparison = "history_guided_not_exact_manual_replay" });
        if (_nodes[0].GetProperty("map_point_type").GetString() != "ancient")
            throw new NotSupportedException("History prefix does not begin at an ancient.");
        if (state.CurrentRoom is not EventRoom)
            await RunManager.Instance.EnterMapCoord(state.Map.StartingMapPoint.coord);
        EventRoom room = state.CurrentRoom as EventRoom ?? throw new InvalidDataException("Native ancient room missing.");
        string recordedEvent = _nodes[0].GetProperty("rooms")[0].GetProperty("model_id").GetString()!;
        if (room.ModelId.ToString() != recordedEvent)
            throw new InvalidDataException($"Ancient differs from history: expected={recordedEvent}, actual={room.ModelId}.");
        string chosen = Stats.GetProperty("ancient_choice").EnumerateArray()
            .Single(choice => choice.GetProperty("was_chosen").GetBoolean()).GetProperty("TextKey").GetString()!;
        var choices = room.LocalMutableEvent.CurrentOptions;
        int index = Enumerable.Range(0, choices.Count).SingleOrDefault(
            i => (choices[i].TextKey == chosen || choices[i].Relic?.Id.Entry == chosen) && !choices[i].IsLocked, -1);
        if (index < 0)
            throw new InvalidDataException($"Historical ancient choice {chosen} is unavailable; actual=[{string.Join(',', choices.Select(choice => choice.TextKey))}].");
        Guidance.Add(new { kind = "ancient", expected = chosen, actual = choices[index].TextKey });
        RunManager.Instance.EventSynchronizer.ChooseLocalOption(index);
        await RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks();
        if (!room.LocalMutableEvent.IsFinished)
            throw new InvalidDataException("Historical ancient choice did not finish the native event.");
        Player player = LocalContext.GetMe(state)!;
        Guidance.Add(new { kind = "initial_loadout", hp = player.Creature.CurrentHp,
            manualHpAfterAncient = Stats.GetProperty("current_hp").GetInt32(), maxHp = player.Creature.MaxHp,
            deck = player.Deck.Cards.Select(card => card.ToSerializable()).ToArray(),
            relics = player.Relics.Select(relic => relic.ToSerializable()).ToArray() });
        if (!await EnterNextAsync()) throw new InvalidDataException(StopReason);
    }

    internal async Task<bool> EnterNextAsync()
    {
        int next = _nodeIndex + 1;
        if (next >= _nodes.Length) { StopReason = "end_of_first_act_history"; return false; }
        string type = _nodes[next].GetProperty("map_point_type").GetString()!;
        if (type is not ("monster" or "elite" or "boss"))
        {
            StopReason = $"historical_noncombat_boundary:{next}:{type}";
            return false;
        }
        RunState state = (RunState)RunManager.Instance.State!;
        MapPointType pointType = Enum.Parse<MapPointType>(type, ignoreCase: true);
        MapPoint[] candidates = state.CurrentMapPoint!.Children.Where(point => point.PointType == pointType)
            .OrderBy(point => point.coord.col).ToArray();
        if (candidates.Length == 0)
        {
            StopReason = $"historical_route_unavailable:{next}:{type}";
            return false;
        }
        _nodeIndex = next;
        _rewardChoiceIndex = 0;
        await RunManager.Instance.EnterMapCoord(candidates[0].coord);
        string expected = _nodes[next].GetProperty("rooms")[0].GetProperty("model_id").GetString()!;
        if (state.CurrentRoom is not CombatRoom room || room.ModelId.ToString() != expected)
            throw new InvalidDataException($"Historical encounter differs at node {next}: expected={expected}, actual={state.CurrentRoom?.ModelId}.");
        Guidance.Add(new { kind = "route", node = next, pointType = type, coordinate = candidates[0].coord,
            expectedEncounter = expected, actualEncounter = room.ModelId.ToString(),
            manualDamageTaken = Stats.GetProperty("damage_taken").GetInt32(),
            manualTurns = _nodes[next].GetProperty("rooms")[0].GetProperty("turns_taken").GetInt32() });
        return true;
    }

    internal async Task TakeRewardsAsync()
    {
        using IDisposable rewardSelector = CardSelectCmd.PushSelector(_selector, localOnly: false);
        Player player = LocalContext.GetMe(RunManager.Instance.State!)!;
        CombatRoom room = RunManager.Instance.State!.CurrentRoom as CombatRoom
            ?? throw new InvalidDataException("History rewards require a combat room.");
        RewardsSet rewards = await RewardsCmd.GenerateForRoomEnd(player, room);
        await Hook.BeforeCombatRewardOffered(rewards, player.RunState, room);
        // Select through the native synchronizer, with the historical choice applied only to actual offers.
        RewardsSet.testSelector = SelectRewardsAsync;
        try { await rewards.Offer(); }
        finally { RewardsSet.testSelector = null; }
        Guidance.Add(new { kind = "post_reward_deck", node = _nodeIndex,
            cards = player.Deck.Cards.Select(card => card.ToSerializable()).ToArray() });
    }

    private async Task SelectRewardsAsync(RewardsSet rewards)
    {
        foreach (Reward reward in rewards.Rewards.ToArray())
        {
            bool take = reward is GoldReward || reward is CardReward && PickedCards.Length > 0;
            if (reward is PotionReward potion)
                take = PickedModels("potion_choices").Contains((potion.Potion
                    ?? throw new InvalidDataException("Native potion reward is unpopulated.")).Id.ToString());
            if (reward is RelicReward relic)
                take = PickedModels("relic_choices").Contains((relic.Relic
                    ?? throw new InvalidDataException("Native relic reward is unpopulated.")).Id.ToString());
            if (take && !await RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward))
                throw new InvalidDataException($"Historical reward could not be taken: {reward.GetType().Name}.");
        }
        if (!RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(rewards))
            RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
    }

    private JsonElement Stats => _nodes[_nodeIndex].GetProperty("player_stats")[0];

    private string[] PickedCards => Stats.TryGetProperty("card_choices", out JsonElement choices)
        ? choices.EnumerateArray().Where(choice => choice.GetProperty("was_picked").GetBoolean())
            .Select(choice => choice.GetProperty("card").GetProperty("id").GetString()!).ToArray() : [];

    private string[] PickedModels(string name) => Stats.TryGetProperty(name, out JsonElement choices)
        ? choices.EnumerateArray().Where(choice => choice.GetProperty("was_picked").GetBoolean())
            .Select(choice => choice.GetProperty("choice").GetString()!).ToArray() : [];

    private CardRewardSelection SelectCardReward(IReadOnlyList<CardCreationResult> options)
    {
        string[] picked = PickedCards;
        if (_rewardChoiceIndex >= picked.Length) return default;
        string desired = picked[_rewardChoiceIndex];
        CardModel? selected = options.Select(option => option.Card).SingleOrDefault(card => card.Id.ToString() == desired);
        if (selected == null)
            throw new InvalidDataException($"Historical reward {desired} is unavailable; actual=[{string.Join(',', options.Select(option => option.Card.Id))}].");
        _rewardChoiceIndex++;
        Guidance.Add(new { kind = "card_reward", node = _nodeIndex, expected = desired, actual = selected.Id.ToString(),
            offered = options.Select(option => option.Card.ToSerializable()).ToArray() });
        return new CardRewardSelection { card = selected };
    }

    private CardModel[] SelectNonCombatCards(CardModel[] options, int minSelect, int maxSelect)
    {
        List<string> desired = [];
        foreach (string property in new[] { "cards_removed", "upgraded_cards" })
            if (Stats.TryGetProperty(property, out JsonElement cards))
                desired.AddRange(cards.EnumerateArray().Select(card => card.ValueKind == JsonValueKind.String
                    ? card.GetString()! : card.GetProperty("id").GetString()!));
        if (Stats.TryGetProperty("cards_transformed", out JsonElement transformed))
            desired.AddRange(transformed.EnumerateArray().Select(card =>
                card.GetProperty("original_card").GetProperty("id").GetString()!));
        CardModel[] selected = options.Where(card => desired.Contains(card.Id.ToString())).Take(maxSelect).ToArray();
        if (selected.Length < minSelect)
            throw new InvalidDataException("Historical noncombat card selection is unavailable or unrecorded.");
        return selected;
    }
}
