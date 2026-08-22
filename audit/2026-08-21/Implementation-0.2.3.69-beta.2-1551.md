# SteamP2PFriends Listen-Host 多观察者远区保活架构规范

- 日期：2026-08-21 15:51 (Asia/Shanghai)
- 基线：`0.2.3.69-beta.2 Issue7-Fix-Debug`
- 性质：只读架构复盘与演进规范；本轮未修改业务代码、未变更版本号
- 结论：Issue #7 的门修复应视为第一个领域 Adapter，而不是全局解法

## 一、根因泛化

### 1.1 三个彼此独立的生命周期

Listen-Host 同时是服务器和有摄像机的客户端。原版许多对象仍以 `MainCamera.RenderingPosition` 为唯一视觉活跃中心，而远端玩家的位置属于网络相关性中心。两者一旦分离，同一个实体会出现三种不同答案：

1. **网络层**认为实体相关，继续收发状态；
2. **权威逻辑层**可能仍在推进计时、生命或 AI；
3. **Unity 对象层**却可能因房主摄像机远离而停用根节点、停止动画采样、关闭碰撞或降低更新频率。

因此，“RPC 已发送”与“主机物理世界已提交同一状态”不是同一事实。

### 1.2 为什么只激活根 GameObject 不够

`SetActive(true)` 只满足了 Unity 调度组件的前提条件，不能保证以下后置条件：

- **生命周期**：先前 `SetActive(false)` 已触发 `OnDisable`，协程可能终止，缓存、订阅、NavMesh cut 或池化状态可能已被清理；重新激活只会触发 `OnEnable`，不会自动重放权威状态。
- **Legacy Animation / Animator**：根节点活跃后仍可能按 Renderer 可见性剔除。若碰撞体或 Barrier 位于被动画驱动的子 Transform，视觉状态可显示为“开门”，而 PhysX 中仍保留关闭姿态。
- **Transform -> PhysX**：直接改变 Transform、动画采样和 `Rigidbody.MovePosition` 属于不同提交路径。若 Transform 自动同步关闭或变更发生在物理步之间，查询、CharacterController 和触发器在下一次模拟/显式同步前可能仍读旧姿态。
- **Rigidbody**：活跃不等于唤醒。睡眠刚体不会因普通网络字段变化自动恢复模拟；相反，对所有刚体无条件 `WakeUp` 又会造成持续 CPU 开销和抖动。
- **功能子对象**：Collider、Trigger、Barrier、Toggle、NavMesh cut 可能位于独立子节点，根节点活跃不能证明它们的 `enabled/activeSelf` 与权威状态一致。
- **脚本调度**：`Update`、`FixedUpdate`、协程和动画事件是否推进，取决于各组件启用状态和对象生命周期，不取决于单一根节点。

门问题只是最容易观察到的实例：`InteractableObjectBinaryState` 一次状态更新同时牵涉 Animation、NavMesh cut、Toggle GameObject、Audio 和事件回调。只修根节点会遗漏其中任一功能后置条件。

### 1.3 其他潜在同类陷阱

| 机制 | 房主远离后的风险 | 规范判定 |
| --- | --- | --- |
| Trigger / Collider | 子节点被关闭；PhysX 尚未同步；`OnTrigger` 不再产生 | 属于功能层，区域有观察者时必须可查询 |
| Rigidbody / WheelCollider | 自动睡眠、未唤醒、轮胎接地状态低频或不再更新 | 仅对运动中、有人驾驶或等待权威作用力的实体保活并按事件唤醒 |
| Legacy Animation / Animator | 基于可见性停止采样，骨骼/门板 Transform 与碰撞体滞留 | 仅对“动画驱动功能 Transform”的实体强制功能采样 |
| LODGroup / Renderer | LOD 或自定义脚本可能连带关闭含碰撞/脚本的子节点 | Renderer 可剔除；功能子节点不得由 LOD 所有 |
| ParticleSystem | 通常纯表现，但碰撞/Trigger 模块可能被工坊资产用于玩法 | 默认剔除；能力扫描发现 gameplay particle 时采用专用 Adapter |
| AudioSource / 灯光 | 停止或重启导致表现差异，一般不影响权威 | 远区剔除；不得用音频播放状态驱动权威逻辑 |
| NavMeshAgent / NavMesh cut | 停用后寻路或动态阻挡不更新 | AI/障碍功能活跃区内保留，退出前提交最终阻挡状态 |
| `OnBecameVisible` 等可见性回调 | 工坊脚本错误地以可见性驱动玩法 | 不接受为权威来源；必要时通过 Adapter 重放明确状态 |

