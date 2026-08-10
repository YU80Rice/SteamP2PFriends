using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    internal enum RegionGenerationState : byte
    {
        NotStarted = 0,
        Preparing = 1,
        Committed = 2
    }

    internal readonly struct RegionGenerationToken
    {
        internal RegionGenerationToken(int epoch, byte x, byte y, bool ownsTransaction)
        {
            Epoch = epoch;
            X = x;
            Y = y;
            OwnsTransaction = ownsTransaction;
        }

        internal int Epoch { get; }
        internal byte X { get; }
        internal byte Y { get; }
        internal bool OwnsTransaction { get; }
    }

    /// <summary>
    /// Pure state ledger used by the Harmony boundary. It does not call Unity, Provider, or ItemManager.
    /// </summary>
    internal sealed class AuthoritativeRegionGenerationLedger
    {
        private readonly RegionGenerationState[,] _states;
        private int _epoch;

        internal AuthoritativeRegionGenerationLedger(int worldSize)
        {
            if (worldSize <= 0 || worldSize > byte.MaxValue + 1)
                throw new ArgumentOutOfRangeException(nameof(worldSize));
            _states = new RegionGenerationState[worldSize, worldSize];
            _epoch = 1;
        }

        internal int Epoch => _epoch;

        internal void Reset()
        {
            Array.Clear(_states, 0, _states.Length);
            unchecked
            {
                _epoch++;
                if (_epoch == 0) _epoch = 1;
            }
        }

        internal bool TryBegin(byte x, byte y, out RegionGenerationToken token)
        {
            if (x >= _states.GetLength(0) || y >= _states.GetLength(1))
            {
                token = default;
                return false;
            }

            if (_states[x, y] != RegionGenerationState.NotStarted)
            {
                token = new RegionGenerationToken(_epoch, x, y, false);
                return false;
            }

            _states[x, y] = RegionGenerationState.Preparing;
            token = new RegionGenerationToken(_epoch, x, y, true);
            return true;
        }

        internal bool Commit(in RegionGenerationToken token)
        {
            if (!IsCurrentOwner(token)) return false;
            _states[token.X, token.Y] = RegionGenerationState.Committed;
            return true;
        }

        internal bool Abort(in RegionGenerationToken token)
        {
            if (!IsCurrentOwner(token)) return false;
            _states[token.X, token.Y] = RegionGenerationState.NotStarted;
            return true;
        }

        internal RegionGenerationState GetState(byte x, byte y)
        {
            if (x >= _states.GetLength(0) || y >= _states.GetLength(1))
                return RegionGenerationState.NotStarted;
            return _states[x, y];
        }

        private bool IsCurrentOwner(in RegionGenerationToken token)
        {
            return token.OwnsTransaction
                && token.Epoch == _epoch
                && token.X < _states.GetLength(0)
                && token.Y < _states.GetLength(1)
                && _states[token.X, token.Y] == RegionGenerationState.Preparing;
        }
    }

    /// <summary>
    /// Listen-host-only single-writer gate for ItemManager.generateItems(byte, byte).
    /// The first successful producer commits the region. Later local lazy-generation calls are skipped.
    /// Vanilla singleplayer, clients, and U3DS pass through unchanged.
    /// </summary>
    public static class AuthoritativeItemGenerationGatePatch
    {
        private const string Point = "[ItemAuthorityGate]";
        private static readonly Type[] TargetParameters = { typeof(byte), typeof(byte) };
        private static readonly AuthoritativeRegionGenerationLedger Ledger =
            new AuthoritativeRegionGenerationLedger(Regions.WORLD_SIZE);

        private static int _commitLogCount;
        private static int _skipLogCount;
        private static bool _resetCallbackRegistered;

        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "not registered";

        public sealed class CallState
        {
            internal RegionGenerationToken Token;
            internal bool Eligible;
        }

        public static bool RegisterManual(Harmony harmony)
        {
            try
            {
                if (!_resetCallbackRegistered)
                {
                    WorldSyncDiagnosticCore.RegisterSessionResetCallback(ResetForSession);
                    _resetCallbackRegistered = true;
                }

                MethodInfo original = AccessTools.Method(typeof(ItemManager), "generateItems", TargetParameters);
                MethodInfo prefix = AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Prefix));
                MethodInfo postfix = AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Postfix));
                MethodInfo finalizer = AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Finalizer));
                if (harmony == null || original == null || prefix == null || postfix == null || finalizer == null)
                {
                    AllRegistrationsSucceeded = false;
                    RegistrationSummary = "target or patch method resolution failed";
                    return false;
                }

                if (!IsRegistered(prefix, HarmonyPatchType.Prefix))
                    harmony.Patch(original, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
                if (!IsRegistered(postfix, HarmonyPatchType.Postfix))
                    harmony.Patch(original, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
                if (!IsRegistered(finalizer, HarmonyPatchType.Finalizer))
                    harmony.Patch(original, finalizer: new HarmonyMethod(finalizer) { priority = Priority.Last });

                AllRegistrationsSucceeded = VerifyRegistration();
                RegistrationSummary = AllRegistrationsSucceeded
                    ? "Prefix/Postfix/Finalizer=3/3 owner+MethodInfo verified; listen-host-only"
                    : "runtime patch identity verification failed";
                SafeInfo($"registration={AllRegistrationsSucceeded} summary={RegistrationSummary}");
                return AllRegistrationsSucceeded;
            }
            catch (Exception ex)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = ex.GetType().Name + ": " + ex.Message;
                SafeError("RegisterManual exception: " + ex);
                return false;
            }
        }

        public static bool VerifyTargetSignatures()
        {
            return AccessTools.Method(typeof(ItemManager), "generateItems", TargetParameters) != null
                && AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Prefix)) != null
                && AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Postfix)) != null
                && AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Finalizer)) != null;
        }

        public static bool VerifyRegistration()
        {
            MethodInfo prefix = AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Prefix));
            MethodInfo postfix = AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Postfix));
            MethodInfo finalizer = AccessTools.Method(typeof(AuthoritativeItemGenerationGatePatch), nameof(Finalizer));
            return IsRegistered(prefix, HarmonyPatchType.Prefix)
                && IsRegistered(postfix, HarmonyPatchType.Postfix)
                && IsRegistered(finalizer, HarmonyPatchType.Finalizer);
        }

        private static bool IsRegistered(MethodInfo patch, HarmonyPatchType patchType)
        {
            return WorldSyncDiagnosticCore.IsPatchRegistered(
                typeof(ItemManager), "generateItems", patch, patchType, TargetParameters, false);
        }

        public static bool Prefix(byte x, byte y, ref CallState __state)
        {
            __state = new CallState();
            try
            {
                ThreadUtil.assertIsGameThread();
                // Do not use ShouldProcessClientHostListen here: initial P0-B-6 generation runs
                // during onLevelLoaded before Level.isLoaded / Provider.isConnected are guaranteed.
                // IsP2PHostMode is established before Provider.host and remains the session identity.
                if (!HostManager.IsP2PHostMode || !Provider.isServer)
                    return true;

                __state.Eligible = true;
                if (Ledger.TryBegin(x, y, out RegionGenerationToken token))
                {
                    __state.Token = token;
                    return true;
                }

                if (_skipLogCount < 48)
                {
                    _skipLogCount++;
                    SafeInfo($"skip #{_skipLogCount}/48 region=({x},{y}) " +
                        $"state={Ledger.GetState(x, y)} reason=already-generated-or-preparing");
                }
                return false;
            }
            catch (Exception ex)
            {
                SafeError($"Prefix fail-closed region=({x},{y}) exception={ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public static void Postfix(CallState __state, bool __runOriginal)
        {
            try
            {
                if (__state == null || !__state.Eligible || !__state.Token.OwnsTransaction) return;
                ThreadUtil.assertIsGameThread();

                if (!__runOriginal)
                {
                    Ledger.Abort(__state.Token);
                    SafeError($"transaction aborted because another Prefix skipped original region=({__state.Token.X},{__state.Token.Y})");
                    return;
                }

                if (!Ledger.Commit(__state.Token))
                {
                    SafeError($"Commit rejected region=({__state.Token.X},{__state.Token.Y}) epoch={__state.Token.Epoch}");
                    return;
                }

                if (_commitLogCount < 16)
                {
                    _commitLogCount++;
                    SafeInfo($"commit #{_commitLogCount}/16 region=({__state.Token.X},{__state.Token.Y}) epoch={__state.Token.Epoch}");
                }
            }
            catch (Exception ex)
            {
                try { Ledger.Abort(__state?.Token ?? default); } catch { }
                SafeError("Postfix exception: " + ex);
            }
        }

        public static Exception Finalizer(Exception __exception, CallState __state)
        {
            try
            {
                if (__exception != null && __state != null && __state.Token.OwnsTransaction)
                {
                    Ledger.Abort(__state.Token);
                    SafeError($"abort region=({__state.Token.X},{__state.Token.Y}) exception={__exception.GetType().Name}");
                }
            }
            catch { }
            return __exception;
        }

        public static void ResetForSession()
        {
            try
            {
                Ledger.Reset();
                _commitLogCount = 0;
                _skipLogCount = 0;
                SafeInfo($"reset epoch={Ledger.Epoch}");
            }
            catch (Exception ex)
            {
                SafeError("ResetForSession exception: " + ex.Message);
            }
        }

        internal static AuthoritativeRegionGenerationLedger CreateLedgerForTests(int worldSize)
        {
            return new AuthoritativeRegionGenerationLedger(worldSize);
        }

        private static void SafeInfo(string message)
        {
            try { RoleLogger.Info("[Host]", Point + " " + message); } catch { }
        }

        private static void SafeError(string message)
        {
            try { RoleLogger.Error("[Shared]", Point + " " + message); } catch { }
        }
    }
}
