# 未知 RNG 随机机制扩展交接（2026-09-28）

> 2026-09-29 已按本文完成三批扩展验收（药水通道与直接生成到手牌、随机费用/取牌/药水/球、机制族扫尾与弹跳药瓶镜像），覆盖矩阵与剩余范围见[机制族扫尾验收](blind-rng-family-completion-20260929.md)及其同日两份报告；第 9 节架构项仍需单独安排。

## 1. 接手入口与基线

目标是继续扩展带随机池的卡牌、药水、Power、遗物及其他战斗机制：保留原版生成分布和结算顺序，在结果揭晓后重新决策，并评价能实际执行的策略收益。

本机工作目录为 `D:\FORSTS2\.reference\CombatSolver-latest`，分支 `codex/blind-rng`。行为基线提交为 `a4b58d0`；上游已同步到 `origin/main@7236330`，合并提交为 `b027c35`。当前参考游戏版本为 `0.111.0`，上游 Mod 为 `0.47.2`。本指南整理既有工作，本次仅改文档，没有新增行为测试结果。

先阅读 [AGENTS.md](../../AGENTS.md)、[架构地图](../ARCHITECTURE.md)、[战斗语义工作规范](../../.agents/skills/combat-semantic-change/SKILL.md) 和 [离线宿主指南](../OFFLINE_SEARCH_HARNESS.md)。涉及评分、剪枝和预算时，再使用仓库的 `search-performance-optimization` 规范。

开发继续复用 CombatSolver 的 C# 语义后端。OfflineSearchHarness 在普通 .NET 9 进程中加载真实 `sts2.dll`，可以原生推进战斗；需要更完整的差分断言时使用无人测试。Fast/Instant 只影响动画与等待，未知 RNG 边界由决策层实现。

## 2. 已完成什么，以及能证明什么

| 层面 | 当前实现 | 证明范围或限制 |
|---|---|---|
| 未知未来采样 | 替换九条战斗 RNG、未知抽牌顺序、未来意图 forecast、后续怪物创建使用的 run RNG | 已有采样/Fork 合同；当前默认知道抽牌堆卡牌多重集，不知道顺序 |
| 首动作覆盖 | 发现阶段提名之外，补齐当前枚举入口准入的全部合法首命令 | 全候选乘全外层样本；准入政策和未支持语义仍限制覆盖面 |
| 首动作估值 | 外层样本逐步执行；动作后冻结观察，以独立内层样本决定下一命令 | 已替换直接采用样本全知完整路线的旧复评方式 |
| 药水选择 | 复用 Prepare/Complete；已枚举的可见选择与合法跳过使用同一组内层样本 | 九种原版手动选牌药水有绑定；选择枚举受分支上限约束，本轮原生政策验收覆盖攻击、技能、弃牌重抽三种 |
| 普通卡牌与回合内选择 | 使用已有可见牌优先级 | 可观察性与选择质量是不同验收项；尚未通用接入药水式逐选项续搜评分 |
| 跨回合 | EndTurn 完整推进，稳定观察后重新决策 | 已有延迟能量正例和立即击杀反例；短预算不能证明复杂长期策略充分 |
| 原生执行 | 无头宿主发起一个命令，等待动作完成，再观察/重解 | 未知来源或复杂嵌套选择明确失败；Mod 在线 UI 仍用确定性求解器 |
| 信息边界 | 决策前独立重采样，并对外层隐藏未来做扰动检查 | 怪物私有 AI、第三方隐藏字段的全局可见字段白名单尚未完成 |

必须分别记录“已有语义实现”“blind 估值已覆盖”“原生执行已验收”。在源码中找到一个 Mirror，不能直接把该机制登记为三项全部完成。

## 3. 制作经验：池收益怎样评，未来怎样隔离

卡池收益取决于当前战斗：生成牌的费用、当前能量、手牌空间、敌人威胁、消耗掉的资源以及后续回合都会改变价值。因此通过原版分布采样、完整结算、后续可执行动作评价池收益。静态牌优先级可以提供提案，但不能代替随机动作在不同结果下的轨迹收益。

当前算法的两个样本层各有职责：