U3DS 的做法提供了能力分层证据，而不是可直接复制的运行模式：`ServerPrefabUtil` 保留功能组件、把 Legacy `Animation` 设为 `AlwaysAnimate`，同时删除 LOD、Mesh、粒子、灯光、音频和 Renderer。Listen-Host 不应删除这些表现组件，而应让它们继续由本地摄像机管理。

## 二、设计范式

### 2.1 三轴模型

废弃“实体是否 Active”这一单值判断，改为三条正交轴：

```text
NetworkRelevant  = 是否需要向某玩家发送快照/增量
FunctionalActive = 主机是否必须推进权威逻辑、碰撞和功能状态
VisualActive     = 房主本地是否需要渲染、特效和声音
```

三者的需求分别计算。网络轴必须保留接收者身份，只有功能轴可以取观察者并集：

```text
R_network(observer, domain) = regions relevant to this observer
R_functional(domain)        = Union(functional regions around every world-presence observer)
R_visual                    = host camera visibility only
```

网络层维护 `ObserverId -> DomainRegionSet + LoadedGeneration`，并维护反向 `DomainRegion -> ObserverSet` 供定向增量使用。全局并集或引用计数不能回答“应发给谁、谁已收到首次快照、谁刚退出相关性”，因此不得直接驱动发送。

观察者资格与审核权限必须分离：

- `WorldPresenceObserver`：已创建世界内 Player、连接有效且尚未销毁；包括待审核软隔离玩家。它参与必要的网络快照、碰撞和世界功能需求。
- `GameplayAuthorized`：是否允许开火、拾取、丢弃、破坏、背包操作、命令和其他受限行为；只用于行为授权和可选 AI 仇恨策略。

待审核玩家已经身处世界，不能因未授权而从 ObserverSet 排除。视觉状态和审批状态均不得反向关闭其周边必要世界状态。

### 2.2 区域需求与引用计数

核心数据流：

```text
Provider player lifecycle / authoritative positions
        -> WorldPresenceObserverSet
        -> PerObserverRelevance(observer, domain, regions, loadedGeneration)
        -> FunctionalRegionDemand(domain, region, capability, refCount)
        -> ActivationLease(entity, capabilities)
        -> Adapter.Apply / Verify / Restore
```

- 每位世界内玩家分别维护网络相关区域、首次快照完成标记与实例代数；移动、断线或销毁时单独失效，发送目标始终来自该观察者集合。
- 功能保活由所有世界观察者的需求派生引用计数，禁止单一 `bool`；重叠覆盖中只有最后一位观察者离开才能释放。`refCount` 只是功能轴优化，不是网络发送依据。
- 半径按领域独立配置，不能把 `LevelObjects.OBJECT_REGIONS=3` 强套到网络半径为 2 的 Object、Resource、Structure、Barricade。
- 区域边界采用滞回：进入立即 Acquire；离开延迟 2-5 秒或多保留一圈；若实体仍在运动、交互事务未完成或有乘员，则不得释放。
- 更新由玩家创建/移动/断线、对象生成/销毁、世界切换和权威状态变化驱动；周期扫描只用于低频自愈，不能每帧全图反射扫描。

### 2.3 能力描述，而非按类型全开

每个实体 Adapter 申明实际需要的能力：

```text
LogicTick            权威计时、AI、状态机
CollisionQueryable   Collider / CharacterController 可查询
TriggerEvents        Trigger 与必要刚体参与物理步
TransformCommit      动画/状态驱动 Transform 提交至 PhysX
NavigationState      Agent、Obstacle、NavMesh cut
DynamicPhysics       Rigidbody、WheelCollider、关节
NetworkSnapshot      首次快照、增量、序列/基线
VisualPresentation   Renderer、LOD、灯、粒子
LocalAudio           AudioSource
```

远区通常只租用前七项中实际需要的子集；`VisualPresentation` 和 `LocalAudio` 始终留给房主摄像机策略。禁止全局启用所有 GameObject、Animator、Animation 或 Rigidbody。

### 2.4 Acquire 状态提交契约

