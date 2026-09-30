# 未知 RNG:随机怪物来源清单与分组代表验收(2026-09-29 晚)

## 来源清单(第 3 项前置交付)

### A. 招式随机分支(RandomBranchState,21 个)

执行时由 `MonsterAi` 流按权重随机选择分支;意图 forecast 由盲采样整体替换。按意图构成分组:

| 组 | 怪物(类名/中文名) |
|---|---|
| 攻/多段+增益 | DecimillipedeSegment(残杀千足虫)、FlailKnight(连枷骑士)、ScrollOfBiting(咬人卷轴)、SludgeSpinner(淤泥旋螺)、TheObscura(胧光怪)、Fogmog(雾菇) |
| 攻/多段+减益 | FossilStalker(化石追踪者)、HunterKiller(猎人杀手)、Mawler(蛮兽)、SoulNexus(灵魂枢纽)、SpectralKnight(幽灵骑士)、TwoTailedRat(双尾鼠)、Flyconid(飞蝇菌子) |
| 攻/多段+治疗/防御 | DecimillipedeSegment、SlitheringStrangler(蛇行扼杀者) |
| 多分支双组 | FakeMerchantMonster(商人？？？,2 个分支态)、Flyconid(2)、Inklet(墨宝,2) |
| 召唤链分支 | Fabricator(组装师,`RAND` 候选召唤)、Exoskeleton(外骨骼虫,槽位条件分支) |
| 纯攻击分支 | LeafSlimeS(树叶史莱姆小)、TwigSlimeM(树枝史莱姆中)、PhrogParasite(异蛙寄生虫) |

### B. 招式效果随机(6 个)

| 怪物 | 随机点 | 流 |
|---|---|---|
| 组装师 Fabricator | `FABRICATE_MOVE` 从候选随机召唤(MonsterAi.NextItem) | MonsterAi |
| 偷窃草蜢 ThievingHopper | `THIEVERY_MOVE` 随机偷玩家手牌一张(NextItem) | CombatCardGeneration |
| 噪音机器人 Noisebot | `NOISE_MOVE` 眩晕牌随机位置塞入抽牌堆(CardPilePosition.Random) | Shuffle |
| 灵魂异鱼 SoulFysh | 塞牌随机位置 | Shuffle |
| 无厌沙虫 TheInsatiable | 塞牌随机位置 | Shuffle |
| 结实的卵 ToughEgg | 孵化体随机生命(Niche.NextInt)——需召唤链构造 | Niche |

### C. 分组代表与结果(MonsterMoveChecks 严格差分:同起点同 RNG,模拟 vs 原生完整对账)

| 组 | 代表 | 夹具 | 结果 |
|---|---|---|---|
| 随机召唤 | Fabricator `FABRICATE_MOVE`(FABRICATOR_NORMAL 遭遇) | `monster-moves-random-batch.json` | **Passed**(召唤槽位 bot1-4、MonsterAi 随机候选差分一致) |
| 随机偷牌 | ThievingHopper `THIEVERY_MOVE`(THIEVING_HOPPER_WEAK) | `monster-moves-random-thief.json` | **Passed**(随机偷牌与攻击、手牌/牌堆/历史/RNG 差分一致) |
| 随机塞牌 | Noisebot `NOISE_MOVE` | — | **经召唤链覆盖**:噪音机器人仅作为组装师召唤物出现(无独立遭遇);其塞牌招随召唤链后续批验收 |
| 随机生命 | ToughEgg | — | 后续(需召唤链构造孵化场景) |
| 随机分支 | Fogmog `SWIPE_MOVE`(FOGMOG_NORMAL,执行链 FollowUp→BRANCH 随机) | `monster-moves-random-branch.json` | **Passed**(重试即恢复,先前为无头基础设施瞬态) |

夹具位于 `tools/OfflineSearchHarness/fixtures/monster-moves-random-*.json`;运行:

```powershell
pwsh tools/run-unattended-test.ps1 -ScenarioId "MONSTER-RANDOM-BRANCH" `
  -MonsterMoveChecksPath "tools/OfflineSearchHarness/fixtures/monster-moves-random-branch.json" `
  -EncounterId "FOGMOG_NORMAL" -Sts2GameRoot "D:\SteamLibrary\steamapps\common\Slay the Spire 2" `
  -RitsuWorkshopRoot "D:\SteamLibrary\steamapps\workshop\content\2868840\3747602295" -CleanupInstanceOnExit