```text
固定某个当前首命令
  -> 外层样本结算随机结果
  -> 冻结已经揭晓的新观察
  -> 用固定 DecisionSeed 派生独立内层样本，提名下一命令
  -> 去掉提案里的选择与后续路线，在外层执行一个命令
  -> 重复到终局或预算边界
  -> 汇总各外层样本实际执行的轨迹
```

外层样本承担“这次实际发生了什么”，内层样本承担“当前观察下可能发生什么”。内层 seed、未知牌序、缓存键不能由外层未揭晓的未来决定。抽到不同牌后，手牌已成为可见信息，可以走不同动作；抽牌前相同观察不能因外层下一张牌不同而提前安排不同命令。

内层仍使用确定化 Coordinator 搜索产生提案，存在乐观性。这一轮修正的是直接把每个外层样本的全知最佳后续当作首动作价值的问题。进一步消除内层提案和选牌评分的乐观性，需要单独改进策略评估。

两个已验证例子说明验收要有对照：

- 攻击药水的外层平均实际扣血为 1.33，直接 EndTurn 为 4；默认 Smart 仍选择省药，因为保存药水的战略成本超过本场减少扣血的收益。要同时展示战损收益、总评分和用药政策。
- `BLUDGEON` 可以当场零损击杀；先 `STOKE` 消耗这张斩杀牌后，平均扣血为 4。随机池的机会成本必须在完整结算中体现。

攻击药水原生候选中 `PILLAGE` 的内层提案预计零损、其余候选和跳过预计损失 4，属于选牌提案评分；它不能当作药水动作的外层平均战损。具体证据见 [随机池与逐步观察策略](blind-pool-policy-20260928.md)。

## 4. 代码职责与查找顺序

| 要处理的事 | 入口 |
|---|---|
| 捕获稳定根、模拟状态重观察、采样隐藏未来 | [CombatRootSnapshot.cs](../../src/Runtime/CombatRootSnapshot.cs)：Capture、ObservePrediction、ForkForSample、IsPredictionObservation |
| 原版卡牌生成与过滤 | [CardGenerationCardMirrors.cs](../../src/Engine/InCombat/Mirrors/Cards/OnPlay/CardGenerationCardMirrors.cs) |
| 原版药水生成与过滤 | [CardGenerationPotionMirrors.cs](../../src/Engine/InCombat/Mirrors/Potions/OnUse/CardGenerationPotionMirrors.cs) |
| 通用生成、加牌与原版抽样原语 | [CombatCardGenerationExtensions.cs](../../src/Engine/InCombat/Extensions/CombatCardGenerationExtensions.cs) |
| 根生成池捕获、原生缓存资格与回退 | [RootCombatCardGenerationPoolSnapshot.cs](../../src/Search/RootCombatCardGenerationPoolSnapshot.cs) |
| 回合开始持续生成 | [TurnStartPowerSupport.cs](../../src/Prediction/TurnStartPowerSupport.cs)、[TurnStartRelicSupport.cs](../../src/Prediction/TurnStartRelicSupport.cs) |
| 卡牌/药水选择定义、逐实例令牌与应用 | [CardChoiceSupport.cs](../../src/Search/CardChoiceSupport.cs)、[PotionChoiceSupport.cs](../../src/Search/PotionChoiceSupport.cs) |
| 药水消费、选择前缀及后置钩子 | [PotionExecutionSupport.cs](../../src/Prediction/PotionExecutionSupport.cs)、[PotionChoiceContinuation.cs](../../src/Prediction/PotionChoiceContinuation.cs) |
| 首命令补齐、矩阵、聚合和输出契约 | [BlindCombatSearch.cs](../../src/Search/BlindCombatSearch.cs) |
| 外层轨迹、独立后续提案、药水选项估值 | [BlindCombatPolicy.cs](../../src/Search/BlindCombatPolicy.cs) |
| 单命令执行、稳定观察及药水前缀桥接 | [CombatBeamSolver.Blind.cs](../../src/Search/CombatBeamSolver.Blind.cs) |
| 原生已揭晓候选接管、实例匹配、取消排空 | [BlindNativeChoices.cs](../../tools/OfflineSearchHarness/BlindNativeChoices.cs) |
| 采样隔离合同与专项检查 | [BlindSearchContracts.cs](../../src/Testing/BlindSearchContracts.cs)、[BlindAuditChecks.cs](../../tools/OfflineSearchHarness/BlindAuditChecks.cs)、[BlindPolicyChecks.cs](../../tools/OfflineSearchHarness/BlindPolicyChecks.cs) |

