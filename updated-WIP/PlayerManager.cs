using System;
using System.Collections.Generic;
using Photon.Realtime;
using Steamworks;

namespace AntiCheatMod
{
    public enum PlayerStatus
    {
        Normal,
        Detected,
        Blocked,
        Whitelisted,
        MasterClient
    }

    public class PlayerInfo
    {
        public int ActorNumber { get; set; }
        public string PhotonName { get; set; }
        public CSteamID SteamID { get; set; }
        public string SteamName { get; set; }
        public PlayerStatus Status { get; set; }
        public DateTime JoinTime { get; set; }
        public DateTime? LastDetectionTime { get; set; }
        public List<string> DetectionReasons { get; set; } = new List<string>();
        public bool IsLocal { get; set; }
        public bool IsMasterClient { get; set; }
        public string AnticheatVersion { get; set; }
        
        public PlayerInfo(Photon.Realtime.Player player)
        {
            ActorNumber = player.ActorNumber;
            PhotonName = player.NickName;
            IsLocal = player.IsLocal;
            IsMasterClient = player.IsMasterClient;
            JoinTime = DateTime.Now;
            Status = player.IsMasterClient ? PlayerStatus.MasterClient : PlayerStatus.Normal;
        }
    }

    public static class PlayerManager
    {
        private static readonly Dictionary<int, PlayerInfo> _players = new Dictionary<int, PlayerInfo>();
        private static readonly Dictionary<int, bool> _manualBlockOverrides = new Dictionary<int, bool>();
        
        // Events for UI updates
        public static event Action<PlayerInfo> OnPlayerAdded;
        public static event Action<PlayerInfo> OnPlayerRemoved;
        public static event Action<PlayerInfo> OnPlayerStatusChanged;
        public static event Action<PlayerInfo> OnPlayerUpdated;

        public static void AddPlayer(Photon.Realtime.Player player)
        {
            var playerInfo = new PlayerInfo(player);
            _players[player.ActorNumber] = playerInfo;
            OnPlayerAdded?.Invoke(playerInfo);
        }

        public static void RemovePlayer(int actorNumber)
        {
            if (_players.TryGetValue(actorNumber, out var playerInfo))
            {
                _players.Remove(actorNumber);
                _manualBlockOverrides.Remove(actorNumber);
                OnPlayerRemoved?.Invoke(playerInfo);
            }
        }

        public static PlayerInfo GetPlayer(int actorNumber)
        {
            return _players.TryGetValue(actorNumber, out var playerInfo) ? playerInfo : null;
        }

        public static List<PlayerInfo> GetAllPlayers()
        {
            return new List<PlayerInfo>(_players.Values);
        }

        public static void UpdatePlayerStatus(int actorNumber, PlayerStatus status)
        {
            if (_players.TryGetValue(actorNumber, out var playerInfo))
            {
                playerInfo.Status = status;
                OnPlayerStatusChanged?.Invoke(playerInfo);
            }
        }

        public static void AddDetectionReason(int actorNumber, string reason)
        {
            if (_players.TryGetValue(actorNumber, out var playerInfo))
            {
                playerInfo.DetectionReasons.Add(reason);
                playerInfo.LastDetectionTime = DateTime.Now;
                OnPlayerUpdated?.Invoke(playerInfo);
            }
        }

        public static void SetManualBlockOverride(int actorNumber, bool blocked)
        {
            _manualBlockOverrides[actorNumber] = blocked;
            
            if (_players.TryGetValue(actorNumber, out var playerInfo))
            {
                playerInfo.Status = blocked ? PlayerStatus.Blocked : PlayerStatus.Detected;
                OnPlayerStatusChanged?.Invoke(playerInfo);
            }
        }

        public static bool IsManuallyBlocked(int actorNumber)
        {
            return _manualBlockOverrides.TryGetValue(actorNumber, out var blocked) && blocked;
        }

        public static bool IsManuallyUnblocked(int actorNumber)
        {
            return _manualBlockOverrides.TryGetValue(actorNumber, out var blocked) && !blocked;
        }

        public static void ClearManualOverrides()
        {
            _manualBlockOverrides.Clear();
        }

        public static void UpdateSteamInfo(int actorNumber, CSteamID steamID, string steamName)
        {
            if (_players.TryGetValue(actorNumber, out var playerInfo))
            {
                playerInfo.SteamID = steamID;
                playerInfo.SteamName = steamName;
                OnPlayerUpdated?.Invoke(playerInfo);
            }
        }

        public static void UpdateAnticheatVersion(int actorNumber, string version)
        {
            if (_players.TryGetValue(actorNumber, out var player))
            {
                player.AnticheatVersion = version;
                OnPlayerUpdated?.Invoke(player);
            }
        }

        public static void HandleMasterClientSwitch(Photon.Realtime.Player newMasterClient)
        {
            // Update all players to remove master client status
            foreach (var player in _players.Values)
            {
                if (player.Status == PlayerStatus.MasterClient)
                {
                    player.Status = PlayerStatus.Normal;
                    player.IsMasterClient = false;
                    OnPlayerStatusChanged?.Invoke(player);
                }
            }

            // Update the new master client
            if (_players.TryGetValue(newMasterClient.ActorNumber, out var newMaster))
            {
                newMaster.Status = PlayerStatus.MasterClient;
                newMaster.IsMasterClient = true;
                OnPlayerStatusChanged?.Invoke(newMaster);
            }
        }
    }
} 