# 缺陷修复执行报告 - 0.2.3.63

### 一、问题定位与修复策略

- **问题**：Elver 权限门在客机独处区域时出现位置回弹，房主同区域时不复现。
- **运行证据边界**：两份诊断包运行的均为 `SteamP2PFriends 0.2.3.63`，不是工作区当前 `0.2.3.68`。
- **首要根因候选**：listen-host 对客机独处区域的 LevelObject 激活、权威碰撞或 Binary State 保持不完整。U3-SDK `LevelObjects.tickRegionalVisibility()` 以房主 `MainCamera.RenderingPosition` 驱动对象区域激活；用户确认房主进入同一区域后故障消失，离开后客机出现拉回/卡住。`.63` 的远端碰撞补丁只记录匿名 Collider root reactivation，没有证明故障门自身状态正确。
- **环境干扰项**：用户实际使用的汉化包是纯文本覆盖 `3759194844`，但房主运行日志证明旧 `Elver汉化` (`2867869391`) 也处于订阅并加载状态。旧包与原版 Elver产生 1,178 条 GUID 冲突、772 条缺失 `Object` 和 242 条缺失 Nav。该污染必须先移除，但因它在同区域/异区域均存在，不能单独解释区域条件。
- **源码溯源**：已归档的四类 Elver 权限门 DAT 声明为 `Interactability Binary_State`，对应 U3-SDK `ObjectManager.ReceiveToggleObjectBinaryStateRequest` / `ReceiveObjectBinaryState`，不是 `InteractableDoor.ReceiveToggleRequest`。副本及哈希见 `issues#7/evidence/elver-door-dat/INDEX.md`。
- **修复策略**：暂不做推测性生产修复。保留 `3759194844` 与原版 Elver、移除旧资源包 `2867869391` 后执行同门同区域/异区域 A/B；若仍复现，定点记录 Object Binary State 全链、LevelObject 激活/Collider 以及位置校正，区分“状态 RPC 未到达”和“状态已到达但权威碰撞错误”。

### 二、核心变更

- 更新 `issues#7/ARCHIVE.md`：补齐原始包哈希、角色映射、运行版本、日志计数、真实 U3-SDK 调用链和复测门槛。
- 新增 `issues#7/evidence/elver-door-dat/`：归档四类代表性原版门 DAT、原始相对路径与 SHA-256。
- 新增 `issues#7/evidence/3759194844-inventory.md`：固定用户所用汉化包为纯 `Schinese.dat` 覆盖，不含资产 bundle。
- 未修改 C#、项目文件、版本号或 DLL。

### 三、静态与运行证据

| 证据 | 结论 |
| --- | --- |
| `_224756/LogOutput.log` | 客机加载插件 `0.2.3.63`；会话正常连接和断开。 |
| `_224758/LogOutput.log` | 房主区域补丁登记成功；对远端 Steam transport 有 Object/Barricade/Structure 区域发送及 send-success。 |
| `_224756/Client_Prev.log` | 客机只记录从 `2136497468` 加载 `metro.masterbundle`。 |
| `_224758/Client_Prev.log` | 房主同时从 `2867869391` 与 `2136497468` 加载 `metro.masterbundle`；门 GUID 被前者抢占并有缺失 Prefab。 |
| U3-SDK `ObjectManager.cs` | Binary State 请求按 region/index 解析对象、校验玩家条件并广播状态。端点对象表不一致会破坏此前提。 |
| 归档的原版 Elver DAT | Biometric/彩色/Church/Elevator 四类代表性权限门均为 `Interactability Binary_State`。 |
| U3-SDK `LevelObjects.cs` | `tickRegionalVisibility()` 以 `MainCamera.RenderingPosition` 更新 LevelObject 区域激活；listen-host 默认只感知房主相机区域。 |

### 四、编译与自测状态

- **生产代码修改**：无。
- **编译状态**：不适用；本轮没有源码或项目变更，现有 DLL 未重建。
- **静态自测**：完成诊断包清单、SHA-256、日志角色、插件版本、资源加载路径、错误计数和 U3-SDK API 交叉核对。
- **运行验证**：当前仅能证明冲突环境中的故障与区域发送事实；纯净 Elver 双机复测尚未执行。

### 五、受控复测用例

1. 两端停用旧包 `2867869391`，保留用户使用的 `3759194844` 与原版 Elver `2136497468` 并重启游戏。
2. 确认两端日志没有 Elver 门 GUID 冲突、`missing "Object" GameObject`，且 `metro.masterbundle` 只来自原版 `2136497468`。
3. 使用同一插件 DLL 哈希，在同一扇权限门分别执行同区域和不同区域测试；记录双方实际 region 坐标及与 `LevelObjects.OBJECT_REGIONS`（当前 3）的距离。
4. 记录门名称/GUID、region/index、权限 Flag、请求时间和回弹时间。
5. 若仍复现，增加同一门的 ObjectManager request/broadcast/receive、LevelObject active/root/Collider 与位置校正定点日志后再判断代码修复。

### 六、最终结论

- **判定**：生产代码修复暂不成立；区域相关的 LevelObject 激活/权威碰撞或 Binary State 保持不完整是当前最强候选，旧包资产冲突是必须先排除的环境干扰项。
- **处置**：先执行纯净资产双机复测。不得以 Barricade 门补丁或强制 Prefab 替换掩盖创意工坊冲突。
- **发布边界**：本报告不是 `0.2.3.68` 运行 PASS，也不授权发布新版本。

### 七、子智能体独立审核记录

| 轮次 | 判定 | 结论 |
| --- | --- | --- |
| 1 | FAIL | 根因措辞越过证据边界；原版 Elver DAT 未归档，无法独立复核。 |
| 2 | PASS | 因果结论已降级为最强候选；四个原始 DAT 哈希与索引一致，副本规范化换行后内容一致；U3-SDK 调用链和版本边界成立。 |
| 3 | PASS | 确认 `3759194844` 是纯文本本地化覆盖；冲突来源限定为运行时仍加载的旧包 `2867869391`，复测保留前者。 |
| 4 | PASS | 区域相关候选排序与 U3-SDK 相机区域激活链一致；`.63` 日志未证明故障门已覆盖，资源冲突正确降为环境干扰项。 |

- **最终审核结论**：PASS，无阻断项。
