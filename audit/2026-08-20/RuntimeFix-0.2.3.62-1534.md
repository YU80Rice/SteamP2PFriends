# 缺陷修复执行报告 - 0.2.3.62

## 一、问题定位与修复策略

- **报告现象**：Steam P2P listen-host 中，客机与房主跨区域后可穿入沙发、柜子、桌子等普通静态场景物体；两人同区域时碰撞正常。
- **证据范围**：已审阅 UMM 双端诊断包 `UMM-诊断包_20260820_142557`（客机）和 `UMM-诊断包_20260820_142615`（房主），两端均加载 `0.2.3.62` 且已建立 P2P 会话。旧版本日志没有 `LevelObjects` 激活或碰撞事件，不能作为修复生效证据。
- **根因**：listen-host 不是 dedicated server。原版 `LevelObject.UpdateActiveAndRenderersEnabled` 将普通静态物件的 root GameObject 激活（包含 collider）与房主本地区域可见性绑定；房主离开远端客机区域后，房主进程关闭该区域普通静态物件的 collider。`ObjectManager` 仅负责物件状态同步，非本问题的碰撞所有者。
- **修复策略**：仅在活动 SteamP2PFriends P2P 房主上，以远端已连接客机的区域覆盖保留普通静态物件碰撞；renderer 仍按房主的原版区域可见性决定。补丁以严格 IL 锚点和登记自检 fail-closed：原版 `Dedicator.IsDedicatedServer` getter 必须恰有两个，且第二个 getter 前必须存在 `ObjectAsset.isCollisionImportant` 和 `Provider.isServer` 分支锚点，否则诊断构建自检失败，不宣称补丁可用。

## 二、源码溯源与核心变更

| 需求点 | 落实位置 | 结果 |
| --- | --- | --- |
| 仅修复远端客机区域的普通静态物件碰撞 | `Patches/LevelObjectRemoteCollisionPatch.cs` 的 `UpdateActiveAndRenderersEnabled_Transpiler` 与 `ShouldKeepCollisionEnabled` | 仅替换碰撞分支；不改 renderer 分支 |
| 仅限 P2P listen-host，不影响 LAN、客机、单人或 U3DS | `LevelObjectRemoteCollisionPatch.ShouldKeepCollisionEnabled`，使用 `HostManager.IsP2PHostMode` 与 `ShouldProcessClientHostListen` | 严格门控 |
| 获取远端客机所在区域并刷新碰撞覆盖 | `LevelObjectRemoteCollisionPatch.LevelObjectsUpdate_Postfix`、`ReconcileRemotePlayerRegions`、`RebuildCoverageAndRefresh` | 按区域差集刷新，避免每帧全地图操作 |
| 防止断线、停服、新会话和卸载残留覆盖状态 | `SteamP2PFriendsPlugin.OnEnemyDisconnectedHandler`、`SteamP2PFriendsPlugin.OnDestroy`、`Host/HostManager.cs` | 断线移除玩家；新会话、启动中止、停服和卸载均 `ResetAll` |
| 将 IL 漂移或补丁登记失败变为可见发布门 | `SteamP2PFriendsPlugin` 启动自检与 `LevelObjectRemoteCollisionPatch.RegisterManual` | 失败时 `DiagnosticBuildValid=false` |

变更文件：

- 新增 `Patches/LevelObjectRemoteCollisionPatch.cs`
- 修改 `SteamP2PFriends.csproj`
- 修改 `SteamP2PFriendsPlugin.cs`
- 修改 `Host/HostManager.cs`

## 三、编译与静态自测

- **构建命令**：`C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release /v:minimal /nologo`
- **构建结果**：成功，`0 errors / 0 warnings`。
- **差异检查**：`git diff --check` 无空白错误；仅出现 Git 的 LF/CRLF 工作副本提示。
- **产物**：`bin/Release/SteamP2PFriends.dll`，SHA-256 `6DB0632D2114FB5C51C26D732E86466FA916374F88EA7021D241229F38967605`，大小 `922112` bytes。

## 四、独立子智能体审核记录

| 审核项 | 判定 | 说明 |
| --- | --- | --- |
| 需求符合性 | 通过 | 碰撞覆盖只扩展至远端 P2P 客机区域，不改网络、认证或物件状态同步 |
| P2P/LAN 环境隔离 | 通过 | `IsP2PHostMode` 排除 LAN；客机、单人和 U3DS 保持原始语义 |
| IL 目标可靠性 | 通过 | 第二个 dedicated getter 位于 `isCollisionImportant + Provider.isServer` 碰撞分支，16 指令锚点匹配 |
| 生命周期与一致性 | 通过 | 远端断线及所有会话退出路径清理区域状态；差集刷新幂等 |
| 异常隔离 | 通过 | 单个 `LevelObject` 刷新异常不会中断整次区域覆盖更新 |

独立审核结论：**PASS，无阻断项**。

## 五、运行时验证门

本报告只确认源码、编译、静态自检设计和独立审核通过，**尚未证明运行时修复已生效，不能据此发布**。

1. 将本报告中的同一 SHA-256 DLL 同时部署到房主和客机。
2. 房主开 P2P 房间并让客机加入；两人移动到相距超过房主区域覆盖的区域。
3. 客机走入并离开沙发、柜子、桌子等普通静态物件，确认无法穿透或卡入；再让房主接近和离开，确认碰撞状态一致。
4. 归档双端 `BepInEx/LogOutput.log`、两端 DLL 哈希、地图、时间与复现步骤。房主日志必须包含：
   - `[LevelObjectCollision] OK ... collisionAnchor=True`
   - 客机加入或移动后的 `[LevelObjectCollision] coverage change ... remotePlayers=1`
5. 客机退出后，房主日志应出现远端断线覆盖更新；P2P 停服后应出现 `ResetAll`。

## 六、最终结论

修复实现、无警告编译和独立审核均已完成，当前 DLL 可移交双机运行时验证。待同版本双端归档通过后，才能将该问题标记为已修复并进入发布流程。
