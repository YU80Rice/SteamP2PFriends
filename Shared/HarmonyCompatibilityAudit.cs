using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace SteamP2PFriends.Shared
{
    internal enum HarmonyCompatibilityDecision
    {
        Warn,
        Block
    }

    internal sealed class HarmonyCompatibilityFinding
    {
        internal string Context { get; set; }
        internal string Target { get; set; }
        internal string Owner { get; set; }
        internal string PatchType { get; set; }
        internal string PatchMethod { get; set; }
        internal int Priority { get; set; }
        internal HarmonyCompatibilityDecision Decision { get; set; }
        internal string Reason { get; set; }
    }

    /// <summary>
    /// Separates our patch-registration integrity checks from foreign-patch compatibility policy.
    /// Missing or duplicate SteamP2PFriends hooks remain fatal. A foreign observation hook is
    /// recorded, while a foreign hook which can alter the P2P transport contract is blocked.
    /// </summary>
    internal static class HarmonyCompatibilityAudit
    {
        private const string OwnOwner = SteamP2PFriendsPlugin.HARMONY_ID;
        private static readonly object Sync = new object();
        private static readonly List<HarmonyCompatibilityFinding> Findings = new List<HarmonyCompatibilityFinding>();
        private static readonly HashSet<string> FindingKeys = new HashSet<string>(StringComparer.Ordinal);

        internal static void Reset()
        {
            lock (Sync)
            {
                Findings.Clear();
                FindingKeys.Clear();
            }
        }

        internal static int CountOwned(IEnumerable<Patch> patches)
        {
            return patches == null ? 0 : patches.Count(p => p.owner == OwnOwner);
        }

        internal static bool Inspect(MethodBase target, string context)
        {
            if (target == null)
            {
                Record(context, "<null>", "<none>", "Metadata", "<none>", 0,
                    HarmonyCompatibilityDecision.Block, "target-null");
                return false;
            }

            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
                if (info == null) return true;

                bool exclusiveTransportTarget = IsExclusiveTransportTarget(target);
                bool ownTranspilerPresent = CountOwned(info.Transpilers) > 0;
                bool compatible = true;

                compatible &= InspectCollection(context, target, info.Prefixes, HarmonyPatchType.Prefix,
                    exclusiveTransportTarget, ownTranspilerPresent);
                compatible &= InspectCollection(context, target, info.Postfixes, HarmonyPatchType.Postfix,
                    exclusiveTransportTarget, ownTranspilerPresent);
                compatible &= InspectCollection(context, target, info.Finalizers, HarmonyPatchType.Finalizer,
                    exclusiveTransportTarget, ownTranspilerPresent);
                compatible &= InspectCollection(context, target, info.Transpilers, HarmonyPatchType.Transpiler,
                    exclusiveTransportTarget, ownTranspilerPresent);
                return compatible;
            }
            catch (Exception ex)
            {
                Record(context, DescribeTarget(target), "<metadata-error>", "Metadata", "<none>", 0,
                    HarmonyCompatibilityDecision.Block, ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        internal static bool InspectOwnedPatchedMethods(Harmony harmony)
        {
            if (harmony == null)
            {
                Record("StartupFullScan", "<null>", "<none>", "Metadata", "<none>", 0,
                    HarmonyCompatibilityDecision.Block, "harmony-instance-null");
                return false;
            }

            bool compatible = true;
            foreach (MethodBase target in harmony.GetPatchedMethods())
            {
                if (!Inspect(target, "StartupFullScan")) compatible = false;
            }
            return compatible;
        }

        internal static void WriteReport(bool diagnosticBuildValid)
        {
            HarmonyCompatibilityFinding[] snapshot;
            lock (Sync)
            {
                snapshot = Findings.ToArray();
            }

            string directory = Path.Combine(Paths.ConfigPath, "SteamP2PFriends");
            string path = Path.Combine(directory, "p2p-harmony-compatibility.json");
            Directory.CreateDirectory(directory);

            var json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"schemaVersion\": 1,\n");
            json.Append("  \"generatedUtc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
            json.Append("  \"pluginOwner\": \"").Append(Escape(OwnOwner)).Append("\",\n");
            json.Append("  \"diagnosticBuildValid\": ").Append(diagnosticBuildValid ? "true" : "false").Append(",\n");
            json.Append("  \"findings\": [");

            for (int i = 0; i < snapshot.Length; i++)
            {
                HarmonyCompatibilityFinding finding = snapshot[i];
                if (i > 0) json.Append(',');
                json.Append("\n    {");
                json.Append("\"context\": \"").Append(Escape(finding.Context)).Append("\",");
                json.Append(" \"target\": \"").Append(Escape(finding.Target)).Append("\",");
                json.Append(" \"owner\": \"").Append(Escape(finding.Owner)).Append("\",");
                json.Append(" \"patchType\": \"").Append(Escape(finding.PatchType)).Append("\",");
                json.Append(" \"patchMethod\": \"").Append(Escape(finding.PatchMethod)).Append("\",");
                json.Append(" \"priority\": ").Append(finding.Priority).Append(',');
                json.Append(" \"decision\": \"").Append(finding.Decision).Append("\",");
                json.Append(" \"reason\": \"").Append(Escape(finding.Reason)).Append("\"}");
            }

            if (snapshot.Length > 0) json.Append('\n');
            json.Append("  ]\n}");

            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, json.ToString(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            RoleLogger.Info("[Shared]", "[Compat] Harmony compatibility report: " + path +
                " findings=" + snapshot.Length);
        }

        private static bool InspectCollection(
            string context,
            MethodBase target,
            IEnumerable<Patch> patches,
            HarmonyPatchType patchType,
            bool exclusiveTransportTarget,
            bool ownTranspilerPresent)
        {
            if (patches == null) return true;

            bool compatible = true;
            foreach (Patch patch in patches)
            {
                if (patch.owner == OwnOwner) continue;

                HarmonyCompatibilityDecision decision = Decide(
                    patchType, exclusiveTransportTarget, ownTranspilerPresent, out string reason);
                Record(context, DescribeTarget(target), patch.owner, patchType.ToString(),
                    DescribePatchMethod(patch.PatchMethod), patch.priority, decision, reason);
                if (decision == HarmonyCompatibilityDecision.Block) compatible = false;
            }
            return compatible;
        }

        private static HarmonyCompatibilityDecision Decide(
            HarmonyPatchType patchType,
            bool exclusiveTransportTarget,
            bool ownTranspilerPresent,
            out string reason)
        {
            if (patchType == HarmonyPatchType.Transpiler && ownTranspilerPresent)
            {
                reason = "foreign-transpiler-on-own-transpiled-target";
                return HarmonyCompatibilityDecision.Block;
            }

            if (exclusiveTransportTarget &&
                (patchType == HarmonyPatchType.Prefix || patchType == HarmonyPatchType.Finalizer ||
                 patchType == HarmonyPatchType.Transpiler))
            {
                reason = "foreign-patch-on-exclusive-p2p-transport-target";
                return HarmonyCompatibilityDecision.Block;
            }

            reason = "foreign-patch-recorded-for-compatibility-review";
            return HarmonyCompatibilityDecision.Warn;
        }

        internal static bool IsExclusiveTransportTarget(MethodBase target)
        {
            Type type = target.DeclaringType;
            if (type == null) return false;

            string typeName = type.FullName ?? type.Name;
            string methodName = target.Name ?? string.Empty;

            if (typeName == "Steamworks.SteamGameServerNetworkingSockets") return true;
            if (typeName.IndexOf("SteamNetworkingSockets.ClientTransport_", StringComparison.Ordinal) >= 0) return true;
            if (typeName.IndexOf("SteamNetworkingSockets.ServerTransport_", StringComparison.Ordinal) >= 0) return true;
            if (typeName == "SDG.Unturned.ClientMethodHandle" &&
                methodName.StartsWith("SendAndLoopback", StringComparison.Ordinal)) return true;
            if (typeName == "SDG.Unturned.Provider" &&
                (methodName == "accept" || methodName == "reject" || methodName == "RequestDisconnect")) return true;
            if (typeName.StartsWith("Steamworks.Callback`1", StringComparison.Ordinal) &&
                methodName == "CreateGameServer") return true;

            return false;
        }

        private static void Record(
            string context,
            string target,
            string owner,
            string patchType,
            string patchMethod,
            int priority,
            HarmonyCompatibilityDecision decision,
            string reason)
        {
            string safeOwner = string.IsNullOrEmpty(owner) ? "<unknown>" : owner;
            string key = target + "|" + safeOwner + "|" + patchType + "|" + patchMethod + "|" + decision;
            bool added;
            lock (Sync)
            {
                added = FindingKeys.Add(key);
                if (added)
                {
                    Findings.Add(new HarmonyCompatibilityFinding
                    {
                        Context = context ?? string.Empty,
                        Target = target ?? string.Empty,
                        Owner = safeOwner,
                        PatchType = patchType ?? string.Empty,
                        PatchMethod = patchMethod ?? string.Empty,
                        Priority = priority,
                        Decision = decision,
                        Reason = reason ?? string.Empty
                    });
                }
            }

            if (!added) return;

            string level = decision == HarmonyCompatibilityDecision.Block ? "BLOCK" : "WARN";
            RoleLogger.Warn("[Shared]", "[Compat] " + level + " context=" + context +
                " target=" + target + " owner=" + safeOwner + " type=" + patchType +
                " method=" + patchMethod + " reason=" + reason);
        }

        private static string DescribeTarget(MethodBase target)
        {
            string typeName = target.DeclaringType == null ? "<unknown>" : target.DeclaringType.FullName;
            return typeName + "." + target.Name;
        }

        private static string DescribePatchMethod(MethodInfo patchMethod)
        {
            if (patchMethod == null) return "<unknown>";
            string typeName = patchMethod.DeclaringType == null ? "<unknown>" : patchMethod.DeclaringType.FullName;
            return typeName + "." + patchMethod.Name;
        }

        private static string Escape(string value)
        {
            if (value == null) return string.Empty;

            var escaped = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '"': escaped.Append("\\\""); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (char.IsControl(c)) escaped.Append("\\u").Append(((int)c).ToString("x4"));
                        else escaped.Append(c);
                        break;
                }
            }
            return escaped.ToString();
        }
    }
}
