# 未知 RNG：随机效果与跨回合决策核查

## 结论

已将 `Torch1230/CombatSolver` 的 `origin/main@7236330` 合入 `codex/blind-rng`，合并提交为 `b027c35`。基线包含 0.47.2 和 PR #144 的后续 GC 修复。合并后修正了并行 worker 未继承 blind 首动作约束的问题。

核查对象是 OfflineSearchHarness 启用 blind 的入口。游戏内常规搜索目前仍使用上游确定性模式，在线 blind UI 尚未接入。

本次无头核查支持以下有限结论：

| 问题 | 结果 |
|---|---|
| 攻击药水、技能药水、`OROBIC_ACID`、添柴是否读取真实未来 RNG？ | 所测路径不依赖真实未来 RNG 或隐藏抽牌顺序；四个采样种子和原生完整结算对账通过 |
| 当下花能量、下回合才获益的牌能否被选中？ | `OUTMANEUVER` 正例被选中，比直接结束回合少掉 1 HP |
| 能立即击杀时是否仍盲目铺垫？ | 反例选择立即打击；先铺垫多掉 4 HP |
| 攻击/技能药水的实际选牌是否与估值闭环？ | 尚未闭环；`FirstLegal` 在可跳过选择时选 0 张，而搜索可能按最优候选估值 |
| 是否已保证整个规划器严格只使用玩家可见信息？ | 没有。完整根中保留的怪物内部状态尚未完成字段级可见性核查 |
| 是否已保证未知随机性的策略估值合理或最优？ | 没有。样本内部知道假设的未来，可能高估随机生成、抽牌和长期铺垫的价值 |

用户提到的“欧罗巴斯之酸”对应原版 `OrobicAcid` / `OROBIC_ACID`；添柴对应 `Stoke` / `STOKE`。

## 同步与修复

- 保留 `0c80939` 的独立采样与首命令聚合，以及 `61e2a98` 的原生命令 rollout、玩家存档和历史指导入口。
- 上游将串并行动作准备迁到共享 `CombatBeamSolver.ExpansionPlan.cs`，blind 约束跟随该入口整合。
- 合并后的最小并行烟测暴露出 `CreateExpansionWorker` 没有传递 `_initialActionConstraint`：复评 EndTurn 时 worker 仍可生成其他首动作。现已在 worker 构造处传递约束。
- 修复后，同一 `blind-potion.json` 的 DOP1/DOP2 首命令、发现证据、完整评估矩阵、动作统计、展开和转移一致；6 次请求累计 1800 展开、5999 转移。所有其他剪枝计数一致，五项差异为并行调度和 lane 缓存计数；DOP2 的父作业和动作回放最大并发均为 2。

上述烟测最后只有 EndTurn 一个提名候选，不能当作全部药水首动作的本轮回归。它直接覆盖了此次发生故障的首动作约束传递。

## 随机效果如何验证

生产路径先从冻结根建立样本：九条未来战斗 RNG 全部由独立 sample seed 构造，未知抽牌顺序先按卡牌语义规范排序再重排，未来意图重新预测；未来怪物创建也使用采样 run RNG。当前手牌和当前意图保留。默认不声明已知堆顶，确定药水奖励预测关闭。

测试没有仅比较某个聚合 HP：

1. 捕获根 A，然后将真实九条战斗 RNG 各前进 17 次并反转隐藏抽牌堆，捕获根 B。
2. 恢复真实状态，确认扰动确实改变源根，恢复后完整 continuation 与原状态相同。
3. 用同一个 sample seed 从 A/B 建立样本，比较完整采样根。
4. 左侧预测执行时使用原真实 RNG，右侧执行时再次改变真实 RNG，检查是否存在绕过分支 RNG 的读取。比较生成选项、完整效果 continuation（含 RNG）、评分和命令选择剥离。
5. 换四个独立 sample seed，确认确实产生不同生成结果。
6. 单独用正常确定性根做原生语义 oracle，经原生动作队列执行效果，比较完整 continuation 和 RNG。这个确定性 oracle 仅用于测试结算正确性，不参与 blind 决策。

攻击/技能药水的原生对账选择器只根据实际可见候选选第一张；它不部署采样选牌，也不代表已实现生产选牌策略。

| 效果 | 原版语义与采样来源 | 检查数 | 证据目录 |
|---|---|---:|---|
| 攻击药水 `ATTACK_POTION` | 生成三张角色攻击牌，选择 0..1 张；选中牌本回合免费；分支 `CombatCardGeneration` | 35 | `verified-attack-potion` |
| 技能药水 `SKILL_POTION` | 生成三张角色技能牌，选择 0..1 张；选中牌本回合免费；分支 `CombatCardGeneration` | 35 | `verified-skill-potion` |
| `OROBIC_ACID` | 攻击、技能、能力各生成一张，直接加入手牌且本回合免费，没有三选一页面；分支 `CombatCardGeneration` | 26 | `final-orobic-acid` |
| 添柴 `STOKE` | 消耗其余手牌，生成等量角色牌；分支 `CombatCardGeneration` | 26 | `final-stoke` |

四项均 Passed。添柴本次夹具是未升级版；升级后生成升级牌已静态核对，但未在本轮单独执行。随机效果夹具的敌人被注入为 1 HP，核查停在效果完成处，不作整场用药或添柴质量结论。

例如攻击药水的四个样本中，第一候选分别为 `POMMEL_STRIKE`、`BLUDGEON`、`CONFLAGRATION`、`CONFLAGRATION`；实际原生候选为 `TWIN_STRIKE`、`UPPERCUT`、`THRASH`。实际选第一张的对账仍通过，说明测试没有把样本生成结果交给原生执行。

## 跨回合正反例

