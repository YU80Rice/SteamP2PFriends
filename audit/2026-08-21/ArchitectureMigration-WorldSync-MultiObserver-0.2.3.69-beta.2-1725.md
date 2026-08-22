# SteamP2PFriends 世界同步迁移蓝图：旧方案 -> Multi-Observer

- 日期：2026-08-21 17:25 (Asia/Shanghai)
- 对照基线：`0.2.3.69-beta.2`
- 上游规范：`audit/2026-08-21/Implementation-0.2.3.69-beta.2-1551.md`
- 性质：只读架构迁移设计；本轮未修改业务代码、未构建、未产出 DLL
- 核心裁决：不推倒已经双机验证的 P0-B/P0-D/P0-C-1/P0-E 语义；先包裹成新架构 Adapter，影子对照后逐域接管

## 一、迁移目标与不变式

新架构不是把所有对象设为 Active，也不是全局伪造 `Dedicator.IsDedicatedServer`。它要把旧补丁中混在一起的四个问题分开：

1. **世界生产**：权威世界中某区域、某代实体是否已创建；
2. **功能保活**：只要任一世界观察者需要，主机是否继续推进 AI、碰撞、触发器、计时和物理变换；
3. **逐玩家网络相关性**：哪一位观察者需要哪个区域，服务端已为他入队哪一代 baseline；实现 ACK 的领域另记录客户端已应用代。
4. **本地表现**：房主摄像机是否需要 Renderer、LOD、粒子和音频。

不变式：

- `FunctionalActive(region) = any WorldPresenceObserver demands capability in region`。
- `NetworkRelevant` 必须保留 `ObserverId -> region -> loadedGeneration`，绝不能用区域并集或引用计数代替。
- 待审核玩家已进入世界，必须是 `WorldPresenceObserver`；`GameplayAuthorized` 只限制玩法行为，不得决定世界加载、碰撞或快照。
- 所有跨会话状态必须带 `SessionEpoch`；重建代、实体代和普通状态顺序必须分离：`RegionGeneration` 只在权威区域/实体集合重建时递增，`EntityGeneration` 只在单实体销毁重建时递增，普通交互使用 `AuthoritativeStateVersion + DeltaSequence`。
- Adapter 变更必须实现 `Apply -> Verify -> Commit`，失败走 `Rollback/Restore`；不能先写“已处理”账本再尝试对象。
- 外部/网络回调只解析并投递带 epoch 和 observer sequence 的不可变事件；Observer registry、Demand、Lease 和所有 Unity 状态变更只能在已注册的游戏线程串行执行并 assert。锁内不得调用 Unity API。
- 旧版双机 PASS 只能证明旧 DLL 的已验证语义，新 Adapter 接管后必须用新哈希重新验收。

## 二、旧同步资产的真实价值

### 2.1 地图随机物品 P0-B

| 旧资产 | 已解决的问题 | 需保留的契约 | 现有边界 |
| --- | --- | --- | --- |
| `ItemManagerP0B6RegenerateOnLevelLoadedPatch` | `OnServerHosted` 时 `LevelItems.spawns=null`，改到 `onLevelLoaded level=2` 后全图 `generateItems` | Listen-Host 单一生产者；输入就绪后生产；失败不得伪装完成 | 一次性 bool 无 session/region generation；全图循环粒度过粗 |
| `AuthoritativeItemGenerationGatePatch` | 限制 `generateItems(x,y)` 的 Listen-Host 单写者 | 区域状态 `Uninitialized -> Preparing -> Committed`；异常回滚允许重试 | 已有私有 reset epoch/token 校验，但尚未绑定统一 `SessionEpoch`/地图身份，无法与 Observer/Lease registry 关联 |
| `ItemManagerRegionSyncPatch` | 只在原生 `onRegionUpdated` 的 Items 发送分支对真实远程玩家开门 | 保留 vanilla `askItems`、safe-region 和 loaded flag 路径；不手写 loaded flag；不全局伪造 dedicated | `LoadedRegion.isItemsLoaded` 是单 bool，不能表示世界已重建后的新代 |
| `InventoryWorldAuthorityProbe` / 物品诊断 | 把地图生成物与玩家/僵尸掉落物分叉 | 地图生成、掉落增量、拾取权威移除是三个事件域 | 不得为统一“物品生产”而再次引入二次生成 |

