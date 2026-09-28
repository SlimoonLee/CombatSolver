# 未知 RNG:随机费用、随机取牌、随机药水与随机球通道验收(2026-09-29 第二批)

## 批次概览

| 项 | 内容 |
|---|---|
| 批次 / 行为基线 | 2026-09-29 第二批;行为基线 `a4b58d0`,本批**零行为源码改动**(`src/` 无变化,仅工具检查与夹具) |
| 游戏版本 / Mod | 0.111.0 / 0.47.2;分支 `codex/blind-rng` |
| 机制族与来源 | 随机费用:`SNECKO_OIL`(异蛇之油)、`SLITHER`(蛇行附魔)、`CONFUSED_POWER`(混乱);随机取牌/消耗:`TRUE_GRIT`(坚毅)、`THRASH`(痛殴)、`CINDER`(余烬)、`ANOINTED`(天选)、`HIDDEN_GEM`(未掘宝石)、`DRAIN_POWER`(能量汲取);随机药水:`ENTROPIC_BREW`(混沌药水)、`ALCHEMIZE`(炼制药水)、`COSMIC_CONCOCTION`(宇宙药剂);随机球:`CHAOS`(混沌) |
| 缺口及修改所在职责层 | 十二个来源的 Mirror、blind 采样与原生执行此前均已存在但未验收;本批修改仅 `BlindPolicyChecks.cs`、`BlindAuditChecks.cs` 与 25 个新夹具 |

| 验收层 | 异蛇之油 | 蛇行 | 混乱 | 坚毅 | 痛殴 | 余烬 | 天选 | 未掘宝石 | 能量汲取 | 混沌药水 | 炼制药水 | 宇宙药剂 | 混沌(球) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 已有语义 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 |
| blind 估值 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 | 已验证 |
| 原生执行 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 | 已验收 |

原版语义依据(0.111.0 反编译源逐条核对,Mirror 一致):

- 异蛇之油:使用后抽 7 张,全手牌(非 X 费)费用随机 0..3(`CombatEnergyCosts` 流,本回合或直到打出)。混乱:抽到的牌费用随机 0..3(`AfterCardDrawn`)。蛇行:附魔牌每次抽到时费用随机 0..3(整场)。
- 坚毅(未升级):获得格挡后随机消耗一张手牌;痛殴:攻击×2 后自动打出随机手牌攻击;余烬:攻击后随机消耗一张手牌;天选:随机取抽牌堆稀有牌补满手牌;未掘宝石:随机"重放"抽牌堆一张可用牌;能量汲取:攻击后随机升级弃牌堆可升级牌(均 `CombatCardSelection` 流;痛殴/狠揍的攻击选取走 `Shuffle` 流)。
- 混沌药水:填满空药水槽(`CombatPotionGeneration`);炼制药水:生成随机药水;宇宙药剂:N 张无色升级后直接到手牌(`CombatCardGeneration`)。混沌:引导随机类型球(`CombatOrbs`)。

观察边界:揭晓前不知道随机费用/选中的牌/药水种类/球的类型;结算完成后费用、牌堆、药水槽、球均为可见信息,轨迹在稳定观察上重新决策。两个费用随机化来源(蛇行/混乱)的随机性发生在**抽牌时**,由 EndTurn 后的下一回合决策消化。

选择规格:全部十二个来源无玩家选择页(坚毅升级版有,本夹具用未升级版)。

## 无头证据

共同配置:盲 seed 902828、3 外层/3 内层样本、Beam 12、300 节点/3000 ms、内层 128 节点、DOP1。证据根 `.local/blind-policy-20260929/`。

| 场景 | 检查数 | 重点 |
|---|---:|---|
| policy ×7(坚毅/余烬/能量汲取/天选/未掘宝石/混沌/痛殴) | 各 12 | 随机池卡进入完整矩阵、斩杀反例(痛殴除外)、生成后重观察 |
| policy 炼制药水 | 11 | 斩杀反例(保留选择断言——生成药水是赌注,平局由字典序裁决) |
| policy ×3(异蛇之油/混沌药水/蛇行/混乱——后者为 EndTurn) | 各 8 | 完整矩阵、隐藏未来独立性、随机化后重新决策 |
| audit ×13 | 各 26 | 四样本隐藏输入扰动 + 生成互异 + 原生完整 continuation/RNG 对账 |

合计新检查 **461 项**(policy 123 + audit 338)。既有场景回归:policy attack/skill/acid/stoke/gambler 13/14/9/9/10,audit attack/skill/orobic-acid/stoke 35/35/26/26,与既有记录一致。Mod/宿主 Release 构建 0 警告/0 错误;`REFACTOR_BOUNDARIES_OK search_files=241`。

## 结果与读法

斩杀反例(完整轨迹口径,数字为平均实际扣血):