```

## 可控敌方意图机制说明

既有 `MonsterMoveChecks` 无人测试即"可控敌方意图夹具":按 `monsterId`+`moveId` 强制敌方招式,同起点同 RNG 状态下模拟执行与原生执行**完整状态/牌堆/历史/RNG 严格对账**;随机招的对账等于验收"模拟镜像与原生在相同随机序列下逐位一致"。本批验证了该机制对随机召唤/随机偷牌直接适用;分支招需从**进入分支的前一招**强制(分支经 FollowUp 链到达,直接强制分支态会因状态机上下文缺失失败——外骨骼 `RAND` 教训)。

## 怪物私有 AI 可见性前置(现状)

盲采样对怪物 AI 的处理(`BranchMonsterAi.Capture` 存 `NextMove`/状态机 `StateLog`/私有计数,盲采样整体替换 MonsterAi 流并重建未来意图)已由 121 项采样合同覆盖隔离性;**字段级"私有状态是否可由公开历史推导"的白名单核查仍属架构任务**(第 4 项第 1 步),本批的严格差分不依赖该白名单(对拍允许读取真实 RNG)。

## 后续

- ~~雾菇分支差分重跑(基础设施瞬态)~~ **已完成**;
- ~~同族扩展:每组其余怪物按代表夹具形状逐源验收~~ **已完成(2026-10-01)**:21 个分支怪 + 6 个效果随机怪全部严格差分通过,夹具 `monster-moves-random-expansion-a..f.json`(注入宿主默认遭遇,条件分支怪走自然遭遇);**零镜像缺口**——既有 BranchMonsterAi/MonsterMoveEffects 镜像覆盖全部来源。教训:①条件分支怪(外骨骼按槽位、组装师)不能裸注入——初始滚动在条件分支无合法后继(`No valid next state found`),须走自然遭遇;②蜈蚣段是抽象基类,夹具需具体段类名(DecimillipedeSegmentFront/Back/Middle);③组装师 DISINTEGRATE 是攻击招,不能沿用召唤招的零掉血预期;④无头宿主会枚举整个游戏目录做快照,陈旧 `.combatsolver-precombat/process-*` 快照(150 个/约 15GB)与 worker 自身的目录 GC 竞态会打断快照(`Get-Item` 找不到正被清理的文件),运行前先清理陈旧快照;⑤上一实例进程未完全退出会让下一次运行"准入超时"或"启动的不是实例私有游戏",连续运行需等待/清理残留进程。
- ~~噪音机器人/灵魂异鱼/无厌沙虫的塞牌招随召唤链批;结实的卵随孵化链批~~ **已完成(2026-10-01)**:Noisebot NOISE_MOVE、SoulFysh DE_GAS_MOVE、TheInsatiable THRASH_MOVE 直接注入宿主即通过(塞牌为 Shuffle 流,无需召唤链);ToughEgg HATCH_MOVE 直接注入+强制孵化招即通过(Niche 流孵化生命差分一致)。
- 怪物私有 AI 可见性白名单(与架构第 4.1 合并,**已完成**,见[可见字段白名单](blind-visibility-whitelist-20260929.md))。

## 更正(同晚,ai-counter 干净重实现后)

雾菇分支差分与知识恶魔 CURSE 差分此前连续失败,根因是**一次 sed 连锁编辑事故损坏了 BlindAuditChecks.cs**(多行诊断删除/插入错位导致方法体破损),损坏状态下产生的"大帽子回合 2 分歧"观察同样是伪象——还原到已提交版本后用编辑工具重放变更,三模式全部通过:雾菇分支 **Passed**、CURSE 重建差分 **Passed 3 项**(重建值==直读值==1,模拟同步推进)、大帽子/橙色团块 initial-relic 全 6 项含 Stamp 恢复通过、攻击药水回归 35 项。CURSE 差分需要给 ChooseCurse 的选择页推 FirstVisibleCardSelector(min=0 可跳过页)——该页在离线宿主直接建 UI 会 NRE。

教训(新增至交接第 5 节):**连续失败时先怀疑工具损坏再怀疑语义**——本轮三次"语义发现"(大帽子分歧、骚动/探寻打击失败模式漂移)最终都归因于同一损坏文件;发现连续异常应立即 `git diff` 核对工具文件完整性。
