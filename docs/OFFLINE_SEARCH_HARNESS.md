# 离线搜索宿主

`tools/OfflineSearchHarness/` 是一个普通的 .NET 9 控制台程序：它加载 `sts2.dll` 但**不启动 Godot
引擎**，用游戏自己的核心层建出一场战斗、推进到玩家第一回合，再在同一个进程里调
`CombatRootSnapshot.Capture` 与 `CombatSearchCoordinator.Solve`（或单次 `CombatBeamSolver`）跑一次
固定预算搜索，把指标、选中路线和搜索策略写成 JSON。未知 RNG 模式还可以通过游戏自己的
动作队列执行首命令，在实际结算后重新观察、重新采样，直到战斗结束或达到停止预算。

它解决的是批量测量的成本问题：游戏内无人测试每一根都要起一次 Godot 进程，宿主不用，几十根到上百根
的宽度/预算/保留规则对照可以在一台机器上连着跑。**它不是 `docs/HEADLESS_TESTING.md` 的替代**——
完整 actual/simulated 正确性验收仍然走无人测试；宿主提供搜索测量和原生命令 rollout 的行为证据。

## 构建

```
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
```

路径解析与 `CombatSolver.csproj` 同一套：先 `Import` 仓库根的 `local.props`，再按操作系统给
`SteamRoot` / `Sts2Dir` / `Sts2DataDir` / `RitsuWorkshopRoot` / `RitsuLibDir` 默认值；RitsuLib 优先走
`RitsuLib.References.props`，没有它才回退 `RitsuLibDir`。模组 DLL 默认取
`.godot/mono/temp/bin/Release/CombatSolver.dll`，可以用 `-p:CombatSolverDll=<path>` 覆盖；运行期还可以
用环境变量 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 换一份（批量对照不同 DLL 时用）。

宿主对 `sts2` 沿用模组自己的公开化（`Publicize`），对模组本体不做公开化——`CombatSolver.csproj` 里加了
`<InternalsVisibleTo Include="OfflineSearchHarness" />`，宿主只经 `internal` 入口进来。

## 单根用法

```
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
    --request <无人测试请求.json> --label R1 --out <产物目录> \
    --profile VeryHigh --beam 135 --nodes 100000 --dop 1 --budget-ms 600000
```

常用选项：