| 场景 | 确定斩杀 | 随机池卡 | 结论 |
|---|---|---|---|
| 坚毅(敌 6/1 费) | 打击 0 | 坚毅 0(格挡挡住反击) | 同分,打击按稳定键胜出 |
| 余烬(敌 26/3 费) | 重锤 0 | 余烬 4 | 重锤保留;余烬 18 伤+2 费后本回合无法补足 |
| 能量汲取(敌 26/3 费) | 重锤 0 | 能量汲取 4 | 升级弃牌堆当回合不兑现 |
| 未掘宝石(敌 26/3 费) | 重锤 0 | 未掘宝石 4 | 重放目标限于抽牌堆打击,伤害不足 |
| 天选(敌 6/1 费) | 打击 0 | 天选 4 | 移入的稀有牌 0 费用打不出 |
| 炼制药水(敌 6/1 费) | 打击 0 | 炼制药水 4 | 生成的药水当回合救不了场 |
| 混沌(敌 6/1 费, Defect) | 打击 0 | 混沌 1.33 | 引导的球当回合提供了少量收益 |
| 痛殴(敌 20/3 费,结构性) | — | 痛殴 0 | 无斩杀反例设计;仅验证矩阵与重观察 |

审计的样本生成互异性(四样本互不相同):随机费用来自每张牌独立 `NextInt(4)`;随机消耗/打出/升级/重放各自由 `CombatCardSelection` 抽取互异候选;药水槽与球的类型逐样本不同。审计结果键现已覆盖手牌、弃牌/抽牌多重集(含升级与重放计数)、药水槽与球类型,使"结果落在手牌之外"的随机性也能被样本互异检查捕捉。

## 本批发现与修复

- **夹具与终局锁定的交互**(诊断后修复,非引擎缺陷):审计夹具默认敌方 1 HP,攻击型来源(余烬/痛殴/能量汲取)的攻击会当场击杀,模拟器在原版安全检查点锁定终局后跳过 OnPlay 余下的随机子效果,而原版会继续执行,造成系统性对账分歧。三个审计夹具改为满额敌方 HP 后全部通过。这确认了"攻击后随机子效果"的既有 Mirror 语义与原生一致。
- **审计结果键扩展**:`BlindAuditChecks` 的样本互异检查从"仅手牌"扩展为手牌 + 弃牌/抽牌有序多重集 + 药水槽 + 球类型,覆盖随机性落在手牌之外的来源(药水生成、球、升级、重放标记);对既有来源的多重集不受采样影响,无假分歧。

## 口径与预算

外层首动作价值为 `completed_policy_trajectory` 或 `observed_prefix_with_state_heuristic`;样本互异属审计证据。`--budget-ms` 软预算;全部场景在预算内完成,未出现 time_limit 截断。隐藏未来独立性以两个分别新建的决策器验证;搜索前后原生根状态不变。

## 未验证范围与下一步

- 本批未验收:随机目标通道(弹跳药瓶/狠揍——**弹跳药瓶尚无 Mirror**,需先补语义并做严格差分,属行为改动任务)、探寻打击(先随机后选,归"卡牌候选续搜评分"批次)、随机位置置入(羽化等)、魔球(多人随机,单人退化)、噤声恐惧等 Niche 流遗物、持续生成 Power 与怪物随机分支(21 个 RandomBranchState)。
- 混乱 Power 的注入仅验证抽牌费用随机化;其实际获取来源(敌方技能/事件)未在 blind 估值中验收。
- 本批无 `src/` 源码变化,不触发重新部署;部署状态继承交接文档第 9 节阻塞记录。

## 重跑

```powershell
$env:OFFLINE_HARNESS_BLIND_POLICY_CHECKS = '1'
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --request tools/OfflineSearchHarness/fixtures/blind-pool-cinder.json `
    --milestone M1 --blind-seed 902828 --beam 12 --nodes 300 --budget-ms 3000 --dop 1 `
    --workspace .local/blind-pool-repro/host-cinder --out .local/blind-pool-repro/cinder
Remove-Item Env:OFFLINE_HARNESS_BLIND_POLICY_CHECKS

$env:OFFLINE_HARNESS_BLIND_AUDIT = 'effects'
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --request tools/OfflineSearchHarness/fixtures/blind-snecko-oil.json `
    --milestone M1 --blind-seed 902828 --beam 12 --nodes 300 --budget-ms 3000 --dop 1 `
    --out .local/blind-pool-repro/audit-snecko-oil
Remove-Item Env:OFFLINE_HARNESS_BLIND_AUDIT
```

其余夹具:`blind-pool-true-grit/cinder/drain-power/anointed/hidden-gem/chaos/thrash/snecko-oil/entropic-brew/alchemize/slither/confused.json` 与 `blind-snecko-oil/entropic-brew/cosmic-concoction/true-grit/thrash/cinder/anointed/hidden-gem/drain-power/alchemize/chaos/slither-draw/confused-draw.json`。一次只启用一个专项开关。
