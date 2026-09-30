# 无原生随机数模式：概率边界 + 短步多次决策（实施中，2026-10-01）

目标：求解器零读零写原生随机数流。操作经原版队列消耗 RNG（游戏自身行为）；求解器只观察操作后的真实状态用于后续决策。影子世界的随机效果**不掷骰**——显式携带概率（分支枚举+权重），搜索携带概率边界（剪枝记录被裁剪概率质量），承诺步长 1 条命令（部署后重观察再决策），评估视野到战斗结束（反无限防守：终局项统治 + EnemyHp 进度 3:1 偏向 + 卖血定价，全部沿用现行 `SolverWeights` 语义——参考依据见会话设计记录）。

与现有确定性/盲模式并存，`SearchPolicySnapshot.ProbabilityBranches` 开关（默认关）；离线宿主用 `OFFLINE_HARNESS_PROBABILITY_BRANCHES=1`。

## 已完成：零接触在线循环（提交 `71fe6ca`）

`--play-combat` 逐决策循环在概率模式下端到端运转：每决策 `RunSearchDetailed` 以零接触方式重捕获+搜索（含怪物分支扇出），部署聚合首命令，重观察再决策。**实证**：雾菇遭遇 8 决策全部 `native-rng-counters.json zeroContact=true`（逐决策目录独立证据），`rollout-summary.json reobserveAfterEveryCommand=true`。修复点：盲模式隔离校验在概率模式下改用无 RNG 段口径比对（模式不追踪原生随机数状态，其余字段照常检测活体变异）。

当前行为注记：blind 轨迹与候选评分重放**穿过**怪物随机分支（首个需求按最高权重候选钉扎续行，最可能怪物响应下的确定性延续；重放通过 `ReplayBlindCommandResolved` 保留计划列表——`BlindCombatCommand.From` 会剥离 TurnStartChoices，这是首次实现时踩的坑）；内层搜索树照常按概率分支展开。批次 2 剩余：把权重真正接入聚合（Σ权重×分支值的期望评估，替代最高权重单路径近似）。

## 已完成：零原生 RNG 接触的根捕获（提交 `6c70f8a`）

模式开启时根捕获**零读写** `RunState.Rng`：

- `SimulatedCombatState` 构造跳过 `ToSerializable`，影子基础九流由战斗局面派生种子构成（`DeriveNoRngSeed`：回合数/玩家数 SplitMix）；下游 `CreatePredictionRngSet`/monster.RunRng/ForkForSample 全部无感工作；
- `IntentForecaster.Build` 概率模式不克隆原生 MonsterAi——随机分支按最高权重确定化（近似预报，入 ApproximationDetails），条件/状态分支的局部 `Rng(0)` 仅为原版签名；
- `ContinuationStamp.CaptureLive/CapturePredicted` 增 `includeRng` 参数——捕获竞态检查在无 RNG 段的文本上比对；
- `CombatRootSnapshot.Capture(..., probabilityBranches)` 线程化；`SolverSettingsSnapshot.ProbabilityBranches` + 两个运行时捕获调用点接线；
- **实证**：`native-rng-counters.json` 记录搜索前后九条原生计数器逐位相同（`zeroContact=true`，雾菇冒烟 `9,0,0,0,0,0,0,0,1` 前后一致）——这是"完全不读/写"的直接证据。

## 已完成：批次 1 片 1（提交 `19bc6a0`）

怪物随机分支概率化，端到端复用知识恶魔诅咒通道模式：

- `MonsterRandomBranchResolver.Enumerate`（纯函数：候选×权重，与 Pick 共享 RepeatWeight×基础权重×动态回调；Pick 语义未变）；
- `MonsterBranchResolutionSupport`（SourceId=`MONSTER_BRANCH:{combatId}:{branchId}`；ResolvePinned 从计划列表读钉扎；RaisePending 挂起需求）；
- `SimulatedCombatState.MonsterBranch.cs`（PendingMonsterBranchDemand 挂起通道；HasPendingChoice/Fork/两处执行捕获守卫集成）；
- `BranchMonsterAi.AdvanceWithBranchPlan`（概率模式推进：钉扎→强制生效；无钉扎→挂起需求+返回 null；全程不消耗 MonsterAi 流）；
- 线程化 `PrepareMonsterMovesForNextRound(..., plannedChoices, probabilityBranches)`；`AdvanceRound` 概率模式传 `turnStartChoices` 并在挂起时返回 PendingChoice；
- 分支层 `BuildPendingChoiceReplayLayer` 新案例（每候选一条分支，归一化权重进 token.Title 供批次 2 聚合）；预算种子（语义宽度=候选数）；5 处 cursor 过滤点加 MonsterBranch；
- 策略位 `ProbabilityBranches` + 求解器字段 + harness 环境变量覆盖。

**验证**：概率模式 ON 扇出生效（雾菇遭遇 703 转移 vs OFF 560，双方均找到胜利线）；确定性回归绿（雾菇差分 + 扩展批次 A 7/7）。冒烟夹具 `prob-branch-smoke.json`。

