using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("SteamP2PFriends")]
[assembly: AssemblyDescription("SteamP2PFriends v0.2.3.60 listen-host inventory UI projection repair candidate.")]
[assembly: AssemblyCompany("YU80Rice")]
[assembly: AssemblyProduct("SteamP2PFriends")]
[assembly: AssemblyCopyright("MIT")]
[assembly: ComVisible(false)]
[assembly: Guid("b3c4d5e6-f7a8-9012-3456-789abcdef012")]
[assembly: AssemblyVersion("0.2.3.60")]
[assembly: AssemblyFileVersion("0.2.3.60")]
// Stage 7-2-2（Codex 133rd §3）：纯单元测试项目 InternalsVisibleTo
//   蓝图：仅此一项 InternalsVisibleTo 条目授权；测试项目不启动 Unturned、不触碰 Provider/Steam API/Unity/文件系统
[assembly: InternalsVisibleTo("SteamP2PFriends.WhitelistTests")]
