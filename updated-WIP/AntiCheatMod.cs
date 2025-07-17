using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
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
using System.Linq;

namespace AntiCheatMod
{
    [BepInPlugin("com.hiccup444.anticheat", "PEAK Anticheat", "1.3.6")]
    public class AntiCheatPlugin : BaseUnityPlugin, IConnectionCallbacks, IMatchmakingCallbacks, IInRoomCallbacks, IOnEventCallback
    {
        public static new ManualLogSource Logger;
        private static AntiCheatPlugin Instance;
        private static new ConfigFile Config;
        
        // Player item tracking
        private static readonly Dictionary<int, (string itemName, DateTime timestamp, bool wasCookable)> _playerLastHeldItems = new Dictionary<int, (string, DateTime, bool)>();

        // Plugin version
        private const string PLUGIN_VERSION = "1.3.6";

        // Custom event code for anticheat communication
        private const byte ANTICHEAT_PING_EVENT = 69;

        // Config entries
        private static ConfigEntry<bool> ShowVisualLogs;
        private static ConfigEntry<bool> CheckSteamNames;
        public static ConfigEntry<bool> AutoBlockCheaters;
        public static ConfigEntry<bool> VerboseRPCLogging;
        private static ConfigEntry<string> WhitelistedSteamIDs;

        // Connection log for visual messages
        private static PlayerConnectionLog _connectionLog;
        private static readonly Queue<(string message, bool onlySendOnce, bool sfxJoin, bool sfxLeave)> _queuedLogs = new Queue<(string, bool, bool, bool)>(8);

        // Track player identities
        private static readonly List<PlayerIdentity> _knownPlayerIdentities = new List<PlayerIdentity>();

        // Reflection methods/fields
        private static MethodInfo _getColorTagMethod;
        private static MethodInfo _addMessageMethod;
        private static FieldInfo _currentLogField;

        // Spawn grace period tracking
        private static readonly Dictionary<int, DateTime> _recentlySpawnedPlayers = new Dictionary<int, DateTime>();
        private const double SPAWN_GRACE_PERIOD_SECONDS = 5.0;

        // Track anticheat users
        private static readonly Dictionary<int, string> _anticheatUsers = new Dictionary<int, string>();

        private static readonly HashSet<string> _recentDetections = new HashSet<string>();

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;

            // Initialize config
            Config = new ConfigFile(Path.Combine(Paths.ConfigPath, "com.hiccup444.PEAKanticheat.cfg"), true);
            ShowVisualLogs = Config.Bind("General", "ShowVisualLogs", true, "Show anti-cheat messages in the connection log");
            CheckSteamNames = Config.Bind("General", "CheckSteamNames", true, "Check if Photon names match Steam names");
            AutoBlockCheaters = Config.Bind("General", "AutoBlockCheaters", true, "Automatically block all RPCs from detected cheaters");
            VerboseRPCLogging = Config.Bind("Debug", "VerboseRPCLogging", false, "Log all RPC calls for debugging");
            WhitelistedSteamIDs = Config.Bind("General", "WhitelistedSteamIDs", "",
                "Comma-separated list of Steam IDs that should never be RPC blocked (e.g. '76561198012345678,76561198087654321')");

            // Parse the whitelist
            ParseWhitelist();

            // Save the config
            Config.Save();

            // Initialize the new systems
            InitializeDetectionManager();
            InitializeEventHandlers();

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

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo($"[PEAKAntiCheat] Scene loaded: {scene.name}");
            
            // Create UI when entering game scenes
            if (scene.name.Contains("Airport") || scene.name.Contains("Level"))
            {
                Logger.LogInfo($"[PEAKAntiCheat] Game scene detected: {scene.name} - creating UI");
                CreateUI();
            }
            
            if (scene.name.Contains("Level"))
            {
                foreach (var player in PhotonNetwork.PlayerList)
                {
                    OnPlayerSpawned(player.ActorNumber);
                }

                if (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null)
                {
                    OnPlayerSpawned(PhotonNetwork.LocalPlayer.ActorNumber);
                }

                Logger.LogInfo($"Entered game scene '{scene.name}' - granting spawn grace period to all players including local");
            }
        }

        private void CreateUI()
        {
            // Check if UI already exists
            GameObject existingUI = GameObject.Find("AntiCheatUI");
            if (existingUI != null)
            {
                Logger.LogInfo("[PEAKAntiCheat] UI already exists, skipping creation");
                return;
            }

            Logger.LogInfo("[PEAKAntiCheat] Creating UI...");
            GameObject uiObject = new GameObject("AntiCheatUI");
            Logger.LogInfo($"[PEAKAntiCheat] UI GameObject created: {uiObject.name}, Active: {uiObject.activeInHierarchy}");
            
            var uiComponent = uiObject.AddComponent<AntiCheatUI>();
            Logger.LogInfo($"[PEAKAntiCheat] UI component added: {uiComponent != null}, Component enabled: {uiComponent?.enabled}");
            
            // Make sure the GameObject is active
            uiObject.SetActive(true);
            Logger.LogInfo($"[PEAKAntiCheat] UI GameObject activated: {uiObject.activeInHierarchy}");
            
            // Force the Start method to be called if it hasn't been
            if (uiComponent != null)
            {
                Logger.LogInfo("[PEAKAntiCheat] Manually calling Start method on UI component");
                uiComponent.SendMessage("Start", SendMessageOptions.DontRequireReceiver);
            }
            
            // Make the UI GameObject persist across scene changes
            DontDestroyOnLoad(uiObject);
            Logger.LogInfo("[PEAKAntiCheat] UI set to persist across scenes");
            
            Logger.LogInfo("[PEAKAntiCheat] UI creation completed");
            
            // Test if we can find the GameObject later
            StartCoroutine(TestUIGameObject());
        }