| 选项 | 含义 |
|---|---|
| `--request <path>` | 无人测试请求 JSON（含 `generatedScenarioPath`），走生成场景开局 |
| `--run-save <path>` | 当前 schema 的原生战前 `.save`，规范化恢复核对后进入已保存坐标 |
| `--combat-replay <path>` | `.mcr` 的原生战前存档；模型编号表 hash 必须一致 |
| `--history-run <path>` | 单人 `.run` 指导的新局前缀，全解锁、按历史节点类型取最低列相邻坐标 |
| `--play-combat` | 未知 RNG 首命令原生执行；每次动作稳定后重新求解 |
| `--history-combats <n>` | 历史指导前缀最多 1..4 场，默认 1；遇非战斗边界停止 |
| `--combat-decisions/--combat-turns` | 每场决策/回合上限，默认 200/20 |
| `--combat-timeout-seconds <n>` | rollout 请求总时限，1..120 秒，默认 120；建局、战斗和领奖共用余量 |
| `--native-choice-policy <p>` | `Blind`（默认，观察候选后决策）、`Stop` 或 `FirstLegal`（选最少张数基线） |
| `--character/--encounter/--seed/--ascension/--act-index` | 不给 `--request` 时直接指定一个新跑局 |
| `--profile` | `Low\|Medium\|High\|VeryHigh\|Custom`（默认 `Custom`），预设值问模组自己要 |
| `--beam/--nodes/--card-branches/--pile-branches/--hand-branches` | 覆盖预设的宽度、节点上限与分支上限 |
| `--dop <int>` | 搜索并行度（默认 1） |
| `--budget-ms <int>` | 搜索软时间预算毫秒（默认 600000） |
| `--potion-policy <p>` | 药水政策（默认 `Smart`） |
| `--search-mode <m>` | `Evaluate`（默认）或 `Coordinator` |
| `--blind-samples <n>` | 未知 RNG 样本数（1..64）；启用后必须使用 `Coordinator`，只输出聚合首动作 |
| `--blind-seed <ulong>` | 派生样本 seed 的根值（默认 `1`） |
| `--blind-aggregation <m>` | `Majority`、`MeanScore` 或 `RiskAverse`（默认 `MeanScore`） |
| `--blind-policy-samples <n>` | 独立内层决策样本数（1..16，默认 3） |
| `--blind-rollout-actions <n>` | 每首动作轨迹的命令上限（1..64，默认 12） |
| `--blind-rollout-turns <n>` | 每首动作轨迹的跨回合上限（1..8，默认 3） |
| `--blind-policy-nodes <n>` | 每个内层搜索的节点上限（1..4096，默认 128） |
| `--blind-known-top <n>` | 观察者明确知道的抽牌堆顶张数（默认 0）；其余顺序独立采样 |
| `--use-portfolio` | 开宽度组合，只对 `Coordinator` 有效 |
| `--out/--label/--language/--verbose-game-log/--milestone` | 产物目录、标签、本地化语言码、是否打游戏日志、跑到 M1 还是 M2 |
| `--measure-phases` | 在运行日志里输出 `SEARCH_PHASE` 逐阶段排他耗时/分配表 |
| `--memory-no-progress-limit <int>` | 实验：连续多少次无进展内存回收后提前收手；0=关闭（生产默认） |
| `--transposition-entry-limit <int>` | 实验：转置支配表合并条目上限；0=不设上限，缺省=生产默认 1000000 |
| `--enable-no-gc-region` | 让 Runtime 的 No-GC / 回收生命周期真正生效；默认关闭 |
| `--no-gc-region-budget-gigabytes <double>` | No-GC 区域预算（十进制 GB，1..256）；只在开启上一项时生效 |
| `--signal-ballast-mb <int>` | 进 No-GC scope 后先持有 N MiB 活对象；只用于制造受控内存压力，0=关闭 |

超时定位可设置 `OFFLINE_HARNESS_STREAM_DIAGNOSTICS=1`：现有 Info 诊断同时写到标准输出；Coordinator 还每秒至多输出一次现有进度消息中的阶段、局部展开、配置额度和回合层等值，进程被外部结束时仍可保留已经写出的记录。进度的 `reviewed_worldlines` 不是模拟转移数，也不能替代完成结果的请求级 `TotalExpanded` / `TotalTransitions`。该模式会启用进度回调及额外输出，可能改变耗时、分配和墙钟截断，只用于定位，不能作为性能或最终质量样本。默认不开启；普通批量对照须保持关闭。

启用阶段测量时，`BEAM_WIDTH_PORTFOLIO_MEMBER_START` 在进入成员前记录实际运行序号、宽度、次段/基础分/能力承诺身份，以及有效节点/时间额度。`run_index` 只计算实际运行的成员，不能当作包含跳过项的最终成员表索引。即使后续成员超时，配合同步诊断也可识别正在执行的成员；不能仅凭“正在精炼路线”的进度文案推断策略身份。

## 未知 RNG 单步决策

```powershell
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --character IRONCLAD --encounter CULTISTS_NORMAL --seed blind-evidence-20260928 `
    --search-mode Coordinator --blind-samples 3 --blind-seed 12345 `
    --blind-aggregation MeanScore --nodes 300 --budget-ms 3000 --dop 1 `
    --out .local/blind-smoke
