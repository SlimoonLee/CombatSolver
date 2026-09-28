# 未知 RNG:药水通道与直接生成到手牌家族验收(2026-09-29)

## 批次概览

| 项 | 内容 |
|---|---|
| 批次 / 行为基线 | 2026-09-29 第一批;行为基线 `a4b58d0`,本批**零行为源码改动**——只扩展专项检查断言与夹具 |
| 游戏版本 / Mod | 0.111.0 / 0.47.2;分支 `codex/blind-rng` |
| 来源 ID 与机制族 | 先生成候选再选一项:`POWER_POTION`、`COLORLESS_POTION`(已有通道验收);直接生成到手牌:`INFERNAL_BLADE`(攻击)、`WHITE_NOISE`(能力)、`DISTRACTION`(技能) |
| 缺口及修改所在职责层 | 五个来源的 Mirror、选择定义、药水 Prepare/Complete、blind 估值与原生选择接管此前均已存在但未验收;本批修改仅 `tools/OfflineSearchHarness/BlindPolicyChecks.cs`、`BlindAuditChecks.cs` 的固定场景断言及 10 个新夹具 |

| 验收层 | POWER_POTION | COLORLESS_POTION | INFERNAL_BLADE | WHITE_NOISE | DISTRACTION |
|---|---|---|---|---|---|
| 已有语义 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 |
| blind 估值 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 |
| 原生执行 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 |

原版语义依据(定向读取 0.111.0 反编译源,与 Mirror 逐条一致):

- `PowerPotion`/`ColorlessPotion.OnUse`:按 `CardType.Power`/无色池 `GetDistinctForCombat` 取 3 张(`CombatCardGeneration` 流),`CardSelectCmd.FromChooseACardScreen(canSkip: true)` 选 0..1 张,选中牌 `SetToFreeThisTurn` 后加入手牌。对应 `CardGenerationPotionMirrors.Generate` 与 `PotionChoiceSupport` 的 `GenerateToHand` 0..1 规格。
- `InfernalBlade`/`WhiteNoise`/`Distraction.OnPlay`:按攻击/能力/技能过滤 `GetDistinctForCombat` 取 1 张,免费本回合加入手牌;InfernalBlade 与 WhiteNoise 另有 Exhaust。对应 `CardGenerationCardMirrors` 三个 OnPlay Mirror。

观察边界:揭晓前决策不知道生成哪张(采样层只替换未来,不预知本次结果);生成/选牌完成后手牌为可见信息,轨迹在稳定观察上重新提名下一命令。药水选择只消费已生成的候选(历史中的 `CardGenerationOptions`),不重新抽样。

选择规格:两种药水均为 3 张 + 跳过共 4 个合法选项,`MinCount=0`/`MaxCount=1`,全部选项共用同一组独立内层样本;卡牌来源无选择页。枚举数未触及分支上限。

## 无头证据

共同配置:M1 专项入口、blind seed 902828、3 外层/3 内层样本、Beam 12、300 节点/3000 ms、内层 128 节点、12 命令/跨 3 回合、DOP1、Smart 药水政策。证据根为仓库 `.local/blind-policy-20260929/`。

| 场景 | 检查数 | 重点 |
|---|---:|---|
| policy `blind-pool-power.json` | 14 | 0 能量注入、完整矩阵、隐藏未来独立性、原生选牌与完整 continuation/RNG 对账 |
| policy `blind-pool-colorless.json` | 14 | 同上 |
| policy `blind-pool-infernal-blade.json` | 11 | 随机池卡牌进入完整矩阵、即时斩杀反例、生成后重观察 |
| policy `blind-pool-white-noise.json` | 11 | 同上 |
| policy `blind-pool-distraction.json` | 11 | 同上 |
| audit `blind-power-potion.json` | 35 | 四样本隐藏输入扰动 + 原生完整对账(与攻击/技能药水同规格) |
| audit `blind-colorless-potion.json` | 35 | 同上 |
| audit `blind-infernal-blade.json` | 26 | 同上(与酸/添柴同规格) |
| audit `blind-white-noise.json` | 26 | 同上 |
| audit `blind-distraction.json` | 26 | 同上 |