已有运行语义：历史证据记录了 `success=4096 fail=0`、非空 `askItems` 和客机 20 个 `ReceiveItems` 包；后续双机验收已将“地图随机物品生成/拾取/权威移除”关闭。这些是迁移回归基线，不是新 DLL 的自动 PASS。

### 2.2 僵尸 P0-D / P0-C-1 / P0-E

| 旧资产 | 已解决的问题 | 需保留的契约 | 现有边界 |
| --- | --- | --- | --- |
| `ZombieManagerP0DGenerateZombiesPatch` | 远程客机首次进入房主从未进入的 bound 时补生成 | 只有权威区域未生成且观察者首次需要时生成；保留 vanilla `PlayerCountInRegion` 更新 | 以 `isNetworked` 同时表示生命周期/网络，语义过载 |
| `ZombieManagerP0C1SendZombieStatesPatch` | Listen-Host 向远程玩家发送僵尸状态 | 每位观察者独立的相关性、序列和快照/增量 | 周期发状态不能证明 AI、碰撞、生命周期正在运行 |
| `ZombieLifecyclePatch v6.6` | 房主离开时，远程玩家仍占用 old bound 则阻止 `destroy`；房主返回时阻止重生成 | 区域的功能生命周期由所有世界观察者并集决定；最后一人离开才能释放 | 临时改写 `isNetworked` 是针对 vanilla 分支的兼容技巧，不应成为新架构真相源 |
| `PlayerCountInRegion -> regionsWithPlayers -> OnUpdate` | 原生已有的多玩家区域调度事实 | 作为 `Zombie FunctionalRegionDemand` 的首选底层信号，插件只校验/补齐 | 要校验待审核玩家、断线和异常销毁时计数不泄漏 |

已有运行语义：远区生成、战斗、击杀、房主返回不重刷已有历史双机证据。仍未整体关闭：服装/外观完整快照、跨重建代一致性和存档持久化。

## 三、旧补丁到新组件的迁移矩阵

| 旧实现 | 新架构归属 | 接管方式 | 退役门槛 |
| --- | --- | --- | --- |
| `ListenRegionSyncEligibility` | `WorldPresenceObserverSet` + `PerObserverRelevance` | 保留为 vanilla 发包入口适配层，输入改由 Observer registry 提供 | 所有领域不再自己遍历 `Provider.clients` 且与原生相关性对照一致 |
| P0-B-6 全图生成 | `ItemGenerationAuthorityAdapter` | 保留 `level=2/spawns-ready`；把一次 bool 升级为 `SessionEpoch + RegionGenerationState` | 同一会话每区域恰好一次 Commit，失败可重试，断线/换图无污染 |
| Item `askItems` 发送补丁 | `ItemObserverReplicationAdapter` | 保留 vanilla RPC；增加 `observer/region/worldGeneration/loadedGeneration` 账本 | 边界进出、重连、区域重建、两客机重叠均无缺包/重复生成 |
| P0-D 首次远区生成 | `ZombieRegionLifecycleAdapter.Acquire` | 以 `FunctionalDemand 0->1` 触发生成/激活，不再以房主进区为中心 | 新旧决策影子日志一致，首个观察者进入只生成一代 |
| P0-E v6.6 防销毁/防重生 | `ZombieRegionLifecycleAdapter.Release` + `ActivationLease` | `refCount>0` 禁止销毁；`0->release` 延迟并验证无交互/战斗事务 | 房主/客机任意移动顺序下，只有最后一名观察者离开才 destroy |
| P0-C-1 状态发送 | `ZombieSnapshotAdapter` | 每观察者维护 loaded bound/generation/sequence；只对该观察者选取快照或增量 | 丢包/重连/重建代变更时能确定性重发完整快照 |
| Issue #7 `AlwaysAnimate` | `LevelObjectBinaryStateAdapter` | 只对“动画驱动功能 Transform”租用 `TransformCommit/CollisionQueryable` | Adapter 事务化、实例代数、滞回释放和双机新哈希验证通过 |

