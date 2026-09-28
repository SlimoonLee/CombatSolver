# 未知 RNG 随机机制扩展交接(2026-09-29,含审核要点)

目标是继续扩展带随机池的卡牌、能力、遗物、药水、球与洗牌等战斗机制:保留原版生成分布与结算顺序,结果揭晓后重新决策,评价实际可执行的策略收益。

- 工作目录 `D:\FORSTS2\.reference\CombatSolver-latest`,分支 `codex/blind-rng`,行为基线 `a4b58d0`,上游仍为 `origin/main@7236330`(0.111.0 / Mod 0.47.2)。
- 本轮共 **16 个提交**(`f4adbef`…`79390c5`),全部在基线之上线性叠加,未推送、未提升版本、未发包;工作树干净。
- 必读:上一份[交接](blind-rng-expansion-handoff-20260928.md)、AGENTS.md、架构地图、combat-semantic-change 规范、离线宿主指南。本文件只增量记录 2026-09-29 之后的变化。

## 2. 完成了什么,能证明什么

三轮扩展 + 若干补遗,共验收约 **58 个随机来源**,新增约 **1630 项检查**。分批报告:[随机池扩展](blind-pool-expansion-20260929.md)、[随机通道](blind-rng-channels-20260929.md)、[机制族扫尾](blind-rng-family-completion-20260929.md)、[遗物 Hook](blind-rng-relics-20260929.md)、[收尾来源](blind-rng-final-sources-20260929.md)。

| 机制族 | 已验收来源(三态均为"语义已验证/blind 估值已验证/原生已验收") |
|---|---|
| 生成三选一药水 | 攻击/技能/能力/无色药水;弃牌重抽、预知之滴、往昔之忆、疯狂之触、**异化水**(经修复转绿) |
| 直接生成到手牌 | 添柴、欧洛巴斯之酸、地狱之刃、白噪声、声东击西、宇宙药剂、新生之喜、大奖、君权自授、疯狂科学(Chaos 骑手) |
| 先随机后选一(卡牌端) | 发现、富足、类星体(需 Regent)、飞溅——通道已验收;逐选项续搜评分仍为优先级边界 |
| 随机置入抽牌堆 | 羽化 |
| 随机费用 | 异蛇之油、蛇行附魔、混乱 |
| 随机取牌/消耗/打出 | 坚毅、痛殴、余烬、天选、未掘宝石、能量汲取、横祸(Shuffle 流) |
| 随机目标 | 弹跳药瓶(**新增 Mirror**)、狠揍(双敌)、球状闪电+闪电球被动、二连/多连/四连激发 |
| 随机药水/随机球 | 混沌药水、炼制药水、混沌(球) |
| 回合开始持续生成 | 虚空之唤、创造性AI、你好世界、光谱偏移 |
| 遗物 Hook | 遗忘之魂、铜钹、锁镰、招架盾 |
| 洗牌流 | 重启 |

证据根 `.local/blind-policy-20260929/`(257 个产物目录,未提交);最终状态:**40 个 policy 场景全部 Passed,34+ 个 audit 场景全部 Passed**,delayed/immediate 17/22,采样合同 121 项,门禁 `REFACTOR_BOUNDARIES_OK search_files=241`,Mod/宿主 Release 0 警告 0 错误。

## 3. 行为源码改动(仅两处,均已部署)

1. **弹跳药瓶 Mirror**(`CardSelectionCardMirrors.BouncingFlaskOnPlay` + 注册):随机目标通道唯一语义缺口——逐击 `CombatTargets.NextItem` 抽随机敌人施加中毒;双敌审计逐样本敌方 HP 互异且原生对账。
2. **异化水缺口修复**(盲续搜药水核算):根因是搜索节点继承快照整场用药计数,用药后的观察根让每条新路线都被记为一次显式用药,Smart/Disabled 排序全判不可满足。修复 = `ZeroInheritedPotionUse` 政策位(盲续搜最终排序只按本次搜索新增用药评估)+ 政策不可满足时盲决策/发现阶段回退免药重试(已定义业务分支,非宽泛捕获)。整场药水核算仍由既有快照字段与外层可执行轨迹承担。
3. 附带 Testing 层参数:`ReplayBlindAuditForTesting` 增加可选观察选择策略(审计用,正式路径未动)。

