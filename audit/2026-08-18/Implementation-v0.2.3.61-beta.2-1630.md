# v0.2.3.61-beta.2 发布执行记录

## 发布范围

- 发布标识：`v0.2.3.61-beta.2`。
- 插件程序集版本：`0.2.3.61`。
- 发布对象：SteamUser P2P listen-host 本地多人联机插件。
- 测试归档：`TestLogs/artifacts/Beta2-P2P-AHost-20260818-1300/evidence-summary.json` 为 `AllOK=true`。
- 开发者决定：部署、双端联机验证和原始日志归档由人工控制；CFG/DLL 哈希助手仅为可选归档辅助，不构成额外发布门。

## 源码与文档溯源

| 项目 | 落实位置 |
| --- | --- |
| 版本 `0.2.3.61` | `Properties/AssemblyInfo.cs` |
| 发布变更 | `CHANGELOG.md` 的 `v0.2.3.61-beta.2` 条目 |
| 安装与当前发布状态 | `README.md` |
| 可选 CFG/DLL 哈希与独立日志归档说明 | `TestLogs/README.md` |
| 发布包 | `publish/SteamP2PFriends-v0.2.3.61-beta.2.zip` |

## 验证记录

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' SteamP2PFriends.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /m
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' WhitelistTests\SteamP2PFriends.WhitelistTests.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /m
.\WhitelistTests\bin\Release\SteamP2PFriends.WhitelistTests.exe
```

- 插件 Release 重建：`0 errors / 0 warnings`。
- WhitelistTests Release 重建：`0 errors / 0 warnings`。
- 自动化测试：`268 / 268 PASS`。
- TestLogs PowerShell 解析、CFG 归档和证据校验测试：PASS。
- Release DLL SHA-256：`3031C999138E850AED61636032B1580FAFBC6DC35B2F1F3D673262C43C67FC89`。
- ZIP SHA-256：`A9212F4467BC8442624077A4EF45F859D6B8C1733FD650CB037E180159E8AD5A`。
- ZIP 内容：仅 `BepInEx/plugins/SteamP2PFriends.dll`；压缩包内 DLL SHA-256 与 Release DLL 一致。

## 独立审核

- 审核结论：PASS。
- 新 CFG/DLL 归档链仅处理双方 DLL 与 Default CFG；不读取或判定游戏日志。
- 旧双端日志归档链保留为独立可选能力，仅允许在 Unturned 已退出后执行，不写入新的 `evidence-summary.json`。

## 交付结论

`v0.2.3.61-beta.2` 的源码、文档与发布 ZIP 已对齐，可提交并发布为 GitHub 最新预发布版。