先定向读取当前游戏版本的原版方法，再沿 Mirror、support、选择应用、回合生命周期和搜索入口追踪。相同效果保持唯一权威结算点；在 Search 添加第二份效果代码会带来双结算风险。

## 5. 按机制族盘点，避免逐名称重复设计

以下是工作分组和源码定位示例，覆盖状态仍须逐来源验收。完整盘点还要搜索 Hook、自动出牌、随机目标和第三方登记点。

| 家族 | 示例或查找线索 | 扩展重点 |
|---|---|---|
| 直接随机生成到手牌 | InfernalBlade、Distraction、WhiteNoise、BundleOfJoy、CosmicConcoction；已测 STOKE、OROBIC_ACID | 原版池、数量、费用/升级、手牌溢出、生成钩子；生成后重观察 |
| 先生成候选，再选一项 | Discovery、Abundance、Quasar、Splash；Attack/Skill/Power/ColorlessPotion | 生成一次；按真实 MinCount/MaxCount 比较合法选项；卡牌端目前仍有优先级策略边界 |
| 随机生成后放入抽牌堆 | Metamorphosis 等 | 放入位置的随机性、整场免费、未来抽取、跨回合收益与牌序隐藏 |
| 丢弃/消耗后抽牌或替换 | GamblersBrew、STOKE 及相关选择来源 | 选择子集、空选择、资源机会成本、洗牌及其 RNG/Hook |
| 每回合或每次触发生成 | CallOfTheVoidPower、CreativeAiPower、HelloWorldPower、SpectrumShiftPower；遗物 Hook | 每次原版抽样的次数/顺序、持续时间、叠加与移除、下一回合实际收益 |
| 从已知牌堆取牌或改费用 | 其他已有药水选择定义 | 玩家实际可见的候选、逐实例差异、费用持续时间；是否含随机性需逐来源确认 |
| 非卡牌随机机制 | 随机目标、随机数值、球、召唤、敌方后续行动等 | 随机结果是否已经揭晓、属于哪条流、分支状态和原生结算一致性 |
| 嵌套或重复触发 | 自动出牌、重复出牌、生成牌再选牌、选择中再次抽牌 | 事务稳定边界、来源身份、选择前缀续执行；超出现有 blind 原生支持时作为单独架构任务 |

建议先验收 PowerPotion/ColorlessPotion 的已有通道，再做直接入手家族的代表来源，随后扩展卡牌候选的续搜评分与持续生成。每批选少量共享机制的来源，保留每个来源的覆盖记录。

## 6. 每批可重复的工作流程

1. 建立来源清单：模型 ID、原版方法、触发阶段、池/候选定义、是否需要玩家选择、现有语义路径、blind 路径、原生验收状态。找到已有实现后先验证缺口在哪一层。
2. 核对生成分布与时序：角色/无色/稀有度/解锁/多人约束/排除自身等过滤；有放回、distinct、权重；使用的 RNG 流、调用次数和顺序；先消耗、先伤害还是先生成。
3. 核对产物与副作用：升级、附魔和临时状态、本回合免费或整场免费、落入牌堆及随机位置、满手处理、历史、生成/抽牌/消耗/用药钩子。复用现有生成 helper 和 Prepare/Complete。
4. 接通观察与估值：当前准入首命令进入完整外层矩阵；动作完成后的观察决定下一步。选择来源只消费已经生成的候选，已枚举的每项合法选择共用内层 seeds；按真实边界包含跳过或子集选择，记录枚举上限。
5. 接通原生动作：按实际候选的逐实例语义令牌匹配；等待具体 GameAction.CompletionTask。第二层选择和未知来源有明确支持定义后再接入，失败应保留可定位上下文。
6. 构造最小夹具：断言注入后的真实状态；做该来源的 actual/simulated 差分、隐藏未来独立性和矩阵检查。涉及跨回合或机会成本时加一正一反对照；新增共享状态时检查 Fork、指纹、续用与兄弟隔离。
7. 解释结果：列首动作/选项、样本数、完整或截断、实际已执行扣血、总评分、相对差距、消耗资源、停止原因。以“少掉多少血，但花掉什么”为第一层解释。
8. 提交与交付：同步受影响文档和覆盖记录，显式暂存本批文件，提交当前任务分支；保留原始产物于 `.local/`。验证范围遵循 AGENTS.md 的 L0/L1/L2/L3，已有成功证据在输入未变时复用。

