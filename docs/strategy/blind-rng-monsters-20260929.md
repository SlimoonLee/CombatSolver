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
| 随机分支 | Fogmog `SWIPE_MOVE`(FOGMOG_NORMAL,执行链 FollowUp→BRANCH 随机) | `monster-moves-random-branch.json` | **待重跑**:无头基础设施瞬态("not the instance's private game"),非语义失败;夹具已入库 |

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

- 雾菇分支差分重跑(基础设施瞬态);
- 同族扩展:每组其余怪物按代表夹具形状逐源验收;
- 噪音机器人/灵魂异鱼/无厌沙虫的塞牌招随召唤链批;结实的卵随孵化链批;
- 怪物私有 AI 可见性白名单(与架构第 4.1 合并)。
