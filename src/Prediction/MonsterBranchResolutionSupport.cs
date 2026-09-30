using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace CombatSolver;

/// <summary>
/// 概率分支模式下怪物随机分支的决议支持：镜像不掷骰，先按候选集挂起需求（搜索按概率分支展开），
/// 回放时从计划列表读取选中的分支并强制状态机生效（同 ApplyKnowledgeCurse 的计划内直读模式）。
/// </summary>
internal static class MonsterBranchResolutionSupport
{
    public static string SourceId(Creature owner, RandomBranchState branch)
        => SourceId(owner, branch.Id);

    public static string SourceId(Creature owner, string branchId)
        => $"MONSTER_BRANCH:{owner.CombatId ?? uint.MaxValue}:{branchId}";

    /// <summary>从计划列表读取该怪物分支的钉扎决议；无计划返回 null（调用方挂起需求）。</summary>
    public static string? ResolvePinned(
        Creature owner,
        RandomBranchState branch,
        IReadOnlyList<(string StateId, float Weight)> candidates,
        IReadOnlyList<PlanCardChoice>? plannedChoices)
    {
        string sourceId = SourceId(owner, branch);
        PlanCardChoice? choice = plannedChoices?.FirstOrDefault(candidate =>
            candidate.Effect == PlanChoiceEffect.MonsterBranch
            && string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
        if (choice == null)
            return null;
        string selectedId = choice.Cards.Count == 1 ? choice.Cards[0].CardId : string.Empty;
        if (!candidates.Any(candidate => candidate.StateId == selectedId))
            throw new InvalidOperationException(
                $"怪物分支计划 {sourceId} 选择了不在候选集中的状态 {selectedId}。");
        return selectedId;
    }

    public static void RaisePending(
        SimulatedCombatState combat,
        Creature owner,
        RandomBranchState branch,
        IReadOnlyList<(string StateId, float Weight)> candidates)
    {
        combat.SetPendingMonsterBranchDemand(new MonsterBranchDemand(
            owner,
            branch.Id,
            candidates));
    }
}

/// <summary>一条怪物随机分支的候选需求：候选状态 × 未归一化权重（归一化由消费方按总和进行）。</summary>
internal sealed record MonsterBranchDemand(
    Creature Owner,
    string BranchId,
    IReadOnlyList<(string StateId, float Weight)> Candidates);
