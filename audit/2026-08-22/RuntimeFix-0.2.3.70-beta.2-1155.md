# SteamP2PFriends 发布执行报告 - 0.2.3.70-beta.2

## 一、发布范围与运行结论

本次正式发布源仅为稳定工作区 `SteamP2PFriends` 的 `v0.2.3.70-beta.2`。实验工作区 `SteamP2PFriends-0.2.4-Experimental` 未包含在提交、压缩包或 Release 中。

用户已反馈：以当前 `0.2.3.70-beta.2` Debug DLL 测试时，Elver 权限门在房主离开区域后可正常通过，不再出现开门动画正常但被关闭碰撞体拉回的问题。该运行结论只关闭 Issue #7 的权限门碰撞验收，不外推至树木、资源、动物、载具、建筑或完整 Object 状态同步。

## 二、发布二进制身份

| 配置 | SHA-256 | 用途 |
| --- | --- | --- |
| Debug | `A575A0F72DB8C1C1837223F03A3973F51043044D4B5BEFA7035C9AC7B5365E37` | 正式 Beta 安装包；与用户 Issue #7 回归候选一致 |
| Release | `B8282D8111438FDA896334B9CE7A6F10015550A6A70F3365B84F553AAA17F95A` | 编译验证产物，不作为本次发布包 |

## 三、编译与自动化验证

使用 Visual Studio Insiders MSBuild 18.9.1、.NET Framework 4.7.2 串行执行：

```powershell
MSBuild SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Debug /m
MSBuild WhitelistTests/SteamP2PFriends.WhitelistTests.csproj /t:Rebuild /p:Configuration=Debug /m
.\WhitelistTests\bin\Debug\SteamP2PFriends.WhitelistTests.exe
MSBuild SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release /m
MSBuild WhitelistTests/SteamP2PFriends.WhitelistTests.csproj /t:Rebuild /p:Configuration=Release /m
.\WhitelistTests\bin\Release\SteamP2PFriends.WhitelistTests.exe
```

结果：Debug/Release 均为 0 errors / 0 warnings；两套测试均为 `61/61 PASS`；`git diff --check` 无空白错误。

## 四、打包与发布契约

发布目录为 `publish/0.2.3.70-beta.2/`。压缩包仅命名为 `SteamP2PFriends.zip`，且只包含：

```text
BepInEx/
  plugins/
    SteamP2PFriends.dll
```

压缩包中的 DLL 必须等于上述 Debug SHA-256。仓库将提交源码、测试、审计报告、README 与 CHANGELOG；本地诊断包、`bin/`、`obj/` 与 `publish/` 继续由 `.gitignore` 排除。GitHub tag 和 Release 均使用 `v0.2.3.70-beta.2`。

## 五、发布门结论

| 门 | 结论 |
| --- | --- |
| 源码和版本一致性 | PASS |
| Debug/Release 编译 | PASS |
| 自动化回归 | PASS，61/61 x2 |
| Issue #7 权限门运行回归 | PASS，用户确认，绑定 Debug SHA-256 |
| 当前哈希 P2P 加入/审核全量双端诊断 | PENDING，不作已关闭声明 |
| 独立静态审核 | PASS，无源码阻断；审核确认 Release DLL 未获运行证据，因此不作为本次安装包 |

## 六、发布说明正文

`v0.2.3.70-beta.2` 修复了 listen-host 远区权限门的动画剔除问题：房主离开区域后，客机开门的状态会继续驱动房主权威碰撞体，避免出现“看似开门、实际被拉回”的情况。该修复仅对带 Collider 的动态 `InteractableObjectBinaryState` 采用 `AlwaysAnimate`，并在覆盖撤销、断线或会话重置时恢复原始动画剔除策略。

本版还修复了 SteamUser P2P `offlineOnly` 握手中 economy proof 使主机队列停留在 `hasProof=false` 的兼容路径，并将 35 秒连接 watchdog 改为观测告警，避免提前把仍在进行的原生握手标记为超时。

这是 Beta 预发布。当前用户回归已确认权限门碰撞；其它世界域及当前哈希的完整连接、审核、撤销和重连回归仍请配套提交双端 UMM 诊断包。
