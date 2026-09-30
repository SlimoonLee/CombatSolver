using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;

namespace CombatSolver;

internal static class MonsterRandomBranchResolver
{
    public static string Pick(
        MonsterMoveStateMachine machine,
        RandomBranchState branch,
        IReadOnlyList<string> stateLog,
        Rng rng,
        Func<RandomBranchState.StateWeight, float>? baseWeight = null)
    {
        (List<(RandomBranchState.StateWeight State, float Weight)> weighted, float total) =
            WeightedCandidates(machine, branch, stateLog, baseWeight);

        // Vanilla intentionally still calls NextFloat(total). Its selection loop then returns the
        // first branch when the zero-total roll remains <= 0 after subtracting a zero weight.
        float roll = rng.NextFloat(total);
        foreach ((RandomBranchState.StateWeight state, float weight) in weighted)
        {
            roll -= weight;
            if (roll <= 0f)
                return state.stateId;
        }

        return weighted.LastOrDefault(item => item.Weight > 0f).State.stateId
            ?? throw new InvalidOperationException($"No valid state found in RandomBranchState {branch.Id}!");
    }

    /// <summary>
    /// 概率分支模式的候选枚举：与 <see cref="Pick"/> 同一套权重（重复限制 × 基础权重 × 动态权重回调），
    /// 纯函数、不消耗任何随机数流。权重未经归一化，调用方按总和归一。
    /// </summary>
    public static IReadOnlyList<(string StateId, float Weight)> Enumerate(
        MonsterMoveStateMachine machine,
        RandomBranchState branch,
        IReadOnlyList<string> stateLog,
        Func<RandomBranchState.StateWeight, float>? baseWeight = null)
    {
        (List<(RandomBranchState.StateWeight State, float Weight)> Weighted, float _) =
            WeightedCandidates(machine, branch, stateLog, baseWeight);
        List<(string StateId, float Weight)> candidates = new(Weighted.Count);
        foreach ((RandomBranchState.StateWeight State, float Weight) item in Weighted)
        {
            candidates.Add((item.State.stateId, item.Weight));
        }
        return candidates;
    }

    private static (List<(RandomBranchState.StateWeight State, float Weight)> Weighted, float Total)
        WeightedCandidates(
        MonsterMoveStateMachine machine,
        RandomBranchState branch,
        IReadOnlyList<string> stateLog,
        Func<RandomBranchState.StateWeight, float>? baseWeight)
    {
        List<(RandomBranchState.StateWeight State, float Weight)> weighted = new(branch.States.Count);
        float total = 0f;
        foreach (RandomBranchState.StateWeight state in branch.States)
        {
            float weight = RepeatWeight(machine, state, stateLog)
                * (baseWeight?.Invoke(state) ?? state.GetWeight());
            weighted.Add((state, weight));
            total += weight;
        }
        return (weighted, total);
    }

    private static float RepeatWeight(
        MonsterMoveStateMachine machine,
        RandomBranchState.StateWeight state,
        IReadOnlyList<string> stateLog)
    {
        if (state.repeatType == MoveRepeatType.UseOnlyOnce && stateLog.Contains(state.stateId))
            return 0f;

        if (state.repeatType == MoveRepeatType.CannotRepeat && stateLog.LastOrDefault() == state.stateId)
            return 0f;

        if (state.repeatType == MoveRepeatType.CanRepeatXTimes)
        {
            int consecutive = 0;
            for (int index = stateLog.Count - 1;
                 index >= 0 && stateLog[index] == state.stateId;
                 index--)
            {
                consecutive++;
            }
            if (consecutive >= state.maxTimes)
                return 0f;
        }

        if (state.cooldown > 0
            && stateLog
                .Where(id => machine.States.TryGetValue(id, out MonsterState? logged) && logged.IsMove)
                .Reverse()
                .Take(state.cooldown)
                .Contains(state.stateId))
        {
            return 0f;
        }

        return 1f;
    }
}
