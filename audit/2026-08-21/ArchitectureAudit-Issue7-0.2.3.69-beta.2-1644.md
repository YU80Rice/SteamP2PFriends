# Issues #7 修复与历史方案架构审计

- 日期：2026-08-21 16:44 (Asia/Shanghai)
- 审计基准：`Implementation-0.2.3.69-beta.2-1551.md`
- 审计对象：`.69` `LevelObjectRemoteCollisionPatch`、Issue7 诊断链、`.62/.63` 远区碰撞历史方案
- 性质：只读审计；本轮不修改业务代码、不构建新 DLL、不改变 issues 状态

## 一、裁决摘要

| 层面 | 判定 | 说明 |
| --- | --- | --- |
| 新多观察者规范 | PASS | 三轴分离、逐观察者网络账本、功能区域需求和能力租约方向成立。 |
| Issue7 根因归纳 | PASS | `.68` 双端证据与 U3-SDK 源码支持“主机远区 Legacy Animation 被剔除，权威 Collider 姿态滞留”。 |
| `.69` 定点修复方向 | CONDITIONAL PASS | 对远区 IOBS 补 `AlwaysAnimate` 与保存/恢复原值是正确能力，但尚无当前哈希双机运行验证。 |
| `.69` 作为新架构首个 Adapter | FAIL | 缺少事务化 Acquire/Verify/Rollback、完整观察者采集、稳定身份/代数、滞回 Release 和最小功能组件边界。 |
| issues#7 运行关闭 | OPEN | `.69` DLL 尚无同哈希 Host/Client 行为证据，不得标记为已修复。 |

## 二、阻断项

### A1 - Apply 失败后仍激活根节点，可能提交半完成保活

- 严重性：P1 / 阻断 `.69` 迁移为正式 Adapter
- 位置：`Patches/LevelObjectRemoteCollisionPatch.cs:155-159,265-306`
- 事实：`ApplyRemoteAnimationPolicy` 捕获异常后只记录一次警告，不向调用方返回失败；调用方随后无条件执行 `gameObject.SetActive(true)`。
- 风险：多个 Animation 中部分已改为 `AlwaysAnimate`、部分失败时，根节点和所有功能/非功能组件仍被激活。对于碰撞由失败 Animation 驱动的工坊门，原问题仍可复现，同时日志只显示策略被跳过。
- 规范冲突：新规范 2.4 要求 Apply/Verify 事务化，失败时逆序撤销本轮修改、保留旧租约并禁止发布半完成状态。
- 修复要求：`Apply` 返回结构化结果；记录本轮成功修改项；任一关键功能 Animation 失败时回滚本轮 TransformCommit/culling 修改，不得把 IOBS 动态能力租约标为成功。若该实体已有 Static `CollisionQueryable` 租约，应保留既有根状态并隔离动态交互能力，不能为回滚动画而破坏普通碰撞。

### A2 - 全局 Restore 失败仍清空追踪表，永久丢失恢复重试能力

- 严重性：P1 / 生命周期阻断
- 位置：`Patches/LevelObjectRemoteCollisionPatch.cs:348-383`
- 事实：`RestoreAllRemoteAnimationPolicies` 对单项 setter 异常只告警，但 `finally` 无条件 `RemoteAnimationCulling.Clear()`。
- 风险：活对象若恢复失败，将继续保持 `AlwaysAnimate`，而插件已丢失其原值与待恢复身份；下一次 stop/unload 无法重试。会话切换后可能形成性能或行为残留。
- 规范冲突：新规范 2.5 要求恢复原值、失败项保留/隔离，不能清除未成功释放的租约。
- 修复要求：仅删除成功恢复或已确认销毁的条目；失败项保留为 quarantined lease，输出剩余数量，并在 stop/unload 的最终恢复阶段重试。

### A3 - 功能代码重新使用 Unity `== null`，违反已证实的早期生命周期防线

- 严重性：P2 / 生命周期合规缺口（当前位于 try/catch 内，不是已证实的未隔离崩溃路径）
- 位置：`Patches/LevelObjectRemoteCollisionPatch.cs:279-280,316-318,323-324,358-360`
- 事实：`.62-1606` 的运行证据已证明早期 Transform/Component 上的 Unity `Object.op_Equality` 可能抛出 `NullReferenceException`；当前新增 Animation 恢复代码再次使用 `animation == null`、`transform == null`。`LevelObject` 本身不是 UnityEngine.Object，不应与该风险混写。
- 风险：区域刷新、断线、世界退出或销毁竞态中，恢复链可能异常退出；对象级 Restore 会保留条目，但 ResetAll 随后又可能触发 A2 丢失追踪。
- 修复要求：区分 CLR null 与 Unity fake-null。入口先用 `ReferenceEquals`，需要判断 destroyed state 时使用异常隔离的专用 helper，不能散落重载 equality。