普通 Fork 仍拒绝不稳定事务；新增分支值须明确所有者及重映射，写牌使用 MutablePreview。新增池缓存须保留原生资格门和自定义池回退，不能因为某个池“看起来一样”就共享可变状态。

## 7. 验收要求与已踩过的坑

| 验收目标 | 必须取得的证据 |
|---|---|
| 夹具确实生效 | 实际手牌/能量/Power/药水槽/候选边界断言；字段使用 `initialPlayerEnergy`，旧 `playerEnergy` 曾被忽略 |
| 精确结算 | 同起点、同原生随机状态、同已选动作的 actual/simulated 完整状态与相关 RNG 对账；包含费用、逐实例有序牌堆、历史及 Hook |
| 决策不预知外层未来 | 固定当前观察和内层 seeds，改变外层 RNG/未知牌序；分别新建两个策略实例，比较首命令和完整选项评分向量；先证明隐藏未来确实不同 |
| 结果揭晓后会重新决策 | 记录生成/抽牌后的观察与下一命令；允许不同已揭晓结果产生不同命令 |
| 随机池动作没有被漏掉 | 候选数乘外层样本数等于矩阵行数；每行实际执行其约束首命令，包含准入但发现阶段零票的动作 |
| 原生选择可执行 | 从真实候选选合法数量、包含合法跳过、实例令牌匹配；对账完整 continuation/RNG，原生根在搜索前后不变 |
| 收益合理 | 随机池有用正例与浪费资源/已有斩杀反例；延迟收益有下一回合兑现正例和当前击杀反例 |
| 预算与失败诚实报告 | ObservedActions、Decisions、StopReason、ValueScope；超时、未支持、截断单列 |

精确随机状态仅用于语义对拍的真值端。决策独立性测试另外验证策略不使用该确切未来；“对拍时能看到 RNG”和“规划器允许使用 RNG”不能混为一项。

现有专项入口并非通用测试器：

- `BlindAuditChecks` 的 `effects` 只接受 STOKE、ATTACK_POTION、SKILL_POTION、OROBIC_ACID。
- `BlindPolicyChecks` 没有药水时断言 BLUDGEON/STOKE；生成药水分支断言三张候选加跳过，且原生测试要求选到有用牌。新机制、允许最优跳过的反例、多选或非三选一都要按真实规格改断言。
- 新夹具不能仅复制这些环境开关就称为已验收。按原生边界适配检查，公共 helper 改动要覆盖受影响调用类型族。
- 同一个策略缓存里重复查询可能掩盖隐藏输入泄露；独立策略实例是当前已采用的验证方式。
- 生成请求/spec 要随前缀保存；恢复时重新生成候选会再消耗 RNG。候选历史、分支 RNG、消耗槽位和完成钩子的所有权要分别核对。
- 原生选择 worker 超时/取消后先取消并排空，再释放前缀；保留 Task 或共享可变候选会破坏隔离。

选择枚举使用 `MaxPileChoiceBranchesPerAction` / `MaxHandChoiceBranchesPerAction` 上限。三选一夹具可以核对全部选项加跳过；大手牌的多选子集可能被截断，必须记录候选组合数、实际枚举数和上限，不能称为所有合法组合都已评价。

分数口径固定如下：

