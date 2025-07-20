using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using static AntiCheatEvents;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using UnityEngine.SceneManagement;

namespace AntiCheatMod
{
    // Extension method for ConfigFile compatibility
    public static class ConfigFileExtensions
    {
        public static bool ContainsKey(this ConfigFile config, ConfigDefinition key)
        {
            try
            {
                // Try to get all entries and check if the key exists
                var entries = config.GetType().GetProperty("Entries", BindingFlags.NonPublic | BindingFlags.Instance);
                if (entries != null)
                {
                    var entriesDict = entries.GetValue(config) as System.Collections.IDictionary;
                    if (entriesDict != null)
                    {
                        return entriesDict.Contains(key);
                    }
                }

                // Fallback: try to bind and see if it has a non-default value
                var testBind = config.Bind(key.Section, key.Key, "TEST_DEFAULT");
                bool exists = testBind.Value != "TEST_DEFAULT";
                config.Remove(key); // Clean up test bind
                return exists;
            }
            catch
            {
                return false;
            }
        }
    }

    [BepInPlugin("com.hiccup444.anticheat", "PEAK Anticheat", "1.3.2")]
    public class AntiCheatPlugin : BaseUnityPlugin, IConnectionCallbacks, IMatchmakingCallbacks, IInRoomCallbacks
    {
        public static new ManualLogSource Logger;
        private static AntiCheatPlugin Instance;
        private static new ConfigFile Config;
        private static readonly Dictionary<int, (string itemName, DateTime timestamp)> _playerLastHeldItems = new Dictionary<int, (string, DateTime)>();

        // Changed from _softLockedPlayers to _blockedPlayers for clarity
        private static readonly HashSet<int> _blockedPlayers = new HashSet<int>();

        // Track players who have been detected (regardless of whether they're blocked)
        private static readonly HashSet<int> _detectedPlayers = new HashSet<int>();

        public static bool IsBlocked(int actorNumber)
        {
            return _blockedPlayers.Contains(actorNumber);
        }

        // Config entries
        private static ConfigEntry<bool> ShowVisualLogs;
        private static ConfigEntry<bool> CheckSteamNames;
        public static ConfigEntry<bool> AutoBlockCheaters; // Renamed from AutoPunishCheaters
        public static ConfigEntry<bool> VerboseRPCLogging;

        // Connection log for visual messages
        private static PlayerConnectionLog _connectionLog;
        private static readonly Queue<(string message, bool onlySendOnce, bool sfxJoin, bool sfxLeave)> _queuedLogs = new Queue<(string, bool, bool, bool)>(8);

        // Track player identities
        private static readonly List<PlayerIdentity> _knownPlayerIdentities = new List<PlayerIdentity>();

        // Reflection methods/fields
        private static MethodInfo _getColorTagMethod;
        private static MethodInfo _addMessageMethod;
        private static FieldInfo _currentLogField;

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;

            // Initialize config
            Config = new ConfigFile(Path.Combine(Paths.ConfigPath, "com.hiccup444.PEAKanticheat.cfg"), true);
            ShowVisualLogs = Config.Bind("General", "ShowVisualLogs", true, "Show anti-cheat messages in the connection log");
            CheckSteamNames = Config.Bind("General", "CheckSteamNames", true, "Check if Photon names match Steam names");

            // Handle config migration from old AutoPunishCheaters to new AutoBlockCheaters
            bool shouldAutoBlock = true; // Default value

            // Check if the old config exists
            var oldConfigKey = new ConfigDefinition("General", "AutoPunishCheaters");
            if (Config.ContainsKey(oldConfigKey))
            {
                // Read the old value
                var oldConfig = Config.Bind("General", "AutoPunishCheaters", true, "DEPRECATED - Use AutoBlockCheaters instead");
                shouldAutoBlock = oldConfig.Value;

                // Remove the old config entry
                Config.Remove(oldConfigKey);

                Logger.LogInfo($"[CONFIG MIGRATION] Migrated AutoPunishCheaters ({shouldAutoBlock}) to AutoBlockCheaters");
            }

            // Create the new config with the migrated value
            AutoBlockCheaters = Config.Bind("General", "AutoBlockCheaters", shouldAutoBlock, "Automatically block all RPCs from detected cheaters (replaces AutoPunishCheaters)");

            VerboseRPCLogging = Config.Bind("Debug", "VerboseRPCLogging", false, "Log all RPC calls for debugging");

            // Save the config to write any migrations
            Config.Save();

            Logger.LogInfo("[PEAKAntiCheat] Protection active! (RPC Blocking Mode)");