### A4 - 覆盖账本先提交，单实体失败后不会自动重试

- 严重性：P1 / 一致性阻断
- 位置：`Patches/LevelObjectRemoteCollisionPatch.cs:415-417,454-463,557-570`
- 事实：`RebuildCoverageAndRefresh` 在刷新实体之前就把 `RemoteCoverage` 替换为 `NextRemoteCoverage`；`RefreshObjectsInRegion` 对单对象异常只告警，不返回失败或登记 dirty lease。下一帧远端玩家区域字典相等时直接早退，不会再次刷新失败对象。
- 风险：Acquire 失败的实体被账本视为已覆盖，Release 失败的实体被视为已释放。除非玩家再次跨区或会话重置，否则该对象永久处于错误状态；ResetAll 的 root 刷新若失败也没有独立根状态租约可恢复。
- 规范冲突：需求/租约状态先于实体 Verify 成功被提交，违反 Apply/Verify/Rollback 和失败隔离重试契约。
- 修复要求：先计算 demand delta，逐实体 Apply/Verify；成功后提交 lease/coverage generation，失败项进入有界 dirty queue 并低频重试。区域需求可先更新为 desired state，但不得把 entity applied generation 一并推进。

### A5 - 不完整观察者采集会被误判为离场并立即释放区域

- 严重性：P1 / 观察者生命周期阻断
- 位置：`Patches/LevelObjectRemoteCollisionPatch.cs:396-426,474-519`
- 事实：每帧采集先清空 `NextRemotePlayerRegions`；当 `Provider.clients` 项为空、`player/channel/transform` 暂不可用、坐标读取异常，或遍历期间列表发生变化时，`TryGetRemotePlayerRegion` 只返回 `false`。调用方仍将这个不完整快照覆盖到 `RemotePlayerRegions`，并立即执行覆盖 Release。
- 风险：有效客机仍在世界时，短暂采集缺口会被解释为观察者离场，撤销该区域的 `AlwaysAnimate` 与根节点保活；门动画过渡或碰撞提交可再次在权威端冻结。下一帧即使恢复采集，也已经产生不必要的 Release/Acquire 抖动。
- 规范冲突：新规范要求 `WorldPresenceObserverSet` 由明确生命周期和权威位置维护，周期扫描只能低频自愈；不完整采样不能证明观察者缺席。
- 修复要求：采集结果必须携带 completeness/generation。采集不完整时可保守更新已成功捕获的观察者，但禁止 absence removal；保留 last-known world presence，并记录限流的 incomplete/recovery 事件后有界重试。

## 三、架构准入与演进缺口

### B1 - 强制全部子 Legacy Animation，不是最小能力租约

`ApplyRemoteAnimationPolicy` 使用 `GetComponentsInChildren<Animation>(true)`，会把 BinaryState 根下装饰、灯光或其他非碰撞动画全部设为 `AlwaysAnimate`。U3DS 可以在剥离 Renderer/Audio/粒子后这样做；图形化 Listen-Host 不能直接照搬。IOBS 源码已经持有明确的 `animationComponent`（由 `interactabilityChildPathOverride` 或 `Root` 解析），Adapter 应只租用该功能 Animation，必要时再由资产能力扫描扩展。

判定：阻断正式 Adapter 准入；不阻断继续把 `.69` 当作受限运行候选收集证据。

### B2 - 根节点激活扩大了功能边界

`.63` 与 `.69` 都通过 `root.SetActive(true)` 保留碰撞。Renderer 虽仍由原版禁用，但根下脚本、AudioSource、Trigger、粒子、NavMesh 组件和工坊自定义行为会一并进入 Unity 生命周期。该方案已验证普通家具碰撞，但不能成为所有 LevelObject 的通用保活策略。新 Adapter 需要记录并启用最小功能节点；无法拆分的资产只能走显式兼容模式和预算。

### B3 - 没有滞回和未决事务 Release