改动文件(9 个,103+/15-):`SearchPolicySnapshot`、`CombatBeamSolver(.cs/.Phases/.FinalPlanOrdering)`、`BlindCombatSearch`、`BlindCombatPolicy`、`CardSelectionCardMirrors`、`CardOnPlayMirrors`、`BlindSearchContracts`。部署:游戏 `mods/CombatSolver` DLL 与构建产物时间戳一致(04:56),交接 20260928 第 9 节的占用阻塞已解除。

## 4. 可复用的批次工作方法

1. **两级验收**:policy 检查(`OFFLINE_HARNESS_BLIND_POLICY_CHECKS=1` + `blind-pool-*.json`,矩阵/隐藏未来独立性/斩杀反例/原生选牌对账)与 effects 审计(`OFFLINE_HARNESS_BLIND_AUDIT=effects` + `blind-*.json`,四样本扰动 + 生成互异 + 原生完整 continuation/RNG 对账)。一次只开一个专项开关。
2. **斩杀反例模板**:高价值随机动作 vs 确定击杀(重锤 3 费 32 伤/打击),断言"确定收益保留于随机赌注之上";自身带伤害的来源(痛殴/大奖/骚动/球状闪电)不设。
3. **审计结果键**:手牌 + 弃牌/抽牌多重集(含升级与重放计数,抽牌堆**保序**使洗牌可观测)+ 药水槽 + 球类型 + **敌方 HP**(随机目标可观测)。
4. **审计前缀按来源泛化**:`Prefix(source, first)`——球状闪电补 EndTurn;二连/多连/四连前置球状闪电并补逐实例键与目标;锁镰跨计数器(前缀两张攻击 + 审计命令第三张);招架盾(DEFEND+IRON_WAVE+EndTurn);原生对账同步执行完整前缀。
5. **骑手/状态注入**:`EnumMembers` 反射注入可写枚举属性(如疯狂科学 `TinkerTimeType=Skill`+`TinkerTimeRider=Chaos`);Power/遗物注入走 `Powers`/`Relics` 字段。
6. **通道清单驱动**:每批从[统计准备清单](../../../.local/rng-survey/rng-survey-prep-20260929.md)(中文名 + 机制 + 通道映射)选同机制来源,核对 0.111.0 反编译源与 Mirror 一致后再写夹具。

## 5. 已踩坑与教训(审核重点)

| 教训 | 内容 |
|---|---|
| 攻击型审计夹具敌方 HP | 默认 1 HP 会被当场击杀并触发终局锁定,跳过攻击后的随机子效果,造成系统性对账分歧(骚动/余烬/痛殴/能量汲取均中过招);**必须显式给足敌方 HP**。已写入宿主指南 |
| 随机候选需互异 | 单攻击牌/唯一候选会让样本互异检查失败——那是夹具无随机可辨,不是缺陷(骚动二次教训) |
| 教训升级:两次误判同型(大帽子时序、探寻打击"镜像不完整")——都因未先跑足额敌方 HP 夹具与深读现有实现;探寻打击实为**选择链完整**,真实差异是模拟候选序未贴原版 `(Rarity, Id)` 屏幕排序(已修 `BuildSeekerSpec`),并新增原生卡牌选择接管段。审核时验收前先核对 mirror 主效果**和**夹具敌方 HP |
| 时序在根捕获前 | 大帽子/橙色团块/工具箱都是 turn≤1 触发且发生在盲根捕获(玩家第一回合 Play)之前,盲审计形状无法复现;大帽子/橙色团块镜像其实在 `SimulatedCombatState.RelicTurnStart.cs`(曾误判缺分发面,已勘误) |
| python heredoc 不可靠 | 本机 `python - <<EOF` 多次静默失败/写盘丢失(exit 49);改文件一律用编辑工具或 pwsh |
| 开局生成被注入清除 | 宿主注入的 `clearPlayerPiles` 会把回合开始生成的牌清掉——回合开始遗物夹具不能清牌堆,断言靠池类型过滤 |
| 角色池守卫 | 大帽子在 IRONCLAD 池无虚化牌(原版守卫不生成)——夹具改用 NECROBINDER |
| 虚化=回合末消耗 | 虚化生成牌跨回合区计数为 0 是正确原版行为,存活断言只适用于非虚化来源 |
| 本机 CoverageCatalog | 因 RitsuLib 布局与 SDK 兼容问题构建失败(先于本轮存在),镜像覆盖以审计原生对账为准 |
| local.props | 参考仓库需 `SteamRoot=D:\SteamLibrary`(已建,不入库);用 Sts2Dir 直赋会破坏 RitsuLib References 导入 |

