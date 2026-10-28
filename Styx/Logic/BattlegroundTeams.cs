using System;
using System.Threading;
using Styx.Helpers;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic
{
    /// <summary>
    /// Single source of truth for battleground team membership.
    ///
    /// WoW assigns BG teams independently of race: on cross-realm / cross-faction
    /// servers a Horde character can be placed on the Alliance team and vice versa.
    /// The client publishes that assignment in PLAYER_BYTES_3 byte 3
    /// (<see cref="WoWPlayer.BattlefieldArenaFaction"/>) using the same 0/1 indexing
    /// as <see cref="Battlegrounds.GetBattlefieldWinner"/> — 0 = Horde team,
    /// 1 = Alliance team.
    ///
    /// Bot code must consult this type instead of WoWPlayer.IsHorde / IsAlliance,
    /// which come from the race descriptor and are wrong for cross-faction placement.
    ///
    /// Safety: the per-player descriptor is only trusted after it has been cross-checked
    /// against the client's GetBattlefieldArenaFaction and shown to actually split the
    /// battleground population across two values. On a server that does not serialise
    /// the field (every player reads the same byte) it is rejected and every accessor
    /// degrades to the pre-existing behaviour, so this can never invert friend/foe.
    /// </summary>
    public static class BattlegroundTeams
    {
        /// <summary>No team data available.</summary>
        public const int UnknownTeam = -1;

        /// <summary>Horde battleground team. Matches <see cref="BattlefieldWinner.Horde"/>.</summary>
        public const int HordeTeam = 0;

        /// <summary>Alliance battleground team. Matches <see cref="BattlefieldWinner.Alliance"/>.</summary>
        public const int AllianceTeam = 1;

        /// <summary>How many refreshes we spend trying to validate the per-player field.</summary>
        private const int MaxTrustAttempts = 20;

        /// <summary>
        /// How many times we ask the client for our faction before giving up.
        /// The budget is spent with a growing backoff (see
        /// <see cref="FirstClientQueryDelayMs"/>) so it is not burned in the first
        /// second after map change, while the client is still on the loading screen
        /// and Lua cannot answer yet.
        /// </summary>
        private const int MaxClientQueryAttempts = 10;

        /// <summary>First spacing between client-API attempts (milliseconds); doubles up to the cap.</summary>
        private const int FirstClientQueryDelayMs = 1000;

        /// <summary>Upper bound for the spacing between client-API attempts (milliseconds).</summary>
        private const int MaxClientQueryDelayMs = 8000;

        private static readonly WaitTimer _retryTimer = new WaitTimer(TimeSpan.FromSeconds(2));

        private static int _myTeamIndex = UnknownTeam;
        private static int _clientTeam = UnknownTeam;
        private static int _clientQueryAttempts;

        /// <summary>
        /// UTC ticks when the next client-API attempt is allowed — paces the
        /// bounded budget across the loading screen instead of spending it in
        /// the first unresolved resolve calls after map change.
        /// </summary>
        private static long _nextClientQueryUtcTicks;
        private static bool _descriptorTrusted;
        private static int _trustAttempts;
        private static uint _resolvedMapId;
        private static bool _resolved;

        /// <summary>
        /// Team index of the local player: 0 = Horde team, 1 = Alliance team,
        /// -1 when unknown (not in a battleground, or not published yet).
        /// </summary>
        public static int MyTeamIndex => Resolve(false);

        /// <summary>Whether the local player's team index is currently known.</summary>
        public static bool IsKnown => MyTeamIndex != UnknownTeam;

        /// <summary>
        /// True when the per-player team descriptor was validated for this battleground
        /// and may be used to classify individual players.
        /// </summary>
        public static bool UsesPlayerTeamDescriptor
        {
            get
            {
                Resolve(false);
                return _myTeamIndex != UnknownTeam && _descriptorTrusted;
            }
        }

        /// <summary>
        /// True when the local player is on the Horde battleground team.
        /// Falls back to race only when the team index is unknown.
        /// </summary>
        public static bool IsHordeSide
        {
            get
            {
                int team = MyTeamIndex;
                if (team == UnknownTeam)
                    return ObjectManager.Me?.IsHorde ?? false;

                return team == HordeTeam;
            }
        }

        /// <summary>
        /// True when the local player is on the Alliance battleground team.
        /// Falls back to race only when the team index is unknown.
        /// </summary>
        public static bool IsAllianceSide => !IsHordeSide;

        /// <summary>
        /// Team index (0/1) declared by a unit's descriptor, or
        /// <see cref="UnknownTeam"/> when the unit is not a player or carries no
        /// plausible value. This is the raw field; use <see cref="IsOnMyTeam"/> for
        /// the validated answer.
        /// </summary>
        public static int GetTeamIndex(WoWUnit? unit)
        {
            var player = unit as WoWPlayer;
            if (player == null)
                return UnknownTeam;

            byte faction = player.BattlefieldArenaFaction;
            return faction <= AllianceTeam ? (int)faction : UnknownTeam;
        }

        /// <summary>
        /// Whether <paramref name="unit"/> belongs to the local player's battleground
        /// team. Uses the validated per-unit team descriptor when available and falls
        /// back to the native (BG-team-aware) reaction otherwise.
        /// </summary>
        public static bool IsOnMyTeam(WoWUnit? unit)
        {
            if (unit == null)
                return false;

            var me = ObjectManager.Me;
            if (me == null)
                return false;

            if (unit.Guid == me.Guid)
                return true;

            int myTeam = MyTeamIndex;
            if (myTeam != UnknownTeam && _descriptorTrusted)
            {
                int theirTeam = GetTeamIndex(unit);
                if (theirTeam != UnknownTeam)
                    return theirTeam == myTeam;
            }

            // No usable team data for this unit: IsFriendly uses GetReactionTowards,
            // which the server resolves from real BG team membership, so it stays
            // correct here on every server.
            return unit.IsFriendly;
        }

        /// <summary>Whether <paramref name="unit"/> is on the opposing battleground team.</summary>
        public static bool IsEnemy(WoWUnit? unit)
        {
            if (unit == null)
                return false;

            var me = ObjectManager.Me;
            if (me == null)
                return false;

            if (unit.Guid == me.Guid)
                return false;

            return !IsOnMyTeam(unit);
        }

        /// <summary>
        /// Drops cached state. Call when entering or leaving a battleground so a stale
        /// team index from the previous match is never reused.
        /// </summary>
        public static void Reset()
        {
            _myTeamIndex = UnknownTeam;
            _clientTeam = UnknownTeam;
            _clientQueryAttempts = 0;
            _nextClientQueryUtcTicks = 0;
            _descriptorTrusted = false;
            _trustAttempts = 0;
            _resolvedMapId = 0;
            _resolved = false;
            _retryTimer.Reset();
        }

        /// <summary>
        /// Blocks briefly (for BG-entry timing) until the team index is known.
        /// Returns false when the client never publishes it, in which case callers
        /// transparently keep the race-based behaviour.
        /// </summary>
        public static bool WaitForTeam(TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                if (Resolve(true) != UnknownTeam)
                    return true;

                Thread.Sleep(50);
            }

            return Resolve(true) != UnknownTeam;
        }

        /// <summary>
        /// Human readable description of the resolved side, used for the once-per-BG
        /// diagnostic line in the log.
        /// </summary>
        public static string Describe()
        {
            var me = ObjectManager.Me;
            if (me == null)
                return "team unknown (no local player)";

            int team = MyTeamIndex;
            string teamName = team == HordeTeam ? "Horde team"
                            : team == AllianceTeam ? "Alliance team"
                            : "unknown team";

            string raceName = me.IsHorde ? "Horde race"
                            : me.IsAlliance ? "Alliance race"
                            : "unknown race";

            bool crossFaction = team != UnknownTeam && ((team == HordeTeam) != me.IsHorde);

            return string.Format("{0} (index {1}), race = {2}, per-player team data = {3}{4}",
                teamName,
                team,
                raceName,
                team == UnknownTeam ? "n/a" : _descriptorTrusted ? "yes" : "no (using reactions)",
                crossFaction ? " — CROSS-FACTION placement" : string.Empty);
        }

        private static int Resolve(bool force)
        {
            // Never let a transient memory/descriptor failure break the caller: a bot
            // tick must not throw just because the team index is momentarily unreadable.
            try
            {
                return ResolveCore(force);
            }
            catch
            {
                _myTeamIndex = UnknownTeam;
                return _myTeamIndex;
            }
        }

        private static int ResolveCore(bool force)
        {
            var me = ObjectManager.Me;
            if (me == null)
            {
                _myTeamIndex = UnknownTeam;
                return _myTeamIndex;
            }

            // Memory-based gate: cheap and true the moment the BG map loads, unlike
            // Battlegrounds.IsInsideBattleground which polls the Lua queue status.
            if (!me.CurrentMap.IsBattleground)
            {
                _myTeamIndex = UnknownTeam;
                _clientTeam = UnknownTeam;
                _descriptorTrusted = false;
                _resolvedMapId = 0;
                _resolved = false;
                return _myTeamIndex;
            }

            bool sameMap = _resolved && _resolvedMapId == me.MapId;

            // Our own side never changes within one battleground instance, so a resolved
            // answer is sticky until Reset()/a new map. The per-player descriptor is a
            // separate matter: the BG population is still filling in at entry, so keep
            // trying to validate it for a bounded number of refreshes.
            if (sameMap && _myTeamIndex != UnknownTeam)
            {
                MaybeValidateDescriptor(me);
                return _myTeamIndex;
            }

            if (!force && sameMap && !_retryTimer.IsFinished)
                return _myTeamIndex;

            _retryTimer.Reset();

            // New battleground instance: never carry the previous match's answer over.
            if (!sameMap)
            {
                _clientTeam = UnknownTeam;
                _clientQueryAttempts = 0;
                _nextClientQueryUtcTicks = 0;
                _descriptorTrusted = false;
                _trustAttempts = 0;
            }

            _resolvedMapId = me.MapId;
            _resolved = true;

            // Bounded: if the client function does not exist on this core we must not
            // hammer Lua every retry for the rest of the match. The budget is also
            // paced with a growing backoff: firing one attempt per resolve call
            // exhausted it within the first second on a live BG — while the client was
            // still on the loading screen and Lua could not answer yet — and the
            // resolver then stayed unknown for the entire match (observed live:
            // client=unknown for 90+ attempts after the entry timed out).
            if (_clientTeam == UnknownTeam && _clientQueryAttempts < MaxClientQueryAttempts)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now >= _nextClientQueryUtcTicks)
                {
                    _clientQueryAttempts++;
                    _clientTeam = QueryClientTeam();

                    if (_clientTeam != UnknownTeam)
                    {
                        Logging.Write("[BGTeam] client API answered after {0} attempt(s): team={1}",
                            _clientQueryAttempts, _clientTeam == HordeTeam ? "Horde" : "Alliance");
                        _nextClientQueryUtcTicks = long.MaxValue;
                    }
                    else
                    {
                        int delayMs = FirstClientQueryDelayMs * (1 << Math.Max(0, _clientQueryAttempts - 1));
                        if (delayMs > MaxClientQueryDelayMs)
                            delayMs = MaxClientQueryDelayMs;
                        _nextClientQueryUtcTicks = now + delayMs * TimeSpan.TicksPerMillisecond;
                    }
                }
            }

            _myTeamIndex = _clientTeam;

            ValidateDescriptor(me);

            LogResolution(!sameMap);

            return _myTeamIndex;
        }

        /// <summary>
        /// Retries descriptor validation while the team is already known and the
        /// per-player field has not been proven yet.
        /// </summary>
        private static void MaybeValidateDescriptor(WoWPlayer me)
        {
            if (_descriptorTrusted || _trustAttempts >= MaxTrustAttempts)
                return;

            if (!_retryTimer.IsFinished)
                return;

            _retryTimer.Reset();
            ValidateDescriptor(me);
        }

        /// <summary>
        /// Marks the per-player descriptor usable only when it carries a value, agrees
        /// with the client API, actually splits the battleground population into two
        /// teams, and does not contradict the server's own friend/foe reactions.
        /// A field that reads identically for every player is not team data, and a field
        /// that disagrees with the reactions (e.g. some unrelated byte) is rejected too.
        /// </summary>
        private static void ValidateDescriptor(WoWPlayer me)
        {
            _trustAttempts++;

            if (_descriptorTrusted)
                return;

            int descriptorTeam = GetTeamIndex(me);

            if (descriptorTeam == UnknownTeam || _clientTeam == UnknownTeam)
                return;

            if (descriptorTeam != _clientTeam)
                return;

            if (CountPlayersOfOppositeTeam(descriptorTeam) == 0)
                return;

            if (!DescriptorAgreesWithReactions(descriptorTeam))
                return;

            _descriptorTrusted = true;
        }

        /// <summary>
        /// Cross-checks the descriptor against the native BG-team-aware reaction on a
        /// small sample of other players. The server resolves those reactions from real
        /// team membership, so they are an independent oracle for the raw byte.
        /// </summary>
        private static bool DescriptorAgreesWithReactions(int myDescriptorTeam)
        {
            var me = ObjectManager.Me;
            if (me == null)
                return false;

            int sampled = 0;
            var players = ObjectManager.GetObjectsOfType<WoWPlayer>(false, false);

            for (int i = 0; i < players.Count && sampled < 8; i++)
            {
                var player = players[i];
                if (player == null || player.Guid == me.Guid)
                    continue;

                int team = GetTeamIndex(player);
                if (team == UnknownTeam)
                    continue;

                sampled++;

                bool descriptorSaysFriendly = team == myDescriptorTeam;
                bool reactionSaysFriendly = player.IsFriendly;

                if (descriptorSaysFriendly != reactionSaysFriendly)
                    return false;
            }

            // Require a couple of samples before believing it; a single teammate proves
            // nothing about a field that could be reading anything.
            return sampled >= 2;
        }

        /// <summary>
        /// Number of other players whose descriptor declares the team opposite to
        /// <paramref name="descriptorTeam"/>. A real battleground always has some.
        /// </summary>
        private static int CountPlayersOfOppositeTeam(int descriptorTeam)
        {
            if (descriptorTeam == UnknownTeam)
                return 0;

            int opposite = 0;
            var players = ObjectManager.GetObjectsOfType<WoWPlayer>(false, false);
            for (int i = 0; i < players.Count; i++)
            {
                int team = GetTeamIndex(players[i]);
                if (team != UnknownTeam && team != descriptorTeam)
                    opposite++;
            }

            return opposite;
        }

        private static void LogResolution(bool firstAttempt)
        {
            var me = ObjectManager.Me;

            // One line on the first attempt per instance, then only while still
            // unresolved — the retry loop must not spam the log.
            if (!firstAttempt && _myTeamIndex != UnknownTeam)
                return;

            int descriptorTeam = GetTeamIndex(me);

            Logging.WriteDebug(
                "[BGTeam] map={0} race={1} descriptor={2} client={3} trusted={4} resolved={5} attempts={6}",
                me?.MapId ?? 0,
                me?.IsHorde == true ? "Horde" : "Alliance",
                descriptorTeam == UnknownTeam ? "unknown" : descriptorTeam.ToString(),
                _clientTeam == UnknownTeam ? "unknown" : _clientTeam.ToString(),
                _descriptorTrusted,
                _myTeamIndex == UnknownTeam ? "unknown" : _myTeamIndex.ToString(),
                _trustAttempts);

            if (descriptorTeam != UnknownTeam && _clientTeam != UnknownTeam && descriptorTeam != _clientTeam)
            {
                Logging.Write("[BGTeam] WARNING: PLAYER_BYTES_3 team index {0} disagrees with " +
                              "GetBattlefieldArenaFaction {1}; trusting the client API.",
                    descriptorTeam, _clientTeam);
            }

            if (_myTeamIndex != UnknownTeam && !_descriptorTrusted && descriptorTeam != UnknownTeam)
            {
                Logging.Write("[BGTeam] PLAYER_BYTES_3 team field not used (does not split the BG " +
                              "population or disagrees with the client); friend/foe uses unit reactions.");
            }
        }

        /// <summary>
        /// Reads the local player's battlefield faction straight from the client.
        /// Returns <see cref="UnknownTeam"/> when the call is unavailable or undefined.
        /// </summary>
        private static int QueryClientTeam()
        {
            try
            {
                var results = Lua.GetReturnValues("return GetBattlefieldArenaFaction()");
                if (results == null || results.Count == 0)
                    return UnknownTeam;

                string value = results[0];
                if (string.IsNullOrEmpty(value) || value == "nil")
                    return UnknownTeam;

                int team = Lua.ParseLuaValue<int>(value);
                return team >= HordeTeam && team <= AllianceTeam ? team : UnknownTeam;
            }
            catch
            {
                return UnknownTeam;
            }
        }
    }
}