`RebuildCoverageAndRefresh` 在区域集合变化时立即释放退出区域，没有 2-5 秒滞回，也不检查门动画是否仍在播放。玩家跨区域边界或最后一位观察者断线时，当前开闭过渡可能被立刻恢复 culling 并停用根节点。应至少等待过渡结束/提交最终权威姿态，再恢复策略。

判定：阻断正式 Adapter 准入。

### B4 - 身份键没有实例代数

区域需求使用 `SteamID -> encoded region`，Animation 租约直接以 Unity 对象引用为键；没有 `region/index + instanceID/GUID + world generation`。世界重载、工坊对象替换或销毁重建时无法明确租约属于哪一代实体。新 Adapter 必须把稳定对象身份与会话/实例代数绑定。

判定：阻断正式 Adapter 准入。

### B5 - 诊断只能证明 Collider 已启用，不能证明 Collider 姿态正确

`Issue7ObjectBinaryStateDiagnosticPatch.DescribeActivation` 记录 Collider 数量、enabled/activeInHierarchy 和 `AlwaysAnimate` 数量，但不记录功能 Animation clip/normalizedTime、Collider bounds/Transform 或状态提交序列。因此 `.69` 日志即使出现 `alwaysAnimate=1`，仍不能单独证明 PhysX 已得到打开姿态。QA 的实际穿越行为是必要证据；下一版诊断应增加限流的功能 Animation 状态、关键 Collider bounds/局部与世界姿态、lease Acquire/Release、observerId/loadedGeneration、原 culling 值、残余租约数，以及 Apply -> animation sample -> physics commit 的顺序指纹。

### B6 - 观察者计算是每帧重建并集，不是通用需求服务

当前实现每次 `LevelObjects.Update` 扫描 `Provider.clients`，重建 `RemotePlayerRegions` 和区域并集。只有采集完整时，集合重算才在重叠玩家场景下语义正确，并包含已进入 `Provider.clients` 的待审核世界玩家；当前没有采集完整性、领域能力引用计数、事件驱动 Acquire/Release、滞回或预算。应保留其位置派生和差集思想，迁移到 `WorldPresenceObserverSet + FunctionalRegionDemand`，周期扫描仅作低频自愈；不完整采样不得触发 absence removal。

### B7 - 网络轴未被破坏，但尚未形成显式契约

`.69` 没有重写 Binary State RPC，这一点正确；`.68` 已证明 request、authority、recipient 与 client receive 链完整。迁移 Adapter 时应声明 Object 领域坐标、`askObjects` 首次快照、每玩家 `isObjectsLoaded`/generation 和 `GatherRemoteClientConnections` 增量目标，不应把 `RemoteCoverage` 并集拿去替代逐观察者网络账本。

## 四、历史方案复盘

| 阶段 | 方案 | 当时结果 | 新架构裁决 | 处置 |
| --- | --- | --- | --- | --- |
| `.62-1534` | Transpiler 改写 dedicated collision 分支 | 静态审核 PASS；随后实机暴露 Unity null 崩溃/路径未生效 | FAIL | 淘汰 IL 改写；保留“物理与渲染分离、P2P 双门控、区域差集、fail-closed”原则。 |
| `.62-1606` | ReferenceEquals + 无覆盖早退 + 异常回退 | 修复补丁自身早期生命周期异常 | PASS（防御规则） | 必须沉淀为所有 Adapter 的 Unity 对象访问规范；`.69` 当前有回归。 |
| `.62-1642` | `Priority.Last` Postfix 恢复 collider-bearing root | 编译/审核 PASS；随后同一技术路径由 `.63` 双机验证 | PARTIAL | 比 Transpiler 稳定；保留原生更新后补齐的顺序，但根节点全开只能作为兼容降级。 |
| `.63-1823` | 发布并双机验证普通家具远区碰撞 | 当前范围运行 PASS | PASS（受限基线） | 必须保留为回归底线；不得外推到动态门、资源或载具。 |
| `.68-1246` | Debug request/authority/receive/collision/correction 证据链 | 成功排除 RPC 丢失，定位权威碰撞姿态 | PASS（诊断资产） | 保留真实 U3 入口和关联链；修复 activation NRE，并补 Collider 姿态/物理提交证据。 |
| `.69-1536` | IOBS Legacy Animation `AlwaysAnimate` + 原值恢复 | 静态/结构测试 PASS，运行待验 | CONDITIONAL PASS | 保留能力方向；修复 A1-A5 与 B1/B3/B4 后迁移为 `LevelObjectBinaryStateAdapter`，不再扩展现有大类补丁。 |