## 6. 剩余工作(每项独立批次)

1. ~~探寻打击镜像补全~~ **已完成(2026-09-29 晚)**:选择链本就完整;补了 `BuildSeekerSpec` 屏幕排序、policy/audit 夹具、原生卡牌选择接管段(policy 18 + audit 26)。无人测试 `seeker-strike-choice-audit-308.json` 已于同晚重跑 **Passed**(排序改动后双端验证,日志 `.local/seeker-308-rerun.log` 未提交)。
2. ~~turn≤1 时序族~~ **已完成(2026-09-29 晚)**:大帽子(NECROBINDER,虚化消耗型)/橙色团块(无色存活型)经 `initial-relic` 审计模式验收(各 6 项,开局生成+两回合对账防重复触发);工具箱引用既有 `INITIAL-TOOLBOX-INFUSED-CORE` 原生验收。见[回合开始时序族验收](blind-rng-turnstart-20260929.md)。
3. **随机怪物** **部分完成(2026-09-29 晚)**:来源清单与分组见[随机怪物来源清单](blind-rng-monsters-20260929.md)。组代表经 MonsterMoveChecks 严格差分:随机召唤(组装师 FABRICATE_MOVE,FABRICATOR_NORMAL 遭遇)**Passed**、随机偷牌(偷窃草蜢 THIEVERY_MOVE)**Passed**;分支代表(雾菇 SWIPE_MOVE→BRANCH 链)夹具入库,待无头基础设施重跑(瞬态);噪音机器人经组装师召唤链覆盖,结实的卵随孵化链批。怪物私有 AI 可见字段审计作为同族扩展前置(与第 5 条白名单合并推进)。
4. **低语耳环**:第一回合自动出牌引擎 + AnyAlly 多人目标,按单人硬约束保持边界。
5. **架构任务**(不因来源扫尾自动完成,建议顺序):全局可见字段白名单、卡牌/Power/遗物通用逐选项 blind 评分(发现族/探寻打击的逐选项续搜)、复杂嵌套选择和取消排空、硬预算与截断偏差、Mod 在线 blind UI、多 seed 质量统计。

## 7. 审核要点清单

- 核对两处行为改动与部署:`git diff a4b58d0..HEAD -- src/`(9 文件);游戏 mods 目录 DLL 时间戳。
- 抽查证据:`.local/blind-policy-20260929/policy-*` 与 `audit-*` 的 `blind-policy-checks.json`/`blind-audit.json`(status/checks 数);对照本文件第 2 节与各批次报告。
- 复跑入口:任选夹具按第 4 节第 1 条的两级命令重跑(输入未变,结果应逐项复现)。
- 重点质询项:探寻打击第三次勘误的证据链(报告与 policy/audit v3+ 目录)、异化水修复是否影响既有药水场景语义(40 场景回归+121 合同已覆盖)、统计准备清单是否与本轮验收一致(`.local/rng-survey/rng-survey-prep-20260929.md` 末尾映射表)。

## 8. 每批交付模板

沿用上一份交接第 10 节模板,不变;本轮所有批次报告均按其执行,可作范本。