            // Apply Harmony patches
            var harmony = new Harmony("com.hiccup444.PEAKanticheat");
            harmony.PatchAll();

            // Subscribe to Photon callbacks
            PhotonNetwork.AddCallbackTarget(this);

            // Start checking for cheat mods
            StartCoroutine(CheckPlayersForCheats());

            // Start tracking player items
            StartCoroutine(TrackPlayerItems());

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Update()
        {
            // Process queued visual logs
            while (_queuedLogs.Count != 0)
            {
                var log = _queuedLogs.Peek();
                if (!LogVisually(log.message, log.onlySendOnce, log.sfxJoin, log.sfxLeave))
                {
                    break;
                }
                _queuedLogs.Dequeue();
            }
        }

        private void OnDestroy()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        public static void UpdatePlayerHeldItem(int actorNumber, string itemName)
        {
            if (!string.IsNullOrEmpty(itemName))
            {
                _playerLastHeldItems[actorNumber] = (itemName.ToLower(), DateTime.Now);
                if (VerboseRPCLogging.Value)
                {
                    Logger.LogInfo($"Updated held item for Actor #{actorNumber}: {itemName}");
                }
            }
        }

        public static bool PlayerHadItem(int actorNumber, string itemNamePart, float withinSeconds = 2f)
        {
            Logger.LogInfo($"[PlayerHadItem] Checking actor {actorNumber} for item containing '{itemNamePart}' within {withinSeconds} seconds");

            if (_playerLastHeldItems.TryGetValue(actorNumber, out var itemData))
            {
                var timeSince = (DateTime.Now - itemData.timestamp).TotalSeconds;
                Logger.LogInfo($"[PlayerHadItem] Found item: {itemData.itemName}, held {timeSince:F2} seconds ago");

                // Check if it was held within the time window
                if (timeSince <= withinSeconds)
                {
                    bool contains = itemData.itemName.Contains(itemNamePart.ToLower());
                    Logger.LogInfo($"[PlayerHadItem] Within time window. Contains '{itemNamePart}'? {contains}");
                    return contains;
                }
                else
                {
                    Logger.LogInfo($"[PlayerHadItem] Outside time window ({timeSince:F2} > {withinSeconds})");
                }
            }
            else
            {
                Logger.LogInfo($"[PlayerHadItem] No item history found for actor {actorNumber}");
            }
            return false;
        }