既有五个 policy 场景回归(attack/skill/acid/stoke/gambler)分别 13/14/9/9/10 项通过,与 2026-09-28 记录一致,确认检查文件改动无回归。Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=241`;Mod 与宿主 Release 构建 0 警告/0 错误。

## 结果与读法

首动作矩阵(完整轨迹口径 `completed_policy_trajectory`):

| 场景 | 首命令 | 平均实际扣血 | 完成胜利 | 结论 |
|---|---|---:|---:|---|
| power | 直接 EndTurn | 4 | 3/3 | Smart 保留药水:节省的 1.33 HP 低于用药战略成本 |
| power | 用 POWER_POTION | 2.67 | 3/3 | 随机池实际减损;原生选 `STONE_ARMOR`(提议值 0 扣血) |
| colorless | 直接 EndTurn | 4 | 3/3 | 同上 |
| colorless | 用 COLORLESS_POTION | 2.67 | 3/3 | 原生选 `JACK_OF_ALL_TRADES`(提议值 2.67 扣血) |
| infernal-blade | `BLUDGEON` | 0 | 3/3 | **确定零损击杀保留**;领先 InfernalBlade 160000 |
| infernal-blade | `INFERNAL_BLADE` | 2.67 | 3/3 | 机会成本:花 1 能量后 BLUDGEON 本回合不可用,仅样本抽到免费高伤攻击才追平 |
| white-noise | `STRIKE_DEFECT` | 0 | 3/3 | **确定零损击杀保留**;领先 WhiteNoise 240000 |
| white-noise | `WHITE_NOISE` | 4 | 3/3 | 与 EndTurn 同分:敌 6 HP 下回合必死,生成能力牌无战局贡献,内层理性弃打 |
| distraction | `STRIKE_SILENT` | 0 | 3/3 | **确定零损击杀保留**;领先 Distraction 140000 |
| distraction | `DISTRACTION` | 2.33 | 3/3 | 免费技能略降战损(2.33 < 4)但仍劣于当场击杀 |

生成多样性与生成后重观察(effects 审计,四样本生成结果互不相同,并经原生对账):

| 来源 | 四个样本的实际生成 |
|---|---|
| INFERNAL_BLADE | POMMEL_STRIKE / BLUDGEON / CONFLAGRATION / CONFLAGRATION |
| WHITE_NOISE | CREATIVE_AI / TRASH_TO_TREASURE / DEFRAGMENT / ITERATION |
| DISTRACTION | PREPARED / HAZE / DODGE_AND_ROLL / SHADOWMELD |
| POWER_POTION | DEMON_FORM / VICIOUS / FEEL_NO_PAIN / JUGGLING |
| COLORLESS_POTION | PROLONG / STRATAGEM / THINKING_AHEAD / SALVO |

三个卡牌来源的池动作轨迹均在生成揭晓后形成新的后续决策;INFERNAL_BLADE 与 DISTRACTION 进一步断言后续命令实际打出揭晓的生成牌。WHITE_NOISE 只断言重观察级、不断言采纳:其夹具的确定斩杀对照与"生成牌必须有用"互相冲突(敌 6 HP 时任何能力牌都不改变结果),内层策略不打出免费能力牌是正确决策而非缺陷。

## 口径与预算

药水选项评分为 `independent_determinized_choice_proposal`(独立内层确定化提案,仍可能乐观);外层首动作价值为 `completed_policy_trajectory` 或 `observed_prefix_with_state_heuristic`。五个新场景的搜索工作量:power 109 次搜索/12525 展开/29103 转移,colorless 100/11527/31439,infernal-blade 40/527/1287,white-noise 31/289/649,distraction 37/847/2507。`--budget-ms` 仍为软预算;本批各场景均未出现截断叶子(全部 `completed_policy_trajectory` 或检查期望的对照结构)。隐藏未来独立性继续以两个分别新建的决策器验证;搜索前后原生根状态不变。

## 未验证范围与下一步

- 本批未验收:BundleOfJoy(生成多张不免费)、CosmicConcoction(生成并升级无色牌)、先生成再选一的卡牌端(Abundance/Discovery/Quasar/Splash 的续搜评分)及持续生成族。
- 照旧全局缺口:全局可见字段白名单、内层确定化提案乐观性、硬预算截断偏差、复杂嵌套原生选择、Mod 在线 blind UI、统计胜率。
- WhiteNoise 的采纳级正例需要一个生成牌确实改变战局的夹具设计,与确定斩杀反例分开做。
- 本批无 Mod(`src/`)源码变化,不触发重新部署;本地部署状态继承交接文档第 9 节的阻塞记录(玩家游戏进程占用 DLL),未重试复制。

## 重跑

```powershell
$env:OFFLINE_HARNESS_BLIND_POLICY_CHECKS = '1'
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --request tools/OfflineSearchHarness/fixtures/blind-pool-power.json `
    --milestone M1 --blind-seed 902828 --beam 12 --nodes 300 --budget-ms 3000 --dop 1 `
    --workspace .local/blind-pool-repro/host-power --out .local/blind-pool-repro/power
Remove-Item Env:OFFLINE_HARNESS_BLIND_POLICY_CHECKS

$env:OFFLINE_HARNESS_BLIND_AUDIT = 'effects'
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --request tools/OfflineSearchHarness/fixtures/blind-infernal-blade.json `
    --milestone M1 --blind-seed 902828 --beam 12 --nodes 300 --budget-ms 3000 --dop 1 `
    --out .local/blind-pool-repro/audit-infernal-blade
Remove-Item Env:OFFLINE_HARNESS_BLIND_AUDIT
```

其余来源把夹具换为 `blind-pool-colorless/infernal-blade/white-noise/distraction.json` 或 `blind-power-potion/colorless-potion/white-noise/distraction.json`。一次只启用一个专项开关;两种开关各自单独运行。
