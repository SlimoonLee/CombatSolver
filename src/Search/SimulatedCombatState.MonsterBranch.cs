using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private MonsterBranchDemand? _pendingMonsterBranchDemand;

    /// <summary>概率分支模式挂起的怪物随机分支需求；除挂起方外只读。</summary>
    public MonsterBranchDemand? PendingMonsterBranchDemand => _pendingMonsterBranchDemand;

    public void SetPendingMonsterBranchDemand(MonsterBranchDemand demand)
    {
        if (_pendingMonsterBranchDemand != null)
            throw new InvalidOperationException("模拟状态已经存在待处理的怪物随机分支需求。");
        _pendingMonsterBranchDemand = demand;
    }

    public void ClearPendingMonsterBranchRequest()
        => _pendingMonsterBranchDemand = null;
}