## 四、抽象接口与状态契约

建议最小组件（逻辑接口，非本轮实现）：

```text
WorldPresenceObserverSet
  Upsert(observerId, player, sessionEpoch, position, lifecycleState)
  Remove(observerId, reason)

PerObserverRelevance<TRegion>
  Reconcile(observerId, desiredRegions, sessionEpoch)
  GetBaselineState(observerId, connectionGeneration, region)
  CommitBaselineEnqueued(observerId, connectionGeneration, region, regionGeneration, snapshotSequence)
  CommitBaselineApplied(ack) // 仅当领域实现客端 ACK

FunctionalRegionDemand<TRegion, TCapability>
  Acquire(observerId, region, capabilities) -> DemandToken
  Release(DemandToken) -> release candidate with hysteresis

ActivationLease<TEntity>
  Apply(snapshot) -> Verify -> Commit
  Rollback(failure)
  Restore(on final release / unload)

DomainAdapter
  ObserveVanillaState()
  PlanTransition(oldDemand, newDemand, authoritativeGeneration)
  Apply / Verify / Commit / Rollback / Restore
```

三类账本分别使用下列 schema，不强迫无关字段进入同一键：

```text
ProductionLedgerKey = SessionEpoch + WorldIdentity + Domain + RegionKey + RegionGeneration
FunctionalDemandKey = SessionEpoch + Domain + RegionKey + Capability + ObserverId/DemandToken
ReplicationLedgerKey = SessionEpoch + ConnectionGeneration + ObserverId + Domain + RegionKey
ReplicationValue = RegionGeneration + BaselineState + BaselineSequence + LastDeltaSequence
EntityStateKey = SessionEpoch + Domain + RegionKey + EntityStableId + EntityGeneration
EntityStateValue = AuthoritativeStateVersion
```

- 生产账本无 `ObserverId`：它描述权威世界。
- 发送账本必须有 `ObserverId`：它描述某一接收者。
- 功能需求保留每个 observer 的 token，区域反向索引可计算 refCount，不反过来伪造逐玩家发送账本。
- `BaselineRegionGeneration == RegionGeneration` 才能发增量；重建代不等时必须先发完整快照。普通拾取、受击或开关门只推进 `AuthoritativeStateVersion/DeltaSequence`，绝不递增 `RegionGeneration`。
- 默认迁移阶段复用原生可靠有序连接，账本名确为 `BaselineEnqueued`，只证明服务端成功构造/入队，不宣称客户端“已接收/已应用”。发送异常不 Commit；断线或 `ConnectionGeneration` 变化立即清除该观察者 baseline，不得跨连接继承。
- 对丢失 baseline 会造成不可恢复后果的领域，必须实现有界 `BaselineAppliedAck(observerId, sessionEpoch, connectionGeneration, regionGeneration, snapshotSequence)`，只在 ACK 后允许增量。ACK 按完整键幂等，过期/乱序/错连接代 ACK 丢弃；超时重发全量快照，达有界上限后进入 `ResyncRequired/Quarantine`，不得永久静默卡在无增量状态。

### 发布与恢复时序