```

`--seed` 构造真实战斗，`--blind-seed` 只生成未知未来的模型假设，两者用途独立。采样保留手牌和当前意图，替换未来 RNG、未知抽牌顺序和未来意图 forecast；默认不读取真实堆顶顺序作为已知信息。只有确实观察到堆顶时才提供 `--blind-known-top`。

搜索先从每个样本发现首命令，再补齐当前动作枚举入口准入的全部合法首命令，将每个候选在全部外层样本上执行。每一步结算后重新冻结观察，使用与外层 RNG/牌序无关的固定内层 seed 提名下一命令。`Majority` 比较外层轨迹分数投票；`MeanScore` 比较完整矩阵均分；`RiskAverse` 先比较已执行部分仍存活的比例，再比较最差四分之一战损、已完成胜利比例和均分。均值不只使用该动作赢得发现阶段的幸运样本。

`--budget-ms` 是每样本发现阶段的软预算，评估阶段另将同等预算按候选数平分；`--nodes` 应用于发现及诊断搜索，内层受 `--blind-policy-nodes` 约束。总请求数还包括轨迹每个决策及药水每项选择的独立内层 Coordinator，不再是固定 K+K*C 次。内层至少 25 ms/样本，单次结算与收尾可能超出软预算；相同配置不保证实际耗时或轨迹深度相同。总工作量看 `blindPolicy.TotalExpandedNodes` / `TotalTransitionCount` / `Elapsed`。普通 `solverMetrics` / `pruneCounters` 属于诊断代表样本，`metricScope=representative_sample`，不能当作采样总量。blind 模式不接受 `--production-budget`、`--observe-portfolio` 或 `--portfolio-model`。

输出命令按 `Kind` 处理，可能是出牌、药水或 EndTurn。原 `PlanAction.IsExecutable` 仅表示卡牌/药水，不能用它过滤 blind 的 EndTurn。命令中的选择为空：发起动作后若出现选牌页，默认 Blind 处理实际可见选择；在下一个稳定决策点重新采样。`--play-combat` 已覆盖原生动作后的稳定 Play 根；Mod 在线 UI、任意 mid-combat 存档恢复和复杂原生嵌套选择仍未接入。

最终首动作价值来自可执行的外层轨迹，内层确定化搜索仍可能乐观地提名动作。药水选项评分的 `ValueScope=independent_determinized_choice_proposal` 只表示独立的选牌提案价值。外层的 `completed_policy_trajectory` 表示完成战斗，`observed_prefix_with_state_heuristic` 表示截断前缀加当前观察的启发式值；每项保留 `ObservedActions`、`Decisions`、`StopReason` 和 `BoundaryReason`。未完成项的扣血/存活只覆盖执行前缀，胜利比例也不是统计战斗胜率，不能声称严格随机策略最优。

可选设置 `OFFLINE_HARNESS_BLIND_CHECKS=1`，在本次根上运行 121 项采样、RNG/Fork 隔离和聚合合同并写出 `blind-checks.json`。可重跑的小夹具为 `tools/OfflineSearchHarness/fixtures/blind-endturn.json` 与 `blind-potion.json`；分别覆盖零能量 EndTurn 和药水首命令约束。这些合同验证 blind 入口，不替代完整游戏的原生差分。

设置 `OFFLINE_HARNESS_BLIND_AUDIT=effects`，配合 `--milestone M1` 运行随机效果的隐藏输入扰动与原生完整 continuation/RNG 对账。夹具分四组：生成三选一药水 `blind-attack-potion.json`、`blind-skill-potion.json`、`blind-power-potion.json`、`blind-colorless-potion.json`；直接生成/随机池卡 `blind-orobic-acid.json`、`blind-stoke.json`、`blind-infernal-blade.json`、`blind-white-noise.json`、`blind-distraction.json`；随机费用/取牌/药水/球 `blind-snecko-oil.json`、`blind-entropic-brew.json`、`blind-cosmic-concoction.json`、`blind-true-grit.json`、`blind-thrash.json`、`blind-cinder.json`、`blind-anointed.json`、`blind-hidden-gem.json`、`blind-drain-power.json`、`blind-alchemize.json`、`blind-chaos.json`；抽牌时费用随机化（EndTurn 模式）`blind-slither-draw.json`、`blind-confused-draw.json`、`blind-call_of_the_void_power.json`、`blind-creative_ai_power.json`、`blind-hello_world_power.json`、`blind-spectrum_shift_power.json`；充能球随机目标（引导+EndTurn 两命令，双敌遭遇）`blind-ball-lightning.json`；先随机后选一与随机目标 `blind-bundle-of-joy.json`、`blind-metamorphosis.json`、`blind-discovery.json`、`blind-abundance.json`、`blind-quasar.json`、`blind-splash.json`、`blind-bouncing-flask.json`、`blind-beat-down.json`。攻击型来源的审计夹具必须给足敌方 HP（默认 1 HP 会被当场击杀并触发终局锁定，跳过攻击后的随机子效果）。`delayed` 配合 `blind-delayed-energy.json`，`immediate` 配合 `blind-delayed-energy-lethal.json`；输出 `blind-audit.json`。专项入口会推进宿主内的原生战斗，应单独运行，不与正常 M2 搜索或 rollout 混用。配置、检查数和证明范围见[专项核查](strategy/blind-rng-audit-20260928.md)、[随机池扩展验收](strategy/blind-pool-expansion-20260929.md)与[随机通道验收](strategy/blind-rng-channels-20260929.md)。

设置 `OFFLINE_HARNESS_BLIND_POLICY_CHECKS=1`，配合 `--milestone M1` 和 37 个 `blind-pool-*.json` 夹具（随机池/药水/费用/取牌/球/生成 Power/先随机后选一族，见 `tools/OfflineSearchHarness/fixtures/`），运行可执行轨迹、独立决策器的隐藏未来扰动、生成药水候选/跳过评分、卡牌随机池反例及原生选择对账，输出 `blind-policy-checks.json`。不要与其他专项开关或正常 M2 rollout 混用。既有七场景的 94 项检查见[随机池与逐步观察策略](strategy/blind-pool-policy-20260928.md)，2026-09-29 两批共 17 个新场景的检查与结果解读见[随机池扩展验收](strategy/blind-pool-expansion-20260929.md)与[随机通道验收](strategy/blind-rng-channels-20260929.md)。

此处的“隐藏输入独立性”仅覆盖已扰动的真实 RNG 和未知抽牌顺序。根中的怪物内部状态和第三方隐藏字段尚未完成可见性白名单核查，不能宣称整个入口严格只使用玩家可见信息。内层已复用 Coordinator 的 Smart 审计和长期计划入口，但短预算不保证复杂计划搜索充分。

## 玩家存档与原生战斗推进

```powershell
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --run-save <current_run.save> --play-combat `
    --search-mode Coordinator --blind-samples 3 --blind-seed 902828 `
    --blind-aggregation MeanScore --beam 12 --nodes 300 --budget-ms 300 --dop 1 `
    --native-choice-policy Blind --combat-timeout-seconds 120 `
    --out .local/blind-native/current
