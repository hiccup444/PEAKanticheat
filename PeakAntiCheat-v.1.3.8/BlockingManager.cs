using System;
using System.Collections.Generic;
using System.Linq;
using Photon.Realtime;
using Steamworks;
using Photon.Pun;

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
        // Track recently unblocked players by detection type to prevent immediate re-blocking for the same reason
        private static readonly Dictionary<int, HashSet<DetectionType>> _recentlyUnblockedDetections = new Dictionary<int, HashSet<DetectionType>>();
        
        // Track players with immunity from auto-kick and auto-block no anticheat
        private static readonly HashSet<int> _playersWithImmunity = new HashSet<int>();
        
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

        public static void BlockPlayer(Photon.Realtime.Player player, string reason, BlockReason blockReason = BlockReason.AutoDetection, CSteamID steamID = default, DetectionType? detectionType = null)
        {
            // Never block master client
            if (player.IsMasterClient)
            {
                AntiCheatPlugin.Logger.LogWarning($"[BLOCK PREVENTED] Attempted to block master client {player.NickName}");
                return;
            }

            // Check if player is already blocked
            if (IsBlocked(player.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[BLOCK PREVENTED] Player {player.NickName} is already blocked - skipping duplicate block");
                return;
            }

            // Check whitelist
            if (steamID != CSteamID.Nil && _whitelistedSteamIDs.Contains(steamID.m_SteamID))
            {
                AntiCheatPlugin.Logger.LogInfo($"[WHITELIST] Player {player.NickName} is whitelisted - not blocking");
                // Grant immunity to whitelisted players
                GrantImmunity(player.ActorNumber);
                return;
            }

            // Prevent auto-blocking if recently unblocked for the same detection type, unless this is a manual block
            if (blockReason == BlockReason.AutoDetection && detectionType.HasValue && 
                _recentlyUnblockedDetections.TryGetValue(player.ActorNumber, out var unblockedTypes) && 
                unblockedTypes.Contains(detectionType.Value))
            {
                AntiCheatPlugin.Logger.LogInfo($"[BLOCK PREVENTED] Player {player.NickName} was recently unblocked for {detectionType.Value} - not auto-blocking");
                return;
            }
            
            // Prevent auto-blocking if player was manually unblocked (they have immunity to all detection types)
            if (blockReason == BlockReason.AutoDetection && 
                _recentlyUnblockedDetections.TryGetValue(player.ActorNumber, out var manualUnblockedTypes) && 
                manualUnblockedTypes.Count == Enum.GetValues(typeof(DetectionType)).Length)
            {
                AntiCheatPlugin.Logger.LogInfo($"[BLOCK PREVENTED] Player {player.NickName} was manually unblocked - has immunity to all detection types");
                return;
            }

            // Track any item the player is currently holding
            TrackPlayerHeldItem(player.ActorNumber);

            var blockEntry = new BlockEntry(player.ActorNumber, player.NickName, blockReason, reason, steamID);
            _blockedPlayers[player.ActorNumber] = blockEntry;
            
            // Remove this detection type from recently unblocked list
            if (detectionType.HasValue && _recentlyUnblockedDetections.TryGetValue(player.ActorNumber, out var types))
            {
                types.Remove(detectionType.Value);
            }
            
            // Update player status
            PlayerManager.UpdatePlayerStatus(player.ActorNumber, PlayerStatus.Blocked);
            
            OnPlayerBlocked?.Invoke(blockEntry);
            
            // Auto-kick if enabled and player has anticheat (but not immune)
            if (AntiCheatPlugin.AutoKickBlockedPlayers && AntiCheatPlugin.HasAnticheat(player.ActorNumber) && !HasImmunity(player.ActorNumber))
            {
                // Check if kicks are allowed (security against recent master client changes)
                if (AntiCheatPlugin.AreKicksAllowed())
                {
                    AntiCheatPlugin.Logger.LogInfo($"[AUTO KICK] Auto-kicking blocked player {player.NickName} (Actor #{player.ActorNumber})");
                    AntiCheatPlugin.KickPlayer(player.ActorNumber);
                }
                else
                {
                    AntiCheatPlugin.Logger.LogWarning($"[AUTO KICK] Skipping auto-kick for {player.NickName} - kicks disabled due to recent master client change");
                }
            }
            else if (AntiCheatPlugin.AutoKickBlockedPlayers && AntiCheatPlugin.HasAnticheat(player.ActorNumber) && HasImmunity(player.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[AUTO KICK] Skipping auto-kick for immune player {player.NickName} (Actor #{player.ActorNumber})");
            }
        }

        public static void UnblockPlayer(int actorNumber, DetectionType? detectionType = null)
        {
            if (_blockedPlayers.TryGetValue(actorNumber, out var blockEntry))
            {
                _blockedPlayers.Remove(actorNumber);
                
                // Clean up any tracked items to prevent duplication
                AntiCheatPlugin.CleanupBlockedPlayerItems(actorNumber);
                
                PlayerManager.UpdatePlayerStatus(actorNumber, PlayerStatus.Detected);
                OnPlayerUnblocked?.Invoke(actorNumber);
                
                // Handle immunity based on block reason
                if (blockEntry.Reason == BlockReason.Manual)
                {
                    // Manual unblocks grant immunity from auto-kick, auto-block no anticheat, and mod detections
                    GrantImmunity(actorNumber);
                    AntiCheatPlugin.Logger.LogInfo($"[MANUAL UNBLOCK] Player {blockEntry.PlayerName} unblocked manually - granted immunity from auto-kick, auto-block no anticheat, and mod detections");
                }
                else if (detectionType.HasValue)
                {
                    // Auto-detection unblocks give immunity to the specific detection type and mod detections
                    if (!_recentlyUnblockedDetections.ContainsKey(actorNumber))
                    {
                        _recentlyUnblockedDetections[actorNumber] = new HashSet<DetectionType>();
                    }
                    _recentlyUnblockedDetections[actorNumber].Add(detectionType.Value);
                    
                    // Also grant immunity from mod detections to prevent immediate re-blocking
                    GrantImmunity(actorNumber);
                    
                    AntiCheatPlugin.Logger.LogInfo($"[AUTO UNBLOCK] Player {blockEntry.PlayerName} unblocked for {detectionType.Value} - giving immunity to this detection type and mod detections");
                }
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

        public static bool HasImmunity(int actorNumber)
        {
            return _playersWithImmunity.Contains(actorNumber);
        }

        public static void GrantImmunity(int actorNumber)
        {
            _playersWithImmunity.Add(actorNumber);
            AntiCheatPlugin.Logger.LogInfo($"[IMMUNITY] Granted immunity to player Actor #{actorNumber}");
        }

        public static void RemoveImmunity(int actorNumber)
        {
            _playersWithImmunity.Remove(actorNumber);
            AntiCheatPlugin.Logger.LogInfo($"[IMMUNITY] Removed immunity from player Actor #{actorNumber}");
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

        // Track item held by player when they're blocked
        private static void TrackPlayerHeldItem(int actorNumber)
        {
            // Find the player's character and check if they're holding an item
            var allCharacters = UnityEngine.Object.FindObjectsOfType<Character>();
            foreach (var character in allCharacters)
            {
                var photonView = character.GetComponent<PhotonView>();
                if (photonView != null && photonView.Owner != null && 
                    photonView.Owner.ActorNumber == actorNumber)
                {
                    var characterData = character.GetComponent<CharacterData>();
                    if (characterData != null && characterData.currentItem != null)
                    {
                        // Track the item they're holding
                        AntiCheatPlugin.TrackBlockedPlayerItem(actorNumber, characterData.currentItem.gameObject);
                        break;
                    }
                }
            }
        }
    }
} 