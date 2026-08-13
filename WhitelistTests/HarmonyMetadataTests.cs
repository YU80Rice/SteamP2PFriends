using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Patches;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-3 v3/v4 §4[指令 D] Harmony 元数据自检。
    /// 蓝图 v3 §3.3 + v4 [指令 D]：
    ///   H1：验证 P2PWhitelistRequestCapturePatch 注册元数据正确（attribute + Prefix 签名）。
    ///   H2：真实 Harmony 实例 patch 后用 GetPatchInfo 验证 owner 与 Prefix MethodInfo
    ///       （证明激活，非仅 attribute）。
    /// </summary>
    internal static class HarmonyMetadataTests
    {
        // H2 专用 Harmony ID（隔离测试，避免污染生产 HARMONY_ID）
        private const string H2_HARMONY_ID = "com.yu80rice.steamp2pfriends.test.h2";
        private const string H3_HARMONY_ID = "com.yu80rice.steamp2pfriends.test.h3";
        private const string H4_HARMONY_ID = "com.yu80rice.steamp2pfriends.test.h4";
        private const string H6_HARMONY_ID = "com.yu80rice.steamp2pfriends.test.h6";
        private const string H9_HARMONY_ID = "com.yu80rice.steamp2pfriends.test.h9.stage92";
        private const string H10_HARMONY_ID = "com.yu80rice.steamp2pfriends.test.h10.stage10death";
        private const string H11_HARMONY_ID = "com.yu80rice.steamp2pfriends.test.h11.stage10avatar";

        // H2 测试目标方法（签名与 Provider.reject 一致，便于复用 Prefix MethodInfo 验证机制）
        // 标记为 public static void 以匹配 Harmony Prefix 注入约定
        public static void H2TargetMethod(ITransportConnection conn, ESteamRejection rejection)
        {
            // 测试目标方法体无关紧要；仅作为 patch 锚点
        }

        public sealed class H3PendingTarget
        {
            public H3PendingTarget(object transport, SteamPlayerID playerID)
            {
            }
        }

        public static void H6RejectTarget(
            ITransportConnection transportConnection,
            ESteamRejection rejection,
            string explanation)
        {
        }

        public static void H10DeathCommitTarget(byte amount, UnityEngine.Vector3 newRagdoll,
            EDeathCause newCause, ELimb newLimb, CSteamID newKiller, out EPlayerKill kill,
            bool trackKill, ERagdollEffect newRagdollEffect, bool canCauseBleeding)
        {
            kill = EPlayerKill.NONE;
        }

        // Stage 9-2 H9 surrogate: signature-equivalent to vanilla TryGetQueryPort(out ushort).
        // Independent test CLR cannot detour the game assembly method, so this surrogate anchors
        // the real Harmony binding of the production Postfix (ref bool __result + ref ushort __0).
        public static bool H9QueryPortTarget(out ushort queryPort)
        {
            queryPort = 27015;
            return true;
        }

        internal static bool Test_v3_Harmony_Metadata_SelfCheck()
        {
            Type patchType = typeof(P2PWhitelistRequestCapturePatch);

            // 1. HarmonyPatch 属性存在（Harmony 2.x 中类名就是 HarmonyPatch，无 Attribute 后缀）
            var harmonyAttrs = patchType.GetCustomAttributes<HarmonyPatch>().ToList();
            if (harmonyAttrs.Count == 0)
                return Fail("P2PWhitelistRequestCapturePatch must have [HarmonyPatch] attribute", "no attributes");

            // 2. 找到目标 Provider.reject(ITransportConnection, ESteamRejection) 的 patch
            HarmonyPatch targetAttr = null;
            foreach (var attr in harmonyAttrs)
            {
                if (attr.info.declaringType == typeof(Provider) && attr.info.methodName == "reject")
                {
                    targetAttr = attr;
                    break;
                }
            }
            if (targetAttr == null)
                return Fail("missing [HarmonyPatch(typeof(Provider), \"reject\", ...)]", "no target attr");

            // 3. 参数类型匹配 (ITransportConnection, ESteamRejection)
            var paramTypes = targetAttr.info.argumentTypes;
            if (paramTypes == null || paramTypes.Length != 2)
                return Fail("argumentTypes must have 2 entries", "len=" + (paramTypes?.Length ?? -1));
            if (paramTypes[0] != typeof(ITransportConnection))
                return Fail("param[0] must be ITransportConnection", paramTypes[0]?.Name);
            if (paramTypes[1] != typeof(ESteamRejection))
                return Fail("param[1] must be ESteamRejection", paramTypes[1]?.Name);

            // 4. Prefix 方法存在且签名正确
            MethodInfo prefix = patchType.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static);
            if (prefix == null)
                return Fail("public static Prefix method not found", "null");

            ParameterInfo[] prefixParams = prefix.GetParameters();
            if (prefixParams.Length != 2)
                return Fail("Prefix must have 2 parameters", "len=" + prefixParams.Length);
            if (prefixParams[0].ParameterType != typeof(ITransportConnection))
                return Fail("Prefix param[0] must be ITransportConnection", prefixParams[0].ParameterType.Name);
            if (prefixParams[1].ParameterType != typeof(ESteamRejection))
                return Fail("Prefix param[1] must be ESteamRejection", prefixParams[1].ParameterType.Name);

            // 5. Prefix 返回 void（不阻断原版 reject）
            if (prefix.ReturnType != typeof(void))
                return Fail("Prefix must return void (don't suppress vanilla reject)", prefix.ReturnType.Name);

            // 6. Prefix 必须是 public static（Harmony 要求）
            if (!prefix.IsPublic)
                return Fail("Prefix must be public", "isPublic=false");
            if (!prefix.IsStatic)
                return Fail("Prefix must be static", "isStatic=false");

            return true;
        }

        /// <summary>
        /// H2（蓝图 v4 [指令 D/F]）：真实 Harmony 实例 patch 后用 GetPatchInfo 验证 owner 与 Prefix MethodInfo。
        /// 在 H2TargetMethod 上挂一个测试 Prefix，复现生产 VerifyP2PWhitelistCaptureRegistration 的验证机制
        /// （Harmony.GetPatchInfo -> Prefixes -> owner + PatchMethod）。
        /// </summary>
        internal static bool Test_v4_H2_RealHarmonyGetPatchInfo_VerifiesOwnerAndMethod()
        {
            MethodInfo target = typeof(HarmonyMetadataTests).GetMethod(
                nameof(H2TargetMethod), BindingFlags.Public | BindingFlags.Static);
            if (target == null)
                return Fail("H2 target method not found", "null");

            // 测试 Prefix（签名与目标匹配）
            MethodInfo testPrefix = typeof(HarmonyMetadataTests).GetMethod(
                nameof(H2TestPrefix), BindingFlags.Public | BindingFlags.Static);
            if (testPrefix == null)
                return Fail("H2 test prefix not found", "null");

            Harmony harmony = null;
            try
            {
                harmony = new Harmony(H2_HARMONY_ID);
            }
            catch (Exception ex)
            {
                // 解包 TypeInitializationException 以输出真实根因
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("Harmony instance init failed", root.GetType().Name + ": " + root.Message);
            }

            // 清理可能的残留 patch（幂等）
            try { harmony.Unpatch(target, HarmonyPatchType.Prefix, H2_HARMONY_ID); } catch { }

            try
            {
                // 真实 patch：挂入测试 Prefix
                harmony.Patch(target, prefix: new HarmonyMethod(testPrefix));

                // 用 Harmony.GetPatchInfo 读取运行时 patch info（生产验证机制）
                HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
                if (info == null)
                    return Fail("GetPatchInfo should return non-null after patch", "null");
                if (info.Prefixes == null || info.Prefixes.Count == 0)
                    return Fail("Prefixes should have at least 1 entry", "count=" + (info.Prefixes?.Count ?? -1));

                // 验证 owner + PatchMethod（与生产 VerifyP2PWhitelistCaptureRegistration 相同逻辑）
                bool found = false;
                foreach (Patch p in info.Prefixes)
                {
                    if (p.owner == H2_HARMONY_ID && p.PatchMethod == testPrefix)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return Fail("GetPatchInfo should contain owner + Prefix PatchMethod",
                        "owners=" + string.Join(",", info.Prefixes.Select(p => p.owner)));

                // 负向验证：错误的 owner 不应匹配
                bool wrongOwner = false;
                foreach (Patch p in info.Prefixes)
                {
                    if (p.owner == "wrong.owner" && p.PatchMethod == testPrefix)
                    {
                        wrongOwner = true;
                        break;
                    }
                }
                if (wrongOwner)
                    return Fail("wrong owner should not match", "matched wrong.owner");

                return true;
            }
            catch (Exception ex)
            {
                // 解包 TypeInitializationException 以输出真实根因
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("H2 patch/getpatchinfo failed", root.GetType().Name + ": " + root.Message);
            }
            finally
            {
                // 清理：移除测试 patch，避免污染其他测试
                try { harmony.Unpatch(target, HarmonyPatchType.All, H2_HARMONY_ID); } catch { }
            }
        }

        // H2 测试 Prefix（与 H2TargetMethod 签名匹配）
        public static void H2TestPrefix(ITransportConnection conn, ESteamRejection rejection)
        {
            // 测试 Prefix 体无关紧要
        }

        internal static bool Test_v5_H3_PendingIdentityConstructorPatchActivates()
        {
            Type patchType = typeof(P2PPendingIdentityCapturePatch);
            MethodInfo targetMethods = patchType.GetMethod("TargetMethods", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo postfix = patchType.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            if (targetMethods == null || postfix == null)
                return Fail("pending identity patch methods missing", "targetMethods=" + (targetMethods != null) + " postfix=" + (postfix != null));

            var targets = ((IEnumerable<MethodBase>)targetMethods.Invoke(null, null)).ToList();
            if (targets.Count != 1 || !(targets[0] is ConstructorInfo))
                return Fail("pending identity patch should resolve exactly one constructor", "count=" + targets.Count);

            ConstructorInfo surrogate = typeof(H3PendingTarget).GetConstructor(
                new[] { typeof(object), typeof(SteamPlayerID) });
            if (surrogate == null)
                return Fail("H3 surrogate constructor missing", "null");

            Harmony harmony = new Harmony(H3_HARMONY_ID);
            try
            {
                // Assembly-CSharp 的真实 SteamPending 构造函数含 Unity ECall 参数，无法在独立
                // 控制台 CLR 中 detour；先验证真实目标解析，再用等价的第二参数 SteamPlayerID
                // 构造函数验证生产 Postfix 的 Harmony __1 绑定和注册元数据。
                harmony.Patch(surrogate, postfix: new HarmonyMethod(postfix));
                HarmonyLib.Patches info = Harmony.GetPatchInfo(surrogate);
                bool found = info != null && info.Postfixes.Any(p => p.owner == H3_HARMONY_ID && p.PatchMethod == postfix);
                if (!found) return Fail("pending identity constructor postfix not active", "missing owner/method");
                return true;
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("pending identity constructor patch failed", root.GetType().Name + ": " + root.Message);
            }
            finally
            {
                try { harmony.Unpatch(surrogate, HarmonyPatchType.All, H3_HARMONY_ID); } catch { }
            }
        }

        internal static bool Test_v5_H4_GroupProbeComplexSignaturesActivate()
        {
            MethodInfo receive = AccessTools.Method(typeof(PlayerQuests), nameof(PlayerQuests.ReceiveGroupState),
                new[] { typeof(CSteamID), typeof(EPlayerGroupRank) });
            MethodInfo receivePrefix = typeof(P2PGroupStateProbe_ReceiveGroupState).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo receivePostfix = typeof(P2PGroupStateProbe_ReceiveGroupState).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo initialPrefix = typeof(P2PGroupStateProbe_SendInitialPlayerState).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo initialPostfix = typeof(P2PGroupStateProbe_SendInitialPlayerState).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);

            MethodBase[] initialTargets = typeof(PlayerQuests).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "SendInitialPlayerState").Cast<MethodBase>().ToArray();

            if (receive == null || receivePrefix == null || receivePostfix == null || initialPrefix == null || initialPostfix == null)
                return Fail("group probe method resolution failed", "null method");
            if (initialTargets.Length != 2)
                return Fail("expected two SendInitialPlayerState overloads", "count=" + initialTargets.Length);

            Harmony harmony = new Harmony(H4_HARMONY_ID);
            var patched = new List<MethodBase>();
            try
            {
                harmony.Patch(receive, new HarmonyMethod(receivePrefix), new HarmonyMethod(receivePostfix));
                patched.Add(receive);
                for (int i = 0; i < initialTargets.Length; i++)
                {
                    harmony.Patch(initialTargets[i], new HarmonyMethod(initialPrefix), new HarmonyMethod(initialPostfix));
                    patched.Add(initialTargets[i]);
                }

                for (int i = 0; i < patched.Count; i++)
                {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(patched[i]);
                    if (info == null || !info.Prefixes.Any(p => p.owner == H4_HARMONY_ID) ||
                        !info.Postfixes.Any(p => p.owner == H4_HARMONY_ID))
                        return Fail("group probe prefix/postfix not active", patched[i].Name);
                }
                return true;
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("group probe complex signature patch failed", root.GetType().Name + ": " + root.Message);
            }
            finally
            {
                for (int i = 0; i < patched.Count; i++)
                {
                    try { harmony.Unpatch(patched[i], HarmonyPatchType.All, H4_HARMONY_ID); } catch { }
                }
            }
        }

        internal static bool Test_v6_H6_RejectPendingIdentityPrefixActivates()
        {
            MethodInfo target = AccessTools.Method(typeof(Provider), "reject",
                new[] { typeof(ITransportConnection), typeof(ESteamRejection), typeof(string) });
            MethodInfo prefix = typeof(P2PRejectPendingIdentityCapturePatch).GetMethod(
                "Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null)
                return Fail("reject pending identity method resolution failed", "target=" + (target != null) + " prefix=" + (prefix != null));

            MethodInfo surrogate = typeof(HarmonyMetadataTests).GetMethod(
                nameof(H6RejectTarget), BindingFlags.Public | BindingFlags.Static);
            if (surrogate == null) return Fail("H6 surrogate target missing", "null");

            Harmony harmony = new Harmony(H6_HARMONY_ID);
            try
            {
                // 独立测试 CLR 缺少 Assembly-CSharp 的 BattlEye 依赖，无法 detour 真实
                // Provider.reject；先精确验证真实目标，再在等价签名上验证生产 Prefix。
                harmony.Patch(surrogate, prefix: new HarmonyMethod(prefix));
                HarmonyLib.Patches info = Harmony.GetPatchInfo(surrogate);
                bool found = info != null && info.Prefixes.Any(p => p.owner == H6_HARMONY_ID && p.PatchMethod == prefix);
                if (!found) return Fail("reject pending identity prefix not active", "missing owner/method");
                return true;
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("reject pending identity patch failed", root.GetType().Name + ": " + root.Message);
            }
            finally
            {
                try { harmony.Unpatch(surrogate, HarmonyPatchType.All, H6_HARMONY_ID); } catch { }
            }
        }

        internal static bool Test_Alpha_H7_AuthorityProbe16ExactTargetsResolve()
        {
            try
            {
                bool resolved = InventoryWorldAuthorityProbe.VerifyTargetSignatures();
                if (!resolved)
                    return Fail("authority probe exact target resolution failed", InventoryWorldAuthorityProbe.TargetSignatureSummary);
                return InventoryWorldAuthorityProbe.TargetSignatureSummary == "resolved=16/16"
                    || Fail("authority probe resolved count mismatch", InventoryWorldAuthorityProbe.TargetSignatureSummary);
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("authority probe target resolution threw", root.GetType().Name + ": " + root.Message);
            }
        }

        internal static bool Test_Alpha_H8_ItemAuthorityGateExactHooksResolve()
        {
            try
            {
                return AuthoritativeItemGenerationGatePatch.VerifyTargetSignatures()
                    || Fail("item authority gate target resolution failed", "generateItems + Prefix/Postfix/Finalizer");
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("item authority gate target resolution threw", root.GetType().Name + ": " + root.Message);
            }
        }

        /// <summary>
        /// H9（Stage 9-2 [指令 D]）：真实 Harmony 激活测试，证明生产
        /// DirectIpSinglePortQueryPortPatch.Postfix（ref bool __result + ref ushort __0）
        /// 能被 Harmony 真正绑定和登记，并用 GetPatchInfo 核验专用 owner + 生产 Postfix MethodInfo。
        /// 独立 CLR 不能 detour 游戏程序集方法，因此先验证真实 TargetMethod() 解析，
        /// 再用等价签名的 H9QueryPortTarget surrogate 验证生产 Postfix 的 Harmony 绑定。
        /// </summary>
        internal static bool Test_H9_Stage92QueryPortPostfixActivates()
        {
            MethodBase realOriginal = DirectIpSinglePortQueryPortPatch.TargetMethod();
            if (realOriginal == null ||
                realOriginal.DeclaringType != typeof(
                    SDG.NetTransport.SteamNetworkingSockets.ClientTransport_SteamNetworkingSockets))
                return Fail("Stage9-2 real target resolution failed", "null/wrong type");

            ParameterInfo[] realParameters = realOriginal.GetParameters();
            if (realParameters.Length != 1 ||
                realParameters[0].ParameterType != typeof(ushort).MakeByRefType())
                return Fail("Stage9-2 real target ABI mismatch", realOriginal.ToString());

            MethodInfo productionPostfix = typeof(DirectIpSinglePortQueryPortPatch).GetMethod(
                nameof(DirectIpSinglePortQueryPortPatch.Postfix),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo surrogate = typeof(HarmonyMetadataTests).GetMethod(
                nameof(H9QueryPortTarget), BindingFlags.Static | BindingFlags.Public);
            if (productionPostfix == null || surrogate == null)
                return Fail("Stage9-2 activation methods missing", "postfix/surrogate null");

            Harmony harmony = new Harmony(H9_HARMONY_ID);
            try
            {
                harmony.Patch(surrogate, postfix: new HarmonyMethod(productionPostfix));
                HarmonyLib.Patches info = Harmony.GetPatchInfo(surrogate);
                bool exact = info != null && info.Postfixes.Any(p =>
                    p.owner == H9_HARMONY_ID && p.PatchMethod == productionPostfix);
                if (!exact)
                    return Fail("Stage9-2 production Postfix not active", "owner/method missing");
                return true;
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("Stage9-2 Harmony binding failed",
                    root.GetType().Name + ": " + root.Message);
            }
            finally
            {
                try { harmony.Unpatch(surrogate, HarmonyPatchType.All, H9_HARMONY_ID); }
                catch { }
            }
        }

        internal static bool Test_H10_Stage10DeathCommitPatchActivates()
        {
            MethodBase realOriginal = P2PWorldDeathCommitPatch.TargetMethod();
            if (realOriginal == null || realOriginal.DeclaringType != typeof(PlayerLife) ||
                realOriginal.Name != "doDamage")
                return Fail("Stage10 death commit real target resolution failed", "null/wrong target");

            MethodInfo productionPrefix = typeof(P2PWorldDeathCommitPatch).GetMethod(
                nameof(P2PWorldDeathCommitPatch.Prefix),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo productionPostfix = typeof(P2PWorldDeathCommitPatch).GetMethod(
                nameof(P2PWorldDeathCommitPatch.Postfix),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo surrogate = typeof(HarmonyMetadataTests).GetMethod(
                nameof(H10DeathCommitTarget), BindingFlags.Static | BindingFlags.Public);
            if (productionPrefix == null || productionPostfix == null || surrogate == null)
                return Fail("Stage10 death commit activation methods missing", "prefix/postfix/surrogate null");

            Harmony harmony = new Harmony(H10_HARMONY_ID);
            try
            {
                harmony.Patch(surrogate, new HarmonyMethod(productionPrefix),
                    new HarmonyMethod(productionPostfix));
                HarmonyLib.Patches info = Harmony.GetPatchInfo(surrogate);
                bool prefixExact = info != null && info.Prefixes.Any(p =>
                    p.owner == H10_HARMONY_ID && p.PatchMethod == productionPrefix);
                bool postfixExact = info != null && info.Postfixes.Any(p =>
                    p.owner == H10_HARMONY_ID && p.PatchMethod == productionPostfix);
                return prefixExact && postfixExact;
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("Stage10 death commit Harmony binding failed",
                    root.GetType().Name + ": " + root.Message);
            }
            finally
            {
                try { harmony.Unpatch(surrogate, HarmonyPatchType.All, H10_HARMONY_ID); }
                catch { }
            }
        }

        internal static bool Test_H11_Stage10AvatarPatchActivates()
        {
            MethodInfo productionPrefix = typeof(P2PWorldChatAvatarPatch).GetMethod(
                nameof(P2PWorldChatAvatarPatch.PrefixProject),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodBase[] targets = P2PWorldChatAvatarPatch.TargetMethods().ToArray();
            if (productionPrefix == null || targets.Length != 2 || targets.Any(t => t == null))
                return Fail("Stage10 avatar activation methods missing", "prefix/targets invalid");

            Harmony harmony = new Harmony(H11_HARMONY_ID);
            try
            {
                foreach (MethodBase target in targets)
                    harmony.Patch(target, new HarmonyMethod(productionPrefix));
                return targets.All(target =>
                {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
                    return info != null && info.Prefixes.Any(p =>
                        p.owner == H11_HARMONY_ID && p.PatchMethod == productionPrefix);
                });
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("Stage10 avatar Harmony binding failed",
                    root.GetType().Name + ": " + root.Message);
            }
            finally
            {
                foreach (MethodBase target in targets)
                {
                    try { harmony.Unpatch(target, HarmonyPatchType.All, H11_HARMONY_ID); }
                    catch { }
                }
            }
        }

        private static bool Fail(string msg, string detail)
        {
            Console.WriteLine("    FAIL: " + msg + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }
    }
}