| ValueScope / 字段 | 可作什么结论 |
|---|---|
| `completed_policy_trajectory` | 该外层样本已完成轨迹，可报告整场实际执行战损 |
| `observed_prefix_with_state_heuristic` | 前缀已执行战损加叶子启发式；不能当作完整战损 |
| `independent_determinized_choice_proposal` | 独立内层的选择提案评分，仍有乐观性；不能替代外层收益 |
| `SurvivalRate` / `VictoryRate` | 已执行部分存活比例 / 预算内完成胜利比例；不能直接称为统计整场胜率 |

`--budget-ms` 是软预算。内层至少 25 ms/样本，选牌还逐选项续搜；同步结算、评分和收尾可能超时。相同样本数和配置不保证相同耗时或深度，记录 `blindPolicy.TotalExpandedNodes`、`TotalTransitionCount`、`Elapsed` 及截断情况。质量与吞吐结论分别取证。

## 8. 现有证据与重跑入口

最近行为证据在本仓库 `.local/blind-policy-20260928/`，共七个场景、94 项检查：

| 目录 | 检查数 | 重点 |
|---|---:|---|
| attack-final | 13 | 完整矩阵、隐藏未来独立性、原生攻击药水选牌与 continuation/RNG |
| skill-final | 14 | 实际零能量、免费牌、原生技能药水选牌与完整对账 |
| acid-final | 9 | 全样本池收益与生成后重观察；一条 time_limit，仅前缀 |
| stoke-final | 9 | 保留确定斩杀、消耗手牌的机会成本 |
| gambler-final | 10 | 弃牌子集、实际选择两张 WOUND、原生完整对账 |
| delayed-final | 17 | OUTMANEUVER 的下一回合 5 能量及完整两回合状态 |
| immediate-final | 22 | 当场击杀优先于延迟铺垫 |

对应报告为 [随机池策略](blind-pool-policy-20260928.md)、[专项审计](blind-rng-audit-20260928.md)。行为基线已通过 Mod/宿主 Release 构建（0 警告/0 错误）、Windows 结构门禁（search_files=241）及普通 Coordinator 烟测；Linux 门禁、完整通关和 Mod 在线 blind 未验收。

[玩家存档指导的九场原生实测](blind-native-rollout-20260928.md) 使用旧算法及 FirstLegal 选牌基线，保留为宿主能力证据；不能当作最新策略的质量证明。历史 `.run` 缺少完整坐标、解锁和逐张操作，属于路线/选牌指导，不能称为精确手操回放。

需要因行为改动重新构建时，从仓库目录执行：

```powershell
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
```

以下只重跑既有攻击药水夹具，不是任意新机制的通用验收命令。先确认没有启用其他专项环境开关：

```powershell
$previousPolicyChecks = $env:OFFLINE_HARNESS_BLIND_POLICY_CHECKS
try {
    $env:OFFLINE_HARNESS_BLIND_POLICY_CHECKS = '1'
    dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
        --request tools/OfflineSearchHarness/fixtures/blind-pool-attack.json `
        --milestone M1 --blind-seed 902828 --beam 12 --nodes 300 --budget-ms 3000 --dop 1 `
        --workspace .local/blind-pool-repro/host-attack --out .local/blind-pool-repro/attack
    if ($LASTEXITCODE -ne 0) { throw 'Blind policy checks failed.' }
} finally {
    $env:OFFLINE_HARNESS_BLIND_POLICY_CHECKS = $previousPolicyChecks
}
```

`--seed` 构造原生世界，`--blind-seed` 构造未知未来假设。原生 rollout 使用 `--play-combat`、M2、`--native-choice-policy Blind`、`--blind-known-top 0`，具体存档/历史参数见宿主指南。`--request`、`--run-save`、`--combat-replay`、`--history-run` 互斥。一次只启用一个专项检查入口，M1 专项和正常 M2 rollout 单独运行。

玩家 profile3 原档保持只读；解析复用游戏 SerializableRun/RunState.FromSerializable，保存后端为 MemorySaveStore。当前 schema、未知非空 Mod 扩展、mcr 模型编号映射按现有恢复门禁处理。原始存档、完整日志和构建产物留在忽略目录。

普通 .NET 宿主不创建 Godot 游戏实例；若改用 `run-unattended-test`，实例放仓库 `.local/headless-instances/`，最后带 `-CleanupInstanceOnExit` / `--cleanup-instance-on-exit`。单个快速请求总超时默认不超过 120 秒；超时后缩小场景，已有通过项不反复重跑。