同一主线程批次内按以下顺序执行：

1. 解析实体稳定身份（NetId/实例代数/区域坐标），保存原始组件属性；重复 Acquire 必须幂等。
2. 仅激活承载功能组件的最小节点；保持 Renderer、LOD、粒子、灯光、Audio 由视觉策略控制。
3. 从权威数据重放完整状态，而不是依赖 `OnEnable` 猜测：alive/open/powered/health/owner/seat 等。
4. 若动画驱动功能 Transform，仅对对应 Legacy Animation 使用 `AlwaysAnimate`，或对 Animator 使用等价功能采样策略；无功能骨骼的动画不保活。
5. 重建 Barrier、Collider、Trigger、Toggle 和 NavMesh cut 的最终状态。
6. 在批次末按需执行物理同步；动态实体只有在输入、作用力、轮胎变化、拖挂变化或迁移时 `WakeUp`。对于持续动画，要求先提交当前权威姿态并启动确定性过渡，不要求阻塞到整段动画结束。
7. 功能状态成功进入可验证的当前姿态/过渡后，才向**对应观察者集合**发布网络增量或确认基线，避免客户端先看到动画、主机碰撞仍旧。
8. 验证最小不变量并输出限流诊断：根/功能节点、碰撞姿态、动画剔除策略、刚体睡眠、状态序列。
9. Apply/Verify 作为事务：任一能力失败时，按逆序撤销本轮已修改组件，保留旧租约和旧网络基线，禁止发布半完成状态；回滚失败则隔离该实体并输出阻断级诊断。

### 2.5 Release / 恢复契约

只有 `refCount==0`、滞回期结束、无乘员、无未决交互、无活动刚体/AI 目标时才可释放：

1. 完成当前事务并把最终权威状态写回模型/存档基线；
2. 停止领域自建任务，撤销事件订阅；
3. 按逆序恢复每个组件的**原值**，禁止恢复成硬编码默认值；
4. 清除租约和实例代数，销毁对象时容忍 Unity fake-null；
5. 断线、主机停止、插件卸载、世界切换和异常中止都必须走同一恢复路径；单实体失败需隔离，不能阻断其他实体释放。

### 2.6 原生网络相关性契约

Multi-Observer 层不取代 U3 原生的每玩家加载账本，而是补齐 Listen-Host 分支并与其对齐：

| 领域 | 坐标/相关性 | 首次快照与每玩家账本 | 增量目标与退出语义 |
| --- | --- | --- | --- |
| Object | `region_x/y`, 半径 `ObjectManager.OBJECT_REGIONS=2` | `player.movement.loadedRegions[x,y].isObjectsLoaded`; `askObjects(connection,x,y)` | `GatherRemoteClientConnections(x,y,2)`；离区清除此玩家 loaded 标志 |
| Resource | `region_x/y`, 半径 `ResourceManager.RESOURCE_REGIONS=2` | `loadedRegions[x,y].isResourcesLoaded`; 领域初始 region 发送 | `GatherRemoteClientConnections(x,y,2)`；离区逐玩家失效 |
| Structure | `region_x/y`, 半径 `STRUCTURE_REGIONS=2` | `loadedRegions[x,y].isStructuresLoaded`; `askStructures(connection,x,y,sortOrder)` | `GatherRemoteClientConnections(x,y,2)`；实体 NetId/代数随快照校验 |
| Barricade | 世界 region 或 vehicle/plant parent region | `loadedRegions[x,y].isBarricadesLoaded`; `SendRegion(client,region,...)` | 世界区用 `GatherRemoteClientConnections`；父级区域需按 parent NetId/乘员相关性处理，不能强套二维方格 |
| Zombie | Navigation `bound`，不是普通 world region | `player.movement.loadedBounds[bound].isZombiesLoaded`; `SendZombiesToPlayer(connection,bound)` | `PlayerCountInRegion` 驱动功能 OnUpdate；离 bound 清除该玩家 loadedBounds |
| Animal | 全局实体表 + 当前原生发送链，非普通 region 快照同构 | 每位连接必须有明确初始实体基线/序列 | 增量按目标连接发送；不得仅用全局 `tickingAnimals` 推断谁已加载 |
| Vehicle | 全局车辆实例/instanceID，动态位置和乘员关系 | 连接加入时的全量车辆基线与实例代数 | 周期/事件增量按连接和实体身份；驾驶/乘员相关性优先于静态方格区域 |