        private IEnumerator TestUIGameObject()
        {
            yield return new WaitForSeconds(2f);
            
            GameObject uiObject = GameObject.Find("AntiCheatUI");
            if (uiObject != null)
            {
                Logger.LogInfo($"[PEAKAntiCheat] Found UI GameObject: {uiObject.name}, Active: {uiObject.activeInHierarchy}");
                var uiComponent = uiObject.GetComponent<AntiCheatUI>();
                if (uiComponent != null)
                {
                    Logger.LogInfo($"[PEAKAntiCheat] Found UI component: {uiComponent.enabled}");
                }
                else
                {
                    Logger.LogWarning("[PEAKAntiCheat] UI component not found!");
                }
            }
            else
            {
                Logger.LogWarning("[PEAKAntiCheat] UI GameObject not found!");
            }
        }

        private void InitializeDetectionManager()
        {
            // Set up default detection settings based on config
            DetectionManager.SetDetectionSettings(DetectionType.CherryMod, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.AtlasMod, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.SteamNameMismatch, 
                new DetectionSettings(CheckSteamNames.Value, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.NameImpersonation, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.MidGameNameChange, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.OwnershipTheft, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedDestroy, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.RateLimitExceeded, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedKill, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedRevive, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedWarp, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedStatusEffect, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedMovement, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
                        DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedEmote,
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedItemDrop, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedCampfireModification, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedFlareLighting, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.UnauthorizedBananaSlip, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.MasterClientTheft, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.InfinityWarp, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
            DetectionManager.SetDetectionSettings(DetectionType.BlackScreenAttempt, 
                new DetectionSettings(true, AutoBlockCheaters.Value, ShowVisualLogs.Value, true));
        }

        private void InitializeEventHandlers()
        {
            // Subscribe to events
            DetectionManager.OnDetectionOccurred += OnDetectionOccurred;
            PlayerManager.OnPlayerAdded += OnPlayerAdded;
            PlayerManager.OnPlayerStatusChanged += OnPlayerStatusChanged;
            BlockingManager.OnPlayerBlocked += OnPlayerBlocked;
            BlockingManager.OnPlayerUnblocked += OnPlayerUnblocked;
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
            
            // Unsubscribe from events
            DetectionManager.OnDetectionOccurred -= OnDetectionOccurred;
            PlayerManager.OnPlayerAdded -= OnPlayerAdded;
            PlayerManager.OnPlayerStatusChanged -= OnPlayerStatusChanged;
            BlockingManager.OnPlayerBlocked -= OnPlayerBlocked;
            BlockingManager.OnPlayerUnblocked -= OnPlayerUnblocked;
        }

        // Event handlers
        private void OnDetectionOccurred(DetectionResult result)
        {
            if (result.Target == null)
                return;

            // Log to console if enabled
            if (DetectionManager.ShouldLogToConsole(result.Type))
            {
                Logger.LogWarning($"[DETECTION] {result.Target.NickName} ({result.Type}): {result.Reason}");
            }

            // If not master client, always report to host and do nothing else
            if (!PhotonNetwork.IsMasterClient)
            {
                ReportDetectionToMaster(result.Type, result.Target, result.Reason);
                return;
            }

            // Host: Handle based on detection settings
            var settings = DetectionManager.GetDetectionSettings(result.Type);
            if (settings.IsEnabled)
            {
                if (settings.AutoBlock)
                {
                    // Auto-block mode: Block immediately
                    BlockPlayer(result.Target, result.Reason);
                    LogVisually($"{{userColor}}{result.Target.NickName}</color> {{leftColor}}was auto-blocked for {result.Type}</color>", false, false, true);
                }
                else if (settings.ShowVisualWarning)
                {
                    // Warn mode: Show visual warning (prevent duplicates)
                    string detectionKey = $"{result.Target.ActorNumber}_{result.Type}";
                    if (!_recentDetections.Contains(detectionKey))
                    {
                        LogVisually($"{{userColor}}{result.Target.NickName}</color> {{leftColor}}detected for {result.Type}</color>", false, false, true);
                        _recentDetections.Add(detectionKey);
                        StartCoroutine(RemoveDetectionFromRecent(detectionKey, 30f));
                    }
                }
            }

            // Record detection in player manager
            PlayerManager.AddDetectionReason(result.Target.ActorNumber, result.Reason);
        }

        private System.Collections.IEnumerator RemoveDetectionFromRecent(string detectionKey, float delay)
        {
            yield return new UnityEngine.WaitForSeconds(delay);
            _recentDetections.Remove(detectionKey);
        }

        private void OnPlayerAdded(PlayerInfo playerInfo)
        {
            Logger.LogInfo($"[PLAYER ADDED] {playerInfo.PhotonName} (Actor #{playerInfo.ActorNumber})");
        }

        private void OnPlayerStatusChanged(PlayerInfo playerInfo)
        {
            Logger.LogInfo($"[STATUS CHANGE] {playerInfo.PhotonName}: {playerInfo.Status}");
        }

        private void OnPlayerBlocked(BlockEntry blockEntry)
        {
            Logger.LogWarning($"[PLAYER BLOCKED] {blockEntry.PlayerName}: {blockEntry.SpecificReason}");
        }

        private void OnPlayerUnblocked(int actorNumber)
        {
            Logger.LogInfo($"[PLAYER UNBLOCKED] Actor #{actorNumber}");
        }

        // Main blocking method
        public static void BlockPlayer(Photon.Realtime.Player cheater, string reason)
        {
            // Never block ourselves
            if (cheater.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                Logger.LogInfo($"Not blocking local player");
                return;
            }

            // NEVER block the master client
            if (cheater.IsMasterClient)
            {
                Logger.LogWarning($"[BLOCK PREVENTED] Attempted to block master client {cheater.NickName} for: {reason}");
                return;
            }

            Logger.LogWarning($"CHEATER DETECTED: {cheater.NickName} - Reason: {reason}");

            // Compute SteamID
            CSteamID cheaterSteamID = CSteamID.Nil;
            var identity = _knownPlayerIdentities.Find(p => p.ActorNumber == cheater.ActorNumber);
            if (identity != null)
            {
                cheaterSteamID = identity.SteamID;
            }
            else
            {
                cheaterSteamID = GetPlayerSteamID(cheater);
            }

            // Notify the banning mod
            AntiCheatEvents.NotifyCheaterDetected(cheater, reason, cheaterSteamID);

            if (!AutoBlockCheaters.Value)
            {
                Logger.LogInfo($"Auto-blocking disabled - only logging cheater: {cheater.NickName}");
                LogVisually($"{{userColor}}{cheater.NickName}</color> {{leftColor}}detected - {reason} (no action taken)</color>", false, false, true);
                return;
            }

            // Use the new blocking system
            BlockingManager.BlockPlayer(cheater, reason, BlockReason.AutoDetection, cheaterSteamID);

            // Apply visual warning
            LogVisually($"{{userColor}}{cheater.NickName}</color> {{leftColor}}RPC blocked - {reason}</color>", false, false, true);

            Logger.LogInfo($"All RPCs from {cheater.NickName} (Actor #{cheater.ActorNumber}) are now blocked");
        }

        // Legacy compatibility for RPC patches
        public static bool IsBlocked(int actorNumber)
        {
            return BlockingManager.IsBlocked(actorNumber);
        }

        // Item tracking methods
        public static void UpdatePlayerHeldItem(int actorNumber, string itemName, bool wasCookable = false)
        {
            if (!string.IsNullOrEmpty(itemName))
            {
                _playerLastHeldItems[actorNumber] = (itemName.ToLower(), DateTime.Now, wasCookable);
            }
        }

        public static bool PlayerHadItem(int actorNumber, string itemNamePart, float withinSeconds = 2f, bool checkCookable = false)
        {
            if (_playerLastHeldItems.TryGetValue(actorNumber, out var itemData))
            {
                var timeSince = (DateTime.Now - itemData.timestamp).TotalSeconds;
                if (timeSince <= withinSeconds)
                {
                    if (checkCookable)
                    {
                        return itemData.wasCookable;
                    }
                    else
                    {
                        return itemData.itemName.Contains(itemNamePart.ToLower());
                    }
                }
            }
            return false;
        }

        public static bool PlayerHadCookableItem(int actorNumber, float withinSeconds = 2f)
        {
            return PlayerHadItem(actorNumber, "", withinSeconds, true);
        }

        // Visual logging
        public static bool LogVisually(string message, bool onlySendOnce = false, bool sfxJoin = false, bool sfxLeave = false)
        {
            if (!ShowVisualLogs.Value)
                return true;

            if (!_connectionLog)
            {
                _connectionLog = FindObjectOfType<PlayerConnectionLog>();
                if (_connectionLog)
                {
                    var logType = _connectionLog.GetType();
                    _getColorTagMethod = logType.GetMethod("GetColorTag", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    _addMessageMethod = logType.GetMethod("AddMessage", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    _currentLogField = logType.GetField("currentLog", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
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

                var joinedColorField = _connectionLog.GetType().GetField("joinedColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var leftColorField = _connectionLog.GetType().GetField("leftColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var userColorField = _connectionLog.GetType().GetField("userColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                if (joinedColorField != null && leftColorField != null && userColorField != null)
                {
                    var joinedColor = joinedColorField.GetValue(_connectionLog);
                    var leftColor = leftColorField.GetValue(_connectionLog);
                    var userColor = userColorField.GetValue(_connectionLog);

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

        // Spawn grace period methods
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
                    _recentlySpawnedPlayers.Remove(actorNumber);
                }
            }
            return false;
        }

        // Steam ID resolution
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

                var steamNameToId = new Dictionary<string, List<CSteamID>>();
                var allLobbyMembers = new List<CSteamID>();

                for (int i = 0; i < numLobbyMembers; i++)
                {
                    CSteamID lobbyMember = SteamMatchmaking.GetLobbyMemberByIndex(currentLobby, i);
                    string steamName = SteamFriends.GetFriendPersonaName(lobbyMember);

                    if (!steamNameToId.ContainsKey(steamName))
                        steamNameToId[steamName] = new List<CSteamID>();

                    steamNameToId[steamName].Add(lobbyMember);
                    allLobbyMembers.Add(lobbyMember);
                }

                if (steamNameToId.ContainsKey(player.NickName))
                {
                    var possibleSteamIDs = steamNameToId[player.NickName];
                    Logger.LogInfo($"[GetPlayerSteamID] Found {possibleSteamIDs.Count} Steam players with name '{player.NickName}': {string.Join(", ", possibleSteamIDs)}");

                    List<Photon.Realtime.Player> photonPlayersWithSameName = new List<Photon.Realtime.Player>();
                    foreach (var otherPlayer in PhotonNetwork.PlayerList)
                    {
                        if (otherPlayer.NickName == player.NickName)
                            photonPlayersWithSameName.Add(otherPlayer);
                    }

                    if (photonPlayersWithSameName.Count > possibleSteamIDs.Count)
                    {
                        Logger.LogWarning($"[GetPlayerSteamID] More Photon players ({photonPlayersWithSameName.Count}) than Steam players ({possibleSteamIDs.Count}). Returning NIL as it's likely spoofed.");
                        return CSteamID.Nil;
                    }

                    photonPlayersWithSameName.Sort((a, b) => a.ActorNumber.CompareTo(b.ActorNumber));

                    int index = photonPlayersWithSameName.FindIndex(p => p.ActorNumber == player.ActorNumber);
                    if (index >= 0 && index < possibleSteamIDs.Count)
                    {
                        Logger.LogInfo($"[GetPlayerSteamID] Matched ActorNumber {player.ActorNumber} to SteamID {possibleSteamIDs[index]} (index {index}).");
                        return possibleSteamIDs[index];
                    }

                    Logger.LogWarning($"[GetPlayerSteamID] Could not find matching ActorNumber for {player.NickName}. Returning first available SteamID: {possibleSteamIDs[0]}");
                    return possibleSteamIDs[0];
                }

                Logger.LogInfo($"[GetPlayerSteamID] No exact match for '{player.NickName}', checking for unassigned Steam IDs...");

                int photonPlayerCount = PhotonNetwork.PlayerList.Length;

                if (photonPlayerCount > numLobbyMembers)
                {
                    Logger.LogWarning($"[GetPlayerSteamID] More Photon players ({photonPlayerCount}) than Steam lobby members ({numLobbyMembers}). Possible spoofer.");
                    return CSteamID.Nil;
                }

                foreach (var steamId in allLobbyMembers)
                {
                    bool isAssigned = false;

                    foreach (var identity in _knownPlayerIdentities)
                    {
                        if (identity.SteamID == steamId && identity.ActorNumber != player.ActorNumber)
                        {
                            isAssigned = true;
                            break;
                        }
                    }

                    if (!isAssigned)
                    {
                        bool needsRefresh = SteamFriends.RequestUserInformation(steamId, true);
                        string steamName = SteamFriends.GetFriendPersonaName(steamId);
                        Logger.LogWarning($"[GetPlayerSteamID] Found unassigned Steam ID {steamId} (name: '{steamName}', needs refresh: {needsRefresh})");
                        return steamId;
                    }
                }

                Logger.LogWarning($"[GetPlayerSteamID] No unassigned Steam IDs found. All {numLobbyMembers} lobby members are accounted for.");
                return CSteamID.Nil;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[GetPlayerSteamID] Exception while resolving Steam ID for {player.NickName}: {ex.Message}");
                return CSteamID.Nil;
            }
        }

        // Whitelist parsing
        private static void ParseWhitelist()
        {
            string whitelistString = WhitelistedSteamIDs.Value;
            if (string.IsNullOrWhiteSpace(whitelistString))
                return;

            string[] steamIds = whitelistString.Split(',');
            foreach (string steamId in steamIds)
            {
                string trimmedId = steamId.Trim();
                if (ulong.TryParse(trimmedId, out ulong id))
                {
                    BlockingManager.AddToWhitelist(id);
                    Logger.LogInfo($"[WHITELIST] Added Steam ID to whitelist: {id}");
                }
                else if (!string.IsNullOrEmpty(trimmedId))
                {
                    Logger.LogWarning($"[WHITELIST] Invalid Steam ID format: {trimmedId}");
                }
            }
        }

        // Cheat detection
        private IEnumerator CheckPlayersForCheats()
        {
            while (true)
            {
                yield return new WaitForSeconds(5f);

                if (PhotonNetwork.InRoom)
                {
                    foreach (var player in PhotonNetwork.PlayerList)
                    {
                        CheckPlayerForCheatMods(player);
                    }
                }
            }
        }

        private void CheckPlayerForCheatMods(Photon.Realtime.Player player)
        {
            if (player.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
                return;

            // Record the player's Steam ID when they first join
            if (!_knownPlayerIdentities.Exists(p => p.ActorNumber == player.ActorNumber))
            {
                var steamId = GetPlayerSteamID(player);
                Logger.LogInfo($"[Identity Log] Joined: PhotonName={player.NickName} | ActorNumber={player.ActorNumber} | SteamName={SteamFriends.GetFriendPersonaName(steamId)} | SteamID={steamId}");

                _knownPlayerIdentities.Add(new PlayerIdentity(player.NickName, player.ActorNumber, steamId));
            }

            // NEVER check or block the master client
            if (player.IsMasterClient)
            {
                Logger.LogInfo($"[CHECK SKIPPED] Not checking master client {player.NickName} for cheat mods");
                return;
            }

            // Cheat mod property checks
            if (player.CustomProperties.ContainsKey("CherryUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Cherry cheat mod!</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.CherryMod, player, "Cherry cheat mod user");
                return;
            }

            if (player.CustomProperties.ContainsKey("CherryOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Cherry cheat mod!</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.CherryMod, player, "Cherry cheat mod owner");
                return;
            }

            if (player.CustomProperties.ContainsKey("AtlUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Atlas cheat mod!</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.AtlasMod, player, "Atlas cheat mod user");
                return;
            }

            if (player.CustomProperties.ContainsKey("AtlOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Atlas cheat mod!</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.AtlasMod, player, "Atlas cheat mod owner");
                return;
            }

            // Steam name match check
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
                bool needsRefresh = SteamFriends.RequestUserInformation(identity.SteamID, true);

                if (needsRefresh)
                {
                    StartCoroutine(WaitForPersonaStateChange(player, identity.SteamID, 5f, false));
                    return;
                }

                string steamName = SteamFriends.GetFriendPersonaName(identity.SteamID);

                if (steamName.ToLower() == player.NickName.ToLower())
                {
                    return;
                }

                Logger.LogWarning($"Name mismatch detected on join: Photon='{player.NickName}' vs Steam='{steamName}'");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}has mismatched Steam name: '{steamName}'</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.SteamNameMismatch, player, $"Name mismatch: Photon='{player.NickName}' vs Steam='{steamName}'");
            }
        }

        private IEnumerator WaitForPersonaStateChange(Photon.Realtime.Player player, CSteamID steamId, float timeout, bool blockOnMismatch = true)
        {
            float startTime = Time.time;
            bool receivedUpdate = false;

            Callback<PersonaStateChange_t> personaCallback = null;
            personaCallback = Callback<PersonaStateChange_t>.Create((PersonaStateChange_t param) =>
            {
                if (param.m_ulSteamID == steamId.m_SteamID)
                {
                    if ((param.m_nChangeFlags & EPersonaChange.k_EPersonaChangeName) != 0)
                    {
                        receivedUpdate = true;
                        Logger.LogInfo($"Received PersonaStateChange for {steamId} - name was updated");
                    }
                }
            });

            while (!receivedUpdate && (Time.time - startTime) < timeout)
            {
                yield return new WaitForSeconds(0.1f);

                if (PhotonNetwork.CurrentRoom == null || PhotonNetwork.CurrentRoom.GetPlayer(player.ActorNumber) == null)
                {
                    Logger.LogInfo($"Player {player.NickName} left before name verification completed");
                    personaCallback?.Dispose();
                    yield break;
                }
            }

            personaCallback?.Dispose();

            string freshSteamName = SteamFriends.GetFriendPersonaName(steamId);

            if (freshSteamName.ToLower() != player.NickName.ToLower())
            {
                if (blockOnMismatch)
                {
                    Logger.LogWarning($"Name mismatch confirmed after PersonaStateChange: Photon='{player.NickName}' vs Steam='{freshSteamName}'");
                    LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}confirmed spoofing - real Steam name is '{freshSteamName}'!</color>", true, false, true);

                    if (PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.GetPlayer(player.ActorNumber) != null)
                    {
                        DetectionManager.RecordDetection(DetectionType.SteamNameMismatch, player, $"Name spoofing confirmed - real name: {freshSteamName}");
                    }
                }
                else
                {
                    Logger.LogWarning($"Name mismatch on join after refresh: Photon='{player.NickName}' vs Steam='{freshSteamName}'");
                    LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}has mismatched Steam name: '{freshSteamName}'</color>", true, false, true);
                    DetectionManager.RecordDetection(DetectionType.SteamNameMismatch, player, $"Name mismatch: Photon='{player.NickName}' vs Steam='{freshSteamName}'");
                }
            }
            else
            {
                Logger.LogInfo($"Name verification passed for {player.NickName} after fresh data");
                LogVisually($"{{userColor}}{player.NickName}</color> {{joinedColor}}name verified successfully</color>", true, false, false);
            }
        }

        // Master client protection
        public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
        {
            Logger.LogInfo($"{newMasterClient.NickName} (#{newMasterClient.ActorNumber}) is the new master client!");

            if (newMasterClient.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber && !PhotonNetwork.LocalPlayer.IsMasterClient)
            {
                var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();

                if (lobbyHandler != null && lobbyHandler.InSteamLobby(out CSteamID currentLobby))
                {
                    if (SteamMatchmaking.GetLobbyOwner(currentLobby) == SteamUser.GetSteamID())
                    {
                        Logger.LogWarning($"Master client stolen by: {newMasterClient.NickName}");
                        LogVisually($"{{userColor}}{newMasterClient.NickName}</color> {{leftColor}}tried to take master client</color>", false, false, true);

                        DetectionManager.RecordDetection(DetectionType.MasterClientTheft, newMasterClient, "Stole master client");

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
            _playerLastHeldItems.Clear();
            _recentlySpawnedPlayers.Clear();
            _anticheatUsers.Clear();
            _recentDetections.Clear();
        }

        // IInRoomCallbacks implementations
        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
        {
            OnPlayerSpawned(newPlayer.ActorNumber);
            PlayerManager.AddPlayer(newPlayer);
            StartCoroutine(CheckNewPlayerDelayed(newPlayer));
            if (newPlayer.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber)
            {
                StartCoroutine(SendAntiCheatPingToNewPlayer(newPlayer));
            }
        }

        private IEnumerator CheckNewPlayerDelayed(Photon.Realtime.Player player)
        {
            yield return new WaitForSeconds(2f);
            CheckPlayerForCheatMods(player);
        }

        private IEnumerator SendAntiCheatPingToNewPlayer(Photon.Realtime.Player newPlayer)
        {
            yield return new WaitForSeconds(2f);

            if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom || PhotonNetwork.LocalPlayer == null)
                yield break;

            if (PhotonNetwork.CurrentRoom?.GetPlayer(newPlayer.ActorNumber) == null)
                yield break;

            object[] pingData = new object[]
            {
                PhotonNetwork.LocalPlayer.NickName,
                PhotonNetwork.LocalPlayer.UserId,
                PLUGIN_VERSION
            };

            RaiseEventOptions opts = new RaiseEventOptions
            {
                TargetActors = new int[] { newPlayer.ActorNumber }
            };

            PhotonNetwork.RaiseEvent(ANTICHEAT_PING_EVENT, pingData, opts, SendOptions.SendReliable);
            Logger.LogInfo($"[AntiCheat] Sent anticheat ping to new player {newPlayer.NickName}");
        }

        private IEnumerator TrackPlayerItems()
        {
            while (true)
            {
                yield return new WaitForSeconds(2f);
                if (!PhotonNetwork.InRoom)
                    continue;

                var allCharacters = FindObjectsOfType<Character>();
                foreach (var character in allCharacters)
                {
                    var photonView = character.GetComponent<PhotonView>();
                    if (photonView == null || photonView.Owner == null)
                        continue;

                    if (IsBlocked(photonView.Owner.ActorNumber))
                        continue;

                    var characterData = character.GetComponent<CharacterData>();
                    if (characterData != null && characterData.currentItem != null)
                    {
                        bool canBeCooked = characterData.currentItem.cooking != null &&
                                          characterData.currentItem.cooking.canBeCooked;

                        UpdatePlayerHeldItem(photonView.Owner.ActorNumber,
                                           characterData.currentItem.name,
                                           canBeCooked);
                    }
                }
            }
        }

        public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
            _knownPlayerIdentities.RemoveAll(p => p.ActorNumber == otherPlayer.ActorNumber);
            _playerLastHeldItems.Remove(otherPlayer.ActorNumber);
            _recentlySpawnedPlayers.Remove(otherPlayer.ActorNumber);
            PlayerManager.RemovePlayer(otherPlayer.ActorNumber);
        }

        public void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer, Hashtable changedProps)
        {
            if (changedProps.ContainsKey("name") || changedProps.ContainsKey("NickName"))
            {
                Logger.LogInfo($"Player {targetPlayer.ActorNumber} changed their name mid-game to {targetPlayer.NickName}");

                if (targetPlayer.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
                    return;

                var identity = _knownPlayerIdentities.Find(p => p.ActorNumber == targetPlayer.ActorNumber);
                if (identity != null && identity.PhotonName != targetPlayer.NickName)
                {
                    Logger.LogWarning($"{targetPlayer.ActorNumber} changed their Photon name from {identity.PhotonName} to {targetPlayer.NickName} - definite spoof attempt");
                    LogVisually($"{{userColor}}{targetPlayer.NickName}</color> {{leftColor}}changed name mid-game from '{identity.PhotonName}' - spoofing detected!</color>", true, false, true);
                    DetectionManager.RecordDetection(DetectionType.MidGameNameChange, targetPlayer, $"Mid-game name change - spoofing");
                    return;
                }

                var steamId = identity?.SteamID ?? GetPlayerSteamID(targetPlayer);
                if (steamId != CSteamID.Nil)
                {
                    bool needsRefresh = SteamFriends.RequestUserInformation(steamId, true);
                    if (needsRefresh)
                    {
                        StartCoroutine(WaitForPersonaStateChange(targetPlayer, steamId, 5f, true));
                    }
                    else
                    {
                        string steamName = SteamFriends.GetFriendPersonaName(steamId);
                        if (steamName.ToLower() != targetPlayer.NickName.ToLower())
                        {
                            Logger.LogWarning($"Mid-game name change doesn't match Steam: Photon='{targetPlayer.NickName}' vs Steam='{steamName}'");
                            LogVisually($"{{userColor}}{targetPlayer.NickName}</color> {{leftColor}}changed name mid-game - doesn't match Steam name '{steamName}'!</color>", true, false, true);
                            DetectionManager.RecordDetection(DetectionType.MidGameNameChange, targetPlayer, $"Mid-game name spoofing - Steam name: {steamName}");
                        }
                    }
                }

                CheckPlayerForCheatMods(targetPlayer);
            }
        }

        public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged) { }

        // --- MASTER: On join, send sync request ---
        public void OnJoinedRoom()
        {
            Logger.LogInfo($"[AntiCheat] Joined room as {(PhotonNetwork.IsMasterClient ? "Master Client" : "Client")}");
            
            // Add all current players to the player manager
            foreach (var player in PhotonNetwork.CurrentRoom.Players.Values)
            {
                PlayerManager.AddPlayer(player);
            }

            // --- ATLAS MOD AUTO-REVIVE TRIGGER ---
            try
            {
                // Only run if not master client and Atlas is present
                if (!PhotonNetwork.IsMasterClient &&
                    BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("synq.peak.atlas"))
                {
                    Logger.LogWarning("[AntiCheat] Atlas detected on client, sending RPCA_Revive to master client!");
                    SendReviveRpcToMaster();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[AntiCheat] Error checking for Atlas or sending revive RPC: {ex}");
            }
            // --------------------------------------

            // Request sync from master client if we're not the master
            if (!PhotonNetwork.IsMasterClient)
            {
                PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.SyncRequest, null, 
                    new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
            }
            else
            {
                // Master client sends sync data to all clients
                var blockList = BlockingManager.GetAllBlockedPlayers().Select(b => b.ActorNumber).ToArray();
                var detectionSettings = DetectionManager.SerializeSettings();
                var whitelist = BlockingManager.GetWhitelistedSteamIDs().Select(id => (long)id).ToArray(); // Convert ulong to long

                object[] syncData = { blockList, detectionSettings, whitelist };
                RaiseEventOptions opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
                PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.SyncResponse, syncData, opts, SendOptions.SendReliable);
            }
        }

        // Helper to send RPCA_Revive to master client
        private void SendReviveRpcToMaster()
        {
            // Find the master client
            var master = PhotonNetwork.MasterClient;
            if (master == null || master.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                Logger.LogWarning("[AntiCheat] No valid master client to send revive RPC to.");
                return;
            }
            // Find the master client's player object (PhotonView)
            var playerObjects = GameObject.FindObjectsOfType<MonoBehaviour>()
                .Where(mb => mb.GetType().Name == "Player" || mb.GetType().Name.Contains("Player")).ToArray();
            foreach (var obj in playerObjects)
            {
                var pv = obj.GetComponent<PhotonView>();
                if (pv != null && pv.Owner != null && pv.Owner.ActorNumber == master.ActorNumber)
                {
                    // Call the revive RPC on the master client
                    pv.RPC("RPCA_Revive", master, new object[] { true });
                    Logger.LogWarning($"[AntiCheat] Sent RPCA_Revive to master client (Actor {master.ActorNumber})");
                    return;
                }
            }
            Logger.LogWarning("[AntiCheat] Could not find master client's player object to send revive RPC.");
        }

        private IEnumerator SendAntiCheatPingDelayed()
        {
            yield return new WaitForSeconds(1f);
            SendAntiCheatPing();
        }

        private void SendAntiCheatPing()
        {
            if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom || PhotonNetwork.LocalPlayer == null)
            {
                Logger.LogWarning("[AntiCheat] Cannot send ping - not in a room yet");
                return;
            }

            object[] pingData = new object[]
            {
                PhotonNetwork.LocalPlayer.NickName,
                PhotonNetwork.LocalPlayer.UserId,
                PLUGIN_VERSION
            };

            RaiseEventOptions opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(ANTICHEAT_PING_EVENT, pingData, opts, SendOptions.SendReliable);
            Logger.LogInfo($"[AntiCheat] Sent anticheat detection ping to other players");
        }

        private IEnumerator CheckAllPlayersOnJoin()
        {
            yield return new WaitForSeconds(3f);

            foreach (var player in PhotonNetwork.PlayerList)
            {
                if (player.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber)
                {
                    CheckPlayerForCheatMods(player);
                }
            }
        }

        public void OnEvent(EventData photonEvent)
        {
            var code = (AntiCheatNetEvent)photonEvent.Code;
            switch (code)
            {
                case AntiCheatNetEvent.DetectionReport:
                    if (PhotonNetwork.IsMasterClient)
                        HandleDetectionReportFromClient(photonEvent);
                    break;
                case AntiCheatNetEvent.BlockListUpdate:
                    HandleBlockListUpdate(photonEvent);
                    break;
                case AntiCheatNetEvent.DetectionSettingsUpdate:
                    HandleDetectionSettingsUpdate(photonEvent);
                    break;
                case AntiCheatNetEvent.WhitelistUpdate:
                    HandleWhitelistUpdate(photonEvent);
                    break;
                case AntiCheatNetEvent.SyncRequest:
                    if (PhotonNetwork.IsMasterClient)
                        HandleSyncRequest(photonEvent);
                    break;
                case AntiCheatNetEvent.SyncResponse:
                    HandleSyncResponse(photonEvent);
                    break;
            }
        }

        // --- MASTER: Handle sync request ---
        private void HandleSyncRequest(EventData photonEvent)
        {
            var blockList = BlockingManager.GetAllBlockedPlayers().Select(b => b.ActorNumber).ToArray();
            var detectionSettings = DetectionManager.SerializeSettings();
            var whitelist = BlockingManager.GetWhitelistedSteamIDs().Select(id => (long)id).ToArray(); // Convert ulong to long

            object[] syncData = { blockList, detectionSettings, whitelist };
            RaiseEventOptions opts = new RaiseEventOptions { TargetActors = new int[] { photonEvent.Sender } };
            PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.SyncResponse, syncData, opts, SendOptions.SendReliable);
        }

        // --- CLIENT: Handle sync response ---
        private void HandleSyncResponse(EventData photonEvent)
        {
            var data = (object[])photonEvent.CustomData;
            int[] blockList = (int[])data[0];
            object[] detectionSettings = (object[])data[1];
            long[] whitelistLong = (long[])data[2]; // Receive as long array

            BlockingManager.SetBlockList(blockList);
            DetectionManager.DeserializeSettings(detectionSettings);
            // Convert long back to ulong
            ulong[] whitelist = whitelistLong.Select(id => (ulong)id).ToArray();
            BlockingManager.SetWhitelist(whitelist);
        }

        // --- CLIENT: On detection, report to master ---
        public void ReportDetectionToMaster(DetectionType type, Photon.Realtime.Player target, string reason)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                object[] report = { target.ActorNumber, (int)type, reason };
                PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.DetectionReport, report, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
            }
        }

        // --- MASTER: Handle detection report ---
        private void HandleDetectionReportFromClient(EventData photonEvent)
        {
            var data = (object[])photonEvent.CustomData;
            int actorNumber = (int)data[0];
            DetectionType type = (DetectionType)(int)data[1];
            string reason = (string)data[2];

            var player = PhotonNetwork.CurrentRoom.GetPlayer(actorNumber);
            if (player != null && DetectionManager.IsDetectionEnabled(type))
            {
                BlockPlayer(player, reason);
            }
        }

        // --- MASTER: When master blocks/unblocks, toggles detection, or changes whitelist, broadcast to all ---
        public static void BroadcastBlockListUpdate(int actorNumber, bool isBlocked)
        {
            object[] data = { actorNumber, isBlocked };
            PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.BlockListUpdate, data, 
                new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        }
        public static void BroadcastDetectionSettingsUpdate()
        {
            var settings = DetectionManager.SerializeSettings();
            PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.DetectionSettingsUpdate, settings, 
                new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        }
        public static void BroadcastWhitelistUpdate(ulong[] whitelist)
        {
            // Convert ulong to long for Photon serialization
            long[] whitelistLong = whitelist.Select(id => (long)id).ToArray();
            PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.WhitelistUpdate, whitelistLong, 
                new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        }

        // --- ALL: Handle block/unblock, detection toggle, whitelist update broadcasts ---
        private void HandleBlockListUpdate(EventData photonEvent)
        {
            var data = (object[])photonEvent.CustomData;
            int actorNumber = (int)data[0];
            bool isBlocked = (bool)data[1];
            if (isBlocked)
                BlockingManager.BlockPlayer(PhotonNetwork.CurrentRoom.GetPlayer(actorNumber), "MasterClient block", BlockReason.Manual);
            else
                BlockingManager.UnblockPlayer(actorNumber);
        }
        private void HandleDetectionSettingsUpdate(EventData photonEvent)
        {
            object[] settings = (object[])photonEvent.CustomData;
            DetectionManager.DeserializeSettings(settings);
        }
        private void HandleWhitelistUpdate(EventData photonEvent)
        {
            long[] whitelistLong = (long[])photonEvent.CustomData; // Receive as long array
            // Convert long back to ulong
            ulong[] whitelist = whitelistLong.Select(id => (ulong)id).ToArray();
            BlockingManager.SetWhitelist(whitelist);
        }

        // --- UI: Only master can call these, and must broadcast ---
        // When master blocks/unblocks, toggles detection, or changes whitelist, call the above Broadcast* methods

        // ... rest of your plugin ...
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