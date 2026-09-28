# 未知 RNG:回合开始时序族验收(2026-09-29 晚)

## 批次概览

| 项 | 内容 |
|---|---|
| 批次 | 2026-09-29 晚(第 2 项路线);工具层改动(`BlindAuditChecks` 新增 `initial-relic` 审计模式),零行为源码改动 |
| 来源 | `BIG_HAT`(大帽子:第一回合回合开始生成 2 张互异虚化角色牌到手)、`ORANGE_DOUGH`(橙色团块:生成 2 张互异无色到手)、`TOOLBOX`(工具箱:第一回合抽牌前三选一,**引用既有原生验收**) |
| 验收层 | 三源均为镜像已在位 + **原生已验收** |

## 验收形状:为什么不用盲 policy/audit 四样本流

三者的随机触发都发生在**盲根捕获(玩家第一回合 Play)之前**——根捕获时手牌已含生成牌。可验收面是:
1. **开局状态**:根手牌中生成牌恰好 2 张、互异、归属正确池(大帽子=虚化,橙色团块=无色池);
2. **两回合对账**:EndTurn 原生推进第二回合,模拟与原生全量状态一致(Stamp),证明触发只发生一次、盲根内不重复消费、RNG 连续;
3. **后续回合计数**:生成牌跨回合区(手/抽/弃)计数恒定——无色存活=2;**虚化回合末消耗=0**(大帽子的虚化牌按关键字回合末消耗,计数 0 是正确原版行为)。

工具箱的选择页验收引用既有 `INITIAL-TOOLBOX-INFUSED-CORE`(原生 ChooseCard 页+setup choice 断言,`coverage/unattended/initial-toolbox-infused-core.json`);本日 src 改动(BuildSeekerSpec 排序、盲药水核算、弹跳药瓶镜像)不触及该场景输入,既有通过记录继续有效。

## 结果

| 场景 | 结果 | 关键证据 |
|---|---|---|
| `blind-bighat.json`(NECROBINDER) | Passed 6 项 | 开局 2 张互异虚化牌;第二回合模拟/原生虚化计数均=0(消耗);两回合 Stamp 对账一致 |
| `blind-orange-dough.json`(IRONCLAD) | Passed 6 项 | 开局 2 张互异无色牌;第二回合模拟/原生无色计数均=2(存活);两回合 Stamp 对账一致 |
| Toolbox | 引用既有 | `initial-toolbox-infused-core.json` 原生验收(2026-08-30 批次) |

## 过程教训(已并入新交接第 5 节)

1. **宿主注入会清掉开局生成**:无人测试/宿主注入的 `clearPlayerPiles` 会把回合开始生成的牌一起清掉——此类夹具**不能清牌堆**,断言靠池类型过滤。
2. **IRONCLAD 池无虚化牌**:大帽子的原版守卫(`readOnlyList.Count > 0`)在 IRONCLAD 下不生成——夹具改用 NECROBINDER(池含 5 张虚化)。
3. **虚化=回合末消耗**:跨回合存活断言不适用虚化生成牌,按来源区分预期计数。

## 重跑

```powershell
$env:OFFLINE_HARNESS_BLIND_AUDIT = 'initial-relic'
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll `
    --request tools/OfflineSearchHarness/fixtures/blind-bighat.json `
    --milestone M1 --blind-seed 902828 --beam 12 --nodes 300 --budget-ms 3000 --dop 1 `
    --out .local/blind-policy-20260929/audit-bighat-final
Remove-Item Env:OFFLINE_HARNESS_BLIND_AUDIT
```

`blind-orange-dough.json` 同式。工具箱引用 `INITIAL-TOOLBOX-INFUSED-CORE` 既有记录。