任何 Adapter 都必须明确上述四项：坐标系、首次快照入口、每玩家 loaded generation、增量/卸载目标。无法明确时，只允许启用功能诊断，不得接管网络发送。

## 三、全局实体同步矩阵

### 3.1 当前运行证据基线

以下状态是后续 Multi-Observer 实施的回归底线。`✅` 只约束表内已经实测的行为，不自动证明同一领域的外观、存档、复杂交互或其他实体类型；`◐` 表示已有部分链路/行为证据，但不能关闭领域；`⬜` 表示尚无有效双机验收。

| 子项 | 状态 | 已证实范围 / 仍缺什么 |
| --- | :---: | --- |
| 普通家具、柜子、沙发等 `LevelObject` 远区碰撞 | ✅ | 最新双机实测：房主离开 Alberton 至 Airport 后，客机仍不能穿过家具。该 PASS 仅证明静态碰撞保活，不证明所有动画交互物件。 |
| 树、灌木、可采集资源 `ResourceSpawnpoint` | ⬜ | 尚未复现、未修复、未测试。 |
| 地图随机物品生成、拾取、权威移除 | ✅ | 历史 `P0-B` 已关闭：防止二次随机生成，客机拾取对应权威实例可正常移除。 |
| 僵尸/玩家掉落物的基础可见与拾取 | ✅ | 历史双机证据已将其与地图随机物品分叉明确区分，掉落增量同步正常。 |
| 资源状态，如砍树、采集、破坏、重生 | ⬜ | 有区域发送代码，不等于已验证远区交互。 |
| 地图物件状态 `ObjectManager` | ◐ | 有原生 RPC 复用链路；未做“房主远离、客机交互后双方一致”的专项实测。 |
| 路障 `Barricade` | ◐ | 基础放置、可见、访问曾有双机证据；远区加载、拆除、复杂交互和保存尚未完成。 |
| 建筑 `Structure` | ⬜ | 有实现路径，缺少有效双机行为验收。 |
| 僵尸远区生成、战斗、击杀、房主返回不重刷 | ✅ | 历史 `P0-D`、`P0-C-1` 与生命周期专项双机验证已通过。 |
| 僵尸外观完整一致、持久化 | ◐ | 生命周期已通过；服装/完整快照与存档不是同一项，未整体关闭。 |
| 动物远区生成、AI、战斗、死亡掉落 | ⬜ | 只有发送门补丁与基础链路，尚无完整远区双机实测。 |
| 载具远区状态、驾驶、上下车、换座、保存 | ⬜ | 只有初始/周期状态补丁，尚无完整远区双机实测。 |

新增架构不得使任一 `✅` 项退化。源代码或 DLL 发生变化后，对应 PASS 仍须用新哈希和成对日志重新建立，不能永久继承历史证据。

### 3.2 多观察者防御矩阵

