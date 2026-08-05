using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.24 P0-S4 修复：客机模型"除衣服外全黑"根因修复。
    ///
    /// 根因（Codex 外部审计 §1-2 确认）：
    ///   PlayerClothing.load() 未设置 thirdClothes.skin/face/hair/beard/color/BeardColor，
    ///   HumanClothes._skinColor 保持默认 Color(0,0,0,0)，apply() 把 material._SkinColor 写为黑，
    ///   StandardClothes shader 皮肤裸露区域取 _SkinColor = 黑。
    ///
    /// 修复（审计 §3.1 修订后方案 A）：
    ///   在 load() Prefix 中复制 channel.owner 的 6 个外观字段到 thirdClothes，
    ///   vanilla load() 随后调用 apply() 时 skinColorDirty 仍为 true（Awake 中 markAllDirty(true) 设置），
    ///   apply() 会把正确的 skin 写入 material。
    ///
    /// 门控（严格，审计 §5 要求）：
    ///   - Provider.isServer && !Dedicator.IsDedicatedServer（listen server）
    ///   - HostManager.IsP2PHostMode（P2P 模式）
    ///   - 客机实例（owner.playerID.steamID != Provider.user，排除房主自连）
    ///   - owner.skin.a > 0.001f（数据源已就绪，否则 fail-safe 跳过 + 报警，不伪造默认皮肤）
    ///
    /// 严格禁止（审计 §5）：
    ///   - 不调用 NotifyClothingIsVisible/ReceiveClothingState/updateClothes/额外 apply
    ///   - 不修改网络协议、不广播额外 clothing state
    ///   - 不反射读取 thirdClothes/channel（都是公开属性）
    ///
    /// 一次性诊断（审计 §5 要求）：
    ///   - owner face/hair/beard
    ///   - owner skin RGBA（source）
    ///   - thirdClothes skin before（Prefix 读取）
    ///   - thirdClothes skin after（Postfix 读取，apply 已执行）
    ///   - material _SkinColor after：N/A（HumanClothes.materialClothing 为 private，不反射读取）
    /// </summary>
    [HarmonyPatch(typeof(PlayerClothing), nameof(PlayerClothing.load))]
    public static class PlayerClothingLoadAppearanceFixPatch
    {
        private static readonly ConditionalWeakTable<PlayerClothing, DiagEntry> _diagEntries = new();

        public static bool PrefixRegisteredOnce { get; private set; }
        public static bool PostfixRegisteredOnce { get; private set; }
        public static bool AllRegistrationsSucceeded => PrefixRegisteredOnce && PostfixRegisteredOnce;

        /// <summary>
        /// 自检验证：PlayerClothing.load 上的 Prefix/Postfix 各登记一次（owner=HARMONY_ID）。
        /// 由 SteamP2PFriendsPlugin 自检调用，失败时聚合到 DiagnosticBuildValid=false。
        /// </summary>
        public static bool VerifyRegistration()
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerClothing), nameof(PlayerClothing.load));
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-S4] PlayerClothing.load 反射失败，无法验证登记");
                    PrefixRegisteredOnce = false;
                    PostfixRegisteredOnce = false;
                    return false;
                }

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int prefixCount = 0, postfixCount = 0;
                if (info != null)
                {
                    foreach (Patch p in info.Prefixes)
                        if (p.owner == SteamP2PFriendsPlugin.HARMONY_ID) prefixCount++;
                    foreach (Patch p in info.Postfixes)
                        if (p.owner == SteamP2PFriendsPlugin.HARMONY_ID) postfixCount++;
                }

                PrefixRegisteredOnce = (prefixCount == 1);
                PostfixRegisteredOnce = (postfixCount == 1);

                if (!PrefixRegisteredOnce || !PostfixRegisteredOnce)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-S4] !!! 登记验证失败: Prefix={PrefixRegisteredOnce}(count={prefixCount}) " +
                        $"Postfix={PostfixRegisteredOnce}(count={postfixCount}) - 期望各 1 次 (owner={SteamP2PFriendsPlugin.HARMONY_ID})");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[P0-S4] OK PlayerClothing.load Prefix/Postfix 各登记一次 (owner={SteamP2PFriendsPlugin.HARMONY_ID})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S4] VerifyRegistration 异常: {ex.Message}");
                PrefixRegisteredOnce = false;
                PostfixRegisteredOnce = false;
                return false;
            }
        }

        [HarmonyPrefix]
        private static void Prefix(PlayerClothing __instance)
        {
            if (!Provider.isServer || Dedicator.IsDedicatedServer)
                return;

            if (!HostManager.IsP2PHostMode)
                return;

            SteamPlayer owner = __instance?.channel?.owner;
            HumanClothes clothes = __instance?.thirdClothes;
            // v0.2.3.25 修复：SteamPlayerID 重载 == 但不判空（SteamPlayerID.cs:136-139），
            // owner?.playerID == null 会调用 SteamPlayerID.==(null, null) 触发 null.steamID NRE。
            // 必须用 ReferenceEquals 判空（项目记忆铁律：SteamPlayerID == 运算符 NRE 陷阱）。
            if (ReferenceEquals(owner, null) || ReferenceEquals(owner.playerID, null) || ReferenceEquals(clothes, null))
                return;

            if (owner.playerID.steamID == Provider.user)
                return;

            if (owner.skin.a <= 0.001f)
            {
                RoleLogger.Warn("[Host]",
                    $"[P0-S4] owner.skin 尚未就绪，跳过外观复制 " +
                    $"steamId={owner.playerID.steamID} " +
                    $"skin=({owner.skin.r:F2},{owner.skin.g:F2},{owner.skin.b:F2},{owner.skin.a:F2})");
                return;
            }

            Color skinBefore = clothes.skin;
            _diagEntries.GetValue(__instance, _ => new DiagEntry
            {
                SteamId = owner.playerID.steamID.m_SteamID,
                Face = owner.face,
                Hair = owner.hair,
                Beard = owner.beard,
                SkinSource = owner.skin,
                SkinBefore = skinBefore,
            });

            clothes.face = owner.face;
            clothes.hair = owner.hair;
            clothes.beard = owner.beard;
            clothes.skin = owner.skin;
            clothes.color = owner.color;
            clothes.BeardColor = owner.BeardColor;
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerClothing __instance)
        {
            if (!Provider.isServer || Dedicator.IsDedicatedServer)
                return;
            if (!HostManager.IsP2PHostMode)
                return;

            if (!_diagEntries.TryGetValue(__instance, out DiagEntry entry))
                return;

            if (entry.DiagLogged)
                return;

            entry.DiagLogged = true;

            HumanClothes clothes = __instance?.thirdClothes;
            Color skinAfter = clothes?.skin ?? new Color(0f, 0f, 0f, 0f);

            RoleLogger.Info("[Host]",
                $"[P0-S4] 一次性诊断 steamId={entry.SteamId} " +
                $"source(face={entry.Face} hair={entry.Hair} beard={entry.Beard} " +
                $"skin=({entry.SkinSource.r:F2},{entry.SkinSource.g:F2},{entry.SkinSource.b:F2},{entry.SkinSource.a:F2})) " +
                $"before(skin=({entry.SkinBefore.r:F2},{entry.SkinBefore.g:F2},{entry.SkinBefore.b:F2},{entry.SkinBefore.a:F2})) " +
                $"after(skin=({skinAfter.r:F2},{skinAfter.g:F2},{skinAfter.b:F2},{skinAfter.a:F2})) " +
                $"matSkinColor=N/A(private-field-not-reflected)");
        }

        private sealed class DiagEntry
        {
            public ulong SteamId;
            public byte Face;
            public byte Hair;
            public byte Beard;
            public Color SkinSource;
            public Color SkinBefore;
            public bool DiagLogged;
        }
    }
}
