# 全局可见字段白名单:怪物私有字段审计(2026-09-29 晚,架构第 1 步)

## 范围与方法

冻结根与盲采样中,怪物侧进入决策的私有信息全部来自两处:`BranchMonsterAi.Capture`(状态机快照)与散点的 `MonsterValueReader.ReadInt` / `GetMonsterInt` 私有字段读取。本审计逐字段枚举这些读取点,按"玩家可见(意图 UI/公开事件可推导)/静态游戏数据/待严格化"分类;第三方怪物的未知字段不在本表(既有硬门控显式拒绝,不做浅拷贝)。

## 字段清单与分类

| 捕获点 | 字段/数据 | 分类 | 依据 |
|---|---|---|---|
| `BranchMonsterAiState.Current` = `monster.NextMove` | 当前意图(招式) | **可见** | 意图直接显示在敌人头顶 UI |
| `BranchMonsterAiState.StateLog` = `machine.StateLog` | 已执行招式历史(ID 列表) | **可见** | 怪物已执行的招式是公开战斗事件;`CombatPredictionHistory` 同步记录 |
| `Static.AttacksByMove` | 各招式伤害/段数 | **静态游戏数据** | 图鉴可查的固定数值,非对局隐藏信息 |
| `Static.RandomBaseWeights` | 随机分支权重 | **静态游戏数据** | 固定权重(如雾菇 0.4/0.6),不随对局变化 |
| `Static.ConditionalSelections` | 条件分支的确定结果 | **可见** | 条件由公开状态决定(如外骨骼按槽位);以确定性求值固化 |
| `Static.StaticIntValues` | 静态内部值(逐成员登记) | **静态游戏数据** | `MonsterMoveEffects.CaptureStaticIntValues` 逐成员显式登记,无盲读 |
| `_curseOfKnowledgeCounter`(知识恶魔) | 诅咒知识计数 | **公开事件可推导,实现为直读** | 计数=挂上诅咒后的玩家出牌数,出牌是公开事件;当前实现直读私有字段 |
| `_extraMultiClawCount`(试验体) | 多重利爪追加段数 | **公开事件可推导,实现为直读** | 每次 MULTI_CLAW 打出公开 +1,可由历史推导;当前直读 |
| `_respawns`(试验体) | 复活次数 | **公开事件可推导,实现为直读** | 复活是公开事件 |
| `_turnsUntilSummonable` / `_callForBackupCount`(双尾鼠) | 召唤冷却/呼叫计数 | **公开事件可推导,实现为直读** | 召唤/呼叫均为公开事件且 `MonsterMoveEffects` 在模拟侧维护同名字段 |
| `TestSubjectBaseMultiClawCount` | 静态基础段数 | **静态游戏数据** | 固定初值 |

## 结论

1. **无"真正的隐藏未来"进入决策**:未来意图/分支选择/抽牌序已全部由盲采样替换;捕获的字段要么可见、要么静态、要么可由公开事件推导。上一份交接"相同显示意图不自动证明完整内部招式身份也可见"的疑虑,由本表逐字段闭环:招式身份 = NextMove = 意图 UI 所示同一状态。
2. **残余差距(唯一实质项)**:五个可推导计数的**实现方式**是直读私有字段,而非"由公开历史重建"。数值上等价(公开事件可推导),但严格白名单要求"实现只消费可见信息"。将其改造为历史重建(如诅咒计数改为历史扫描)是一个**独立的小行为任务**,需要历史事件完整性核对(挂诅咒时点、跨回合存续),本审计给出其完整字段清单与推导式。
3. 第三方怪物:未知 gameplay 字段已有硬门控显式拒绝(不浅拷贝、不放行),维持现状;新增怪物可见性随适配逐源登记。

## 对架构第 4 项其余步骤的影响

- 第 2 步(通用逐选项评分):不受本审计影响,独立推进。
- 第 3 步(复杂嵌套选择):不变。
- 怪物批次(第 3 项)的前置"可见性审计"由本文件完成——同族怪物扩展不再被此前置阻塞。

## 后续(独立小任务,待排期)

`_curseOfKnowledgeCounter`/`_extraMultiClawCount`/`_respawns`/`_turnsUntilSummonable`/`_callForBackupCount` 五个计数改为公开历史重建;验收 = 同起点重建值与直读值差分 + 历史扫描性能。
