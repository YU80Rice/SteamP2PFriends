using SDG.Unturned;
using System;

namespace SteamP2PFriends.Host
{
    /// <summary>Immutable room rules captured once before Provider.host().</summary>
    public sealed class P2PRoomRules
    {
        public bool EnablePvp { get; }
        public bool KeepInventoryOnDeath { get; }
        public bool KeepSkillsOnDeath { get; }
        public bool KeepExperienceOnDeath { get; }

        public P2PRoomRules(bool enablePvp, bool keepInventoryOnDeath,
            bool keepSkillsOnDeath, bool keepExperienceOnDeath)
        {
            EnablePvp = enablePvp;
            KeepInventoryOnDeath = keepInventoryOnDeath;
            KeepSkillsOnDeath = keepSkillsOnDeath;
            KeepExperienceOnDeath = keepExperienceOnDeath;
        }

        internal void ApplyTo(ModeConfigData modeConfig)
        {
            PlayersConfigData players = modeConfig?.Players;
            if (players == null)
                throw new InvalidOperationException("P2P room rules require PlayersConfigData");

            if (KeepInventoryOnDeath)
            {
                players.Lose_Weapons_PvP = false;
                players.Lose_Weapons_PvE = false;
                players.Lose_Clothes_PvP = false;
                players.Lose_Clothes_PvE = false;
                players.Lose_Items_PvP = 0f;
                players.Lose_Items_PvE = 0f;
            }

            if (KeepSkillsOnDeath)
            {
                players.Lose_Skills_PvP = 1f;
                players.Lose_Skills_PvE = 1f;
                players.Lose_Skill_Levels_PvP = 0U;
                players.Lose_Skill_Levels_PvE = 0U;
            }

            if (KeepExperienceOnDeath)
            {
                players.Lose_Experience_PvP = 1f;
                players.Lose_Experience_PvE = 1f;
            }
        }
    }
}