```

历史指导测试将 `--run-save <current_run.save>` 换成 `--history-run <history.run> --history-combats 2`。
`--request`、`--run-save`、`--combat-replay`、`--history-run` 互斥；`--play-combat` 要求 blind 模式、
M2 和 `--blind-known-top 0`，不将开局堆顶知识传播到后续动作。

- `.save` 使用游戏 `SerializableRun`、`RunState.FromSerializable` 和 `SetUpSavedSingleplayer`。
  地图生成后完整规范化序列化对账，只将无状态遗物的空 `props:{}` 与省略字段视为等价；
  通过后用 `LoadIntoLatestMapCoord(null)` 继续，不重复记录坐标或重抽地图。
- 仅接受当前 schema 的单人战前材料。空 BaseLib 卡牌 modifier 和明确列出的空 Loadout 元数据可忽略；
  非空或未知扩展拒绝。宿主没有原存档的完整 Mod 栈，不能声称恢复了第三方玩法语义。
- `.mcr` 只提取战前 run，不回放录制操作；没有原模型编号映射时，hash 不匹配会在解码前停止并保存头信息。
- `.run` 没有完整地图坐标、解锁状态和逐张战斗操作。建局使用记录 seed/角色/幕/进阶，
  选择记录中的远古之民选项，按相邻节点类型走路，核对遭遇，并在实际奖励候选中匹配历史选牌。
  这是历史指导前缀，不能作为精确手操回放；非战斗节点、不可用奖励或遭遇不符均显式停止。
- 原生执行使用 `PlayCardAction`、`UsePotionAction`、`EndPlayerTurnAction`，先等待具体动作完成，
  再等待稳定 Play 或战斗结束；每条首命令后重新捕获根，样本选择和跨回合续用均不部署。
- `Blind` 对已支持的药水选择观察实际候选，再用同一组独立内层样本比较选项和跳过；其他有定义的卡牌选择复用可见牌优先级，未知来源或嵌套选择明确停止。原生验收已覆盖攻击药水、技能药水、弃牌重抽药水（2026-09-28）及 PowerPotion、ColorlessPotion（2026-09-29）；其余已绑定来源不等于本轮全部通过。
- `Stop` 遇到需要决策的原生选牌会报告未支持；`FirstLegal` 只选实际候选中的前 `minSelect` 张，是显式基线。攻击/技能药水允许 0..1 张，因此这条基线会跳过。战后选牌仍使用历史记录。
- 建局、领奖、下一场准备和搜索共同扣除请求时限；耗尽后不启动下一条命令或下一场，
  `result.json` 返回 `Held` 和 `rolloutStopReason=TimeLimit`。限制为协作式停止，不中断正在运行的单个同步结算。
- 保存后端始终为游戏的 `MemorySaveStore`，不会写回玩家存档。这里没有 Godot 进程或游戏实例目录；
  若使用 `run-unattended-test` 的 Godot 无头路径，仍须遵循其独立实例与清理规范。

9 场原生战斗、奖励选择与人工历史差距见[本轮实测](strategy/blind-native-rollout-20260928.md)。
启用 `OFFLINE_HARNESS_INFUSED_CORE_CHECKS=1` 并使用 `--character DEFECT --milestone M1` 可运行注能核心的生产 Hook 诊断：从空球队列检查首回合生成、后续回合不重复、参与者条件、4/9 数值及 Fork 隔离，结果写入 `infused-core-checks.json`。该入口只修改宿主内的测试战斗；它不经过原生工具箱页面，不能替代 `coverage/unattended/initial-toolbox-infused-core.json` 的原生准备状态验收。

启用 `OFFLINE_HARNESS_FIXED_PREFIX_CONTINUATIONS=1` 并以 `coverage/unattended/generic-cross-turn-hidden-buffer-positive-v0111.json` 为 `--request`，使用 `--dop 1 --search-mode Evaluate` 且关闭NoGC／增量验证，可运行4／8／17回合完整固定前缀基准。每根预热一次、测量三次生产 `Solve`，计时外用独立前缀重放对账完整续用戳，输出 `fixed-prefix-continuations.json`。它只度量人工长路线的前缀建立与收尾，不代表普通搜索或原生正确性；用 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 交错切换基线／候选，完整比较根、政策和 `annotatedResult`，见[本轮证据](performance/fixed-dop-20260927.md)。

## 批量用法

`tools/OfflineSearchHarness/run_plan.py` 吃一份 plan JSON（数组），起 N 个宿主进程并行消费：

```
python3 tools/OfflineSearchHarness/run_plan.py --plan <plan.json> --workspace <dir> --workers 3
```

plan 每项的字段：`label`（必填，简单目录名）、`request`（必填）、`profile`、`beam`、`nodes`、
`maxCardBranchesPerNode`、`maxPileChoiceBranchesPerAction`、`maxHandChoiceBranchesPerAction`、
`maxDegreeOfParallelism`、`searchBudgetMilliseconds`、`potionPolicy`、`searchMode`、`usePortfolio`、
`dll`（换掉这一根运行时加载的 `CombatSolver.dll`）。

产物在 `<workspace>/runs/<label>/`，另有 `<workspace>/runs.jsonl` 与 `plan-summary.json`。

`tools/OfflineSearchHarness/compare_results.py` 把两份 `runs/` 目录逐字段比较（两侧都提供时还比较选中路径的续用戳；`solverMetrics` 里
与时间/内存/GC 无关的字段、选中路线每个动作的 `turn/kind/cardId/potionId/targetCombatId/cardStateKey`、
根 `ContinuationStamp`、生成场景目录指纹），全等返回 0，有差异返回 1 并把明细写进 `--out`。

## 产物

每根一个目录：

- `result.json`：`label` / `status` / `profile` / `searchMode` / `budget` / `solverMetrics`（与游戏内
  无人测试 `result.json` 同名同形，由游戏自己的 Writer 构造）/ `pruneCounters`（宿主从 `SolverResult`
  读的剪枝与复用计数，游戏内那份没有）/ `timeBoundaryObserved` / `wallSeconds` / 峰值托管堆、峰值
  工作集、总分配字节 / `rootContinuationStamp` / `catalogFingerprint`。
- `route.json`：选中路线的动作序列。
- blind 模式下 `route.json` 和 `planActions` 只包含当前命令。`result.json` / `harness-result.json` 的 `blindPolicy` 含发现样本、完整候选评估矩阵、动作统计及全部工作量；不写原根 RNG 戳或样本续用计划。`rootUnchanged=true` 来自搜索后重新捕获的 live 与冻结影子状态核对。
- `root-diagnostics.txt`：普通模式为 `SolverDiagnostics.DescribeStart` 的完整根描述；blind 模式只描述公开手牌、牌堆数量和当前战斗信息，不写隐藏抽牌顺序。
- `search-policy.json`：这一次求解实际用的 `SearchPolicySnapshot`。
- `harness-result.json`：上面全部加上分步时间线、绕过清单、补丁装载记录。
- 存档入口的 `save-import.json` / `run.restored.json`：恢复身份及完整规范化 run；对账失败另写 `run.expected.json`。
- rollout 的 `rollout-summary.json`：各场状态、HP、原生实际扣血、回合、决策和耗时；
  `combat-NN/decisions.jsonl` 记录每条命令的 selected/executed、完整评估矩阵和实际选牌。
  `history-guidance.json` 在战后领奖完成后更新，包含最后一场奖励与领奖后牌组。

## 口径

**`Evaluate` 与 `Coordinator` 的区别。** `Evaluate` 是单次求解：直接建一个 `CombatBeamSolver` 跑，不经
协调器，也就没有组合成员和审计通道，`totalExpanded` 等于这一棵树自己的展开量。`Coordinator` 走生产
路径 `CombatSearchCoordinator.Solve`，`solverMetrics` 里的 `total*` 字段是**协调器把各条通道（含审计
通道与组合成员）加总**后的值，所以同一个根同样的宽度，`Coordinator` 的 `totalExpanded` 会明显大于
`Evaluate`。要量「一个宽度值到底搜了多少」用 `Evaluate`；要量「玩家实际会等多久、实际选哪条路线」
用 `Coordinator`。

`solverMetrics.searchWorkAttributions` 另列经前沿调度器派发的各 `ContinuationPurpose` 工作量、`UnattributedDirect`（尚未细分的主搜和审计）及 `CoordinatorOverhead`。这是同一请求账本的诊断分解；直接成员尚未全部标记，不能由 `UnattributedDirect` 推断单一瓶颈。超时未产结果时使用常驻会话保存的 `timeout-progress.json`，旧包缺该文件就没有可追溯的末段工作量。

**固定预算口径。** 宿主总是以 `fixedSearchBudget=true` 起一段离线会话
（`UnattendedTestRunner.BeginOfflineSession`），`--budget-ms` 落在 `searchBudgetOverrideMilliseconds`
上，`--dop` 落在 `searchMaxDegreeOfParallelismForTest` 上——与游戏内无人测试请求里的同名字段走同一段
代码（`ProtocolHost.ConfigureSearchOverrides`）。默认 `EnableNoGcRegion` 关闭；显式传
`--enable-no-gc-region` 且目标是验证 Runtime 的内存回收/截断路径时，才由 `SearchGcPolicy` 管理模式
并将回收回调注入搜索。该模式只用于诊断，不替代游戏内无人测试的正确性断言。

**宽度组合。** `--use-portfolio` 把 `useBeamWidthPortfolioForTest` 打开，与无人测试请求里那个开关同义；
成员宽度不指定时用协调器自己的默认成员集，成员明细在 `solverMetrics.portfolioMembers`。

## Godot 绕过

宿主不启动引擎，凡是会打到 Godot 原生层的入口都要绕开。绕过点全部集中在
`tools/OfflineSearchHarness/GameBootstrap.cs` 一个类里，类头有完整的表（目标、为什么必须绕、绕过后
返回什么、对搜索结果有没有影响），结果 JSON 的 `bypasses` 字段列出实际装上的那些。摘要：

| 目标 | 绕过后 | 对搜索结果 |
|---|---|---|
| `Logger.GetIsRunningFromGodotEditor`、`ConsoleLogPrinter.Print` | 不判编辑器、打到 `System.Console` | 无，只决定日志去向 |
| `LocString.GetRawText/GetFormattedText/Exists` | 返回本地化键名 / `true` | 无，搜索不读文案（见下方限制） |
| `PreloadManager.Load{Run,Act,RoomCombat,RoomEvent}Assets` | `Task.CompletedTask` | 无，战斗/事件建立不需要立绘与节点 |
| `NCombatRulesFtue.Create` | `null` | 无，与游戏内无人测试同语义 |
| `NEventRoom.Create`、遗物提示、`NHoverTipSet.Clear`、控制器静态构造 | 不创建事件/提示节点或热键 | 只绕过显示；事件与奖励的原生核心逻辑仍执行 |
| `MigrationRegistry.RegisterAllMigrations` | 跳过实例化，按 attribute 读取最新版本 | 只接受当前 schema，不迁移旧档 |
| `UserDataPathProvider.GetPlatformDirectoryName`、`PlatformUtil.GetLocalPlayerId` | 固定内存存储身份 | 不访问玩家磁盘存档 |
| `Godot.Time.GetTicksMsec` | `Stopwatch` 单调毫秒 | 保留原生命令的计时推进，无 Godot 原生层 |
| `NGame.GetGameVersion`、`PlatformUtil.GetPlatformBranch/GetPlayerNameRaw` | 固定值 | 无，只进联机握手信息与显示名 |
| `Godot.Node` 及其 304 个子类的静态构造 | 跳过 | 无，离线不建节点树 |

还有一处不是补丁：`SolverController.DisplayServerNameProvider` 被设成固定返回 `"headless"`，与游戏内
`--headless` 取到的值一致，帧压力恢复照样关闭。

## 已知限制

- **本地化返回键名**：`LocManager.Initialize` 要用 `Godot.FileAccess` 读 `res://localization`，离线装的是
  一张空表，所有文案取到的是键名。显示字段（`cardTitle` / `targetName` / …）因此不能与游戏内直接比，
  `compare_results.py` 已把它们排除。搜索本身不读文案。
