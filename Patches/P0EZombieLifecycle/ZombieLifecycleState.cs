using System;

namespace SteamP2PFriends.Patches.P0EZombieLifecycle
{
    /// <summary>
    /// v0.2.3.39 Zombie 生命周期 v6.6（Codex 第五十二次审计 §5 放行编码）：
    ///
    /// ZombieManager.onBoundUpdated(Player, byte, byte) 生命周期状态。
    /// 仅在 Prefix 真实写入后才置 *WasModified=true，*Original* 字段记录写入前的原值。
    /// 不存在 *PostfixRestored 字段（v6.5 已删除，改为幂等恢复）。
    /// </summary>
    public struct ZombieLifecycleState
    {
        /// <summary>true 当且仅当 Prefix 真实写入 regions[oldBound].isNetworked=false</summary>
        public bool oldWasModified;

        /// <summary>Prefix 读取的原始值（用于恢复）</summary>
        public bool oldOriginalIsNetworked;

        /// <summary>记录 oldBound（用于恢复）</summary>
        public byte oldBound;

        /// <summary>true 当且仅当 Prefix 真实写入 loadedBounds[newBound].isZombiesLoaded=true</summary>
        public bool newWasModified;

        /// <summary>Prefix 读取的原始值（用于回滚）</summary>
        public bool newOriginalIsZombiesLoaded;

        /// <summary>记录 newBound（用于回滚）</summary>
        public byte newBound;
    }
}
