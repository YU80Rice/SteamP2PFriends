# 缺陷修复执行报告 - 0.2.3.62

## 一、问题定位与修复策略

- **触发场景**：房主使用 SteamP2PFriends 拉起 PEI 本地多人后，进入地图阶段异常退出。
- **运行时证据**：诊断包 `D:\Agent-工作目录\DevelopMyUNMultiplayerModAndModloader\启动器\UnturnedModManager\publish\UMM-v2.1.8-win-x64\UMM-诊断包_20260820_160047` 中，UMM 记录到异常退出码 `-1073741819 (0xC0000005)`。`LogOutput.log` 和 `Client.log` 在地图区域可见性同步时一致记录：
  - `NullReferenceException`；
  - `UnityEngine.Object.op_Equality`；
  - `SteamP2PFriends.Patches.LevelObjectRemoteCollisionPatch.IsDedicatedOrRemoteCollisionRequired`；
  - `LevelObject.UpdateActiveAndRenderersEnabled -> LevelObjects.ImmediatelySyncRegionalVisibility`。
- **根因**：新增碰撞补丁的谓词在 `LevelObject` 原生 Unity 实例尚未完全准备好时，执行了 `levelObject == null` 或 `levelObject.transform == null`。这两处会调用 Unity 重载的 `Object.op_Equality`，日志证明其在此阶段抛出 `NullReferenceException`。故障发生时日志已显示 `clients=1`（仅房主自身）且远端碰撞覆盖为空，本不应访问 `LevelObject.transform`。
- **归因边界**：上述托管异常由本补丁直接引发，且是异常退出前日志的最后一项，必须修复；但诊断包没有 Windows/Unity 原生崩溃转储，故不能仅凭 `0xC0000005` 断言其唯一的 native access-violation 根因。
- **修复策略**：
  1. 在 P2P 门控之后，当 `RemoteCoverage.Count == 0` 时直接返回 `false`，即被替换的 `Dedicator.IsDedicatedServer` 在非 dedicated 情况下的原版结果；
  2. 使用 `ReferenceEquals` 完全绕过 Unity 的重载空比较；
  3. 将唯一必要的 `transform` 获取和坐标读取置于局部 `try/catch`，异常时返回 `false` 回退原版碰撞分支，并每会话最多写一次低频告警；
  4. 不改变连接、认证、物件同步、渲染或远端区域覆盖计算。

## 二、源码溯源与核心变更

| 需求点 | 落实位置 | 结果 |
| --- | --- | --- |
| 消除 Unity 原生对象早期比较崩溃 | `Patches/LevelObjectRemoteCollisionPatch.cs` 的 `IsDedicatedOrRemoteCollisionRequired` | 无 Unity `== null` 比较 |
| 无远端客机时不访问场景物件 | 同方法的 `RemoteCoverage.Count == 0` 早退 | 房主启动期直接保持原版非 dedicated 语义 |
| 原生对象读取异常时保持原版语义 | 同方法的局部 `try/catch` | 返回 `false`，不传播到 `LevelObjects.Update` |
| 限制诊断噪音 | `_collisionPredicateFaultLogged` 与 `ResetAll` | 每会话最多一条告警，清理时复位 |
| 保留 P2P 专用覆盖功能 | `IsP2PHostMode`、`ShouldProcessClientHostListen`、`RemoteCoverage` | LAN、客机、单人、U3DS 不变；客机加入后仍按区域覆盖 |

本轮直接修改文件：

- `Patches/LevelObjectRemoteCollisionPatch.cs`

该文件连同上一轮未提交的碰撞补丁接入改动仍处于工作区，未发布、未推送。

## 三、编译与静态自测

- **构建命令**：`C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release /v:minimal /nologo`
- **构建状态**：成功，`0 errors / 0 warnings`。
- **差异检查**：`git diff --check` 无空白错误；仅有 Git 的 LF/CRLF 工作副本提示。
- **产物**：`bin/Release/SteamP2PFriends.dll`，SHA-256 `AC38E1DE113621D38BBB7636044815F191B7AD33DCE59015FA2EE4F27EFE52E5`，大小 `922624` bytes。

## 四、独立子智能体审核记录

| 审核项 | 判定 | 说明/证明位置 |
| --- | --- | --- |
| Unity 原生对象早期状态 | 通过 | `IsDedicatedOrRemoteCollisionRequired` 的 `ReferenceEquals` 和局部 `try/catch` |
| 原版语义回退 | 通过 | 非 dedicated 异常或未覆盖区域均返回 `false` |
| P2P/LAN/U3DS 隔离 | 通过 | `HostManager.IsP2PHostMode` 与 `ShouldProcessClientHostListen` 双重门控 |
| 远端碰撞功能保留 | 通过 | 覆盖非空后仍读取区域并查询 `RemoteCoverage` |
| 日志频率与会话清理 | 通过 | 谓词告警每会话一次，`ResetAll` 复位标记 |

独立审核结论：**PASS，无阻断项**。

## 五、运行时验证门

本轮崩溃的直接异常栈已定位并修复，但新 DLL 尚未部署运行，不能宣称已消除异常退出。

1. 使用上述 SHA-256 的 DLL 替换房主和客机的插件副本，并分别核对哈希。
2. 房主独立启动 P2P 房间并进入地图，至少停留 60 秒；验收标准为无 `NullReferenceException`、无 `LevelObjectRemoteCollisionPatch` 栈和正常退出码。
3. 客机加入后，让房主与客机分处不同区域，验证沙发、柜子、桌子等普通静态物件仍不可穿透。
4. 归档双端日志、两端 DLL 哈希、地图、时间、复现步骤。房主日志应包含 `[LevelObjectCollision] OK ... collisionAnchor=True`，以及客机移动后的 `coverage change ... remotePlayers=1`。

## 六、最终结论

异常退出前的补丁托管异常已由运行时调用栈直接定位，并已以最小范围修复；退出码的唯一原生原因仍缺崩溃转储佐证。源码、无警告编译和独立审核均通过；等待新 SHA-256 DLL 的房主启动与跨区域双机回归后，方可关闭运行时门或发布。