## 9. 需要单独安排的工作

| 缺口 | 后续任务应回答的问题 |
|---|---|
| 全局可见字段白名单 | 哪些怪物私有状态可由公开历史推出？哪些第三方字段必须丢弃或重新采样？观察 DTO 如何限制信息？ |
| 内层确定化提案乐观性 | 抽牌前的资源安排、选牌提案能否再用可执行策略评估？质量改善与计算成本如何比较？ |
| 卡牌/Power/遗物通用选择评分 | 怎样复用现有选择续执行，对每个已揭晓候选做同样的独立内层评估，并保留真实 MinCount/MaxCount？ |
| 原生复杂嵌套选择 | 每层来源、纯数据帧、稳定边界、取消排空和逐实例候选如何传递？ |
| 硬预算与截断偏差 | 动作复杂度不同会不会使某些候选更早截断？评分与预算分配如何处理？ |
| Mod 在线 blind UI | 真实主线程根、搜索会话、页面选择和执行后重观察如何接入？ |
| 质量统计 | 在可比原生起点和多 seed 下，平均/尾部战损及决策时间是否改善？ |

这些项目不因更多来源通过最小夹具就自动完成。普通扩展遇到其中的依赖，应记录边界并单独提出有验证范围的实现任务。

本地部署尚有遗留阻塞：上一轮复制 `mods/CombatSolver/CombatSolver.dll` 失败，原因是玩家游戏进程占用 DLL，不能把它写成已部署。后续可部署时覆盖 DLL、manifest、Windows MemoryCleaner.exe、LICENSE、THIRD_PARTY_NOTICES.md，并做一次来源/目标哈希对照。保留玩家游戏会话；同一占用条件未变化时不重复复制。本次文档交接复用既有构建证据，不重建或重试部署。

## 10. 每批交付模板

```text
批次 / 行为基线 / 游戏版本：
来源 ID 与机制族：
原版语义依据（方法、过滤、RNG 流/抽样顺序、钩子）：
缺口及修改所在职责层：
已有语义 / blind 估值 / 原生执行：分别标已验证、已有未验收或未支持
观察边界（揭晓前未知什么、揭晓后可用什么）：
选择规格（来源、候选、MinCount/MaxCount、跳过/子集、枚举数/上限）：
实际夹具注入断言：
actual/simulated 状态、历史、费用、RNG 差分：
独立策略的隐藏未来扰动结果：
全候选乘全样本矩阵与约束首命令：
正例、机会成本/即时斩杀反例、必要的跨回合边界：
首动作/选项结果、对照差距及可读解释：
ValueScope / StopReason / 预算 / 节点 / 转移 / 耗时：
产物目录 / 提交 / 文档更新 / 部署状态：
未验证范围和下一项明确工作：
```

## 11. 可直接交给接手者的任务说明

> 在 `D:\FORSTS2\.reference\CombatSolver-latest` 的 `codex/blind-rng` 上继续未知 RNG 随机机制扩展。行为基线为 `a4b58d0`，已同步上游 `7236330`。先读 AGENTS.md、本文、架构地图、combat-semantic-change 规范和离线宿主指南。先盘点并选择少量共享机制的来源，优先 PowerPotion/ColorlessPotion 已有通道及直接生成到手牌家族。逐来源区分已有语义、blind 估值、原生验收，复用原版生成过滤和抽样原语。补全候选矩阵、生成后重观察、实际候选选择及最小正反例；隐藏未来独立性使用两个新建策略实例，原生与预测比较完整状态/历史/RNG。现有专项检查含固定场景断言，新夹具要同步适配。按模板报告评分差距、资源成本和截断，更新文档并显式提交本批文件。全局可见字段白名单、内层乐观性、通用选择评分、复杂嵌套选择和在线 UI 各有未完成项，声明实际处理范围。玩家原档只读、保存使用 MemorySaveStore；使用无头测试，已有成功证据输入未变时复用。保留玩家游戏会话和其他改动，不自行提升版本、发包、推送或委派新任务。部署阻塞与后续责任见第 9 节。