两个例子均使用三样本、相同首动作搜索预算，所有对照都找到完整胜利；production `SolveBlind` 的首动作与对照获胜动作相同。

| 场景 | 先打 `OUTMANEUVER` | 立即打击 | 直接结束回合 | 实际选择 |
|---|---:|---:|---:|---|
| 双敌各 10 HP，手牌只有铺垫牌，抽牌堆五张打击 | 预测战损 0 | 当前没有打击 | 预测战损 1 | 铺垫 |
| 单敌 6 HP，当前 1 能量，手牌为铺垫和打击 | 预测战损 4 | 预测战损 0 | 预测战损 4 | 打击击杀 |

`OUTMANEUVER` 当前支付 1 能量，下一玩家回合实际为 5 能量。正例还经原生执行该牌和 EndTurn，对账完整两回合 continuation，包括牌堆、状态与 RNG。正反例分别通过 17/22 项检查。

这证明简单延迟能量不是仅按当下收益判断，也不会在已可击杀时机械追求铺垫。它没有覆盖所有能力、复制、延迟伤害或复杂生成链。

## 尚未保证的范围

### 1. 完整冻结根还不是可见信息白名单

`BranchMonsterAi.Capture` 保存 `monster.NextMove`、状态机 `StateLog` 和知识恶魔的私有计数；静态快照还捕获所有招式的参数、条件结果和权重。当前意图和由已发生公开历史推导的计数可以合法使用，但必须逐字段证明这种对应关系。相同显示意图不自动证明完整内部招式身份也可见。

本轮只扰动了真实 RNG 和隐藏抽牌顺序，没有改变这些内部状态。第三方隐藏状态也没有统一可见性合同。因此不能把本次通过表述为整个规划器已经严格只看玩家可见信息，也不能仅凭私有字段存在就断言已经发生作弊。

### 2. 样本内部的未来选择仍可能过于乐观

当前搜索会为每个假设未来分别优化整条路线，没有要求“相同可见情况必须采取相同选择”。例如药水三选一出现后，某个样本可依据其后尚未抽到的牌决定选哪张，现实此时还不知道后面的抽牌。

这不会透露真实未来 RNG，但其平均值可能高于现实可执行策略的价值。随机药水、添柴、抽牌和跨回合能力都可能受影响；增加样本数量不能消除这个问题。搜索未到终局时，战损和存活还只覆盖已搜索前缀。

### 3. 药水的在线选牌策略尚未接上

`BlindCombatCommand.From` 会删除样本 `Choice`、嵌套选择和下一回合选择；这是正确的信息隔离。原生命令完成后，在稳定 Play 根重新观察和采样。

但攻击/技能药水的选择发生在动作完成之前：`ObservedCardSelector("FirstLegal")` 取 `minSelect` 张，原版允许 0..1 张，因此会跳过。默认 `Stop` 遇到需决策的选择明确拒绝。现阶段尚不能在实际三张候选出现后重新采样比较“选其中一张或跳过”。改变成固定选第一张也不能使它成为合理选牌策略。

`OROBIC_ACID` 与添柴直接生成到手牌，其原生效果结束后可以在稳定根重新决策；如果效果内部的其他 Hook 引出选择，仍受同一选牌缺口限制。

### 4. 上游长期计划与 blind 复评的搜索能力不完全一致

上游 Coordinator 增加了复制、能力和免费药链的有界计划续搜。`PlanCommitment` 的部分“收益已实现”证据仅表示后续打出了目标牌，不等于已经证明完整因果收益；这些证据用于中间保路和有界地平线，最终仍比较实际路线质量。

blind 的发现阶段使用 Coordinator，首动作复评使用普通 Beam。因此提名时发现的长期计划不一定能在复评中以同样方式找到。本次简单能量正反例通过，复杂长期计划的复评一致性尚未专项验证。

候选仍来自各样本赢家提名加 EndTurn，没有穷举所有合法首动作。本次未扩大候选枚举范围。

## 可重跑入口与证据

证据根为仓库 `.local/blind-audit-20260928/`。以上六个目录各含 `blind-audit.json`，合计 161 项检查通过。`merged-smoke-fixed/blind-checks.json` 的既有 121 项采样合同也通过。旧故障目录 `merged-smoke` 不计为最终通过证据。

随机效果入口示例：

```powershell
$env:OFFLINE_HARNESS_BLIND_AUDIT = 'effects'
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --request tools/OfflineSearchHarness/fixtures/blind-attack-potion.json `
    --milestone M1 --blind-seed 902828 --beam 12 --nodes 300 `
    --budget-ms 3000 --dop 1 --out .local/blind-audit-repro/attack-potion
Remove-Item Env:OFFLINE_HARNESS_BLIND_AUDIT
```

其余效果替换为 `blind-skill-potion.json`、`blind-orobic-acid.json` 或 `blind-stoke.json`。跨回合正例用 `delayed` 和 `blind-delayed-energy.json`，反例用 `immediate` 和 `blind-delayed-energy-lethal.json`，其余预算相同。入口也可由其他平台设置同名环境变量后直接调用宿主；本轮只在 Windows 执行。

实现位于 `tools/OfflineSearchHarness/BlindAuditChecks.cs`，通过 Testing 层的 `ReplayBlindAuditForTesting` 调用正式回放。玩家原存档未修改，保存后端为 `MemorySaveStore`。

主 Mod 和最终宿主 Release 构建均为 0 警告、0 错误；Windows 结构门禁通过，`REFACTOR_BOUNDARIES_OK search_files=239`。同源码构建用于本地五文件部署。本轮没有可见游戏、Linux 或统计胜率验证。

优先后续工作是实际选择页上的采样选牌，然后建立根字段的可见性合同，再处理相同观察下共享决策及复杂长期计划复评。当前结果不能作为这几项已经完成的依据。
