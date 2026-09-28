# 未知 RNG:到手牌家族收尾与激发球卡验收(2026-09-29 第五批)

## 批次概览

| 项 | 内容 |
|---|---|
| 批次 / 行为基线 | 2026-09-29 第五批;**零行为源码改动**(仅审计前缀泛化与夹具) |
| 来源 | `JACKPOT`(大奖:攻击 25 + 生成等量 0 费角色牌到手牌,有放回抽样)、`MANIFEST_AUTHORITY`(君权自授:格挡后随机 1 张无色到手牌)、`DUALCAST`(二连)、`MULTI_CAST`(多连)、`QUADCAST`(四连)——后三者激发前方球,闪电球激发为随机目标 8 伤 |
| 验收层 | 五源均为已有 Mirror + blind 估值已验证 + **原生已验收**(policy 9/12 项,audit 26 项 ×5,合计 148 项) |

审计前缀泛化:二连需要先引导一颗闪电球——`Prefix(source, first)` 按来源构造上下文命令(球状闪电 → 末尾 EndTurn 触发被动;二连 → 前置球状闪电并补全逐实例键与目标),原生对账同步执行完整前缀命令序列。球状闪电回归通过(26 项)。

## 证据要点

- 大奖:四样本各生成 3 张互异 0 费牌(如 SPITE/SPITE/OFFERING、ANGER/BLOODLETTING/OFFERING、BULLY/PRIMAL_FORCE/BATTLE_TRANCE、BRAND/RAGE/BATTLE_TRANCE),确认有放回抽样被采样覆盖;敌 40 HP 避开终局锁定;原生对账一致。
- 君权自授:PROLONG/STRATAGEM/THINKING_AHEAD/SALVO 互异;policy 中重锤零损击杀胜出(领先君权自授)。
- 二连/多连/四连:激发随机目标逐样本可辨(敌方 HP 如 15/22→23/14→7/30、23/32→31/24→15/40、7/32→15/24→23/16),双命令前缀原生对账一致。

## 边界记录(未验收与理由)

| 来源 | 状态 | 理由 |
|---|---|---|
| LARGESSE(慷慨捐助) | 不适用 | 目标为 AnyAlly,单局无队友,夹具无法构造合法目标;属多人语义,按仓库单人硬约束不做分支 |

## 疯狂科学骑手补充(同日)

骑手经 `EnumMembers` 注入(`TinkerTimeType=Skill`、`TinkerTimeRider=Chaos`)验收:policy 12 项(重锤零损击杀反例胜出)+ audit 26 项(Chaos 骑手的随机生成五样本互异:BLOODLETTING/UNMOVABLE/FEEL_NO_PAIN/HELLRAISER/HOWL_FROM_BEYOND,格挡 8 与免费入手一致),原生完整对账通过。其余骑手(Sapping/Violence/Choking/Energized/Wisdom/Expertise/Curious/Improvement)均为确定效果,不属随机因素验收范围;`MockedChaosCard` 测试后门未使用。
| MAD_SCIENCE(疯狂科学) | 已验收(Chaos 骑手) | 其余骑手为确定效果,见骑手补充章节 |