| 子系统 | 潜在的房主视锥/单人休眠陷阱 | 多观察者保活/同步策略 |
| --- | --- | --- |
| `LevelObject` / `ObjectManager` | 门、闸门、升降机动画被剔除；Barrier、Toggle、NavMesh cut 与动画/权威位不一致；对象根随房主区域关闭 | 以远端玩家区域并集租用 `CollisionQueryable + TransformCommit + NavigationState`；按交互类型重放最终二进制状态；只为动画驱动功能 Transform 强制采样；Renderer 不开启。`.69` 门补丁是此 Adapter 的首个窄实现，仍需扩展到 Animator、升降机及复杂状态机。 |
| `ResourceSpawnpoint` | 非专服 `UpdateActive` 仍按本地区域切换 model/stump；树倒下临时 Rigidbody、树桩碰撞、采集点和重生状态可能分叉 | 按资源区域并集保持 alive model 或 stump 的功能节点/碰撞；health/alive/respawn 由主机唯一权威；死亡切换先提交 model/stump 后发增量；倒树碎片默认视为表现，若参与伤害/阻挡则由专用短期物理租约管理。 |
| `Structure` / `Barricade` | 初始区域包已发送但异构组件未运行；密码门 Barrier、发电机范围、箱子库存、Sentry LOS/Trigger、农作物计时可能停摆 | 网络加载与功能保活分开；建立按 Interactable 能力分类的 Adapter registry。静态结构仅需碰撞；Door 需动画/Barrier；Generator/Farm 需计时；Storage 需事务状态；Sentry 需 Trigger/LOS/逻辑 tick。不得对全部 barricade 统一 `SetActive(true)`。 |
| `AnimalManager` / `ZombieManager` | 网络包、AI tick、模型 Update 是不同链；CharacterController/目标物若被区域关闭，仇恨、移动、攻击和掉落可失真；动画剔除可能影响工坊资产的攻击时序 | WorldPresenceObserverSet 必须包含所有世界内有效玩家（包括待审核玩家），而是否成为仇恨目标可另受 GameplayAuthorized 策略控制。AI 权威以位置/时间戳/碰撞查询推进，不能依赖 Renderer/动画事件；目标区域租用 CharacterController、导航、攻击查询和目标 buildable 功能节点。Zombie 已有 `PlayerCountInRegion -> regionsWithPlayers -> OnUpdate` 多玩家雏形，应复用并验证；Animal 仍需完整远区生成、仇恨、战斗、死亡掉落实测。外观动画继续可剔除。 |
| `VehicleManager` | 源码明确非专服不可见载具按 4 片降频且“listen servers 需要调整”；周期状态原生仅专服发送；刚体睡眠、WheelCollider 接地、拖挂、座位和远端驾驶可分叉 | 有乘员、正在移动、被拖挂或受权威作用力的车辆获得高优先级 `DynamicPhysics + TransformCommit + NetworkSnapshot` 租约，固定物理步运行；远端驾驶输入到达时唤醒刚体。停放且静止车辆可睡眠并低频更新。轮胎 Collider 保活，轮胎模型、履带材质、排气、螺旋桨、灯光和音频仍按房主视觉剔除。 |

## 四、U3-SDK 溯源矩阵

| 事实 | 源码位置 | 架构含义 |
| --- | --- | --- |
| `LevelObjects.tickRegionalVisibility` 以 `MainCamera.RenderingPosition` 计算区域 | `LevelObjects.cs:1154-1189` | 原版视觉中心不能作为远端功能中心 |
| `LevelObject.UpdateActiveAndRenderersEnabled` 分别计算根节点与 Renderer | `LevelObject.cs:1040-1078` | 功能节点与表现可拆分，但不能只改根节点 |
| 专服为 Legacy Animation 设置 `AlwaysAnimate` 并移除表现组件 | `ServerPrefabUtil.cs:22-58,86-160` | U3DS 的可借鉴部分是能力分层，不是全盘常驻 |
| BinaryState 同时更新动画、音频、NavMesh cut、Toggle 和回调 | `InteractableObjectBinaryState.cs:67-216,228-249,446-449` | 一个网络状态必须具备多组件提交契约 |
| Resource 重生由服务器轮询全图，但 model/stump 激活依赖 `isActiveInRegion` | `ResourceManager.cs:661-817`; `ResourceSpawnpoint.cs:383-411` | 逻辑计时正常不代表远区碰撞/表现对象正常 |
| Zombie region 对所有玩家计数并维护 `regionsWithPlayers` | `ZombieManager.cs:1448-1517`; `ZombieRegion.cs:356-385` | 原版已有可复用的观察者并集雏形 |
| Zombie manager 对有玩家区域执行 `OnUpdate` | `ZombieManager.cs:1704-1796` | 应扩展现有计数语义，不另造摄像机判断 |
| Animal manager 在 listen-host 对全部 ticking animals 执行 tick | `AnimalManager.cs:999-1053`; `Animal.cs:124-147` | AI 活动集与视觉仍需独立验收 |
| Vehicle 非可见更新被切成 4 片，源码注明 listen server 需调整 | `VehicleManager.cs:2844-2909` | 远端驾驶车辆必须覆盖该视觉降频策略 |
| Vehicle 状态原生周期发送仅在 dedicated 分支 | `VehicleManager.cs:2913-2927` | 当前发送补丁只补网络轴，不证明物理轴 |
| Vehicle 物理依赖 `FixedUpdate`、Wheel 更新和事件唤醒 | `InteractableVehicle.cs:4229-4282,4925-4930` | 需按运动/乘员租用物理，不能全局常醒 |

## 五、性能底线