## 五、推荐迁移蓝图

1. 先实现只读 `WorldPresenceObserverSet`、`PerObserverRelevance`、`FunctionalRegionDemand` 和带 generation 的 `ActivationLease`；与现有 RemoteCoverage 并行比对，不改变行为。
2. 把普通家具碰撞声明为 `CollisionQueryable` 兼容 Adapter；保留 `.63` 当前行为作为回归基线，但记录根激活带来的非功能组件预算。
3. 把 IOBS 拆为 `LevelObjectBinaryStateAdapter`：只解析功能 Animation、Collider/Barrier、Nav cut 和 Toggle；Acquire 必须返回成功/失败与回滚记录。
4. Apply 顺序固定为：保存原值 -> 最小功能节点 -> 从 `isUsed/state[0]` 解析权威目标 -> 功能动画策略 -> 通过 U3 原生且副作用明确的状态恢复入口或 Adapter 专用提交逻辑重放 -> 必要物理同步 -> 验证 Collider 姿态。不得未经审计直接调用 public `updateState`，因为它还会触发 `onStateInitialized`；当前 U3-SDK `OnEnable -> updateAnimationComponent(true)` 可作为 BinaryState 瞬时动画重放依据，但仍需验证 Collider/Nav 状态。
5. Release 增加观察者引用计数、边界滞回和 animation-in-flight 门；成功恢复后才删除租约，失败进入隔离重试。
6. 网络仍复用 ObjectManager 原生 RPC；Adapter 只消费逐观察者 loaded generation，不自行广播全局并集。
7. `.69` 当前哈希先完成原验收，不因架构返工阻塞事实收集；即使行为 PASS，也只能证明当前 Elver Legacy Animation 门场景，不能豁免 A1-A4 与 B1/B3/B4。

## 六、运行验收门

`.69` Debug SHA-256 `9213E6DDE4C430F4DEC5F79B99961D17CD4D5F58A4494DBF17EF86DD6A37FD0E` 仍需同 Case ID 双端验证：

1. 房主与客机同区，开/关并双向穿越；
2. 房主离开超过 Object 相关半径，客机重复开/关并双向穿越；
3. 动画进行中跨区或断线，再进入区域验证最终姿态；
4. 房主返回，双方状态一致且无回滚/卡门；
5. 普通家具远区碰撞不退化；
6. 日志无 animation policy apply/restore fault，退出后 tracked animation 为 0；
7. 记录装饰动画、音频和 CPU 影响，确认全子 Animation 策略没有明显副作用。

若改动 A1-A4、B1/B3/B4 或迁移 Adapter，必须生成新版本/新哈希并重新执行以上测试，不能继承 `.69` 结果。

## 七、独立审核记录

| 轮次 | 判定 | 发现与处置 |
| --- | --- | --- |
| 1 | FAIL | 补出 A4：覆盖账本先提交、单对象失败后无重试；修正 A1 不得破坏既有静态碰撞租约，A3 降为受异常隔离的 P2。 |
| 2 | FAIL | 要求将 B1/B3/B4 明确为正式 Adapter 准入阻断，并统一历史矩阵、迁移门和新哈希验收引用。 |
| 3 | PASS | A1-A4、B1-B7、历史裁决、运行证据边界与正式 Adapter 准入条件闭合，无遗漏阻断项。 |
| 4 | FAIL | 独立复核补出 A5：不完整的 `Provider.clients` 采集会被当成观察者离场并立即 Release；同时修正 B6 的“语义正确”为仅在完整采集下成立。A1-A4、B1-B7 与历史矩阵仍核验成立。 |

## 八、最终结论

Issue7 的证据链和 `AlwaysAnimate` 根因方向成立，历史 `.63` 普通家具碰撞也仍是必须保留的有效基线。但当前 `.69` 是一个有价值的定点兼容修复，不是新架构的完成实现。它在事务失败、恢复失败、覆盖账本提前提交、不完整观察者采集以及 Unity fake-null 防御上存在生命周期缺口；能力最小化、Release 滞回和实例代数也尚未达到正式 Adapter 准入条件。修复 A1-A5 与 B1/B3/B4 并迁移到能力租约 Adapter 后，才可作为 Multi-Observer 第一项正式实现。issues#7 当前状态继续保持 `fix-not-yet-validated`。