- **不支持任意中途恢复**：宿主已接当前 schema 的原生战前 `.save` 与可解码 `.mcr`；
  无人测试 `runSnapshotPath`、`replayStatePath`、`checkpointArchivePath` 的中途恢复协议仍未接入。
- **RitsuLib 未初始化**：宿主装的是模组里与搜索正确性相关的那 13 个 Harmony 补丁，RitsuLib 自己的运行期
  初始化没跑。实测 30 根里有 2 根的探索量与游戏内不同，结论字段（选中路线、`score`、
  `projectedBattleHpLost`）相同。
- **不替代严格差分验收**：宿主有恢复对账、根隔离检查和原生命令 rollout，仍没有完整无人测试的断言体系。战斗语义改动仍然要过
  `docs/HEADLESS_TESTING.md` 的流程。

## 验证证据

**新宿主对旧研究版宿主，同一份 0.39.0 DLL，逐字段一致。** 两边跑同一批生成场景请求
（`VeryHigh`、beam 135、nodes 100000、分支上限 72/42/54、`--dop 1`、`--budget-ms 600000`、
`potionPolicy=Smart`、`searchMode=Evaluate`），用 `compare_results.py` 比 `solverMetrics`
（排除时间/内存/GC 字段）、选中路线每个动作、根 `ContinuationStamp` 与目录指纹：

