using SDG.Unturned;
using System;
using System.Text;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    ///
    /// Product semantics: MC-style all-player system broadcast for the plugin listen-host world.
    /// Every event reaches host + approved guests + still-quarantined guests via the same
    /// unified sender (fromPlayer=null / toPlayer=null / useRichTextFormatting=false).
    ///
    /// - each ordinary death cause has EXACTLY 5 random slots (2 practical + 3 humorous);
    /// - each slot is a DeathMessageSlot { WithoutKiller, optional WithKiller };
    /// - the RNG picks ONE slot index 0..4 once; then the selected slot is rendered in the
    ///   WithKiller variant when a RELIABLE player instigator exists, otherwise WithoutKiller.
    ///   WithKiller is a contextual variant of the SAME semantic slot, never a sixth candidate.
    /// - SUICIDE has exactly 2 short practical slots and ALWAYS ignores killer.
    ///
    /// Slot-count: 29 ordinary causes x 5 slots + SUICIDE 2 slots = 147 RANDOM SLOTS. WithKiller
    /// variants raise the string total above 147, but the random authority only ever sees 147
    /// slots and the production path can only output a selected slot's two legal variants —
    /// zero out-of-catalog text. Source:
    /// WorldStatusBroadcast-Copywriting-Catalog-v1-20260811.md.
    ///
    /// RNG is the broadcaster's private, main-thread, injectable System.Random (NextIndex with
    /// [0, exclusiveMax) bounds check, fail-closed to index 0). UnityEngine.Random is never
    /// touched. Tests inject indices to prove slot reachability (no probabilistic tests).
    /// </summary>
    internal static class P2PWorldStatusTemplates
    {
        public const string FallbackPlayerName = "一名玩家";
        public const int MaxNameLengthUtf16 = 32;

        // ===== World-status templates (kind -> practical / humorous) =====
        private static readonly string[] JoinApprovedPractical =
            { "{name} 进入了世界。", "{name} 进入了世界。僵尸们开始加班。" };
        private static readonly string[] JoinQuarantinedPractical =
            { "{name} 已连接，正在等待房主审核。", "{name} 抵达世界，正在门口接受房主安检。" };
        private static readonly string[] ApprovalReleasedPractical =
            { "{name} 已通过房主审核。", "{name} 通过了房主审核，正式获得行动权限。" };
        private static readonly string[] ApprovalTimedOutPractical =
            { "{name} 等待审核超时，已被移出世界。", "{name} 在门口等了 30 秒，本次访问超时。" };
        private static readonly string[] LeftApprovedPractical =
            { "{name} 离开了世界。", "{name} 离开了世界。地上的物资暂时安全了。" };
        private static readonly string[] LeftBeforeApprovalPractical =
            { "{name} 在完成审核前离开了世界。", "{name} 离开了安检区，房主还没来得及盖章。" };

        // ===== v3 structured death slot =====
        /// <summary>
        /// One RANDOM slot of a death cause. WithoutKiller is mandatory; WithKiller is the
        /// optional contextual variant of the SAME semantic slot (not a sixth candidate).
        /// </summary>
        internal readonly struct DeathMessageSlot
        {
            internal readonly string WithoutKiller;
            internal readonly string WithKiller;

            internal DeathMessageSlot(string withoutKiller, string withKiller = null)
            {
                WithoutKiller = withoutKiller;
                WithKiller = withKiller;
            }
        }

        // ===== Death template catalog: 29 ordinary causes x 5 slots (2 practical + 3 humorous) =====
        private static readonly DeathMessageSlot[][] DeathCauseSlots =
        {
            // BLEEDING
            new[]
            {
                new DeathMessageSlot("{name} 因流血过多死亡。"),
                new DeathMessageSlot("{name} 没能及时止血。"),
                new DeathMessageSlot("{name} 流血过多，绷带再次证明了存在价值。"),
                new DeathMessageSlot("{name} 把最后一点生命值留在了止血路上。"),
                new DeathMessageSlot("{name} 的伤口拒绝继续配合生存计划。")
            },
            // BONES
            new[]
            {
                new DeathMessageSlot("{name} 因坠落伤害死亡。"),
                new DeathMessageSlot("{name} 的骨折伤势过重。"),
                new DeathMessageSlot("{name} 与重力进行了谈判，谈判破裂。"),
                new DeathMessageSlot("{name} 发现降落速度和落地质量是两回事。"),
                new DeathMessageSlot("{name} 用生命完成了一次落差测量。")
            },
            // FREEZING
            new[]
            {
                new DeathMessageSlot("{name} 因体温过低死亡。"),
                new DeathMessageSlot("{name} 冻死了。"),
                new DeathMessageSlot("{name} 被天气劝退了生命进程。"),
                new DeathMessageSlot("{name} 低估了外套在生存游戏里的职位等级。"),
                new DeathMessageSlot("{name} 与低温长期对峙，最终失去连接。")
            },
            // BURNING
            new[]
            {
                new DeathMessageSlot("{name} 被火焰烧死了。"),
                new DeathMessageSlot("{name} 死于持续燃烧伤害。"),
                new DeathMessageSlot("{name} 的温度管理出现了严重失误。"),
                new DeathMessageSlot("{name} 发现着火以后原地思考并不能灭火。"),
                new DeathMessageSlot("{name} 被火焰强制结束了本轮生存。")
            },
            // FOOD
            new[]
            {
                new DeathMessageSlot("{name} 饿死了。"),
                new DeathMessageSlot("{name} 因长期缺少食物死亡。"),
                new DeathMessageSlot("{name} 饿死了。背包里的罐头对此不予置评。"),
                new DeathMessageSlot("{name} 的生存计划败给了一顿没吃上的饭。"),
                new DeathMessageSlot("{name} 证明了搜刮餐厅应该拥有更高优先级。")
            },
            // WATER
            new[]
            {
                new DeathMessageSlot("{name} 渴死了。"),
                new DeathMessageSlot("{name} 因长期缺水死亡。"),
                new DeathMessageSlot("{name} 渴死了。地图上的水井保持沉默。"),
                new DeathMessageSlot("{name} 的水分管理比僵尸防线先一步崩溃。"),
                new DeathMessageSlot("{name} 忘记了饮料不只是背包里的收藏品。")
            },
            // GUN (attributed: WithKiller for all 5 slots)
            new[]
            {
                new DeathMessageSlot("{name} 被枪械击杀。", "{name} 被 {killer} 使用枪械击杀。"),
                new DeathMessageSlot("{name} 死于枪伤。", "{killer} 使用枪械击杀了 {name}。"),
                new DeathMessageSlot("{name} 在弹道学实践课上提前退场。", "{name} 在 {killer} 主讲的弹道学实践课上提前退场。"),
                new DeathMessageSlot("{name} 没能说服子弹改变行进路线。", "{name} 没能说服 {killer} 的子弹改变行进路线。"),
                new DeathMessageSlot("{name} 的护甲对本次来访表示无能为力。", "{name} 的护甲没能拒绝 {killer} 发来的高速问候。")
            },
            // MELEE (attributed)
            new[]
            {
                new DeathMessageSlot("{name} 被近战武器击杀。", "{name} 被 {killer} 的近战武器击杀。"),
                new DeathMessageSlot("{name} 死于近战攻击。", "{killer} 用近战攻击击杀了 {name}。"),
                new DeathMessageSlot("{name} 在近距离意见交换中失去了生命值。", "{name} 在跟 {killer} 的近距离意见交换中失去了生命值。"),
                new DeathMessageSlot("{name} 发现近战武器的有效距离比预想中更长。", "{name} 发现 {killer} 近战武器的有效距离比预想中更长。"),
                new DeathMessageSlot("{name} 没能赢下这场贴脸物理讨论。", "{name} 没能赢下和 {killer} 的贴脸物理讨论。")
            },
            // ZOMBIE
            new[]
            {
                new DeathMessageSlot("{name} 被僵尸杀死了。"),
                new DeathMessageSlot("{name} 没能抵挡僵尸的攻击。"),
                new DeathMessageSlot("{name} 被僵尸纳入了今日业绩。"),
                new DeathMessageSlot("{name} 为本地僵尸提供了一份新鲜战绩。"),
                new DeathMessageSlot("{name} 与僵尸近距离沟通后停止了生命活动。")
            },
            // ANIMAL
            new[]
            {
                new DeathMessageSlot("{name} 被野生动物杀死了。"),
                new DeathMessageSlot("{name} 死于动物攻击。"),
                new DeathMessageSlot("{name} 在野生动物外交中遭遇重大挫折。"),
                new DeathMessageSlot("{name} 把野生动物误判成了可交互的风景。"),
                new DeathMessageSlot("{name} 没能通过本地生态系统的现场考核。")
            },
            // SUICIDE (index 10) — 2 practical ONLY, always ignores killer
            new[]
            {
                new DeathMessageSlot("{name} 自杀了。"),
                new DeathMessageSlot("{name} 主动结束了本次生命。")
            },
            // KILL
            new[]
            {
                new DeathMessageSlot("{name} 被直接处决。"),
                new DeathMessageSlot("{name} 被服务器判定死亡。"),
                new DeathMessageSlot("{name} 收到了一张无法申诉的生命值清零通知。"),
                new DeathMessageSlot("{name} 被系统管理员按下了快速重生按钮。"),
                new DeathMessageSlot("{name} 的本轮生存被一条直接指令结束。")
            },
            // INFECTION
            new[]
            {
                new DeathMessageSlot("{name} 死于感染。"),
                new DeathMessageSlot("{name} 的感染程度达到了致命水平。"),
                new DeathMessageSlot("{name} 的免疫系统宣布提前下班。"),
                new DeathMessageSlot("{name} 没能及时把感染值列入待办事项。"),
                new DeathMessageSlot("{name} 最终输给了状态栏里那条越来越长的感染值。")
            },
            // PUNCH (attributed)
            new[]
            {
                new DeathMessageSlot("{name} 被徒手击杀。", "{name} 被 {killer} 徒手击杀。"),
                new DeathMessageSlot("{name} 死于拳击伤害。", "{killer} 用拳头击杀了 {name}。"),
                new DeathMessageSlot("{name} 在没有武器的战斗里依然看到了重生界面。", "{name} 在跟 {killer} 没有武器的战斗里依然看到了重生界面。"),
                new DeathMessageSlot("{name} 低估了拳头在末日世界里的装备等级。", "{name} 低估了 {killer} 拳头在末日世界里的装备等级。"),
                new DeathMessageSlot("{name} 输掉了一场极其朴素的物理争论。", "{name} 输掉了和 {killer} 极其朴素的物理争论。")
            },
            // BREATH
            new[]
            {
                new DeathMessageSlot("{name} 因缺氧死亡。"),
                new DeathMessageSlot("{name} 窒息而死。"),
                new DeathMessageSlot("{name} 忘记了氧气也是一种消耗品。"),
                new DeathMessageSlot("{name} 的肺部未能通过本次水下耐久测试。"),
                new DeathMessageSlot("{name} 在寻找空气的途中抵达了重生界面。")
            },
            // ROADKILL (attributed)
            new[]
            {
                new DeathMessageSlot("{name} 被车辆撞死了。", "{name} 被 {killer} 驾驶的车辆撞死了。"),
                new DeathMessageSlot("{name} 死于车辆撞击。", "{killer} 驾驶车辆撞死了 {name}。"),
                new DeathMessageSlot("{name} 未能通过本世界的交通安全考试。", "{name} 在 {killer} 的护送下提前通过了本世界的交通安全考试。"),
                new DeathMessageSlot("{name} 与一辆行驶中的载具争夺了路权。", "{name} 与 {killer} 驾驶的行驶中载具争夺了路权。"),
                new DeathMessageSlot("{name} 发现斑马线在末日世界不提供保护。", "{name} 发现 {killer} 的载具不认可斑马线在末日世界的保护。")
            },
            // VEHICLE
            new[]
            {
                new DeathMessageSlot("{name} 死于载具事故。"),
                new DeathMessageSlot("{name} 被载具爆炸波及。"),
                new DeathMessageSlot("{name} 与载具共同完成了一次失败的工程实验。"),
                new DeathMessageSlot("{name} 发现载具耐久归零时不宜继续坐在里面。"),
                new DeathMessageSlot("{name} 的驾驶行程以一场计划外爆炸结束。")
            },
            // GRENADE (attributed)
            new[]
            {
                new DeathMessageSlot("{name} 被手榴弹炸死了。", "{name} 被 {killer} 的手榴弹炸死了。"),
                new DeathMessageSlot("{name} 死于手榴弹爆炸。", "{killer} 用手榴弹送走了 {name}。"),
                new DeathMessageSlot("{name} 对手榴弹的安全距离做出了错误估计。", "{name} 对 {killer} 手榴弹的安全距离做出了错误估计。"),
                new DeathMessageSlot("{name} 发现倒计时结束前最好离得更远一点。", "{name} 发现 {killer} 的倒计时结束前最好离得更远一点。"),
                new DeathMessageSlot("{name} 与一枚手榴弹共享了最后几秒。", "{name} 与 {killer} 投来的手榴弹共享了最后几秒。")
            },
            // SHRED
            new[]
            {
                new DeathMessageSlot("{name} 死于撕裂伤害。"),
                new DeathMessageSlot("{name} 被尖锐陷阱杀死。"),
                new DeathMessageSlot("{name} 对尖锐物体进行了过于直接的质量检测。"),
                new DeathMessageSlot("{name} 发现这处陷阱并不提供无伤参观服务。"),
                new DeathMessageSlot("{name} 在障碍物面前选择了生命值换路线。")
            },
            // LANDMINE (attributed)
            new[]
            {
                new DeathMessageSlot("{name} 踩中地雷死亡。", "{name} 踩中 {killer} 埋下的地雷死亡。"),
                new DeathMessageSlot("{name} 死于地雷爆炸。", "{killer} 埋下的地雷结果了 {name}。"),
                new DeathMessageSlot("{name} 找到了一枚埋得很认真的地雷。", "{name} 找到了一枚 {killer} 埋得很认真的地雷。"),
                new DeathMessageSlot("{name} 用行动证明了脚下检查的重要性。", "{name} 用行动证明了对 {killer} 脚下布防检查的重要性。"),
                new DeathMessageSlot("{name} 的下一步被地雷改成了重生。", "{name} 的下一步被 {killer} 的地雷改成了重生。")
            },
            // ARENA
            new[]
            {
                new DeathMessageSlot("{name} 被竞技场规则淘汰。"),
                new DeathMessageSlot("{name} 死于竞技场边界或阶段伤害。"),
                new DeathMessageSlot("{name} 没能赶上竞技场的安全区安排。"),
                new DeathMessageSlot("{name} 被比赛规则亲自请出了本轮。"),
                new DeathMessageSlot("{name} 与竞技场边界进行了一次失败的协商。")
            },
            // MISSILE (attributed)
            new[]
            {
                new DeathMessageSlot("{name} 被导弹击杀。", "{name} 被 {killer} 发射的导弹击杀。"),
                new DeathMessageSlot("{name} 死于火箭弹爆炸。", "{killer} 用火箭弹击杀了 {name}。"),
                new DeathMessageSlot("{name} 收到了一份高速送达的爆炸包裹。", "{name} 收到了 {killer} 高速送达的爆炸包裹。"),
                new DeathMessageSlot("{name} 没能避开一枚目标十分明确的导弹。", "{name} 没能避开 {killer} 那枚目标十分明确的导弹。"),
                new DeathMessageSlot("{name} 发现火箭弹的劝退范围比视觉效果更大。", "{name} 发现 {killer} 火箭弹的劝退范围比视觉效果更大。")
            },
            // CHARGE (attributed)
            new[]
            {
                new DeathMessageSlot("{name} 被爆破炸药炸死。", "{name} 被 {killer} 的爆破炸药炸死。"),
                new DeathMessageSlot("{name} 死于爆破装置。", "{killer} 的爆破装置结果了 {name}。"),
                new DeathMessageSlot("{name} 站在了爆破工程不建议站立的位置。", "{name} 站在了 {killer} 爆破工程不建议站立的位置。"),
                new DeathMessageSlot("{name} 对炸药的工作半径表现出了过度自信。", "{name} 对 {killer} 炸药的工作半径表现出了过度自信。"),
                new DeathMessageSlot("{name} 参与了一次没有安全员的拆迁作业。", "{name} 参与了 {killer} 组织的一次没有安全员的拆迁作业。")
            },
            // SPLASH
            new[]
            {
                new DeathMessageSlot("{name} 死于爆炸溅射伤害。"),
                new DeathMessageSlot("{name} 被附近的爆炸波及。"),
                new DeathMessageSlot("{name} 虽然没站在爆心，但爆炸仍然记得他。"),
                new DeathMessageSlot("{name} 对爆炸边缘的安全性做出了错误判断。"),
                new DeathMessageSlot("{name} 被一场并非冲自己来的爆炸顺便带走。")
            },
            // SENTRY (attributed)
            new[]
            {
                new DeathMessageSlot("{name} 被哨戒炮击杀。", "{name} 被 {killer} 的哨戒炮击杀。"),
                new DeathMessageSlot("{name} 死于自动防御设施。", "{killer} 的自动防御设施击杀了 {name}。"),
                new DeathMessageSlot("{name} 没能通过哨戒炮的访客验证。", "{name} 没能通过 {killer} 哨戒炮的访客验证。"),
                new DeathMessageSlot("{name} 被自动防御系统标记为不受欢迎的移动目标。", "{name} 被 {killer} 的自动防御系统标记为不受欢迎的移动目标。"),
                new DeathMessageSlot("{name} 与哨戒炮进行了一场单方面的火力交流。", "{name} 与 {killer} 的哨戒炮进行了一场单方面的火力交流。")
            },
            // ACID
            new[]
            {
                new DeathMessageSlot("{name} 死于酸液伤害。"),
                new DeathMessageSlot("{name} 被酸液腐蚀致死。"),
                new DeathMessageSlot("{name} 被酸液进行了不必要的化学分析。"),
                new DeathMessageSlot("{name} 发现防具没有附带耐酸实验认证。"),
                new DeathMessageSlot("{name} 的生存计划在强酸面前失去了结构完整性。")
            },
            // BOULDER
            new[]
            {
                new DeathMessageSlot("{name} 被巨石砸死了。"),
                new DeathMessageSlot("{name} 死于巨石冲击。"),
                new DeathMessageSlot("{name} 对巨石的运动轨迹判断得过于乐观。"),
                new DeathMessageSlot("{name} 发现大石头确实拥有很高的说服力。"),
                new DeathMessageSlot("{name} 没能在巨石抵达前完成路线调整。")
            },
            // BURNER
            new[]
            {
                new DeathMessageSlot("{name} 被火焰僵尸烧死。"),
                new DeathMessageSlot("{name} 死于燃烧僵尸的火焰攻击。"),
                new DeathMessageSlot("{name} 被火焰僵尸提供了过量供暖。"),
                new DeathMessageSlot("{name} 发现会着火的僵尸并不适合近距离取暖。"),
                new DeathMessageSlot("{name} 的温度计和生命值同时到达了极端。")
            },
            // SPIT
            new[]
            {
                new DeathMessageSlot("{name} 被僵尸喷吐物击杀。"),
                new DeathMessageSlot("{name} 死于远程喷吐攻击。"),
                new DeathMessageSlot("{name} 没能躲开一份极不卫生的远程问候。"),
                new DeathMessageSlot("{name} 被僵尸用最没有礼貌的方式远程击中。"),
                new DeathMessageSlot("{name} 发现保持社交距离仍然不够远。")
            },
            // SPARK
            new[]
            {
                new DeathMessageSlot("{name} 死于电击。"),
                new DeathMessageSlot("{name} 被电流杀死。"),
                new DeathMessageSlot("{name} 亲自验证了自己的导电性。"),
                new DeathMessageSlot("{name} 的生命值未能通过本次高压测试。"),
                new DeathMessageSlot("{name} 与电流完成了一次短暂而明亮的交流。")
            }
        };

        /// <summary>
        /// v3: EXACTLY 30 EDeathCause entries in catalog order, one slot array per enum value.
        /// 29 ordinary causes hold 5 random slots; SUICIDE (index 10) holds exactly 2.
        /// </summary>
        internal static readonly DeathMessageSlot[][] AllDeathSlots = DeathCauseSlots;

        /// <summary>Number of RANDOM slots for a cause (5 ordinary, 2 SUICIDE). Fail-closed 5.</summary>
        internal static int SlotCount(EDeathCause cause)
        {
            int raw = (int)cause;
            if (raw < 0 || raw >= DeathCauseSlots.Length) return 5;
            return DeathCauseSlots[raw].Length;
        }

        /// <summary>The unique slot array for a cause (5 ordinary, 2 SUICIDE). Fail-closed to cause 0.</summary>
        internal static DeathMessageSlot[] GetSlots(EDeathCause cause)
        {
            int raw = (int)cause;
            if (raw < 0 || raw >= DeathCauseSlots.Length) return DeathCauseSlots[0];
            return DeathCauseSlots[raw];
        }

        internal static bool IsSuicide(EDeathCause cause)
        {
            return cause == EDeathCause.SUICIDE;
        }


        internal static bool TryGetOrdinaryIndex(EDeathCause cause, out int index)
        {
            int raw = (int)cause;
            if (raw < 0 || raw >= DeathCauseSlots.Length)
            {
                index = 0;
                return false;
            }
            index = raw;
            return raw != (int)EDeathCause.SUICIDE;
        }

        // ===== Name sanitization (指令 F): pure, no I/O =====

        /// <summary>
        ///  - strips CR/LF/TAB and all Unicode C0/C1 control characters
        ///  - removes chars that could spoof a newline/system message (0x00-0x1F, 0x7F, 0x85,
        ///    Unicode line/paragraph separators 0x2028/0x2029)
        ///  - rejects ALL isolated surrogates (never emits a lone high/low surrogate)
        ///  - appends by Unicode scalar: a surrogate pair is only appended when BOTH units fit
        ///    within the 32-unit budget
        ///  - removes UnicodeCategory.Format chars (bidi overrides U+202E/U+2066, ZWJ/ZWNJ, etc.)
        ///  - strips rich-text angle brackets
        ///  - trims; empty/invalid result falls back to "一名玩家"
        /// Never returns SteamID or raw user input into chat.
        /// </summary>
        internal static string NormalizePlayerName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return FallbackPlayerName;

            var sb = new StringBuilder(Math.Min(raw.Length, MaxNameLengthUtf16 + 4));
            int i = 0;
            int len = raw.Length;
            while (i < len && sb.Length < MaxNameLengthUtf16)
            {
                char c = raw[i];
                int code = c;

                // C0 controls, DEL, C1 controls, NEL, LS/PS, rich-text tags.
                if (code == '\r' || code == '\n' || code == '\t' || code < 0x20 ||
                    code == 0x7F || (code >= 0x80 && code <= 0x9F) || code == 0x85 ||
                    code == 0x2028 || code == 0x2029 || code == '<' || code == '>')
                {
                    i++;
                    continue;
                }

                // Reject isolated surrogates outright (never emit a lone high/low surrogate).
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= len || !char.IsLowSurrogate(raw[i + 1]))
                    {
                        i++; // lone high surrogate -> drop
                        continue;
                    }
                    // A real pair: only append if BOTH units fit within the 32-unit budget.
                    if (sb.Length + 2 > MaxNameLengthUtf16)
                    {
                        i++; // stop consuming; the pair no longer fits
                        break;
                    }
                    sb.Append(c);
                    sb.Append(raw[i + 1]);
                    i += 2;
                    continue;
                }
                if (char.IsLowSurrogate(c))
                {
                    i++; // lone low surrogate -> drop
                    continue;
                }

                // Remove Unicode format chars (bidi overrides/embeddings, ZWJ/ZWNJ, variation
                // selectors) that could spoof display or hide characters.
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                {
                    i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            string trimmed = sb.ToString().Trim();
            if (trimmed.Length == 0) return FallbackPlayerName;
            return trimmed;
        }

        // ===== Rendering =====

        internal static string Render(string template, string name)
        {
            string safeName = NormalizePlayerName(name);
            return template.Replace("{name}", safeName);
        }

        internal const string PlayerNameColorHex = "#55FFFF";

        /// <summary>
        /// Applies presentation-only rich text after the name has already passed strict
        /// normalization. The normalized value contains no angle brackets, so it cannot escape the
        /// fixed color tag or inject arbitrary markup.
        /// </summary>
        internal static string ColorizePlayerName(string name)
        {
            string safeName = NormalizePlayerName(name);
            return "<color=" + PlayerNameColorHex + ">" + safeName + "</color>";
        }

        internal static string RenderRich(string template, string name)
        {
            return template.Replace("{name}", ColorizePlayerName(name));
        }

        /// <summary>
        /// v3: render the selected slot. When a reliable killer name is present AND the slot
        /// defines a WithKiller variant, render WithKiller; otherwise render WithoutKiller.
        /// killerName null/empty -> WithoutKiller. Index out of range fails closed to slot 0.
        /// </summary>
        internal static string RenderSlot(EDeathCause cause, string name, string killerName, int selectedIndex)
        {
            DeathMessageSlot[] slots = GetSlots(cause);
            int idx = selectedIndex;
            if (idx < 0 || idx >= slots.Length) idx = 0; // fail-closed to first slot
            DeathMessageSlot slot = slots[idx];
            string template = slot.WithKiller;
            bool useWith = !IsSuicide(cause) &&
                           !string.IsNullOrEmpty(template) &&
                           !string.IsNullOrEmpty(killerName);
            return Render(useWith ? template : slot.WithoutKiller, name, killerName);
        }

        /// <summary>
        /// Renders a template substituting BOTH {name} (victim) and, when non-null, {killer}.
        /// </summary>
        internal static string Render(string template, string name, string killerName)
        {
            string safeName = NormalizePlayerName(name);
            string rendered = template.Replace("{name}", safeName);
            if (!string.IsNullOrEmpty(killerName))
            {
                string safeKiller = NormalizePlayerName(killerName);
                rendered = rendered.Replace("{killer}", safeKiller);
            }
            else
            {
                // A WithKiller template must never reach chat without a killer: drop the token.
                rendered = rendered.Replace("{killer}", "");
            }
            return rendered;
        }

        internal static string RenderRich(string template, string name, string killerName)
        {
            string rendered = template.Replace("{name}", ColorizePlayerName(name));
            if (!string.IsNullOrEmpty(killerName))
                rendered = rendered.Replace("{killer}", ColorizePlayerName(killerName));
            else
                rendered = rendered.Replace("{killer}", "");
            return rendered;
        }

        internal static string RenderSlotRich(EDeathCause cause, string name, string killerName,
            int selectedIndex)
        {
            DeathMessageSlot[] slots = GetSlots(cause);
            int idx = selectedIndex;
            if (idx < 0 || idx >= slots.Length) idx = 0;
            DeathMessageSlot slot = slots[idx];
            bool useWith = !IsSuicide(cause) && !string.IsNullOrEmpty(slot.WithKiller) &&
                           !string.IsNullOrEmpty(killerName);
            return RenderRich(useWith ? slot.WithKiller : slot.WithoutKiller, name, killerName);
        }

        /// <summary>
        /// Compatibility helper used by the broadcaster's SUICIDE / non-attributed path and by
        /// existing tests: render WITHOUT a killer (strictly the selected slot's WithoutKiller).
        /// </summary>
        internal static string RenderDeath(EDeathCause cause, string name, int selectedIndex)
        {
            return RenderSlot(cause, name, null, selectedIndex);
        }

        internal static string[] GetWorldStatusTemplate(EWorldBroadcastKind kind)
        {
            switch (kind)
            {
                case EWorldBroadcastKind.JoinApproved: return JoinApprovedPractical;
                case EWorldBroadcastKind.JoinQuarantined: return JoinQuarantinedPractical;
                case EWorldBroadcastKind.ApprovalReleased: return ApprovalReleasedPractical;
                case EWorldBroadcastKind.ApprovalTimedOut: return ApprovalTimedOutPractical;
                case EWorldBroadcastKind.LeftApproved: return LeftApprovedPractical;
                case EWorldBroadcastKind.LeftBeforeApproval: return LeftBeforeApprovalPractical;
                default: return LeftApprovedPractical;
            }
        }

        // ===== Slot/authority verification (147 random slots) =====

        /// <summary>
        /// v3 audit: 29 ordinary causes each exactly 5 DeathMessageSlots, SUICIDE exactly 2.
        /// Every slot's WithoutKiller and (when present) WithKiller must be non-empty and render
        /// within MAX_MESSAGE_LENGTH (512, mirrors ChatManager.MAX_MESSAGE_LENGTH; kept as a const
        /// so this pure catalog never triggers the ChatManager static ctor). Worst-case name is
        /// 32 units and worst-case killer name is 32 units.
        /// </summary>
        private const int MaxMessageLength = 512;

        internal static bool VerifyCatalogIntegrity(out int total, out int failed)
        {
            total = 0;
            failed = 0;
            for (int i = 0; i < DeathCauseSlots.Length; i++)
            {
                EDeathCause cause = (EDeathCause)i;
                DeathMessageSlot[] slots = DeathCauseSlots[i];
                bool expectedSuicide = IsSuicide(cause);
                int expectedCount = expectedSuicide ? 2 : 5;
                if (slots == null || slots.Length != expectedCount)
                {
                    failed++;
                    continue;
                }
                for (int s = 0; s < slots.Length; s++)
                {
                    string without = slots[s].WithoutKiller;
                    string with = slots[s].WithKiller;
                    if (string.IsNullOrWhiteSpace(without)) { failed++; continue; }
                    total++; // one RANDOM slot
                    // Replace with the longest legal victim + killer names to bound worst-case length.
                    string renderedWithout = without.Replace("{name}", new string('名', 32));
                    if (renderedWithout.Length > MaxMessageLength) failed++;
                    if (!string.IsNullOrEmpty(with))
                    {
                        string renderedWith = with.Replace("{name}", new string('名', 32))
                                                   .Replace("{killer}", new string('名', 32));
                        if (renderedWith.Length > MaxMessageLength) failed++;
                    }
                }
            }
            return failed == 0;
        }
    }

    internal enum EWorldBroadcastKind : byte
    {
        JoinApproved,
        JoinQuarantined,
        ApprovalReleased,
        ApprovalTimedOut,
        LeftApproved,
        LeftBeforeApproval
    }
}
