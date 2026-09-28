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

## 洗牌流与先随机后选取补(同日)

| 来源 | 结果 |
|---|---|
| `CATASTROPHE`(横祸:Shuffle 流随机取牌打出) | policy 12 项 + audit 26 项;抽牌堆逐张顺序逐样本互异(保序键),原生对账一致 |
| `REBOOT`(重启:手牌回抽洗牌再抽,0 费) | policy 12 项 + audit 26 项;洗牌后抽牌堆顺序逐样本互异,原生对账一致 |
| `SEEKER_STRIKE`(探寻打击:先随机候选再选一,1 费攻击) | **镜像不完整,真实语义缺口**:`SeekerStrikeOnPlay` 镜像只结算攻击,缺"随机候选 + 玩家选 1 张到手"主效果;其 policy"12 项通过"基于不完整镜像属无效验收,夹具与名单已回滚。修复需定义卡牌侧"从牌堆选牌"的选择来源(与发现族的生成候选不同),属行为改动任务 |
| `UPROAR`(骚动:攻击×2 + Shuffle 流随机取攻击自动打出) | **audit 已转绿(26 项)**:先前失败源于夹具敌方 HP 未设(默认 1,攻击×2 触发终局锁定——宿主指南既有教训)与随机候选唯一不可辨;足额敌方 HP + 多张互异攻击牌后原生对账一致,镜像无缺陷 |

审计样本结果键的抽牌堆部分改为**保序**,使洗牌效果可观测;对其它来源无假通过风险(互异检查是真差异检查)。探寻打击的失败为真实镜像缺口(见上);骚动的失败为夹具设计问题,已修复转绿。
