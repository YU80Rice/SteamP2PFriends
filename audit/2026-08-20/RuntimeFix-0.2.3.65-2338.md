# SteamP2PFriends 缺陷修复执行报告 - 0.2.3.65

## 一、问题定位与修复策略

- **故障证据**：`issues#4/machine-230102/LogOutput.log` 与 `issues#4/machine-230150/LogOutput.log` 都加载了 `SteamP2PFriends 0.2.3.64`，随后在 `SteamP2PFriendsPlugin.Awake()` 调用 `P2PApprovalManager.InstallProviderLifecycleHooks()` 时触发 `ThreadUtil.assertIsGameThread()`。插件继而记录 `DiagnosticBuildValid=false` 并跳过“多人联机”按钮注入。
- **根因**：BepInEx `Awake()` 早于 Unturned 注册可供 `Provider` 生命周期事件使用的游戏主线程；Route B 审批 Hook 不可在该时机订阅。
- **修复策略**：保留 `ThreadUtil.assertIsGameThread()`，将审批生命周期 Hook 延后到 `P2PWorldStatusBroadcaster` 已确认主线程就绪的 `Update()`。新增独立入口就绪门，只有“诊断构建有效 + Route B 生命周期 Hook 已安装并复核”时才开放菜单、SteamID 直连、Lobby 自动连接、房主启动及客机最终连接入口。Hook 成功后对已创建的原版菜单幂等补注入；失败或卸载时关闭入口并移除陈旧按钮。

## 二、需求溯源与代码变更

| 需求 / 风险 | 落实位置 |
| --- | --- |
| 不在错误线程订阅 Provider 生命周期 | `SteamP2PFriendsPlugin.EnsureRouteBLifecycleHooksOnGameThread` |
| Hook 成功前禁止显示或操作入口 | `Shared/P2PEntryReadinessGate.cs`、`MenuPlaySingleplayerUIPatch` |
| 已创建菜单在成功后出现一次按钮 | `MenuPlaySingleplayerUIPatch.EnsureMultiplayerButton` |
| 所有 Steam P2P Host/Join 入口均失败关闭 | `MenuPlayConnectP2PRoutePatch`、`P2PJoinManager`、`HostManager`、`P2PNativeMenuUI` |
| 失败/卸载无残留 UI 或生命周期事件 | `SteamP2PFriendsPlugin.OnDestroy`、`MenuPlaySingleplayerUIPatch.DestroyMultiplayerButton` |
| 早菜单、失败与幂等性回归 | `WhitelistTests/P2PEntryReadinessGateTests.cs` |

本轮增加：

- `Shared/P2PEntryReadinessGate.cs`
- `WhitelistTests/P2PEntryReadinessGateTests.cs`

本轮修改：

- `SteamP2PFriendsPlugin.cs`
- `Patches/MenuPlaySingleplayerUIPatch.cs`
- `Patches/MenuPlayConnectP2PRoutePatch.cs`
- `Client/P2PJoinManager.cs`
- `Host/HostManager.cs`
- `UI/P2PNativeMenuUI.cs`
- 两个工程文件、版本信息、README 与 CHANGELOG。

## 三、核心变更对比

```diff
- P2PApprovalManager.InstallProviderLifecycleHooks(); // Awake
+ // Update: world-status 不再 Pending 后执行
+ P2PApprovalManager.InstallProviderLifecycleHooks();
+ EntryReadiness.TryMarkRouteBLifecycleReady(...);
+ MenuPlaySingleplayerUIPatch.EnsureMultiplayerButton();

- if (!DiagnosticBuildValid) reject/start/connect
+ if (!IsP2PEntryReady) reject/start/connect
```

## 四、编译与自测状态

- 主项目：Visual Studio Insiders MSBuild Release Rebuild，`0 errors / 0 warnings`。
- 测试项目：Release Rebuild，`0 errors / 0 warnings`。
- 控制台回归：`55/55 PASS`，新增 `E1 EntryEarlyMenu`、`E2 EntryLifecycleFailure`、`E3 EntryIdempotentReset`。
- DLL：`bin/Release/SteamP2PFriends.dll`，AssemblyVersion `0.2.3.65`，SHA-256 `76203F23F61EABFDC620ED520F207EAEA39EE1288ADD9A280B8C53A5386440A9`。

## 五、子智能体审核记录

| 审核轮次 | 判定 | 结论 |
| --- | --- | --- |
| 1 | FAIL | 发现菜单构造/点击仅检查 `DiagnosticBuildValid`，Hook 未就绪时可能提前开放。已新增 readiness 门与幂等补注入。 |
| 2 | FAIL | 发现 SteamID 直连和 `HostManager.StartP2PServer` 最终入口可绕过。已把门下沉至直连、Lobby、最终 Join 与最终 Host 入口。 |
| 3 | PASS | 确认主线程顺序、fail-closed、菜单补注入、所有可达 Steam P2P Host/Join 入口、卸载清理与 DLL 身份均通过。 |

## 六、最终结论与运行验证边界

- **静态/构建结论**：修复完成，独立审核通过，可移交双机运行验证。
- **尚未证明**：本报告不替代双机运行证据。新的房主和客机必须部署同一 SHA-256 的 DLL，并归档两端 `LogOutput.log`，至少确认冷启动出现“多人联机”按钮、主线程生命周期安装日志、房主开房、客机加入和 P 键审批。