1. 观察者进入相关区：先 Acquire 功能能力，Adapter Verify 权威世界就绪，再向该玩家发快照。
2. 状态交互：主机验证 gameplay authorization，应用权威事务，只更新 `AuthoritativeStateVersion/DeltaSequence`，然后向已建立同代 baseline 的相关观察者发增量。
3. 观察者离开：先移除其网络相关性，再 Release token；引用计数为 0 时进入滞回，不立即销毁。
4. 最终释放：确认无玩家、无进行中交互、无运动/乘员、无未发送权威事件，才 Restore 或允许 vanilla 销毁。
5. 单一观察者断线：只关闭该 observer/connection gate，递增该 `ConnectionGeneration + ObserverLifecycleSequence`，丢弃该观察者的队列/baseline，并在游戏线程释放其 DemandToken。不推进全局 `SessionEpoch`，不使其他在线观察者过期。
6. 停服/换图/返回菜单/插件卸载：以一个游戏线程事务先原子执行 `close global receive gate + publish next SessionEpoch`，使已入队旧 epoch 事件立即失效；再 drain/丢弃旧队列，按旧 epoch 租约的逆序 Restore。只有 Restore 成功的条目可删除；失败条目转入 teardown recovery/quarantine 通道，禁止无条件 clear。

### 线程与事件顺序

- Steam/网络/Provider 回调不得直接写 ObserverSet、Demand、Lease 或 Unity 对象，只构造不可变事件并入有界队列。
- 事件键包含 `SessionEpoch + ObserverId + ObserverLifecycleSequence + EventSequence`；游戏线程对重复序列幂等丢弃，对过期 epoch/连接代丢弃，对不可安全跳过的缺口进入 Resync。
- 游戏线程每帧在有界预算内串行处理；进入 `generateItems/generateZombies/Collider/Animation/Rigidbody` 前显式 assert game thread。
- 跨线程只读诊断通过不可变快照发布，不持有 registry 锁调用 Unity API。

### Teardown recovery/quarantine 通道

- 该通道与普通 observer event gate 分离，只接受已登记的旧 lease 管理操作，不接受 Acquire、新网络事件或业务状态写入。
- 重试携带 `OldSessionEpoch + OldWorldIdentity + Region/EntityGeneration + direct lease identity`，仍只在游戏线程执行。
- Restore 前必须验证目标仍属于旧 world identity 且 generation 一致；Unity fake-null/对象已确认销毁时可将该 lease 终结为 `TargetDestroyed`。
- 若新世界已复用相同区域/实体标识但 world identity 或 generation 不符，禁止写入，lease 保留为 release blocker 并输出诊断。
- recovery 重试次数、间隔和日志必须有界；release blocker 只能进入 `TargetDestroyed`、`ManualIntervention`、`ProcessExitEvidence` 等可审计终态，不得无限增长或静默丢弃。

## 五、实施路线（六个可回滚阶段）

### M0：基础设施影子模式

- 实现 ObserverSet、PerObserverRelevance、FunctionalDemand、Lease registry、SessionEpoch，但不改变 vanilla/旧补丁决策。
- 每次区域迁移输出 `legacyDecision/newDecision/difference/reason`，配额限制且可按 Case ID 聚合。
- fail-closed：基础设施异常时旧补丁继续权威运行，新层不写游戏状态。
- 验收：房主+两客机边界往返、待审核进世界、断线重连、返回主菜单，账本无负计数/泄漏/跨局污染。
- 单独验收“一名客机断线、另一名仍在线”与“整局换图/返回菜单”：前者 `SessionEpoch` 必须不变，后者必须证明所有旧 epoch 事件失效。

### M1：物品权威生产接管

- 新 `ItemGenerationAuthorityAdapter` 包裹 P0-B-6 的正确触发时序和 `AuthoritativeItemGenerationGate`。
- 先以区域状态记录全图执行，不立即改为懒生成；这样保留已验证行为。
- 单区域只在 `generateItems` 正常返回且验证 region 就绪后 Commit；Verify 不能用 `items.Count>0`，因为合法空生成区也必须 Commit。最小不变式是：调用正常返回、region 对象仍有效、token/epoch 仍为当前且生成副作用摘要自洽。异常不新增第四个枚举状态，而是回到 `Uninitialized` 并在独立 `RetryMetadata(attempt,lastError,nextDeadline)` 中记录可重试失败。
- 旧 P0-B-6 仅在新 Adapter 未启用/自检失败时 fallback；完成新哈希双机回归后才退役。

