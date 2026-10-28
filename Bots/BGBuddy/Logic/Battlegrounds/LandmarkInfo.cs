using System;
using Styx;
using Styx.Logic;

namespace Bots.BGBuddy.Logic.Battlegrounds
{
    /// <summary>
    /// Tracks the state of a single battleground landmark (node/flag).
    /// Contains control status, player counts around the node, and a MapBox defining its area.
    /// </summary>
    public class LandmarkInfo
    {
        public LandmarkInfo(int lmType, LandmarkControlType ctrl, MapBox box)
        {
            Type = lmType;
            Control = ctrl;
            LastProcessed = DateTime.Now;
            Box = box;
        }

        public MapBox Box { get; set; }

        public int Type { get; set; }

        public int FriendlyPlayersAround { get; set; }

        public int EnemyPlayersAround { get; set; }

        public DateTime LastProcessed { get; set; }

        public LandmarkControlType Control { get; set; }

        /// <summary>
        /// Whether this landmark is currently controlled by our team.
        /// Uses the BG team index, not race (cross-faction placement reverses them).
        /// </summary>
        public bool ControlledByUs
        {
            get
            {
                bool weAreHorde = BattlegroundTeams.IsHordeSide;
                switch (Control)
                {
                    case LandmarkControlType.AllianceControlled:
                        return !weAreHorde;
                    case LandmarkControlType.HordeControlled:
                        return weAreHorde;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Whether this landmark is currently controlled by the enemy team.
        /// </summary>
        public bool ControlledByEnemy
        {
            get
            {
                bool weAreHorde = BattlegroundTeams.IsHordeSide;
                switch (Control)
                {
                    case LandmarkControlType.AllianceControlled:
                        return weAreHorde;
                    case LandmarkControlType.HordeControlled:
                        return !weAreHorde;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Updates friendly/enemy player counts around this landmark's box area.
        /// </summary>
        public void Process()
        {
            FriendlyPlayersAround = RaidHelper.CountFriendlyPlayersInBox(Box);
            EnemyPlayersAround = RaidHelper.CountEnemyPlayersInBox(Box);
            LastProcessed = DateTime.Now;
        }
    }
}
