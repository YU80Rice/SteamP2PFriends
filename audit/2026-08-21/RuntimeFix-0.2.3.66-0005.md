# 缺陷修复执行报告 - 0.2.3.66

## 一、问题定位与修复策略

- **归档证据**：`issues#5/machine-234659/LogOutput.log:236-240` 记录客机 `76561199721762479` 的 Route B 握手许可后，房主点击 P 键“撤销允许”触发 `ApprovalRevoke`。日志同时记录新的 `Whitelist.dat` 为 28 B（只含房主）和旧备份为 53 B（房主与客机），说明原生保存已完成；但操作仍报 `InvalidOperationException` 并未踢出客机。
- **根因**：`NativeWhitelistStore.Contains` 调用的 `SteamWhitelist.checkWhitelisted` 被 `Patch_ServerConnectValidation` 后缀用于握手阶段临时改写。Route B 对未被本会话拒绝的客机返回 `true`，使撤销事务的保存后 `!Contains(target)` 核验永远误判失败。同一污染也会影响世界进入时的可信玩家判断及 P 键行状态。
- **U3-SDK 依据**：`D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Provider/SteamWhitelist.cs:52-63` 的原生 `checkWhitelisted` 本身只遍历公开 `SteamWhitelist.list`；插件的握手放行后缀是额外语义，不能复用于持久化或授权状态查询。
- **修复策略**：保留原版握手补丁的限定用途，将 `NativeWhitelistStore.Contains` 改为直接遍历 `SteamWhitelist.list`。因此持久化核验、`P2PApprovalManager` 的世界内授权判定和 P 键 UI 状态都使用物理白名单成员关系，而握手阶段仍由原补丁临时放行。

## 二、源码溯源清单

| 需求 / 风险 | 落实位置 |
| --- | --- |
| 撤销保存后准确核验 | `Host/P2PWhitelistService.cs:64-83,484-545` |
| 隔离与可信玩家判断不受握手 permit 污染 | `Host/P2PApprovalManager.cs:36-48,225-245` |
| P 键“撤销允许”显示使用物理授权状态 | `Patches/Patch_PlayerDashboardPlayersUI.cs:104-116` |
| 握手放行仍局限在原版验证入口 | `Patches/Patch_ServerConnectValidation.cs:18-23` |
| 防止重新引入被补丁方法的查询 | `WhitelistTests/WhitelistServiceTests.cs` 的 `WL9c` |

## 三、核心代码变更对比

```diff
- public bool Contains(CSteamID steamId) => SteamWhitelist.checkWhitelisted(steamId);
+ public bool Contains(CSteamID steamId)
+ {
+     List<SteamWhitelistID> list = SteamWhitelist.list;
+     if (list == null) throw new InvalidOperationException("SteamWhitelist.list is null");
+     return ContainsRaw(list, steamId);
+ }
```

该方法仍在原有 `IWhitelistStore` 边界内，未新增第三方依赖，未改变白名单文件格式、保存路径、握手补丁、P 键布局或客户端网络协议。

## 四、编译与自测状态

- 主项目：Visual Studio Insiders MSBuild Release Rebuild，`0 errors / 0 warnings`。
- 测试项目：Visual Studio Insiders MSBuild Release Rebuild，`0 errors / 0 warnings`。
- 测试执行：`SteamP2PFriends.WhitelistTests.exe`，`56/56 PASS`。
- 新增测试：`WL9c NativeContainsRaw` 验证物理列表内的房主返回真、未列入的客机返回假。
- 候选 DLL：`bin/Release/SteamP2PFriends.dll`，AssemblyVersion `0.2.3.66`，SHA-256 `EC7487AC77F42703EE714EFBD674D957FD3EEDB4B567D6CE1171430972913430`。

## 五、子智能体审核记录

| 审核项 | 判定 | 说明 / 证明位置 |
| :--- | :--- | :--- |
| 需求符合性 | 通过 | 物理成员查询替代被握手补丁改写的方法；日志根因得到直接消除。 |
| 握手与隔离语义 | 通过 | 握手 permit 只留在 `Patch_ServerConnectValidation`，世界进入走 `ContainsForUi`。 |
| 并发与状态 | 通过 | 原有 `WhitelistSync`、`ConcurrentDictionary`、本会话拒绝状态和主线程断言未被放宽。 |
| 持久化与踢出次序 | 通过 | `Snapshot -> Remove -> Save -> Load -> raw !Contains -> targeted kick` 保持；失败仍恢复内存并不踢客机。 |
| P 键 UI | 通过 | 远端已授权行和世界内 trusted 判定共同使用物理白名单查询。 |
| 独立审核结论 | PASS | 无阻断项。 |

## 六、最终结论与运行验证边界

- **静态/构建结论**：修复完成，独立审核通过，可交付双机验证。
- **本轮尚未验证**：`0.2.3.66` 的新 DLL 尚无运行归档，不能把 `issues#5` 的 `0.2.3.65` 结果前移为新版本通过证据。
- **下一轮双机用例**：先让陌生客机进入并保持 30 秒待审隔离，再批准；随后点击“撤销允许”，验证房主日志出现 `approval revoke committed` 与 `Revoke success`，客机断开；同一客机在同一房间重连时应被拒绝。归档双方 `LogOutput.log` 并绑定上方 SHA-256。
