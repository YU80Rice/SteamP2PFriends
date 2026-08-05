using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    // =====================================================================
    // Stage 7-2-2 白名单管理 UI 模态框（Codex 133rd PASS 授权实施）
    // 蓝图：Codex-Blueprint-Stage7-2-2-NativeWhitelist-ImplementationCompile-v1-20260805.md §2.2
    // 设计：Stage7-2-1-NativeWhitelistDesign-v1.md §3.13
    // =====================================================================
    // 职责：
    //   - 仅 IMGUI 绘制（OnGUI 生命周期）
    //   - SteamID/tag 输入框 + 添加/移除按钮 + 列表显示
    //   - 调用 P2PWhitelistService.TryAdd/TryRemove/SnapshotForUi
    //   - 不引用 SteamWhitelist.*
    //   - 不注册 Commander、Harmony patch 或独立 GameObject
    //
    // 显示条件（蓝图 §2.2 强制）：
    //   HostManager.IsP2PHostMode && Provider.isServer && Provider.isWhitelisted
    // =====================================================================

    internal static class P2PWhitelistModal
    {
        private static bool _visible;
        private static string _steamIdInput = "";
        private static string _tagInput = "MEMBER";
        private static string _feedback = "";
        private static float _lastFeedbackTime = -1f;
        private const float FeedbackFadeSeconds = 5f;

        /// <summary>
        /// 由 SteamP2PFriendsPlugin.OnGUI 调用。
        /// 蓝图 §2.2：首行不满足显示条件则 return 并关闭面板状态。
        /// </summary>
        internal static void OnGUI()
        {
            // 蓝图 §2.2 强制契约：首行显示条件
            if (!HostManager.IsP2PHostMode || !Provider.isServer || !Provider.isWhitelisted)
            {
                if (_visible) _visible = false;
                return;
            }

            // F8 切换面板可见性（IMGUI 事件模型）
            Event current = Event.current;
            if (current != null && current.type == EventType.KeyDown && current.keyCode == KeyCode.F8)
            {
                _visible = !_visible;
                current.Use();
            }

            if (!_visible) return;

            DrawModal();
        }

        private static void DrawModal()
        {
            const float boxW = 540;
            const float boxH = 420;
            float boxX = (Screen.width - boxW) / 2f;
            float boxY = (Screen.height - boxH) / 2f;

            // 半透明黑色背景（复用 SteamIdInputModal 风格）
            GUI.color = new Color(0, 0, 0, 0.9f);
            GUI.DrawTexture(new Rect(boxX, boxY, boxW, boxH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // 标题
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            titleStyle.normal.textColor = Color.yellow;
            GUI.Label(new Rect(boxX + 20, boxY + 15, boxW - 40, 30), "P2P 白名单管理 (F8 关闭)", titleStyle);

            // 说明
            var descStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            descStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(boxX + 20, boxY + 50, boxW - 40, 20),
                "添加/移除客机 SteamID；房主自身不可移除", descStyle);

            // 输入区
            var labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            labelStyle.normal.textColor = Color.white;

            GUI.Label(new Rect(boxX + 20, boxY + 80, 80, 20), "SteamID:", labelStyle);
            _steamIdInput = GUI.TextField(new Rect(boxX + 100, boxY + 80, 220, 24), _steamIdInput, 20);

            GUI.Label(new Rect(boxX + 330, boxY + 80, 40, 20), "Tag:", labelStyle);
            _tagInput = GUI.TextField(new Rect(boxX + 370, boxY + 80, 150, 24), _tagInput, 64);

            // 按钮
            if (GUI.Button(new Rect(boxX + 20, boxY + 110, 160, 28), "添加"))
            {
                OnClickAdd();
            }
            if (GUI.Button(new Rect(boxX + 190, boxY + 110, 160, 28), "移除"))
            {
                OnClickRemove();
            }
            if (GUI.Button(new Rect(boxX + 360, boxY + 110, 160, 28), "关闭 (F8)"))
            {
                _visible = false;
            }

            // 反馈文本（5 秒后淡出）
            if (!string.IsNullOrEmpty(_feedback) && _lastFeedbackTime > 0f)
            {
                float elapsed = Time.realtimeSinceStartup - _lastFeedbackTime;
                if (elapsed < FeedbackFadeSeconds)
                {
                    var feedbackStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                    feedbackStyle.normal.textColor = _feedback.StartsWith("失败") || _feedback.StartsWith("添加失败") || _feedback.StartsWith("移除失败")
                        ? Color.red
                        : Color.green;
                    GUI.Label(new Rect(boxX + 20, boxY + 145, boxW - 40, 20), _feedback, feedbackStyle);
                }
                else
                {
                    _feedback = "";
                }
            }

            // 列表
            DrawWhitelistList(boxX + 20, boxY + 175, boxW - 40, boxH - 195);
        }

        private static void DrawWhitelistList(float x, float y, float w, float h)
        {
            // 列表标题
            var headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
            headerStyle.normal.textColor = Color.cyan;
            GUI.Label(new Rect(x, y, w, 20), "当前白名单 (steamID / tag / judgeID):", headerStyle);

            IReadOnlyList<SteamWhitelistID> snapshot;
            try
            {
                snapshot = P2PWhitelistService.SnapshotForUi();
            }
            catch (Exception ex)
            {
                var errStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                errStyle.normal.textColor = Color.red;
                GUI.Label(new Rect(x, y + 25, w, 20), "快照失败: " + ex.GetType().Name, errStyle);
                return;
            }

            var entryStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            entryStyle.normal.textColor = Color.white;

            float entryY = y + 25;
            float entryHeight = 18;
            int maxVisible = (int)((h - 25) / entryHeight);
            int count = snapshot == null ? 0 : snapshot.Count;

            if (count == 0)
            {
                GUI.Label(new Rect(x, entryY, w, 20), "(空)", entryStyle);
                return;
            }

            int visible = Math.Min(count, maxVisible);
            for (int i = 0; i < visible; i++)
            {
                SteamWhitelistID entry = snapshot[i];
                string line = $"{entry.steamID.m_SteamID} | {entry.tag ?? "(null)"} | judge={entry.judgeID.m_SteamID}";
                GUI.Label(new Rect(x, entryY + i * entryHeight, w, entryHeight), line, entryStyle);
            }

            if (count > maxVisible)
            {
                GUI.Label(new Rect(x, entryY + visible * entryHeight, w, 20),
                    $"... 共 {count} 条，仅显示前 {maxVisible} 条", entryStyle);
            }
        }

        private static void OnClickAdd()
        {
            string input = _steamIdInput?.Trim() ?? "";
            if (string.IsNullOrEmpty(input))
            {
                SetFeedback("请输入 SteamID");
                return;
            }

            if (!ulong.TryParse(input, out ulong steamId) || steamId == 0)
            {
                SetFeedback("SteamID 格式无效（应为 17 位数字）");
                return;
            }

            var target = new CSteamID(steamId);
            string tag = _tagInput?.Trim() ?? "";
            if (string.IsNullOrEmpty(tag)) tag = "MEMBER";

            P2PWhitelistService.TryAdd(target, tag, out string feedback);
            SetFeedback(feedback);
        }

        private static void OnClickRemove()
        {
            string input = _steamIdInput?.Trim() ?? "";
            if (string.IsNullOrEmpty(input))
            {
                SetFeedback("请输入 SteamID");
                return;
            }

            if (!ulong.TryParse(input, out ulong steamId) || steamId == 0)
            {
                SetFeedback("SteamID 格式无效（应为 17 位数字）");
                return;
            }

            var target = new CSteamID(steamId);
            P2PWhitelistService.TryRemove(target, out string feedback);
            SetFeedback(feedback);
        }

        private static void SetFeedback(string text)
        {
            _feedback = text;
            _lastFeedbackTime = Time.realtimeSinceStartup;
            RoleLogger.Info("[Host]", "[P2P-WL-Modal] feedback: " + text);
        }
    }
}