| 批次 | 根数 | 比较字段数 | 不一致的根 |
|---|---:|---:|---:|
| BASE5 | 5 | 466 | 0 |
| 30 根子集 | 30 | 2719 | 0 |

**`Coordinator` + `--use-portfolio`。** 3 根走生产协调器并开宽度组合，全部跑通，
`solverMetrics.portfolioMembers` 各有 3 个成员（当时 0.39.0 基线的默认成员集是 `[W, 2W/3, 3W/2]`）；
开关关闭时只有 1 个成员。

**建根流程本身与游戏内的一致性**（宿主刚做出来时测的，基于研究分支 `4287e03`）：30 根生成场景，
宿主与游戏内无人测试逐字段对照，`solverMetrics` 的可比字段、选中路线、装备与开局产物全部相同，
2 根探索量不同（RitsuLib 未初始化，见上）。


## 生产预算与转置表观测

`--production-budget` 使用现有生产预算流程，包括剩余预算允许的无胜利升级。默认仍是固定预算，用于确定性逐位对照。批量计划的 `productionBudget: true` 允许正常时间边界作为有效观测，仍保留 `timeBoundary` 字段；固定预算计划撞到时间边界仍作废。

批量计划现在支持 `transpositionEntryLimit`，映射已有 CLI 的同名上限。省略字段使用生产默认一百万条；实验放大上限不修改生产值。