## 待实施：批次 1 片 2 —— mid-execution 随机结果原语（设计已定，未编码）

逐流迁移（随机目标/生成族/球/费用/药水/选牌/Niche）的难点：这些消费点在**卡牌执行事务内部**（OnPlay 效果深处），不像怪物分支在回合边界。设计结论：

1. **原语**：引擎新增通用 `RandomOutcomeSupport.Resolve(simulator, combat, key, candidates×weights, plannedResolutions)`：
   - 概率模式：先查已计划决议（nested/turn-start 计划列表中的 `PlanChoiceEffect.RandomOutcome` 项，SourceId=key）；无则 `SetPendingRandomOutcome`（或复用 TurnStartChoiceRequest+新 Effect 值）→ 执行挂起 → PendingChoice 边界 → 分支层每候选一条分支 → 重放时强制生效。
   - 确定性模式：照旧掷流（不迁移的流走影子自造种子，批次 3 前影子流仍在）。
2. **挂起/恢复（帧机制研究结论，2026-10-01）**：`ICombatPredictionExecutionFrame` 的模式是 `Resume(simulator)` **重新进入同一点的决议函数**——`CardSelectionExecutionFrame.Resume` 重调 `ResolveActionCardChoice`（重放携带计划选择时游标命中并 Apply），`TurnSelectionExecutionFrame.Resume` 重调 `ResolveCapturedChoice`。因此随机结果的帧 = `RandomOutcomeExecutionFrame(Key, 候选集)`，`Resume` 重调该消费点的决议 helper（带计划决议则确定性应用；再无决议则再挂起）。**每个效果需要自己的帧类型与阶段拆分**（如弹跳药瓶的"第 i 击"循环索引）——这正是历史"嵌套执行检查点"批次的工作形态，逐效果小批推进。
3. **逐流要点**：
   - 随机目标（CombatTargets，~17 处）：候选=HittableEnemies（1–4 个）；多段随机目标（弹跳药瓶）逐击分支会爆炸——候选为"目标序列"或聚合为期望伤害（概率边界的合法形式：全质量聚合）。
   - 生成族（CombatCardGeneration，~31 处但汇聚于 `CombatCardGenerationExtensions` 6 个内部消耗点）：候选=根池快照枚举；发现族生成后还有玩家选择（已分支化），只需分支"池抽取"。
   - 球（3 类）/费用（0–3）/孵化（[min,max]）/选牌（可见堆枚举）：小候选集直接枚举。
   - 药水池（~30）：大候选集——ε 剪枝的主要场景。
4. **洗牌（片 3）**：不可枚举——显式边界（洗后牌序未知启发式），由每命令重观察吸收；`BuildProjectedShuffleOrder` 评估投影退役。

## 已完成：设置开关与运行时集成（提交 42fbf88/f08285d）\n\n部署会话过滤 MonsterBranch 决议（预测内部：原生自行掷骰，下一次搜索重观察真实分支）——两个部署选择装配点（动作附带与结束回合专用）均已处理，构建+门禁绿。\n\n## qstats A/B：战损与获胜平衡实证（2026-10-01）\n\n双敌 60/60 长战斗（选择密集+简单候选同场）三 seed，基线（确定性）对照模式（概率分支）。结果：**模式三 seed 一致选中进攻性成长线（DISCOVERY）**，平均预测战损 2.89 vs 基线 4.00，且方差更小（2.00–3.33 vs 3.33–5.00）；基线在 902828 选中了防御（正是短视防守误区的形态），模式未出现。反无限防守论证链在概率分支下成立：评估视野到终局 + EnemyHp 进度计价使进攻成长线胜出。证据 `.local/nonrng-ab/`（未提交）。口径：预测战损非实机胜率；单夹具三 seed，不外推。\n\n## 验证检查点（2026-10-01）\n\n全量回归：policy 45 场景、审计套件（discovery 26/attack-potion 35/ai-counter 6/initial-relic 6/delayed 17/nested 26）、合同 121 项全 Passed；门禁 REFACTOR_BOUNDARIES_OK；模式在线循环 8 决策零接触证据；Mod 已同源码部署（哈希校验）。\n\n## 后续批次

- 批次 2：beam 接入概率权重（token.Title 解析或层携带）；聚合 = Σ(概率×终局值)，排序 = 胜利概率（含裁剪质量下界）→ 期望战损；ε/分支上限进策略。
- 批次 3：零原生 RNG 接触（根捕获去 ToSerializable、stamp 去 `;R=`、状态键/缓存键去 RNG、IntentForecaster 去 live 克隆改权重枚举、NativeChoiceRuntime:290 计数日志省略）+ SolverController 逐命令在线循环 + UI 决策摘要（= 工作单 4.5）。
- 批次 4：零接触合同（九流计数不变/无 `;R=`/无 live 读）、qstats A/B（防守倾向 vs 输出倾向场景，验证无防御死锁）、既有模式回归、文档、部署。

## 边界与不做

诊断/取证（bug 报告 RNG 序列化）保留不参与决策；旧模式退役另行决策；可见 Steam 验收/版本提升/发包不做。