### M2：物品逐观察者复制接管

- 新 `ItemObserverReplicationAdapter` 监督 vanilla `askItems`，不自创第二套物品 RPC。
- 每个 Domain 在 capability manifest 明示选择 `ReliableEnqueueBaseline` 或 `AppliedAckBaseline`；不允许接入者在业务代码中临时判断。
- 把 `isItemsLoaded` 视为 vanilla 兼容位，真相源为 `ObserverLoadedGeneration`。
- 地图物品初始快照、地图物品拾取移除、玩家/僵尸掉落增量继续使用独立事件路径。
- 验收必须覆盖：客机单独进新城、双客机同区/异区、拾取后双端移除、断线重连不重生、掉落物不被地图生产门吞掉。

### M3：僵尸区域生命周期接管

- 新 `ZombieRegionLifecycleAdapter` 以原生 `PlayerCountInRegion/regionsWithPlayers` 为 FunctionalDemand 首选事实源，插件 ObserverSet 只作为对照和有界自愈依据。出现差异时先隔离该 bound 并记录完整玩家生命周期序列，不在同帧强改原生计数。
- `0->1` Acquire 负责权威生成/激活；`1->0` Release 经滞回后允许销毁。
- v6.6 临时 `isNetworked=false` 技巧在过渡期仍作为 vanilla 分支 Adapter，但需 `Apply/Restore` 事务和代数校验，禁止恢复到已换代的 region。
- 验收：房主远离、两客机交替占用、最后一人离区、战斗中跨边界、房主返回；僵尸实体 ID/数量/死亡状态不重刷。

### M4：僵尸快照和实体代接管

- 新 `ZombieSnapshotAdapter` 包裹 P0-C-1，每位玩家维护 `bound + generation + baselineSequence`。
- 实体稳定身份至少包含 `bound/entityId/entityGeneration/alive/version`；外观/服装作为完整快照字段验收，不由位置增量隐式继承。
- 只有在权威 Region 真实重建时递增 generation；正常远区保活不得人为换代。
- 这一阶段单独关闭“外观完整一致”；不把生命周期旧 PASS 当作外观 PASS。

### M5：通用 Adapter 接口固化与扩域

- 用已通过 M1-M4 的契约固化 `DomainAdapter` SPI，再迁移 LevelObject/Object、Resource、Structure/Barricade、Animal、Vehicle。
- 每个域只申明实际需要的 capability：`LogicTick/CollisionQueryable/TriggerEvents/TransformCommit/NavigationState/DynamicPhysics/NetworkSnapshot`。
- Renderer、LOD、粒子、灯光和本地音频仍由房主摄像机剔除；除非能力扫描证明工坊脚本错误地用它们驱动玩法。

## 六、各域后续预防策略

| 领域 | 新架构下的主要风险 | 接管策略 |
| --- | --- | --- |
| `LevelObject/ObjectManager` | 动画已播放但 Collider/NavMesh cut 未提交；状态 RPC 与物理不同步 | 二进制状态 Adapter 租用 TransformCommit + CollisionQueryable；事务后校验子 Collider/障碍 |
| `ResourceSpawnpoint` | 砍伐/采集动画、树桩/矿石状态、重生计时在房主远区停滞 | 资源 generation + health/depleted/respawn deadline；计时由权威时间驱动，表现动画只是投影 |
| `Structure/Barricade` | 门、柜子、发电机、陷阱可各自依赖 trigger/animation/tick；保存与网络状态分裂 | 实例稳定 ID + save version；按资产能力租用；复杂交互保持事务未完成禁止 Release |
| `Animal/Zombie` | AI tick、NavMesh、攻击 trigger、死亡掉落可分别停止 | AI functional demand 与 snapshot relevance 分轴；死亡事件先权威 Commit 再生成掉落增量 |
| `VehicleManager` | Rigidbody/WheelCollider 睡眠；乘员在车内却因区域 refCount 误释放；座位和车身代不一致 | 乘员/驾驶/运动为强 lease；仅事件唤醒刚体；车辆 generation + physics sequence + seat transaction |