每次求解的 `TRANSPOSITION_CAP` 行记录首次触顶展开数、跨缓存重建保留的峰值条目、结束时标签数和分布。`LimitBypasses` 按未入表的准入/展开事件计数，包含重复键；标签分布为单通道结束值。Coordinator 多个通道分别输出，不合并成虚假的同时驻留峰值。

## 循环边界对照

`run_loop_boundaries.py` 接受逐 case 的 Evaluate / Coordinator。Evaluate 的局部 time/nodes 计数与日志对账；Coordinator 从全部成员日志提取请求级时间截断，不把所选 solver 的计数当请求总数。新版用 `TotalCycleReplayActions` 检查请求 4096 上限；旧版只在 Evaluate 可回退单 solver 值，旧 Coordinator 缺失请求数明确标为 unavailable。时间截断返回 Inconclusive/2；可比较差异、建局或质量断言失败返回 1，保留全部原始观察。工具的显式 suite 断言不等于原生 expected* 验收。见[完整输入、设计和结果](performance/loop-final-20260921.md)。


### 后置结构探索实验

`--adaptive-novelty` 仅接受 `--search-mode Coordinator --use-portfolio`，通过不可变 `AdaptiveNoveltyRefinement` profile 启用，生产默认关闭。先完整运行原 Beam 组合；达到完整政策目标（含治疗保护）则跳过，否则复用已有新颖性算法。补充额度分别不超过此前实际展开与实际耗时的 1/8，同时受原请求余量及既有 2500 节点/5 秒上限约束。节点额度不等于转移、分配或内存上限；不可分割工作仍可能越过软时间边界。

