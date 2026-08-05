using HarmonyLib;
using SDG.NetTransport;
using SDG.NetTransport.Loopback;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.35 P0-PlayerVisibility 修复（Codex 第二十三次双机测试外部审计 §4.1 授权实施）：
    ///
    /// 根因（U3-SDK 溯源，Codex 确认）：
    ///   - TransportConnection_Loopback.Send 抛 NotSupportedException：
    ///     D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/NetTransport_Loopback/TransportConnection_Loopback.cs:52-55
    ///   - Provider.accept 第二个 foreach 向所有已有客户端广播 PlayerConnected，包括 listen host 自己的 loopback：
    ///     D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Provider/Provider.cs:4924-4945
    ///   - SendMessageToClient 签名（NetMessages 是 internal static class）：
    ///     D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/NetMessaging/NetMessages.cs:25-48
    ///       public static void SendMessageToClient(EClientMessage index, ENetReliability reliability,
    ///         ITransportConnection transportConnection, ClientWriteHandler callback)
    ///
    /// 现象（第二十三次测试决定性证据）：
    ///   - 主机日志 L916/L2473/L3629 三次抛出：
    ///     [Error] SendMessageToClient THREW msg=PlayerConnected(5) transport=TransportConnection_Loopback exceptionType=NotSupportedException
    ///   - 客机连入后客机可以看见主机，但主机看不见客机（客机模型未在主机端注册）
    ///
    /// 修复方案（Codex §4.1 收窄版）：
    ///   Prefix patch NetMessages.SendMessageToClient，仅当目标为 PlayerConnected + TransportConnection_Loopback 时跳过。
    ///   不全局跳过所有 loopback 消息，避免误伤其他必要的本地客户端消息（如 SendInitialGlobalState 等）。
    ///
    ///   Codex 审计原话（§4.1）：
    ///     "不要使用全局跳过所有 TransportConnection_Loopback 的方案 A。推荐最小侵入方案：
    ///      仅拦截 PlayerConnected 向 loopback 发送，避免全局跳过导致本地客户端丢失其他关键消息。"
    ///
    /// 实现细节：
    ///   - NetMessages 是 internal static class，无法用 typeof(NetMessages) 访问
    ///   - 使用 AccessTools.TypeByName("SDG.Unturned.NetMessages") 获取 Type
    ///   - 使用 AccessTools.Method(type, "SendMessageToClient", paramTypes) 获取 MethodInfo
    ///   - Prefix 参数只声明需要的 index + transportConnection（Harmony 参数注入按名字匹配）
    ///
    /// 栈/语义安全性：
    ///   - Prefix 返回 false 跳过 vanilla 方法，不影响其他消息路径
    ///   - 不修改 transportConnection 状态
    ///   - 不触碰 Dedicator.IsDedicatedServer（FACT.md 铁律合规）
    ///
    /// 禁止项：
    ///   - 禁止扩展为全局跳过所有 loopback 消息（Codex §4.1 明确禁止）
    ///   - 禁止在此外拦截其他 EClientMessage（若需扩展必须重新审计）
    /// </summary>
    public static class NetMessagesPlayerConnectedLoopbackPatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static bool PrefixRegistered { get; private set; }
        public static bool PrefixOwnerVerified { get; private set; }
        public static string PrefixOwnerSummary { get; private set; } = "未自检";

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetTypeName = "SDG.Unturned.NetMessages";
        private const string TargetMethodName = "SendMessageToClient";
        private const string PatchPrefixName = nameof(SendMessageToClient_Prefix);

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]",
                "[P0-PlayerVisibility] === 手动登记 Prefix（v0.2.3.35 P0-PlayerVisibility 客机模型可见性修复）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[P0-PlayerVisibility] !!! {RegistrationSummary}");
                return false;
            }

            try
            {
                System.Type netMessagesType = AccessTools.TypeByName(TargetTypeName);
                if (netMessagesType == null)
                {
                    AllRegistrationsSucceeded = false;
                    RegistrationSummary = $"TypeByName({TargetTypeName}) 返回 null";
                    RoleLogger.Error("[Shared]", $"[P0-PlayerVisibility] !!! {RegistrationSummary}");
                    return false;
                }

                // SendMessageToClient 签名：
                //   (EClientMessage index, ENetReliability reliability, ITransportConnection transportConnection, ClientWriteHandler callback)
                // ClientWriteHandler 是 internal delegate，使用 AccessTools 加载类型
                System.Type clientWriteHandlerType = AccessTools.TypeByName("SDG.Unturned.NetMessages+ClientWriteHandler")
                    ?? AccessTools.TypeByName("NetMessages+ClientWriteHandler");

                MethodInfo original;
                if (clientWriteHandlerType != null)
                {
                    original = AccessTools.Method(netMessagesType, TargetMethodName,
                        new System.Type[]
                        {
                            typeof(EClientMessage),
                            typeof(ENetReliability),
                            typeof(ITransportConnection),
                            clientWriteHandlerType
                        });
                }
                else
                {
                    // 回退：按方法名查找（不指定参数类型，容错）
                    original = AccessTools.Method(netMessagesType, TargetMethodName);
                }

                if (original == null)
                {
                    AllRegistrationsSucceeded = false;
                    RegistrationSummary = "SendMessageToClient AccessTools.Method 返回 null";
                    RoleLogger.Error("[Shared]", $"[P0-PlayerVisibility] !!! {RegistrationSummary}");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(NetMessagesPlayerConnectedLoopbackPatch), PatchPrefixName);
                if (prefix == null)
                {
                    AllRegistrationsSucceeded = false;
                    RegistrationSummary = "Prefix 方法未找到";
                    RoleLogger.Error("[Shared]", $"[P0-PlayerVisibility] !!! {RegistrationSummary}");
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
                        $"[P0-PlayerVisibility] !!! DIAGNOSTIC BUILD INVALID: {RegistrationSummary}");
                    return false;
                }

                AllRegistrationsSucceeded = true;
                RegistrationSummary = $"prefix={PrefixRegistered}, prefixOwner={PrefixOwnerVerified}";
                RoleLogger.Info("[Shared]",
                    $"[P0-PlayerVisibility] OK 手动登记成功 summary={RegistrationSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-PlayerVisibility] !!! RegisterManual 异常: {ex}");
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
                            && patchMethod.DeclaringType == typeof(NetMessagesPlayerConnectedLoopbackPatch)
                            && patchMethod.Name == PatchPrefixName)
                        {
                            methodMatched = true;
                        }
                        else
                        {
                            // 同 owner 但不同 PatchMethod（如 D-5 NetMessagesSendDiagnosticPatch.SendMessageToClient_Prefix）
                            // 这是合法共存，不阻断
                            sameOwnerOtherMethodCount++;
                        }
                    }
                    else if (firstForeignOwner == null)
                    {
                        firstForeignOwner = p.owner;
                    }
                }

                string summary = $"ownCount={ownCount} methodMatched={methodMatched} sameOwnerOtherMethod={sameOwnerOtherMethodCount} foreignOwner={firstForeignOwner ?? "none"}";

                // 修复（Codex 第二十四次测试中止后定位）：
                //   原 ownCount != 1 检查过于严格。NetMessages.SendMessageToClient 已有历史 D-5 诊断 Prefix
                //   （NetMessagesSendDiagnosticPatch.SendMessageToClient_Prefix，同 owner=HarmonyId）。
                //   两个同 owner 但不同 PatchMethod 的 Prefix 合法共存，不应阻断。
                //   正确的检查：methodMatched=true（我的 Prefix 已登记）即可。
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
        /// Prefix：仅拦截 PlayerConnected 向 TransportConnection_Loopback 发送的情况。
        ///
        /// 参数名必须匹配 vanilla NetMessages.SendMessageToClient 签名：
        ///   EClientMessage index, ENetReliability reliability, ITransportConnection transportConnection, ClientWriteHandler callback
        ///
        /// Harmony 参数注入按名字匹配，只声明需要的参数（index + transportConnection）。
        /// 返回 false 跳过 vanilla 方法；返回 true 继续原逻辑。
        ///
        /// 注意：不使用 [HarmonyPatch] 属性（NetMessages 是 internal，typeof 不可访问），
        /// 通过 RegisterManual 手动登记。
        /// </summary>
        public static bool SendMessageToClient_Prefix(
            EClientMessage index,
            ITransportConnection transportConnection)
        {
            if (index == EClientMessage.PlayerConnected
                && transportConnection is TransportConnection_Loopback)
            {
                RoleLogger.Info("[Host]",
                    "[P0-PlayerVisibility] Skipped PlayerConnected to TransportConnection_Loopback");
                return false;
            }
            return true;
        }
    }
}