## 七、诊断、回滚与发布门

每个域必须输出可关联但有界的事件：

```text
CaseId, SessionEpoch, Role, ObserverId, Domain, Region,
ConnectionGeneration, RegionGeneration, EntityGeneration, StateVersion,
DeltaSequence, BaselineAckOrEnqueue, DemandBefore/After, Capabilities,
WriterMode, WriterEpoch, QueueSequence, GameThreadId,
AdapterAction, ApplyResult, VerifyResult, RollbackResult,
VanillaDecision, CompatibilityFallback, EntityId(optional)
```

日志不代替验收。每次业务代码/DLL 变更后，需要：

1. 当前 DLL 的 SHA-256/MVID，主客机一致；
2. 同一 Case ID 的 Host/Client 完整诊断包；
3. 受影响域的新场景和历史已通过场景同时回归；
4. SP、Listen-Host P2P、U3DS 结论分开；U3DS 不能代替 Listen-Host 证据；
5. 代码接管、运行验证、部署一致、发布准入四个门分开判定。

兼容 fallback 原则：

- 影子阶段新层绝不修改世界；
- 接管阶段每个 mutation point 恰有一个 writer，旧补丁与新 Adapter 不得对同一 mutation point 双写；
- writer 所有权不按“整域 bool”切换，而是按下表的 mutation point 分配；每个活动会话开始前一次性冻结 `WriterMode + WriterEpoch`。
- 启动自检失败只能在该 mutation point 首个 Commit 前 fallback 到旧 writer。新 Adapter 已有任何 Commit 后，活动会话中禁止热切旧 writer；运行失败进入 Retry/Quarantine，不另启旧写入路径。
- 已 Commit 后运行失败必须保留账本与错误状态供重试，不得清空追踪后假定 Restore 成功。

Writer 切换不仅校验单点，还必须遵守依赖拓扑和原子 ownership bundle：

- `Item.GenerationBundle = GenerationGate -> GenerationTrigger`：先冻结并验证 Gate owner/readiness，后启用 Trigger owner；Trigger 永远不得绕过 Gate。
- `Zombie.AuthorityLifecycleBundle = Generation + LifecycleDestroy`：两点共享 `ZombieRegion.isNetworked` 协议，必须在会话开始原子选择 `AllLegacy` 或 `AllAdapter`，禁止任何新旧混合组合。
- 任一 bundle 成员发生首个 Commit 后，整个 bundle 在本会话禁止热切；运行失败整组进入 Retry/Quarantine。

### Writer ownership 矩阵