- 网络轴使用每观察者差量区域集合与反向接收者索引；功能轴使用区域引用计数。复杂度与“玩家跨区及受影响区域”相关，不与全图对象数每帧线性相关。
- 功能半径按领域设置；最昂贵的 AI/车辆可比静态碰撞半径小，并在高速运动方向预取一圈。
- 每帧设置 Apply/Restore 数量与耗时预算，静态对象可分批；当前玩家脚下碰撞、正在交互的门和远端驾驶车辆不可延后。
- 视觉剔除完全保留：不为远区客机开启 MeshRenderer、SkinnedMeshRenderer、高模 LOD、粒子、灯光或本地 AudioSource。
- 动态物理只保活“有人使用/运动中/有未决作用力”的实体；睡眠是允许的优化，但进入交互前必须确定性唤醒并校验。
- 诊断采用状态变化和限流采样，禁止逐帧逐实体日志。

## 六、诊断指纹与验收门

每次租约变化至少记录：`session/caseId, domain, entityId, generation, region, observerCount, capabilities, cause, before/after, applyResult`。每次定向网络变化另记录 `observerId, loadedGeneration, snapshot/delta, targetResult`；功能状态变化另记录：

- `authoritativeStateVersion` 与最后网络序列；
- root/function node/renderer 是否活跃；
- Collider/Trigger/Barrier 姿态摘要；
- Legacy Animation/Animator culling 策略；
- Rigidbody `isKinematic/isSleeping`、速度摘要；
- Apply 到物理提交、再到网络发送的顺序指纹；
- Release 是否恢复原值及未释放租约数量。

每个领域必须使用同一 DLL SHA-256、同一 Case ID 的双端日志，完成以下三段测试：

1. 房主与客机同区建立基线；
2. 房主离开至少两个相关半径，客机独立交互/战斗/驾驶；
3. 房主返回，双方状态一致且实体不重生、不回滚、不重复掉落。

还必须覆盖客机跨区、断线、重连、交互中断线、世界退出和插件卸载恢复。发送了区域数据、补丁登记成功、构建通过或单端动画正常，均不能替代这些运行证据。

## 七、实施顺序

1. 先抽取 `WorldPresenceObserverSet + PerObserverRelevance + FunctionalRegionDemand + ActivationLease` 基础设施，只接入只读诊断，不改变实体行为。
2. 将 `.69` LevelObject 门逻辑迁移为第一个 `LevelObjectBinaryStateAdapter`，保留原值恢复与当前运行证据门。
3. 依次接入 Resource、Barricade/Structure；这三类以静态碰撞/状态提交为主，风险低于动态物理。
4. 接入 Animal/Zombie，复用原版 `PlayerCountInRegion` 语义并补全生命周期诊断。
5. 最后接入 Vehicle，单独设物理预算、远端驾驶优先级和反作弊/纠偏验收。

## 八、审计结论

- **架构判定：PASS（规范层面）**。观察者并集只支配功能权威，房主摄像机继续独占表现层，能够同时满足远端准确性和本地渲染底线。
- **实现判定：OPEN**。当前 `.69` 只覆盖部分 `LevelObject + Legacy Animation`，尚无通用租约基础设施，也不能外推到 Resource、Barricade/Structure、Animal 或 Vehicle。
- **运行判定：OPEN**。Issue #7 本身仍须当前哈希双机复测；其他领域必须分别建立新证据，不能继承门或僵尸的历史 PASS。
- **编译/测试：N/A**。本轮未修改源代码，因此没有触发新构建或测试；上一轮构建结果不能作为本规范已实现的证明。

## 九、独立审核记录

| 轮次 | 判定 | 发现与处置 |
| --- | --- | --- |
| 1 | FAIL | 网络轴错误压缩为全局并集/引用计数，无法表达每玩家 loaded generation 和定向发送；待审核玩家被错误排除出世界观察者集合。已分别改为 `PerObserverRelevance`，并拆分 `WorldPresenceObserver` 与 `GameplayAuthorized`。 |
| 2 | PASS | 两项阻断均关闭；U3-SDK 引用与各领域证据边界核对成立，无剩余阻断项。 |

非阻断后续项：实现前精确补齐 Animal/Vehicle 的首次基线方法、序列字段与断线失效符号；优先复用原生 RPC 和 `InvokeAndLoopback`，避免为迁就抽象而重写可靠时序。
