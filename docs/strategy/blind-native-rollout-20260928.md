# 未知 RNG 原生战斗无头实测（2026-09-28）

STS2 的原生核心支持无 UI 建局和推进。这里复用游戏 `TestMode`、原生 run 序列化、
事件/奖励同步器和动作队列，在普通 .NET 9 进程加载 `sts2.dll`；不启动 Godot 渲染进程。
未知 RNG 搜索沿用现有 CombatSolver 的 C# 战斗语义后端，无需迁移或重写卡牌规则。

## 输入与恢复

用户提供 profile3 的存档目录。本轮只读取其中一份 `current_run.save` 和五份单人 `.run`；
保存后端始终是游戏 `MemorySaveStore`，恢复副本及日志只写 `.local/blind-native/`。
原始存档、完整日志和个人绝对路径不进入源码提交。

参考 [SaveandLoad2-Public](https://github.com/iRyougi/SaveandLoad2-Public) 的公开材料后，
未取得其解析实现，本轮直接使用游戏自己的 `SerializableRun` 和 `RunState.FromSerializable`。

- 当前原生存档：完整规范化 run 恢复对账通过，再继续保存的地图坐标。
  只接受当前 schema；空遗物 `props:{}` 与省略字段等价，实际遗物计数仍须相等。
  明确列出的空 BaseLib/Loadout 扩展可忽略，非空或未知扩展拒绝。
- 历史 `.run`：按记录 seed、角色、幕和进阶原生建局，采用全解锁；
  选记录中的远古之民选项，按节点类型取最低列相邻坐标，核对实际遭遇，奖励选牌匹配实际候选。
  记录缺少完整坐标、解锁状态和逐张战斗操作，因此这些测试是历史指导前缀，不是精确人工回放。
- `latest.mcr`：头信息中的模型编号表 hash 为 `F986C260`，当前为 `5D828510`。
  没有原编号映射，明确拒绝解码；不作为人工对照证据。

## 执行方式

每次只执行聚合后的首命令，通过 `PlayCardAction`、`UsePotionAction` 或 `EndPlayerTurnAction`
进入原生队列。等待具体动作完成及稳定 Play 后重新捕获真实根、重新采样；
样本中的选牌、嵌套选择、下回合选择、预测遗物效果和续用路线均不部署。

共同搜索配置：3 samples、MeanScore、blind seed `902828`、未知堆顶前缀 0、Beam 12、
300 nodes、每样本发现预算 300 ms、DOP1、Smart、关闭 No-GC。
战斗停止上限为 200 决策、20 回合、请求总计 120 秒；不启用宽度组合。

原生战斗内选择显式采用 `FirstLegal`，只根据实际可见候选选最少张数；
它是选牌基线，不是采样选牌策略。默认 `Stop` 会在需要决策的选牌页报告未支持。
战后奖励卡牌通过历史匹配原生奖励候选。

```powershell
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --history-run <1790550943.run> --history-combats 2 --play-combat `
    --search-mode Coordinator --blind-samples 3 --blind-seed 902828 `
    --blind-aggregation MeanScore --beam 12 --nodes 300 --budget-ms 300 --dop 1 `
    --native-choice-policy FirstLegal --combat-timeout-seconds 120 `
    --out .local/blind-native/defect-complete
```

单份当前存档将历史参数换成 `--run-save <current_run.save>`。
依赖路径通过 `SteamRoot` 等构建参数覆盖，见[宿主指南](../OFFLINE_SEARCH_HARNESS.md)。

## 原生结果

以下均为完整原生战斗结束后的记录，不是样本预测值。伤害统计监听原生 `DamageReceivedEntry`
的实际未格挡扣血，包含战斗结束前的事件；不能用开始 HP 减结束 HP 替代，因为战后可能治疗。
遭遇与卡牌使用模型 ID，避免离线本地化键名冒充游戏显示名称。

| 来源 | 角色 | 遭遇 | HP | 实际扣血 | 人工历史扣血 | 回合 | 决策 | 秒 |
|---|---|---|---|---:|---:|---:|---:|---:|
| current_run.save | DEFECT，A10 第三幕 | SCROLLS_OF_BITING_WEAK | 74→59 | 15 | 无同根记录 | 3 | 14 | 35.12 |
| 1790550943.run，第 1 战 | DEFECT | SLIMES_WEAK | 60→55 | 5 | 0 | 3 | 12 | 12.25 |
| 同上，第 2 战 | DEFECT | SHRINKER_BEETLE_WEAK | 55→54 | 1 | 0 | 4 | 13 | 8.39 |
| 1790530010.run，第 1 战 | SILENT | TOADPOLES_WEAK | 56→54 | 2 | 0 | 5 | 20 | 15.41 |
| 同上，第 2 战 | SILENT | CORPSE_SLUGS_WEAK | 54→50 | 4 | 0 | 4 | 17 | 15.81 |
| 1790490061.run，第 1 战 | NECROBINDER | SLIMES_WEAK | 36→35 | 1 | 0 | 10 | 34 | 28.99 |
| 同上，第 2 战 | NECROBINDER | SHRINKER_BEETLE_WEAK | 35→35 | 0 | 0 | 5 | 14 | 5.22 |
| 1790528433.run，第 1 战 | IRONCLAD | SHRINKER_BEETLE_WEAK | 64→67 | 3 | 3 | 3 | 9 | 9.03 |
| 1790485989.run，第 1 战 | REGENT | CORPSE_SLUGS_WEAK | 60→45 | 15 | 15 | 5 | 17 | 13.47 |

总计 9 场 Victory、150 条实际执行命令、0 次用药；SILENT 两场共有 3 次原生战斗内选牌。
铁甲战斗实际扣血 3，战后回复后 HP 升到 67。前三角色部分战损高于人工历史；
当前短预算方案没有追平这些记录，且测试并非精确同根，不能称为质量等价。

原生奖励选择全部匹配：DEFECT `COOLHEADED` / `BOOST_AWAY`，
SILENT `CALCULATED_GAMBLE` / `FLICK_FLACK`，NECROBINDER `DEATHBRINGER` / `COUNTDOWN`，
IRONCLAD `POMMEL_STRIKE`，REGENT `BEGONE`。领奖后牌组保存在各自 `history-guidance.json`。

## 验证与边界

9 场结构化日志审计：150 selected = 150 executed，所有 `rootUnchanged=true`，
所有候选×样本评估矩阵完整，部署命令均无样本选择、嵌套选择、回合开始选择或预测遗物效果。
证据目录为 `current-final`、`defect-complete`、`silent-complete`、`necro-complete`、
`ironclad-complete` 和 `regent-complete`，均位于 `.local/blind-native/`。

最终收尾修正请求总时限：使用精确剩余 TimeSpan，建局、原生推进、领奖和下一场准备共用余量，
不在时限耗尽时额外给下一场 1 秒。64 samples、3 秒、要求两场的最小边界测试
返回 Held/TimeLimit，首场搜索中停止，0 条执行命令，未进入第二场；证据为 `deadline-final/result.json`。
这些是协作式截止边界，单个同步结算和日志收尾仍可能略超墙钟时限。
已通过场景没有重复运行。最终宿主 Release 构建 0 警告/0 错误，Windows 结构门禁通过。

本轮证明当前后端能进行未知 RNG 首命令采样、原生执行和跨回合重观察。
尚未跑整局通关、Mod 在线 UI、采样选牌策略、任意中途恢复、Linux 门禁或可见渲染验证。
9/9 是有限场景结果，不是统计胜率；完整样本胜利比例也不是经大量真实 rollout 校准的概率。