        public static bool LogVisually(string message, bool onlySendOnce = false, bool sfxJoin = false, bool sfxLeave = false)
        {
            if (!ShowVisualLogs.Value)
                return true;

            if (!_connectionLog)
            {
                _connectionLog = FindObjectOfType<PlayerConnectionLog>();
                if (_connectionLog)
                {
                    // Cache reflection methods and fields
                    var logType = _connectionLog.GetType();
                    _getColorTagMethod = logType.GetMethod("GetColorTag", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    _addMessageMethod = logType.GetMethod("AddMessage", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    _currentLogField = logType.GetField("currentLog", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                    if (_getColorTagMethod == null)
                        Logger.LogWarning("GetColorTag method not found!");
                    if (_addMessageMethod == null)
                        Logger.LogWarning("AddMessage method not found!");
                    if (_currentLogField == null)
                        Logger.LogWarning("currentLog field not found!");
                }
            }

            if (!_connectionLog || _getColorTagMethod == null || _addMessageMethod == null)
            {
                _queuedLogs.Enqueue((message, onlySendOnce, sfxJoin, sfxLeave));
                return false;
            }

            try
            {
                StringBuilder sb = new StringBuilder(message);

                // Get color fields
                var joinedColorField = _connectionLog.GetType().GetField("joinedColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var leftColorField = _connectionLog.GetType().GetField("leftColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var userColorField = _connectionLog.GetType().GetField("userColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                if (joinedColorField != null && leftColorField != null && userColorField != null)
                {
                    var joinedColor = joinedColorField.GetValue(_connectionLog);
                    var leftColor = leftColorField.GetValue(_connectionLog);
                    var userColor = userColorField.GetValue(_connectionLog);

                    // Use reflection to call GetColorTag
                    string joinedColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { joinedColor });
                    string leftColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { leftColor });
                    string userColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { userColor });

                    sb.Replace("{joinedColor}", joinedColorTag ?? "");
                    sb.Replace("{leftColor}", leftColorTag ?? "");
                    sb.Replace("{userColor}", userColorTag ?? "");
                }

                message = sb.ToString();

                if (onlySendOnce && _currentLogField != null)
                {
                    string currentLog = _currentLogField.GetValue(_connectionLog) as string;
                    if (!string.IsNullOrEmpty(currentLog) && currentLog.Contains(message))
                    {
                        return true;
                    }
                }

                // Use reflection to call AddMessage
                _addMessageMethod.Invoke(_connectionLog, new object[] { message });

                // Handle sound effects
                var sfxJoinField = _connectionLog.GetType().GetField("sfxJoin", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var sfxLeaveField = _connectionLog.GetType().GetField("sfxLeave", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                if (sfxJoin && sfxJoinField != null)
                {
                    var sfxJoinObj = sfxJoinField.GetValue(_connectionLog);
                    if (sfxJoinObj != null)
                    {
                        var playMethod = sfxJoinObj.GetType().GetMethod("Play", new Type[] { typeof(Vector3) });
                        playMethod?.Invoke(sfxJoinObj, new object[] { Vector3.zero });
                    }
                }

                if (sfxLeave && sfxLeaveField != null)
                {
                    var sfxLeaveObj = sfxLeaveField.GetValue(_connectionLog);
                    if (sfxLeaveObj != null)
                    {
                        var playMethod = sfxLeaveObj.GetType().GetMethod("Play", new Type[] { typeof(Vector3) });
                        playMethod?.Invoke(sfxLeaveObj, new object[] { Vector3.zero });
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in LogVisually: {ex.Message}");
                return false;
            }
        }

        // Renamed from SoftLockPlayer to BlockPlayer
        public static void BlockPlayer(Photon.Realtime.Player cheater, string reason)
        {
            // Never block ourselves
            if (cheater.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                Logger.LogInfo($"Not blocking local player");
                return;
            }

            // Check if already detected
            if (_detectedPlayers.Contains(cheater.ActorNumber))
            {
                return; // Already processed this cheater
            }

            Logger.LogWarning($"CHEATER DETECTED: {cheater.NickName} - Reason: {reason}");

            // Add to detected list to prevent re-detection spam
            _detectedPlayers.Add(cheater.ActorNumber);

            // Compute best‐effort SteamID even on non-host clients
            CSteamID cheaterSteamID = CSteamID.Nil;
            var identity = _knownPlayerIdentities.Find(p => p.ActorNumber == cheater.ActorNumber);
            if (identity != null)
            {
                cheaterSteamID = identity.SteamID;
                Logger.LogWarning($"[Detection] Found identity for Actor #{cheater.ActorNumber}: PhotonName={identity.PhotonName}, SteamID={cheaterSteamID}");
                if (cheaterSteamID == CSteamID.Nil)
                {
                    Logger.LogWarning($"[Detection] WARNING: Stored SteamID is NIL for {identity.PhotonName}.");
                }
            }
            else
            {
                Logger.LogWarning($"[Detection] No identity found for Actor #{cheater.ActorNumber}, attempting fallback SteamID lookup...");
                cheaterSteamID = GetPlayerSteamID(cheater);
                if (cheaterSteamID == CSteamID.Nil)
                {
                    Logger.LogWarning($"[Detection] Fallback GetPlayerSteamID() returned NIL for {cheater.NickName}.");
                }
            }

            // Notify everyone, regardless of host status
            AntiCheatEvents.NotifyCheaterDetected(cheater, reason, cheaterSteamID);

            if (!AutoBlockCheaters.Value)
            {
                Logger.LogInfo($"Auto-blocking disabled - only logging cheater: {cheater.NickName}");
                // Show visual warning but indicate no action taken
                LogVisually($"{{userColor}}{cheater.NickName}</color> {{leftColor}}detected - {reason} (no action taken)</color>", false, false, true);
                return;
            }

            // Check if already blocked
            if (_blockedPlayers.Contains(cheater.ActorNumber))
            {
                return;
            }

            // Add to blocked list for ALL players (not just master client)
            _blockedPlayers.Add(cheater.ActorNumber);

            // Apply visual warning - changed message to reflect RPC blocking
            LogVisually($"{{userColor}}{cheater.NickName}</color> {{leftColor}}RPC blocked - {reason}</color>", false, false, true);

            Logger.LogInfo($"All RPCs from {cheater.NickName} (Actor #{cheater.ActorNumber}) are now blocked");
        }

        // Compatibility wrapper - redirects old calls to new method
        public static void SoftLockPlayer(Photon.Realtime.Player cheater, string reason)
        {
            BlockPlayer(cheater, reason);
        }

        // Compatibility wrapper
        public static bool IsSoftLocked(int actorNumber)
        {
            return IsBlocked(actorNumber);
        }

        public static void AddToBlockList(int actorNumber)
        {
            if (!_blockedPlayers.Contains(actorNumber))
            {
                _blockedPlayers.Add(actorNumber);
                Logger.LogInfo($"Added actor #{actorNumber} to RPC block list");
            }
        }

        // Compatibility wrapper
        public static void AddToSoftLockList(int actorNumber)
        {
            AddToBlockList(actorNumber);
        }

        private static CSteamID GetPlayerSteamID(Photon.Realtime.Player player)
        {
            try
            {
                var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();
                if (lobbyHandler == null)
                {
                    Logger.LogWarning("[GetPlayerSteamID] SteamLobbyHandler not found.");
                    return CSteamID.Nil;
                }

                var lobbyHandlerType = lobbyHandler.GetType();
                var currentLobbyField = lobbyHandlerType.GetField("m_currentLobby", BindingFlags.NonPublic | BindingFlags.Instance);

                if (currentLobbyField == null)
                {
                    Logger.LogError("[GetPlayerSteamID] Could not find m_currentLobby field via reflection.");
                    return CSteamID.Nil;
                }

                CSteamID currentLobby = (CSteamID)currentLobbyField.GetValue(lobbyHandler);
                if (currentLobby == CSteamID.Nil)
                {
                    Logger.LogWarning("[GetPlayerSteamID] Current Steam lobby is NIL.");
                    return CSteamID.Nil;
                }

                int numLobbyMembers = SteamMatchmaking.GetNumLobbyMembers(currentLobby);
                Logger.LogInfo($"[GetPlayerSteamID] Scanning {numLobbyMembers} Steam lobby members for match with Photon name '{player.NickName}'.");

                // Build Steam name → List<CSteamID>
                var steamNameToId = new Dictionary<string, List<CSteamID>>();

                for (int i = 0; i < numLobbyMembers; i++)
                {
                    CSteamID lobbyMember = SteamMatchmaking.GetLobbyMemberByIndex(currentLobby, i);
                    string steamName = SteamFriends.GetFriendPersonaName(lobbyMember);

                    Logger.LogInfo($"[GetPlayerSteamID] Found Steam user: Name='{steamName}', SteamID={lobbyMember}");

                    if (!steamNameToId.ContainsKey(steamName))
                        steamNameToId[steamName] = new List<CSteamID>();

                    steamNameToId[steamName].Add(lobbyMember);
                }

                if (!steamNameToId.ContainsKey(player.NickName))
                {
                    Logger.LogWarning($"[GetPlayerSteamID] No Steam player found with the name '{player.NickName}'. Returning NIL.");
                    return CSteamID.Nil;
                }

                var possibleSteamIDs = steamNameToId[player.NickName];
                Logger.LogInfo($"[GetPlayerSteamID] Found {possibleSteamIDs.Count} Steam players with name '{player.NickName}': {string.Join(", ", possibleSteamIDs)}");

                // Now find all Photon players with the same nickname
                List<Photon.Realtime.Player> photonPlayersWithSameName = new List<Photon.Realtime.Player>();
                foreach (var otherPlayer in PhotonNetwork.PlayerList)
                {
                    if (otherPlayer.NickName == player.NickName)
                        photonPlayersWithSameName.Add(otherPlayer);
                }

                Logger.LogInfo($"[GetPlayerSteamID] Found {photonPlayersWithSameName.Count} Photon players with name '{player.NickName}'.");

                if (photonPlayersWithSameName.Count > possibleSteamIDs.Count)
                {
                    Logger.LogWarning($"[GetPlayerSteamID] More Photon players ({photonPlayersWithSameName.Count}) than Steam players ({possibleSteamIDs.Count}) for name '{player.NickName}'. Returning NIL as it's likely spoofed.");
                    return CSteamID.Nil;
                }

                // Sort Photon players by ActorNumber for deterministic order
                photonPlayersWithSameName.Sort((a, b) => a.ActorNumber.CompareTo(b.ActorNumber));

                int index = photonPlayersWithSameName.FindIndex(p => p.ActorNumber == player.ActorNumber);
                if (index >= 0 && index < possibleSteamIDs.Count)
                {
                    Logger.LogInfo($"[GetPlayerSteamID] Matched ActorNumber {player.ActorNumber} to SteamID {possibleSteamIDs[index]} (index {index}).");
                    return possibleSteamIDs[index];
                }

                Logger.LogWarning($"[GetPlayerSteamID] Could not find matching ActorNumber for {player.NickName}. Returning first available SteamID: {possibleSteamIDs[0]}");
                return possibleSteamIDs[0]; // fallback: first Steam ID
            }
            catch (Exception ex)
            {
                Logger.LogError($"[GetPlayerSteamID] Exception while resolving Steam ID for {player.NickName}: {ex.Message}");
                return CSteamID.Nil;
            }
        }

        private IEnumerator CheckPlayersForCheats()
        {
            while (true)
            {
                yield return new WaitForSeconds(5f);

                // Allow all players to detect cheats
                if (PhotonNetwork.InRoom)
                {
                    foreach (var player in PhotonNetwork.PlayerList)
                    {
                        CheckPlayerForCheatMods(player);
                    }
                }
            }
        }

        private static List<CSteamID> GetSteamIDsForName(string steamName)
        {
            try
            {
                var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();
                if (lobbyHandler == null)
                    return new List<CSteamID>();

                var lobbyHandlerType = lobbyHandler.GetType();
                var currentLobbyField = lobbyHandlerType.GetField("m_currentLobby", BindingFlags.NonPublic | BindingFlags.Instance);

                if (currentLobbyField == null)
                    return new List<CSteamID>();

                CSteamID currentLobby = (CSteamID)currentLobbyField.GetValue(lobbyHandler);
                if (currentLobby == CSteamID.Nil)
                    return new List<CSteamID>();

                int numLobbyMembers = SteamMatchmaking.GetNumLobbyMembers(currentLobby);
                List<CSteamID> matchingIds = new List<CSteamID>();

                for (int i = 0; i < numLobbyMembers; i++)
                {
                    CSteamID lobbyMember = SteamMatchmaking.GetLobbyMemberByIndex(currentLobby, i);
                    string name = SteamFriends.GetFriendPersonaName(lobbyMember);

                    if (name == steamName)
                        matchingIds.Add(lobbyMember);
                }

                return matchingIds;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting SteamIDs for name {steamName}: {ex.Message}");
                return new List<CSteamID>();
            }
        }

        private static readonly Dictionary<int, DateTime> _recentlySpawnedPlayers = new Dictionary<int, DateTime>();
        private const double SPAWN_GRACE_PERIOD_SECONDS = 5.0;

        // Add this public method to track spawns
        public static void OnPlayerSpawned(int actorNumber)
        {
            _recentlySpawnedPlayers[actorNumber] = DateTime.Now;
            Logger.LogInfo($"Tracking spawn for actor #{actorNumber}");
        }

        public static bool IsInSpawnGracePeriod(int actorNumber)
        {
            if (_recentlySpawnedPlayers.ContainsKey(actorNumber))
            {
                TimeSpan timeSinceSpawn = DateTime.Now - _recentlySpawnedPlayers[actorNumber];
                if (timeSinceSpawn.TotalSeconds <= SPAWN_GRACE_PERIOD_SECONDS)
                {
                    return true;
                }
                else
                {
                    // Clean up old entry
                    _recentlySpawnedPlayers.Remove(actorNumber);
                }
            }
            return false;
        }

        private void CheckPlayerForCheatMods(Photon.Realtime.Player player)
        {
            if (player.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
                return;

            // Skip if already detected (prevents spam)
            if (_detectedPlayers.Contains(player.ActorNumber))
                return;

            // Record the player's Steam ID when they first join
            if (!_knownPlayerIdentities.Exists(p => p.ActorNumber == player.ActorNumber))
            {
                var steamId = GetPlayerSteamID(player);
                Logger.LogInfo($"[Identity Log] Joined: PhotonName={player.NickName} | ActorNumber={player.ActorNumber} | SteamName={SteamFriends.GetFriendPersonaName(steamId)} | SteamID={steamId}");

                _knownPlayerIdentities.Add(new PlayerIdentity(player.NickName, player.ActorNumber, steamId));
            }

            // Duplicate Photon Nickname Check (with Steam name validation)
            List<Photon.Realtime.Player> photonPlayersWithSameName = new List<Photon.Realtime.Player>();
            foreach (var otherPlayer in PhotonNetwork.PlayerList)
            {
                if (otherPlayer.ActorNumber != player.ActorNumber && otherPlayer.NickName == player.NickName)
                {
                    photonPlayersWithSameName.Add(otherPlayer);
                }
            }

            if (photonPlayersWithSameName.Count > 0)
            {
                // Check if one of the duplicate names is the local player
                bool isLocalPlayerName = player.NickName.ToLower() == PhotonNetwork.LocalPlayer.NickName.ToLower();

                if (isLocalPlayerName)
                {
                    Logger.LogWarning($"{player.NickName} joined with the same name as the local player! This can cause character assignment issues.");
                    LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}joined with your name - possible impersonator!</color>", true, false, true);

                    // Block their RPCs immediately
                    BlockPlayer(player, "Name impersonation - using local player's name");
                    return;
                }
                else
                {
                    Logger.LogInfo($"Duplicate Photon name '{player.NickName}' found, but enough matching Steam names exist. Allowing.");
                }
            }

            // Cheat mod property checks
            if (player.CustomProperties.ContainsKey("CherryUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Cherry cheat mod!</color>", true, false, true);
                BlockPlayer(player, "Cherry cheat mod user");
                return;
            }

            if (player.CustomProperties.ContainsKey("CherryOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Cherry cheat mod!</color>", true, false, true);
                BlockPlayer(player, "Cherry cheat mod owner");
                return;
            }

            if (player.CustomProperties.ContainsKey("AtlUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Atlas cheat mod!</color>", true, false, true);
                BlockPlayer(player, "Atlas cheat mod user");
                return;
            }

            if (player.CustomProperties.ContainsKey("AtlOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Atlas cheat mod!</color>", true, false, true);
                BlockPlayer(player, "Atlas cheat mod owner");
                return;
            }

            // Steam name match fallback (if enabled)
            if (CheckSteamNames.Value)
            {
                CheckSteamNameMatch(player);
            }
        }

        private void CheckSteamNameMatch(Photon.Realtime.Player player)
        {
            var identity = _knownPlayerIdentities.Find(p => p.ActorNumber == player.ActorNumber);
            if (identity != null && identity.SteamID != CSteamID.Nil)
            {
                // Get the cached Steam name first
                string cachedSteamName = SteamFriends.GetFriendPersonaName(identity.SteamID);

                if (cachedSteamName.ToLower() == player.NickName.ToLower())
                {
                    // Names match (case-insensitive), all good
                    return;
                }

                // Names don't match - could be stale cache
                Logger.LogWarning($"Potential name mismatch for {player.NickName} - cached Steam name shows '{cachedSteamName}'");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}has potential name mismatch - verifying...</color>", true, false, false);

                // Request fresh persona data (name only, no avatar)
                bool dataRequested = SteamFriends.RequestUserInformation(identity.SteamID, true);

                if (!dataRequested)
                {
                    // Data is already fresh (RequestUserInformation returns false if data is already available)
                    // Double-check the name
                    string freshSteamName = SteamFriends.GetFriendPersonaName(identity.SteamID);

                    if (freshSteamName.ToLower() != player.NickName.ToLower())
                    {
                        // Still doesn't match after fresh data - they're cheating
                        Logger.LogWarning($"Confirmed name mismatch: Photon='{player.NickName}' vs Steam='{freshSteamName}'");
                        LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}confirmed name mismatch - Steam shows '{freshSteamName}'!</color>", true, false, true);
                        BlockPlayer(player, "Name mismatch - spoofing detected");
                    }
                    else
                    {
                        // Names now match - was just stale cache
                        Logger.LogInfo($"Name mismatch resolved for {player.NickName} - was stale cache");
                        LogVisually($"{{userColor}}{player.NickName}</color> {{joinedColor}}name verified successfully</color>", true, false, false);
                    }
                }
                else
                {
                    // Data was requested, we need to wait for PersonaStateChange callback
                    Logger.LogInfo($"Requested fresh Steam data for {player.NickName} (Actor #{player.ActorNumber})");
                    StartCoroutine(WaitForPersonaStateChange(player, identity.SteamID, 5f));
                }

                return;
            }

            // Fallback: Check all lobby members if we don't have the identity stored
            var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();
            if (lobbyHandler == null || !lobbyHandler.InSteamLobby(out CSteamID currentLobby))
                return;

            // Check all lobby members
            int numMembers = SteamMatchmaking.GetNumLobbyMembers(currentLobby);
            bool foundMatch = false;

            for (int i = 0; i < numMembers; i++)
            {
                CSteamID memberSteamId = SteamMatchmaking.GetLobbyMemberByIndex(currentLobby, i);
                string steamName = SteamFriends.GetFriendPersonaName(memberSteamId);

                // Check if this Steam name matches the Photon name
                if (steamName == player.NickName)
                {
                    foundMatch = true;
                    break;
                }
            }

            // If no Steam name matches this Photon name, they're likely spoofing
            if (!foundMatch)
            {
                Logger.LogWarning($"{player.NickName} has no matching Steam name in lobby!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}has no matching Steam name in lobby!</color>", true, false, true);
                BlockPlayer(player, "Name mismatch - possible spoofer");
            }
        }

        private IEnumerator WaitForPersonaStateChange(Photon.Realtime.Player player, CSteamID steamId, float timeout)
        {
            float startTime = Time.time;
            string lastKnownSteamName = SteamFriends.GetFriendPersonaName(steamId);
            bool receivedUpdate = false;

            // Subscribe to Steam callbacks temporarily
            Callback<PersonaStateChange_t> personaCallback = null;
            personaCallback = Callback<PersonaStateChange_t>.Create((PersonaStateChange_t param) =>
            {
                if (param.m_ulSteamID == steamId.m_SteamID)
                {
                    // Check if name was part of the change
                    if ((param.m_nChangeFlags & EPersonaChange.k_EPersonaChangeName) != 0)
                    {
                        receivedUpdate = true;
                        Logger.LogInfo($"Received PersonaStateChange for {steamId} - name was updated");
                    }
                }
            });

            // Wait for update or timeout
            while (!receivedUpdate && (Time.time - startTime) < timeout)
            {
                yield return new WaitForSeconds(0.1f);

                // Also check if player left the room
                if (PhotonNetwork.CurrentRoom == null || PhotonNetwork.CurrentRoom.GetPlayer(player.ActorNumber) == null)
                {
                    Logger.LogInfo($"Player {player.NickName} left before name verification completed");
                    personaCallback?.Dispose();
                    yield break;
                }
            }

            // Clean up callback
            personaCallback?.Dispose();

            // Now check the name again with fresh data
            string freshSteamName = SteamFriends.GetFriendPersonaName(steamId);

            if (freshSteamName.ToLower() != player.NickName.ToLower())
            {
                // Still doesn't match after receiving fresh data - they're cheating
                Logger.LogWarning($"Name mismatch confirmed after PersonaStateChange: Photon='{player.NickName}' vs Steam='{freshSteamName}'");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}confirmed spoofing - real Steam name is '{freshSteamName}'!</color>", true, false, true);

                // Make sure player is still in room before blocking
                if (PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.GetPlayer(player.ActorNumber) != null)
                {
                    BlockPlayer(player, $"Name spoofing confirmed - real name: {freshSteamName}");
                }
            }
            else
            {
                // Names now match - was just stale cache
                Logger.LogInfo($"Name verification passed for {player.NickName} after fresh data");
                LogVisually($"{{userColor}}{player.NickName}</color> {{joinedColor}}name verified successfully</color>", true, false, false);
            }
        }

        public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
        {
            Logger.LogInfo($"{newMasterClient.NickName} (#{newMasterClient.ActorNumber}) is the new master client!");

            // Only protect master client if someone else took it and we're not master client anymore
            if (newMasterClient.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber && !PhotonNetwork.LocalPlayer.IsMasterClient)
            {
                var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();

                if (lobbyHandler != null && lobbyHandler.InSteamLobby(out CSteamID currentLobby))
                {
                    // Check if we're the Steam lobby owner
                    if (SteamMatchmaking.GetLobbyOwner(currentLobby) == SteamUser.GetSteamID())
                    {
                        Logger.LogWarning($"Master client stolen by: {newMasterClient.NickName}");
                        LogVisually($"{{userColor}}{newMasterClient.NickName}</color> {{leftColor}}tried to take master client</color>", false, false, true);

                        // Block the cheater's RPCs
                        BlockPlayer(newMasterClient, "Stole master client");

                        // Take master client back
                        if (!PhotonNetwork.LocalPlayer.IsMasterClient)
                        {
                            PhotonNetwork.SetMasterClient(PhotonNetwork.LocalPlayer);
                            Logger.LogInfo("Reclaimed master client after blocking cheater");
                        }
                    }
                }
            }
        }

        // Required interface implementations
        public void OnConnected() { }
        public void OnConnectedToMaster() { }
        public void OnDisconnected(DisconnectCause cause) { }
        public void OnRegionListReceived(RegionHandler regionHandler) { }
        public void OnCustomAuthenticationResponse(Dictionary<string, object> data) { }
        public void OnCustomAuthenticationFailed(string debugMessage) { }
        public void OnFriendListUpdate(List<FriendInfo> friendList) { }
        public void OnCreatedRoom() { }
        public void OnCreateRoomFailed(short returnCode, string message) { }
        public void OnJoinRoomFailed(short returnCode, string message) { }
        public void OnJoinRandomFailed(short returnCode, string message) { }
        public void OnLeftRoom()
        {
            // Clear all tracking when leaving room
            _knownPlayerIdentities.Clear();
            _blockedPlayers.Clear();
            _detectedPlayers.Clear();
            _playerLastHeldItems.Clear();
        }

        // IInRoomCallbacks implementations
        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
        {
            // Track spawn for all clients
            OnPlayerSpawned(newPlayer.ActorNumber);

            // Allow all players to check for cheats
            StartCoroutine(CheckNewPlayerDelayed(newPlayer));
        }

        private IEnumerator CheckNewPlayerDelayed(Photon.Realtime.Player player)
        {
            // Wait a moment for Steam data to sync
            yield return new WaitForSeconds(2f);
            CheckPlayerForCheatMods(player);
        }

        private IEnumerator TrackPlayerItems()
        {
            while (true)
            {
                yield return new WaitForSeconds(2f);

                if (!PhotonNetwork.InRoom)
                    continue;

                // Track all players' current items
                var allCharacters = FindObjectsOfType<Character>();
                foreach (var character in allCharacters)
                {
                    var photonView = character.GetComponent<PhotonView>();
                    if (photonView == null || photonView.Owner == null)
                        continue;

                    // Skip blocked players
                    if (IsBlocked(photonView.Owner.ActorNumber))
                        continue;

                    var characterData = character.GetComponent<CharacterData>();
                    if (characterData != null && characterData.currentItem != null)
                    {
                        UpdatePlayerHeldItem(photonView.Owner.ActorNumber, characterData.currentItem.name);
                    }
                }
            }
        }

        public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
            _knownPlayerIdentities.RemoveAll(p => p.ActorNumber == otherPlayer.ActorNumber);

            // Remove from held items tracking
            _playerLastHeldItems.Remove(otherPlayer.ActorNumber);
            _recentlySpawnedPlayers.Remove(otherPlayer.ActorNumber);

            if (_blockedPlayers.Contains(otherPlayer.ActorNumber))
            {
                _blockedPlayers.Remove(otherPlayer.ActorNumber);
                Logger.LogInfo($"Removed {otherPlayer.NickName} from RPC block list (disconnected)");
            }

            if (_detectedPlayers.Contains(otherPlayer.ActorNumber))
            {
                _detectedPlayers.Remove(otherPlayer.ActorNumber);
                Logger.LogInfo($"Removed {otherPlayer.NickName} from detected list (disconnected)");
            }
        }

        public void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer, Hashtable changedProps)
        {
            // Check if nickname was changed (potential mid-game name spoofing)
            if (changedProps.ContainsKey("name") || changedProps.ContainsKey("NickName"))
            {
                Logger.LogInfo($"Player {targetPlayer.ActorNumber} changed their name mid-game to {targetPlayer.NickName}");

                if (targetPlayer.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
                    return;

                // Compare against the originally logged name
                var identity = _knownPlayerIdentities.Find(p => p.ActorNumber == targetPlayer.ActorNumber);
                if (identity != null && identity.PhotonName != targetPlayer.NickName)
                {
                    Logger.LogWarning($"{targetPlayer.ActorNumber} changed their Photon name from {identity.PhotonName} to {targetPlayer.NickName} - possible spoof attempt");
                    LogVisually($"{{userColor}}{targetPlayer.NickName}</color> {{leftColor}}name change detected - possible spoofer!</color>", true, false, true);
                    BlockPlayer(targetPlayer, "Mid-game name change - possible spoofer");
                    return;
                }

                // Also re-check Steam/Photon match and mod properties in case they're covering their tracks
                CheckSteamNameMatch(targetPlayer);
                CheckPlayerForCheatMods(targetPlayer);
            }
        }

        public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged) { }

        // Check all players when we join a room
        public void OnJoinedRoom()
        {
            Logger.LogInfo("Joined room - checking all existing players for cheats/spoofing");

            StartCoroutine(CheckAllPlayersOnJoin());
        }

        public void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Check if this is a game map (not lobby/menu)
            if (scene.name.Contains("Game") || scene.name.Contains("Island") || scene.name.Contains("Map"))
            {
                // Give all players spawn grace period when loading into game
                foreach (var player in PhotonNetwork.PlayerList)
                {
                    OnPlayerSpawned(player.ActorNumber);
                }

                Logger.LogInfo($"Entered game scene '{scene.name}' - granting spawn grace period to all players");
            }
        }

        private IEnumerator CheckAllPlayersOnJoin()
        {
            // Wait for room data to fully sync
            yield return new WaitForSeconds(3f);

            foreach (var player in PhotonNetwork.PlayerList)
            {
                if (player.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber)
                {
                    CheckPlayerForCheatMods(player);
                }
            }
        }
    }

    public class PlayerIdentity
    {
        public string PhotonName;
        public int ActorNumber;
        public CSteamID SteamID;

        public PlayerIdentity(string photonName, int actorNumber, CSteamID steamID)
        {
            PhotonName = photonName;
            ActorNumber = actorNumber;
            SteamID = steamID;
        }
    }
}