`ADAPTIVE_NOVELTY_START/END` 记录实际预算、展开/转移和选择结果。`NOVELTY_SEARCH_STOP reason=...` 覆盖所有新颖性搜索；宿主分类器将 `time_limit` 单独记入 `noveltyStops` 并标记 `TimeLimited`，不混入回合层计数。以前没有该事件的 DLL 不能据“没有 SEARCH_TIME_BUDGET”断言该算法没有时间截断。探索预算依赖墙钟，质量观察必须保留时间截断与重复波动，不能称为固定工作量等价。完整取舍与验证结果见上下文排序报告。


算法配置记录补充：`searchPolicy` 现在显式输出 `BeamWidthPortfolioPlainBaselineMember` 与 `UseNoveltyPortfolio`。旧宿主未记录这两项时，比较报告显示 `Unrecorded`，必须结合保存的命令与实际成员表判断，不能把缺失值当作默认值。上下文排序对照允许这两项算法配置作为显式实验差异，仍拒绝根、预算、可接受战损和用药政策等目标差异。

`Coordinator --use-portfolio` 默认启用组合再分配。`--disable-reallocated-refinement` 在同一最终程序集恢复旧默认成员列表，供明确A/B；`--reallocated-refinement` 可显式开启。真实运行是否采用新布局由 `PORTFOLIO_REALLOCATION` 与实际成员表确认，显式成员布局和其他可选实验不被改写。配置保存在 `searchPolicy.Profile.ReallocatedRefinementPortfolio`，对照工具将其视为算法差异而保留目标政策/根/预算核对。
