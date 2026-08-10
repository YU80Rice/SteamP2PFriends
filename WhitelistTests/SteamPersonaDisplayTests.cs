using SteamP2PFriends.Shared;
using Steamworks;
using System;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-4 IdentityWaitUX [指令 A/B] + EscApprovalMenu §4 门 4 授权键测试。
    /// 蓝图：名称仅为展示；CSteamID 仍是唯一授权键。
    ///   - P1: Normalize null/empty/whitespace -> fallback
    ///   - P2: Normalize 控制符剥离
    ///   - P3: Normalize 32 截断
    ///   - P4: Normalize 正常字符串保留
    ///   - P5: FormatPlayer 含 SteamID（授权键永留）+ 名称回退
    ///   - P6: GetRemoteDisplayName 无效 SteamID -> "未知玩家"
    /// </summary>
    internal static class SteamPersonaDisplayTests
    {
        internal static bool Test_v4_P1_Normalize_Empty_Fallback()
        {
            if (SteamPersonaDisplay.Normalize(null, "fallback") != "fallback")
                return Fail("null should fallback", "got=" + SteamPersonaDisplay.Normalize(null, "fallback"));
            if (SteamPersonaDisplay.Normalize("", "fallback") != "fallback")
                return Fail("empty should fallback", "");
            if (SteamPersonaDisplay.Normalize("   ", "fallback") != "fallback")
                return Fail("whitespace should fallback", "ws");
            if (SteamPersonaDisplay.Normalize("\t\n", "fallback") != "fallback")
                return Fail("control-only trimmed to empty should fallback", "ctrl");
            return true;
        }

        internal static bool Test_v4_P2_Normalize_ControlChars_Stripped()
        {
            // 控制符被剥离，可见字符保留
            string result = SteamPersonaDisplay.Normalize("a\tb\nc", "fb");
            if (result != "abc")
                return Fail("control chars should be stripped", "got=" + result);
            return true;
        }

        internal static bool Test_v4_P3_Normalize_Truncates_32()
        {
            string long40 = new string('X', 40);
            string result = SteamPersonaDisplay.Normalize(long40, "fb");
            if (result.Length != 32)
                return Fail("should truncate to 32", "len=" + result.Length);
            if (result != new string('X', 32))
                return Fail("truncated content mismatch", "got=" + result);
            return true;
        }

        internal static bool Test_v4_P4_Normalize_Valid_Preserved()
        {
            string result = SteamPersonaDisplay.Normalize("易烨不会玩FPS", "fb");
            if (result != "易烨不会玩FPS")
                return Fail("valid string should be preserved", "got=" + result);
            // 带空格的 trim
            result = SteamPersonaDisplay.Normalize("  DiDATuT  ", "fb");
            if (result != "DiDATuT")
                return Fail("should trim surrounding whitespace", "got=" + result);
            return true;
        }

        internal static bool Test_v4_P5_FormatPlayer_KeepsSteamId_AndFallback()
        {
            SteamPersonaDisplay._testBypassThreadAssert = true;
            SteamPersonaDisplay._testRemotePersonaProvider = (id) => null; // 模拟 persona 不可得
            try
            {
                CSteamID steamId = new CSteamID(76561199721762479UL);
                string formatted = SteamPersonaDisplay.FormatPlayer(steamId);
                // SteamID 必须保留（授权键不被名称替代）
                if (!formatted.Contains("76561199721762479"))
                    return Fail("FormatPlayer must keep SteamID", "got=" + formatted);
                // persona 不可得 -> "未知玩家" 回退
                if (!formatted.Contains("未知玩家"))
                    return Fail("null persona should fallback to 未知玩家", "got=" + formatted);
                if (!formatted.StartsWith("玩家："))
                    return Fail("FormatPlayer should start with 玩家：", "got=" + formatted);
            }
            finally
            {
                SteamPersonaDisplay._testRemotePersonaProvider = null;
                SteamPersonaDisplay._testBypassThreadAssert = false;
            }
            return true;
        }

        internal static bool Test_v4_P6_GetRemoteDisplayName_InvalidId_Fallback()
        {
            SteamPersonaDisplay._testBypassThreadAssert = true;
            try
            {
                if (SteamPersonaDisplay.GetRemoteDisplayName(CSteamID.Nil) != "未知玩家")
                    return Fail("Nil SteamID should fallback", "nil");
                // 构造一个无效 CSteamID（m_SteamID=0 但非 Nil 对象）
                CSteamID zero = new CSteamID(0UL);
                if (SteamPersonaDisplay.GetRemoteDisplayName(zero) != "未知玩家")
                    return Fail("zero SteamID should fallback", "zero");
            }
            finally
            {
                SteamPersonaDisplay._testBypassThreadAssert = false;
            }
            return true;
        }

        private static bool Fail(string msg, string detail)
        {
            Console.WriteLine("    FAIL: " + msg + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }
    }
}
