using HarmonyLib;
using SDG.NetTransport.Loopback;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 背景：
    ///   24th 双机测试发现：主机 ESC 菜单时 timeScale=0.00 持续 33.32s（主机日志 L886-L988），
    ///   期间 VehicleManager.Update 几乎停滞（L891 timeSinceLastTick=18.53s），客机世界状态完全停滞。
    ///
    ///   vanilla listen host 行为：ESC 菜单打开时设置 Time.timeScale=0 + AudioListener.pause=true，
    ///   所有 Update() 方法的时间增量冻结，导致：
    ///     - ZombieManager.Update 不生成僵尸 / 不发送 zombie states
    ///     - ItemManager.Update 不刷新物品
    ///     - VehicleManager.Update 不刷新载具
    ///     - AnimalManager.Update 不刷新动物
    ///   listen host 是"主机客户端 + 服务器"同体，主机暂停 = 服务器暂停 = 客机世界停滞。
    ///
    ///   真实 pause 逻辑在 PlayerUI.updatePauseTimeScale()（PlayerUI.cs:1724-1736）：
    ///     private void updatePauseTimeScale()
    ///     {
    ///         if (Provider.isServer && (...menu active...))
    ///         {
    ///             Time.timeScale = 0f;
    ///             AudioListener.pause = true;
    ///         }
    ///         else
    ///         {
    ///             Time.timeScale = 1f;
    ///             AudioListener.pause = false;
    ///         }
    ///     }
    ///
    /// U3-SDK 溯源：
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Player/PlayerUI.cs:1724-1736 updatePauseTimeScale
    ///   - PlayerUI.cs:1726 条件：Provider.isServer && (MenuConfigurationOptionsUI.active || MenuConfigurationDisplayUI.active || ... || PlayerPauseUI.active)
    ///   - PlayerUI.cs:1728-1729 Time.timeScale = 0f; AudioListener.pause = true;
    ///   - PlayerUI.cs:1733-1734 Time.timeScale = 1f; AudioListener.pause = false;
    ///
    ///   Prefix patch PlayerUI.updatePauseTimeScale：
    ///   - 当 listen host + 有远端客机连接时，强制保持 Time.timeScale=1f + AudioListener.pause=false，
    ///     返回 false 跳过 vanilla pause 逻辑。
    ///   - 其他情况（单机 / 无远端客机）返回 true 执行 vanilla 原逻辑。
    ///
    ///   注意：listen host 的 Provider.isServer=true，所以 vanilla 的 if 分支会触发 pause。
    ///   我们只在 IsP2PServerActive + HasRemoteClients 时跳过 pause，保留单机 listen host 的 pause 体验。
    ///
    /// 安全性：
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 不修改 vanilla IL
    ///   - 仅在 listen host + 有远端客机时跳过 pause
    ///   - 单机模式 / 无客机 listen host 行为不变
    ///   - 显式设置 timeScale=1，避免遗留的 0 状态
    ///
    /// FACT.md 合规：
    ///   ✅ 未触碰 Dedicator.IsDedicatedServer
    ///   ✅ 未修改 vanilla IL（仅 Prefix 返回 false 跳过）
    ///   ✅ 单机模式行为完全不变
    /// </summary>
    public static class PlayerUIPauseTimeScalePatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static bool PrefixRegistered { get; private set; }
        public static bool PrefixOwnerVerified { get; private set; }
        public static string PrefixOwnerSummary { get; private set; } = "未自检";

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetMethodName = "updatePauseTimeScale";
        private const string PatchPrefixName = nameof(UpdatePauseTimeScale_Prefix);

        //   25th 测试 Prefix 自检通过但运行时未生效，需诊断日志验证
        private static int _prefixCallCount = 0;
        private static float _lastDiagLogTime = 0f;
        private static bool _lastPauseActive = false;

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]",
                "[P0-D-ESC] === 手动登记 Prefix（v0.2.3.36 P0-D-ESC ESC 暂停保持 world Update 修复）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[P0-D-ESC] !!! {RegistrationSummary}");
                return false;
            }

            try
            {
                // PlayerUI.updatePauseTimeScale 是 private instance void()
                //   U3-SDK: PlayerUI.cs:1724 private void updatePauseTimeScale()
                MethodInfo original = AccessTools.Method(typeof(PlayerUI), TargetMethodName);
                if (original == null)
                {
                    AllRegistrationsSucceeded = false;
                    RegistrationSummary = "updatePauseTimeScale AccessTools.Method 返回 null";
                    RoleLogger.Error("[Shared]", $"[P0-D-ESC] !!! {RegistrationSummary}");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(PlayerUIPauseTimeScalePatch), PatchPrefixName);
                if (prefix == null)
                {
                    AllRegistrationsSucceeded = false;
                    RegistrationSummary = "Prefix 方法未找到";
                    RoleLogger.Error("[Shared]", $"[P0-D-ESC] !!! {RegistrationSummary}");
                    return false;
                }

                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                PrefixRegistered = true;

                bool ownerOk = VerifyPatchOwner(original);
                if (!ownerOk)
                {
                    AllRegistrationsSucceeded = false;
                    RegistrationSummary = $"Prefix owner 自检失败 summary={PrefixOwnerSummary}";
                    RoleLogger.Error("[Shared]",
                        $"[P0-D-ESC] !!! DIAGNOSTIC BUILD INVALID: {RegistrationSummary}");
                    return false;
                }

                AllRegistrationsSucceeded = true;
                RegistrationSummary = $"prefix={PrefixRegistered}, prefixOwner={PrefixOwnerVerified}";
                RoleLogger.Info("[Shared]",
                    $"[P0-D-ESC] OK 手动登记成功 summary={RegistrationSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-D-ESC] !!! RegisterManual 异常: {ex}");
                return false;
            }
        }

        private static bool VerifyPatchOwner(MethodInfo original)
        {
            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                System.Collections.ICollection patches = info?.Prefixes as System.Collections.ICollection;

                if (patches == null || patches.Count == 0)
                {
                    PrefixOwnerVerified = false;
                    PrefixOwnerSummary = "prefixes count=0";
                    return false;
                }

                int ownCount = 0;
                bool methodMatched = false;
                int sameOwnerOtherMethodCount = 0;
                string firstForeignOwner = null;

                foreach (Patch p in patches)
                {
                    if (p.owner == HarmonyId)
                    {
                        ownCount++;
                        MethodInfo patchMethod = p.PatchMethod;
                        if (patchMethod != null
                            && patchMethod.DeclaringType == typeof(PlayerUIPauseTimeScalePatch)
                            && patchMethod.Name == PatchPrefixName)
                        {
                            methodMatched = true;
                        }
                        else
                        {
                            sameOwnerOtherMethodCount++;
                        }
                    }
                    else if (firstForeignOwner == null)
                    {
                        firstForeignOwner = p.owner;
                    }
                }

                string summary = $"ownCount={ownCount} methodMatched={methodMatched} sameOwnerOtherMethod={sameOwnerOtherMethodCount} foreignOwner={firstForeignOwner ?? "none"}";

                if (!methodMatched)
                {
                    PrefixOwnerVerified = false;
                    PrefixOwnerSummary = summary;
                    return false;
                }

                PrefixOwnerVerified = true;
                PrefixOwnerSummary = summary;
                return true;
            }
            catch (System.Exception ex)
            {
                PrefixOwnerVerified = false;
                PrefixOwnerSummary = $"异常: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Prefix：当 listen host + 有远端客机连接时，强制保持 timeScale=1 + AudioListener.pause=false，
        /// 返回 false 跳过 vanilla pause 逻辑。
        ///
        /// PlayerUI.updatePauseTimeScale 无参数无返回值，Prefix 无需声明参数。
        ///
        /// 注意：
        ///   - listen host 的 Provider.isServer=true，所以 vanilla 的 if 分支会触发 pause
        ///   - 我们只在 IsP2PServerActive + HasRemoteClients 时跳过 pause
        ///   - 单机模式 / 无客机 listen host 保留原 pause 体验
        ///   - 显式设置 timeScale=1，避免遗留的 0 状态（vanilla 之前可能已设为 0）
        /// </summary>
        /// <summary>
        ///   25th 测试 Prefix 自检通过但 timeScale=0.00 持续，无法从日志确定根因。
        ///
        /// 诊断逻辑：
        ///   - 状态变化时立即输出（menuActive / shouldIntervene / timeScale 变化）
        ///   - 每 5s 输出一次心跳日志
        ///   - 记录 Prefix 调用次数、isP2PActive、hasRemote、menuActive、shouldIntervene、timeScale
        ///
        /// 26th 测试后根据诊断日志确定具体修复方向：
        ///   - 若日志无 Prefix 调用 -> Prefix 未被 Harmony 调用，需检查注入
        ///   - 若日志有调用但 isP2PActive=false -> 检查 IsP2PServerActive 设置时机
        ///   - 若日志有调用但 hasRemote=false -> 修正 HasRemoteClients 逻辑
        ///   - 若日志有调用且 shouldIntervene=true 但 timeScale 仍为 0 -> 检查返回值是否被覆盖
        /// </summary>
        public static bool UpdatePauseTimeScale_Prefix()
        {
            _prefixCallCount++;

            bool isP2PActive = Host.HostManager.IsP2PServerActive;
            bool hasRemote = HasRemoteClients();
            bool menuActive = IsMenuUIActive();
            bool shouldIntervene = isP2PActive && hasRemote;

            //   isNowPaused = menuActive && !shouldIntervene（即 vanilla 会触发 pause 的条件）
            bool isNowPaused = menuActive && !shouldIntervene;
            if (isNowPaused != _lastPauseActive)
            {
                _lastPauseActive = isNowPaused;
                RoleLogger.Info("[Host]",
                    $"[P0-D-ESC-2] 状态变化 call#{_prefixCallCount} menuActive={menuActive} " +
                    $"isP2PActive={isP2PActive} hasRemote={hasRemote} shouldIntervene={shouldIntervene} " +
                    $"isNowPaused={isNowPaused} timeScale={Time.timeScale:F2}");
            }

            if (Time.realtimeSinceStartup - _lastDiagLogTime > 5f)
            {
                _lastDiagLogTime = Time.realtimeSinceStartup;
                RoleLogger.Info("[Host]",
                    $"[P0-D-ESC-2] 心跳 call#{_prefixCallCount} menuActive={menuActive} " +
                    $"isP2PActive={isP2PActive} hasRemote={hasRemote} shouldIntervene={shouldIntervene} " +
                    $"timeScale={Time.timeScale:F2}");
            }

            // 只在 listen host + 有远端客机时干预
            if (!shouldIntervene)
            {
                return true;  // 非主机 / 未启动 / 无远端客机 -> vanilla
            }

            // listen host + 有远端客机 -> 强制保持 world 运行
            // 显式设置 timeScale=1，避免遗留的 0 状态
            if (Time.timeScale != 1f)
            {
                Time.timeScale = 1f;
            }
            if (AudioListener.pause)
            {
                AudioListener.pause = false;
            }

            return false;  // 跳过 vanilla
        }

        /// <summary>
        /// 检查是否有远端客机连接（非 loopback）。
        ///   U3-SDK: Provider.cs 中 Provider.clients 是 List&lt;SteamPlayer&gt;
        ///   SteamPlayer.transportConnection 是 ITransportConnection
        ///   TransportConnection_Loopback 是 listen host 自己的本地"客机"
        /// </summary>
        private static bool HasRemoteClients()
        {
            try
            {
                if (Provider.clients == null)
                {
                    return false;
                }

                foreach (SteamPlayer client in Provider.clients)
                {
                    if (client == null) continue;
                    if (client.transportConnection == null) continue;

                    // 排除 loopback（listen host 自己的本地客户端）
                    if (client.transportConnection is TransportConnection_Loopback)
                    {
                        continue;
                    }

                    return true;  // 发现至少一个远端客机
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P0-D-ESC] HasRemoteClients 异常（返回 false）: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        ///
        /// U3-SDK 溯源：
        ///   D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Player/PlayerUI.cs:1724-1736
        ///   private void updatePauseTimeScale()
        ///   {
        ///       if (Provider.isServer && (MenuConfigurationOptionsUI.active || MenuConfigurationDisplayUI.active ||
        ///           MenuConfigurationGraphicsUI.active || MenuConfigurationControlsUI.active ||
        ///           PlayerPauseUI.audioMenu.active || PlayerPauseUI.active))
        ///       {
        ///           Time.timeScale = 0f;
        ///           AudioListener.pause = true;
        ///       }
        ///       else
        ///       {
        ///           Time.timeScale = 1f;
        ///           AudioListener.pause = false;
        ///       }
        ///   }
        ///
        /// 注意：vanilla 条件包含 6 个 UI，其中 PlayerPauseUI.audioMenu 是 internal static
        ///   （U3-SDK: Unturned/UI/Player/PlayerPauseUI.cs:709 internal static MenuConfigurationAudioUI audioMenu）
        ///   外部程序集无法访问，本方法只检查 5 个 public active 属性。
        ///   边界情况：玩家单独打开音频设置菜单时可能漏检，但主 ESC 暂停由 PlayerPauseUI.active 覆盖。
        /// </summary>
        private static bool IsMenuUIActive()
        {
            try
            {
                return (MenuConfigurationOptionsUI.active ||
                        MenuConfigurationDisplayUI.active ||
                        MenuConfigurationGraphicsUI.active ||
                        MenuConfigurationControlsUI.active ||
                        PlayerPauseUI.active);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P0-D-ESC-2] IsMenuUIActive 异常（返回 false）: {ex.Message}");
                return false;
            }
        }
    }
}
