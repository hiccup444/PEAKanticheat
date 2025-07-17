using System;
using System.Collections.Generic;
using Photon.Realtime;
using Steamworks;

namespace AntiCheatMod
{
    public enum BlockReason
    {
        Manual,
        AutoDetection,
        WhitelistOverride
    }

    public class BlockEntry
    {
        public int ActorNumber { get; set; }
        public string PlayerName { get; set; }
        public BlockReason Reason { get; set; }
        public string SpecificReason { get; set; }
        public DateTime BlockTime { get; set; }
        public CSteamID SteamID { get; set; }
        public bool IsManual { get; set; }
        
        public BlockEntry(int actorNumber, string playerName, BlockReason reason, string specificReason, CSteamID steamID = default)
        {
            ActorNumber = actorNumber;
            PlayerName = playerName;
            Reason = reason;
            SpecificReason = specificReason;
            BlockTime = DateTime.Now;
            SteamID = steamID;
            IsManual = reason == BlockReason.Manual;
        }
    }

    public static class BlockingManager
    {
        private static readonly Dictionary<int, BlockEntry> _blockedPlayers = new Dictionary<int, BlockEntry>();
        private static readonly HashSet<ulong> _whitelistedSteamIDs = new HashSet<ulong>();
        // Track recently unblocked players to prevent immediate re-blocking
        private static readonly HashSet<int> _recentlyUnblocked = new HashSet<int>();
        
        // Events for UI updates
        public static event Action<BlockEntry> OnPlayerBlocked;
        public static event Action<int> OnPlayerUnblocked;
        public static event Action<ulong> OnWhitelistAdded;
        public static event Action<ulong> OnWhitelistRemoved;

        public static bool IsBlocked(int actorNumber)
        {
            return _blockedPlayers.ContainsKey(actorNumber);
        }

        public static BlockEntry GetBlockEntry(int actorNumber)
        {
            return _blockedPlayers.TryGetValue(actorNumber, out var entry) ? entry : null;
        }

        public static List<BlockEntry> GetAllBlockedPlayers()
        {
            return new List<BlockEntry>(_blockedPlayers.Values);
        }

        public static void BlockPlayer(Photon.Realtime.Player player, string reason, BlockReason blockReason = BlockReason.AutoDetection, CSteamID steamID = default)
        {
            // Never block master client
            if (player.IsMasterClient)
            {
                AntiCheatPlugin.Logger.LogWarning($"[BLOCK PREVENTED] Attempted to block master client {player.NickName}");
                return;
            }

            // Check whitelist
            if (steamID != CSteamID.Nil && _whitelistedSteamIDs.Contains(steamID.m_SteamID))
            {
                AntiCheatPlugin.Logger.LogInfo($"[WHITELIST] Player {player.NickName} is whitelisted - not blocking");
                return;
            }

            // Prevent auto-blocking if recently unblocked, unless this is a manual block
            if (blockReason == BlockReason.AutoDetection && _recentlyUnblocked.Contains(player.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[BLOCK PREVENTED] Player {player.NickName} was recently unblocked - not auto-blocking");
                return;
            }

            var blockEntry = new BlockEntry(player.ActorNumber, player.NickName, blockReason, reason, steamID);
            _blockedPlayers[player.ActorNumber] = blockEntry;
            _recentlyUnblocked.Remove(player.ActorNumber); // Allow future auto-blocks after a new detection
            
            // Update player status
            PlayerManager.UpdatePlayerStatus(player.ActorNumber, PlayerStatus.Blocked);
            
            OnPlayerBlocked?.Invoke(blockEntry);
        }

        public static void UnblockPlayer(int actorNumber)
        {
            if (_blockedPlayers.Remove(actorNumber))
            {
                PlayerManager.UpdatePlayerStatus(actorNumber, PlayerStatus.Detected);
                OnPlayerUnblocked?.Invoke(actorNumber);
                _recentlyUnblocked.Add(actorNumber);
            }
        }

        public static void AddToWhitelist(ulong steamID)
        {
            _whitelistedSteamIDs.Add(steamID);
            OnWhitelistAdded?.Invoke(steamID);
        }

        public static void RemoveFromWhitelist(ulong steamID)
        {
            _whitelistedSteamIDs.Remove(steamID);
            OnWhitelistRemoved?.Invoke(steamID);
        }

        public static bool IsWhitelisted(ulong steamID)
        {
            return _whitelistedSteamIDs.Contains(steamID);
        }

        public static List<ulong> GetWhitelistedSteamIDs()
        {
            return new List<ulong>(_whitelistedSteamIDs);
        }

        public static void ClearAllBlocks()
        {
            var blockedActors = new List<int>(_blockedPlayers.Keys);
            foreach (var actorNumber in blockedActors)
            {
                UnblockPlayer(actorNumber);
            }
        }

        public static void RemovePlayer(int actorNumber)
        {
            _blockedPlayers.Remove(actorNumber);
        }

        public static void SetBlockList(int[] actorNumbers)
        {
            _blockedPlayers.Clear();
            foreach (var actor in actorNumbers)
            {
                // You may want to store more info, but this is the minimum for sync
                _blockedPlayers[actor] = new BlockEntry(actor, "", BlockReason.Manual, "Synced from master");
            }
        }
        public static void SetWhitelist(ulong[] ids)
        {
            _whitelistedSteamIDs.Clear();
            foreach (var id in ids)
                _whitelistedSteamIDs.Add(id);
        }
    }
} 