| Mutation point | Legacy writer/hook | New owner | 切换契约 |
| --- | --- | --- | --- |
| `Item.GenerationTrigger` | P0-B-6 `onLevelLoaded` Postfix | `ItemGenerationAuthorityAdapter.Trigger` | 从属 `Item.GenerationBundle`；Gate ready 后才启用，Harmony hook 可保留但非 owner 必须立即旁路 |
| `Item.GenerationGate` | `AuthoritativeItemGenerationGatePatch` | 过渡期仍由原 gate 执行，账本逐步改委托统一 registry | `Item.GenerationBundle` 拓扑根；只在 token/epoch 协议等价后转移 owner |
| `Item.Replication` | `ItemManagerRegionSyncPatch` 开放 vanilla `askItems` | `ItemObserverReplicationAdapter` | M2 才切换；M1 期间保留旧 replication，不影响 Generation owner |
| `Zombie.Generation` | P0-D Prefix | `ZombieRegionLifecycleAdapter.Acquire` | 从属 `Zombie.AuthorityLifecycleBundle`，只允许 AllLegacy/AllAdapter；同一 `0->1` 只能调用一次 `generateZombies` |
| `Zombie.LifecycleDestroy` | P0-E v6.6 old-bound protection | `ZombieRegionLifecycleAdapter.Release` | 从属同一 bundle，禁止与 Generation 异步 fallback，防止 refCount 与临时 `isNetworked` 交叉 |
| `Zombie.Snapshot` | P0-C-1 | `ZombieSnapshotAdapter` | M4 独立切换；不与 Generation/Lifecycle writer 据为同一 mode |

Harmony 层的注册不等于 writer 启用。过渡期可保留 hook 以便自检和影子日志，但每个 Prefix/Postfix 在第一次写入前必须校验已冻结的 `WriterMode/WriterEpoch`；非 owner 只读观测并返回。

## 八、建议的首个实施包

不建议下一版立即同时重写 Item 和 Zombie。第一个实施包应只包含 M0：

- ObserverSet 与待审核/已授权身份分离；
- 逐观察者 Item region 和 Zombie bound 相关性影子账本；
- Item authoritative generation 与 Zombie functional demand 影子账本；
- session reset、单 observer 断线和整局换图的分级清理契约；
- 新旧决策差异日志，不发新 RPC、不改对象状态。

这样能用一个 Debug 版先回答三个关键问题：观察者账本是否覆盖待审核玩家，Item 生产与逐玩家已加载是否被正确分离，Zombie 功能需求是否与 `PlayerCountInRegion` 一致。三项影子证据成立后，才进入 M1 的第一个业务写入。

## 九、本轮验证状态

- 业务代码修改：N/A
- Build/Test：N/A（本轮仅架构审计与迁移设计）
- 静态证据：已对照 Item/Zombie 现有补丁、新架构规范与历史审计报告
- 运行时证据：引用历史旧 DLL 双机结论，本轮未产生新运行证据
- 发布结论：不适用；未授权任何新版本发布

## 十、独立审计记录

### 第 1 轮：FAIL（已修订）

1. teardown 在 Restore 后才递增 epoch，无法拒绝已入队旧回调；已改为先原子关 gate 并发布新 epoch。
2. Region/Entity generation 与普通状态 version 混用；已拆为四个独立序列。
3. 缺少线程所有权；已规定外部回调只投递，游戏线程串行写入并 assert。
4. fallback 粒度过粗；已新增 mutation-point writer matrix 和“首个 Commit 后禁止热切”契约。
5. loaded generation 无法证明客户已应用；已区分 `BaselineEnqueued` 与有界 `BaselineAppliedAck`，并绑定 connection generation。

### 第 2 轮：FAIL（已修订）

1. 单一客机断线误推进全局 epoch；已拆为 observer/connection 断线契约与整局 teardown 契约。
2. 共享 `isNetworked` 的 Zombie Generation/Lifecycle 可被独立 fallback；已组成原子 `Zombie.AuthorityLifecycleBundle`，只允许 AllLegacy/AllAdapter。
3. 旧 epoch Restore 失败无合法重试通道；已新增只接受旧 lease 管理操作的 teardown recovery/quarantine 通道，并绑定 old world identity/generation。

### 第 3 轮：PASS

- 阻断项：无。
- 确认闭合：单 observer 与全局 epoch 分层、writer bundle 原子切换、旧 lease recovery 通道、代数/版本拆分、游戏线程所有权、baseline enqueue/ACK 语义和历史证据边界。
- 最终判定：迁移蓝图可作为 M0 实施的上游设计输入；不等于业务代码或运行时验收已完成。
