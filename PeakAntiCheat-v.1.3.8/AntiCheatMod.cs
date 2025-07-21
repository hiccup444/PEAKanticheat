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
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using UnityEngine.SceneManagement;
using System.Linq;

namespace AntiCheatMod
{
    [BepInPlugin("com.hiccup444.anticheat", "PEAK Anticheat", "1.3.8")]
    public class AntiCheatPlugin : BaseUnityPlugin, IConnectionCallbacks, IMatchmakingCallbacks, IInRoomCallbacks, IOnEventCallback
    {
        public static new ManualLogSource Logger;
        private static AntiCheatPlugin Instance;
        private static new ConfigFile Config;
        
        // Player item tracking
        private static readonly Dictionary<int, (string itemName, DateTime timestamp, bool wasCookable)> _playerLastHeldItems = new Dictionary<int, (string, DateTime, bool)>();

        // Player coordinate tracking for infinity rescue
        private static readonly Dictionary<int, Vector3> _playerLastKnownCoordinates = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, DateTime> _playerCoordinateTimestamps = new Dictionary<int, DateTime>();
        private const float COORDINATE_UPDATE_INTERVAL = 10f; // Update coordinates every 10 seconds

        // Item duplication prevention for blocked players
        private static readonly Dictionary<int, GameObject> _blockedPlayerHeldItems = new Dictionary<int, GameObject>();

        // Plugin version
        private const string PLUGIN_VERSION = "1.3.8";

        // Custom event code for anticheat communication
        private const byte ANTICHEAT_PING_EVENT = 69;

        // Config entries
        private static ConfigEntry<bool> ShowVisualLogs;
        private static ConfigEntry<bool> CheckSteamNames;
        public static ConfigEntry<bool> AutoBlockCheaters;
        public static ConfigEntry<bool> VerboseRPCLogging;
        private static ConfigEntry<string> WhitelistedSteamIDs;
        public static ConfigEntry<KeyboardShortcut> UIToggleKey;

        // Connection log for visual messages
        private static PlayerConnectionLog _connectionLog;
        private static readonly Queue<(string message, bool onlySendOnce, bool sfxJoin, bool sfxLeave)> _queuedLogs = new Queue<(string, bool, bool, bool)>(8);

        // Track player identities
        private static readonly List<PlayerIdentity> _knownPlayerIdentities = new List<PlayerIdentity>();

        // Reflection methods/fields
        private static MethodInfo _getColorTagMethod;
        private static MethodInfo _addMessageMethod;
        private static FieldInfo _currentLogField;
        
        // Cached field info for performance
        private static FieldInfo _joinedColorField;
        private static FieldInfo _leftColorField;
        private static FieldInfo _userColorField;
        private static FieldInfo _sfxJoinField;
        private static FieldInfo _sfxLeaveField;
        
        // Cached delegates for better performance
        private static Func<object, string> _getColorTagDelegate;
        private static Action<object, string> _addMessageDelegate;
        
        // Cached Steam lobby reflection for performance
        private static FieldInfo _currentLobbyField;

        // Spawn grace period tracking
        private static readonly Dictionary<int, DateTime> _recentlySpawnedPlayers = new Dictionary<int, DateTime>();
        private const double SPAWN_GRACE_PERIOD_SECONDS = 5.0;

        // Track anticheat users
        private static readonly Dictionary<int, string> _anticheatUsers = new Dictionary<int, string>();

        private static readonly HashSet<string> _recentDetections = new HashSet<string>();
        
        // Track detected cheat mod users to prevent spam
        private static readonly HashSet<int> _detectedCheatModUsers = new HashSet<int>();
        
        // Track if startup message has been shown
        private static bool _startupMessageShown = false;

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
            UIToggleKey = Config.Bind("General", "UIToggleKey", new KeyboardShortcut(KeyCode.F2), "Key to toggle the anti-cheat UI (default: F2)");

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

            // Start tracking player coordinates for infinity rescue
            StartCoroutine(TrackPlayerCoordinates());

            SceneManager.activeSceneChanged += OnSceneChanged;
        }

        private void OnSceneChanged(Scene oldScene, Scene newScene)
        {
            Logger.LogInfo($"[PEAKAntiCheat] Scene changed from {oldScene.name} to {newScene.name}");
            
            // Create UI when entering game scenes
            if (newScene.name.Contains("Airport") || newScene.name.Contains("Level"))
            {
                Logger.LogInfo($"[PEAKAntiCheat] Game scene detected: {newScene.name} - creating UI");
                CreateUI();
                
                // Show anticheat loaded message for master client in airport scene
                if (newScene.name.Contains("Airport") && !_startupMessageShown)
                {
                    StartCoroutine(ShowAnticheatLoadedMessage());
                }
            }
            
            if (newScene.name.Contains("Level"))
            {
                foreach (var player in PhotonNetwork.PlayerList)
                {
                    OnPlayerSpawned(player.ActorNumber);
                }

                if (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null)
                {
                    OnPlayerSpawned(PhotonNetwork.LocalPlayer.ActorNumber);
                }

                Logger.LogInfo($"Entered game scene '{newScene.name}' - granting spawn grace period to all players including local");
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

        private IEnumerator ShowAnticheatLoadedMessage()
        {
            // Mark as shown to prevent duplicate messages
            _startupMessageShown = true;
            
            // Wait a short delay for the scene to fully load
            yield return new WaitForSeconds(0.25f);
            
            // Wait for room state to be properly established before checking master client status
            float roomTimeout = 10f;
            float roomElapsed = 0f;
            
            while (!PhotonNetwork.InRoom && roomElapsed < roomTimeout)
            {
                yield return new WaitForSeconds(0.25f);
                roomElapsed += 0.25f;
            }
            
            if (!PhotonNetwork.InRoom)
            {
                Logger.LogWarning("[PEAKAntiCheat] Not in room after timeout, skipping anticheat loaded message");
                yield break;
            }
            
            // Now check if we're the master client
            if (!PhotonNetwork.IsMasterClient)
            {
                Logger.LogInfo("[PEAKAntiCheat] Not master client, skipping anticheat loaded message");
                yield break;
            }
            
            // Wait for PlayerConnectionLog to be available
            float timeout = 5f;
            float elapsed = 0f;
            
            while (_connectionLog == null && elapsed < timeout)
            {
                yield return new WaitForSeconds(0.25f);
                elapsed += 0.25f;
                
                // Try to find the PlayerConnectionLog
                _connectionLog = FindObjectOfType<PlayerConnectionLog>();
                if (_connectionLog != null)
                {
                    Logger.LogInfo("[PEAKAntiCheat] Found PlayerConnectionLog, initializing reflection cache");
                    InitializeReflectionCache();
                }
            }
            
            if (_connectionLog != null)
            {
                Logger.LogInfo("[PEAKAntiCheat] Showing anticheat loaded message");
                LogVisually($"{{joinedColor}}Anticheat loaded! Press F2 to open manager.</color>", false, true, false);
            }
            else
            {
                Logger.LogWarning("[PEAKAntiCheat] PlayerConnectionLog not found after timeout, message will be queued");
                // The message will be queued and shown when PlayerConnectionLog becomes available
                LogVisually($"{{joinedColor}}Anticheat loaded! Press F2 to open manager.</color>", false, true, false);
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
            DetectionManager.SetDetectionSettings(DetectionType.SteamIDSpoofing, 
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
            
            // Debug: Log if we have queued messages
            if (_queuedLogs.Count > 0)
            {
                Logger.LogInfo($"[PEAKAntiCheat] {_queuedLogs.Count} queued messages waiting for PlayerConnectionLog");
            }
        }

        private void OnDestroy()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
            SceneManager.activeSceneChanged -= OnSceneChanged;
            
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
                // Always show the initial detection message
                LogVisually($"{{userColor}}{result.Target.NickName}</color> {{leftColor}}{result.Reason}</color>", false, false, true);
                
                if (settings.AutoBlock)
                {
                    // Auto-block mode: Block immediately with detection type
                    BlockPlayer(result.Target, result.Reason, result.Type);
                    LogVisually($"{{userColor}}{result.Target.NickName}</color> {{leftColor}}was auto-blocked for {result.Type}</color>", false, false, true);
                }
                else if (settings.ShowVisualWarning)
                {
                    // Warn mode: Show "no action taken" message (prevent duplicates)
                    string detectionKey = $"{result.Target.ActorNumber}_{result.Type}";
                    if (!_recentDetections.Contains(detectionKey))
                    {
                        LogVisually($"{{userColor}}{result.Target.NickName}</color> {{leftColor}}detected - {result.Type} - no action taken</color>", false, false, true);
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
            
            // Clear cheat mod detection tracking for this player
            // This grants them immunity from cheat mod detection
            _detectedCheatModUsers.Remove(actorNumber);
            
            // Also clear any recent detections for this player
            var player = PhotonNetwork.CurrentRoom?.GetPlayer(actorNumber);
            if (player != null)
            {
                string detectionKey = $"{player.NickName}_{actorNumber}_AtlasMod";
                _recentDetections.Remove(detectionKey);
                detectionKey = $"{player.NickName}_{actorNumber}_CherryMod";
                _recentDetections.Remove(detectionKey);
            }
        }

        // Main blocking method
        public static void BlockPlayer(Photon.Realtime.Player cheater, string reason, DetectionType? detectionType = null)
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

            // Check if we should block based on detection settings
            bool shouldBlock = true;
            if (detectionType.HasValue)
            {
                // Use individual detection setting if provided
                shouldBlock = DetectionManager.ShouldAutoBlock(detectionType.Value);
            }
            else
            {
                // Use global setting for legacy calls
                shouldBlock = AutoBlockCheaters.Value;
            }

            if (!shouldBlock)
            {
                Logger.LogInfo($"Auto-blocking disabled for this detection - only logging cheater: {cheater.NickName}");
                return;
            }

            // Only log "CHEATER DETECTED" when we're actually going to block
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

            // Use the new blocking system with detection type
            BlockingManager.BlockPlayer(cheater, reason, BlockReason.AutoDetection, cheaterSteamID, detectionType);

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
        // Initialize cached reflection data for performance
        private static void InitializeReflectionCache()
        {
            if (_connectionLog == null)
            {
                _connectionLog = FindObjectOfType<PlayerConnectionLog>();
                if (_connectionLog == null)
                {
                    Logger.LogWarning("[PEAKAntiCheat] PlayerConnectionLog not found in scene");
                    return;
                }
                Logger.LogInfo("[PEAKAntiCheat] Found PlayerConnectionLog, setting up reflection cache");
            }

            var logType = _connectionLog.GetType();
            
            // Cache method info
            _getColorTagMethod = logType.GetMethod("GetColorTag", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _addMessageMethod = logType.GetMethod("AddMessage", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _currentLogField = logType.GetField("currentLog", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            
            // Cache field info
            _joinedColorField = logType.GetField("joinedColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _leftColorField = logType.GetField("leftColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _userColorField = logType.GetField("userColor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _sfxJoinField = logType.GetField("sfxJoin", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _sfxLeaveField = logType.GetField("sfxLeave", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            
            // Create delegates for better performance
            try
            {
                if (_getColorTagMethod != null)
                {
                    _getColorTagDelegate = (Func<object, string>)Delegate.CreateDelegate(typeof(Func<object, string>), _connectionLog, _getColorTagMethod);
                }
                
                if (_addMessageMethod != null)
                {
                    _addMessageDelegate = (Action<object, string>)Delegate.CreateDelegate(typeof(Action<object, string>), _connectionLog, _addMessageMethod);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Reflection Cache] Failed to create delegates, falling back to direct reflection: {ex.Message}");
                _getColorTagDelegate = null;
                _addMessageDelegate = null;
            }
            
            // Note: Field getter delegates are not needed since we can use FieldInfo.GetValue directly
            // The main performance gain comes from caching the FieldInfo objects and using delegates for methods
        }

        public static bool LogVisually(string message, bool onlySendOnce = false, bool sfxJoin = false, bool sfxLeave = false, bool allowNonMaster = false)
        {
            // Non-master clients should only see anticheat ping messages unless explicitly allowed
            if (!PhotonNetwork.IsMasterClient && !allowNonMaster)
            {
                // Only allow anticheat ping messages for non-master clients
                if (!message.Contains("has anticheat installed"))
                {
                    return true; // Silently ignore other messages for non-master clients
                }
            }

            if (!ShowVisualLogs.Value)
                return true;

            // Initialize reflection cache if needed
            if (_connectionLog == null || _getColorTagMethod == null || _addMessageMethod == null)
            {
                InitializeReflectionCache();
            }

            if (!_connectionLog || _getColorTagMethod == null || _addMessageMethod == null)
            {
                Logger.LogWarning($"[PEAKAntiCheat] LogVisually failed - ConnectionLog: {_connectionLog != null}, GetColorTagMethod: {_getColorTagMethod != null}, AddMessageMethod: {_addMessageMethod != null}");
                _queuedLogs.Enqueue((message, onlySendOnce, sfxJoin, sfxLeave));
                return false;
            }

            try
            {
                StringBuilder sb = new StringBuilder(message);

                // Use cached field info and delegates for better performance
                if (_joinedColorField != null && _leftColorField != null && _userColorField != null)
                {
                    var joinedColor = _joinedColorField.GetValue(_connectionLog);
                    var leftColor = _leftColorField.GetValue(_connectionLog);
                    var userColor = _userColorField.GetValue(_connectionLog);

                    string joinedColorTag = "";
                    string leftColorTag = "";
                    string userColorTag = "";

                    // Use delegate if available, otherwise fall back to reflection
                    if (_getColorTagDelegate != null)
                    {
                        joinedColorTag = _getColorTagDelegate(joinedColor);
                        leftColorTag = _getColorTagDelegate(leftColor);
                        userColorTag = _getColorTagDelegate(userColor);
                    }
                    else if (_getColorTagMethod != null)
                    {
                        joinedColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { joinedColor });
                        leftColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { leftColor });
                        userColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { userColor });
                    }

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

                // Use cached delegate for better performance
                if (_addMessageDelegate != null)
                {
                    _addMessageDelegate(_connectionLog, message);
                }
                else
                {
                    _addMessageMethod.Invoke(_connectionLog, new object[] { message });
                }

                // Handle sound effects with cached field info
                if (sfxJoin && _sfxJoinField != null)
                {
                    var sfxJoinObj = _sfxJoinField.GetValue(_connectionLog);
                    if (sfxJoinObj != null)
                    {
                        var playMethod = sfxJoinObj.GetType().GetMethod("Play", new Type[] { typeof(Vector3) });
                        playMethod?.Invoke(sfxJoinObj, new object[] { Vector3.zero });
                    }
                }

                if (sfxLeave && _sfxLeaveField != null)
                {
                    var sfxLeaveObj = _sfxLeaveField.GetValue(_connectionLog);
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

                // Cache the field info if not already cached
                if (_currentLobbyField == null)
                {
                    var lobbyHandlerType = lobbyHandler.GetType();
                    _currentLobbyField = lobbyHandlerType.GetField("m_currentLobby", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_currentLobbyField == null)
                {
                    Logger.LogError("[GetPlayerSteamID] Could not find m_currentLobby field via reflection.");
                    return CSteamID.Nil;
                }

                CSteamID currentLobby = (CSteamID)_currentLobbyField.GetValue(lobbyHandler);
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
                return;
            }

            // Skip if we've already detected this player for cheat mods
            if (_detectedCheatModUsers.Contains(player.ActorNumber))
            {
                return;
            }

            // Grant immunity to unblocked players from cheat mod detection
            if (!IsBlocked(player.ActorNumber))
            {
                return;
            }

            // Cheat mod property checks
            if (player.CustomProperties.ContainsKey("CherryUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Cherry cheat mod!</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.CherryMod, player, "Cherry cheat mod user");
                _detectedCheatModUsers.Add(player.ActorNumber);
                return;
            }

            if (player.CustomProperties.ContainsKey("CherryOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Cherry cheat mod!</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.CherryMod, player, "Cherry cheat mod owner");
                _detectedCheatModUsers.Add(player.ActorNumber);
                return;
            }

            if (player.CustomProperties.ContainsKey("AtlUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Atlas cheat mod!</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.AtlasMod, player, "Atlas cheat mod user");
                _detectedCheatModUsers.Add(player.ActorNumber);
                return;
            }

            if (player.CustomProperties.ContainsKey("AtlOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Atlas cheat mod!</color>", true, false, true);
                DetectionManager.RecordDetection(DetectionType.AtlasMod, player, "Atlas cheat mod owner");
                _detectedCheatModUsers.Add(player.ActorNumber);
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
                // Check for Steam ID spoofing - if someone is pretending to be the master client
                CheckForSteamIDSpoofing(player, identity.SteamID);

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

        // Check for Steam ID spoofing where someone pretends to be the master client
        private void CheckForSteamIDSpoofing(Photon.Realtime.Player player, CSteamID playerSteamID)
        {
            // Only check if we're the master client and the player is not the master client
            if (!PhotonNetwork.IsMasterClient || player.IsMasterClient)
                return;

            // Get the master client's Steam ID
            CSteamID masterSteamID = SteamUser.GetSteamID();

            // If the player's Steam ID matches the master client's Steam ID, this is a spoofing attempt
            if (playerSteamID == masterSteamID)
            {
                Logger.LogError($"[STEAM ID SPOOFING DETECTED] {player.NickName} is spoofing the master client's Steam ID!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is spoofing the master client's Steam ID!</color>", false, false, true);
                
                // Check detection settings
                bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.SteamIDSpoofing);
                bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.SteamIDSpoofing);

                // Show visual log if enabled
                if (shouldShowVisual)
                {
                    LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is spoofing the master client's Steam ID!</color>", false, false, true);
                }

                // Block player if auto-block is enabled
                if (shouldBlock)
                {
                    BlockPlayer(player, "Steam ID spoofing - pretending to be master client", DetectionType.SteamIDSpoofing);
                }

                // Record the detection
                DetectionManager.RecordDetection(DetectionType.SteamIDSpoofing, player, "Spoofed master client's Steam ID");
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

            // Update player status in PlayerManager
            PlayerManager.HandleMasterClientSwitch(newMasterClient);

            // CRITICAL: Check if this is a legitimate switch or theft
            bool isLegitimateSwitch = false;
            bool isTheft = false;

            // If the new master client is the local player, this is legitimate
            if (newMasterClient.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                Logger.LogInfo("[MASTER CLIENT] Local player became master client - legitimate switch");
                isLegitimateSwitch = true;
                
                // Create UI for the new master client if they don't have it
                CreateUI();
                
                // Send sync data to all other clients
                StartCoroutine(SendSyncToAllClients());
            }
            else
            {
                // Check if the original master client (lobby owner) is still in the room
                var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();
                if (lobbyHandler != null && lobbyHandler.InSteamLobby(out CSteamID currentLobby))
                {
                    CSteamID lobbyOwner = SteamMatchmaking.GetLobbyOwner(currentLobby);
                    
                    // Check if the lobby owner (original master) is still in the room
                    bool originalMasterStillInRoom = false;
                    foreach (var player in PhotonNetwork.PlayerList)
                    {
                        var playerSteamID = GetPlayerSteamID(player);
                        if (playerSteamID == lobbyOwner)
                        {
                            originalMasterStillInRoom = true;
                            break;
                        }
                    }

                    if (originalMasterStillInRoom)
                    {
                        // Original master is still here - this is DEFINITELY theft!
                        Logger.LogError($"[MASTER CLIENT THEFT DETECTED] {newMasterClient.NickName} stole master client while original master is still in room!");
                        LogVisually($"{{userColor}}{newMasterClient.NickName}</color> {{leftColor}}stole master client while original master is still here!</color>", false, false, true);
                        
                        // Check detection settings
                        bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.MasterClientTheft);
                        bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.MasterClientTheft);

                        // Show visual log if enabled
                        if (shouldShowVisual)
                        {
                            LogVisually($"{{userColor}}{newMasterClient.NickName}</color> {{leftColor}}stole master client while original master is still here!</color>", false, false, true);
                        }

                        // Block player if auto-block is enabled
                        if (shouldBlock)
                        {
                            BlockPlayer(newMasterClient, "Master client theft - original master still in room", DetectionType.MasterClientTheft);
                        }

                        // Record the detection
                        DetectionManager.RecordDetection(DetectionType.MasterClientTheft, newMasterClient, "Stole master client while original master still present");
                        
                        // Take master client back if we're the original master
                        if (lobbyOwner == SteamUser.GetSteamID())
                        {
                            Logger.LogInfo("[MASTER CLIENT] Taking master client back from thief");
                            PhotonNetwork.SetMasterClient(PhotonNetwork.LocalPlayer);
                            
                            // Notify UI that we recovered master client
                            var uiObject = GameObject.Find("AntiCheatUI");
                            if (uiObject != null)
                            {
                                var uiComponent = uiObject.GetComponent<AntiCheatUI>();
                                if (uiComponent != null)
                                {
                                    uiComponent.OnMasterClientRecovered();
                                }
                            }
                        }
                        
                        isTheft = true;
                    }
                    else
                    {
                        // Original master left - this is likely legitimate
                        Logger.LogInfo("[MASTER CLIENT] Original master left - likely legitimate switch");
                        isLegitimateSwitch = true;
                    }
                }
                else
                {
                    // No lobby handler - check if new master has anticheat as fallback
                    if (_anticheatUsers.ContainsKey(newMasterClient.ActorNumber))
                    {
                        Logger.LogInfo($"[MASTER CLIENT] New master client {newMasterClient.NickName} has anticheat - likely legitimate switch");
                        isLegitimateSwitch = true;
                    }
                    else
                    {
                        Logger.LogWarning($"[MASTER CLIENT] New master client {newMasterClient.NickName} does not have anticheat - potential theft");
                        LogVisually($"{{userColor}}{newMasterClient.NickName}</color> {{leftColor}}became master client without anticheat!</color>", false, false, true);
                        DetectionManager.RecordDetection(DetectionType.MasterClientTheft, newMasterClient, "Became master client without anticheat");
                        isTheft = true;
                    }
                }
            }

            // If it's a legitimate switch and the new master has anticheat, sync with them
            if (isLegitimateSwitch && _anticheatUsers.ContainsKey(newMasterClient.ActorNumber))
            {
                Logger.LogInfo($"[MASTER CLIENT] Syncing with new master client {newMasterClient.NickName}");
                
                // Request sync from the new master client
                if (!PhotonNetwork.IsMasterClient)
                {
                    PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.SyncRequest, null, 
                        new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
                }
            }
        }

        // Helper method to send sync data to all clients when becoming master
        private IEnumerator SendSyncToAllClients()
        {
            yield return new WaitForSeconds(1f); // Small delay to ensure everything is ready
            
            if (PhotonNetwork.IsMasterClient)
            {
                Logger.LogInfo("[MASTER CLIENT] Sending sync data to all clients");
                
                var blockList = BlockingManager.GetAllBlockedPlayers().Select(b => b.ActorNumber).ToArray();
                var detectionSettings = DetectionManager.SerializeSettings();
                var whitelist = BlockingManager.GetWhitelistedSteamIDs().Select(id => (long)id).ToArray();

                object[] syncData = { blockList, detectionSettings, whitelist };
                RaiseEventOptions opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
                PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.SyncResponse, syncData, opts, SendOptions.SendReliable);
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
            _playerLastKnownCoordinates.Clear();
            _playerCoordinateTimestamps.Clear();
            _blockedPlayerHeldItems.Clear();
            _detectedCheatModUsers.Clear();
            
            // Reset startup message flag for next session
            _startupMessageShown = false;
        }

        // IInRoomCallbacks implementations
        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
        {
            OnPlayerSpawned(newPlayer.ActorNumber);
            PlayerManager.AddPlayer(newPlayer);
            
            // CRITICAL: Check for Steam ID spoofing immediately when player joins
            if (PhotonNetwork.IsMasterClient && !newPlayer.IsMasterClient)
            {
                var steamID = GetPlayerSteamID(newPlayer);
                if (steamID != CSteamID.Nil)
                {
                    CheckForSteamIDSpoofing(newPlayer, steamID);
                }
            }
            
            StartCoroutine(CheckNewPlayerDelayed(newPlayer));
            
            // Send anticheat ping to the new player
            if (newPlayer.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber)
            {
                StartCoroutine(SendAntiCheatPingToNewPlayer(newPlayer));
            }
            
            // If WE are the new player, send our anticheat ping to all existing players
            if (newPlayer.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                StartCoroutine(SendAntiCheatPingToAllExistingPlayers());
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

        private IEnumerator SendAntiCheatPingToAllExistingPlayers()
        {
            yield return new WaitForSeconds(2f);

            if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom || PhotonNetwork.LocalPlayer == null)
                yield break;

            // Get all existing players except ourselves
            var existingPlayers = PhotonNetwork.CurrentRoom.Players.Values
                .Where(p => p.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber)
                .ToArray();

            if (existingPlayers.Length == 0)
                yield break;

            object[] pingData = new object[]
            {
                PhotonNetwork.LocalPlayer.NickName,
                PhotonNetwork.LocalPlayer.UserId,
                PLUGIN_VERSION
            };

            // Send ping to each existing player
            foreach (var player in existingPlayers)
            {
                RaiseEventOptions opts = new RaiseEventOptions
                {
                    TargetActors = new int[] { player.ActorNumber }
                };

                PhotonNetwork.RaiseEvent(ANTICHEAT_PING_EVENT, pingData, opts, SendOptions.SendReliable);
                Logger.LogInfo($"[AntiCheat] Sent anticheat ping to existing player {player.NickName}");
            }
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

        // Track player coordinates every 10 seconds for infinity rescue
        private IEnumerator TrackPlayerCoordinates()
        {
            while (true)
            {
                yield return new WaitForSeconds(COORDINATE_UPDATE_INTERVAL);
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

                    // Store the player's current position
                    Vector3 currentPosition = character.transform.position;
                    _playerLastKnownCoordinates[photonView.Owner.ActorNumber] = currentPosition;
                    _playerCoordinateTimestamps[photonView.Owner.ActorNumber] = DateTime.Now;

                    // Check if player is at infinity coordinates and rescue them
                    if (PhotonNetwork.IsMasterClient)
                    {
                        CheckAndRescuePlayerFromInfinity(character, photonView.Owner);
                    }
                }
            }
        }

        // Check if a player is at infinity coordinates and rescue them
        private void CheckAndRescuePlayerFromInfinity(Character character, Photon.Realtime.Player player)
        {
            Vector3 position = character.transform.position;
            
            // Check if position is infinity
            bool isAtInfinity = float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z) ||
                               float.IsNegativeInfinity(position.x) || float.IsNegativeInfinity(position.y) || float.IsNegativeInfinity(position.z);

            if (isAtInfinity)
            {
                Logger.LogWarning($"[INFINITY RESCUE] {player.NickName} is at infinity coordinates! Rescuing...");

                // Get their last known safe coordinates
                Vector3 lastKnownPosition = Vector3.zero; // Default to origin
                if (_playerLastKnownCoordinates.TryGetValue(player.ActorNumber, out Vector3 lastPos))
                {
                    lastKnownPosition = lastPos;
                }

                // Teleport them back to their last known position
                character.transform.position = lastKnownPosition;
                
                // Also use the game's warp system to ensure proper sync
                try
                {
                    var warpMethod = character.GetType().GetMethod("WarpPlayerRPC", 
                        new Type[] { typeof(Vector3), typeof(bool) });
                    if (warpMethod != null)
                    {
                        warpMethod.Invoke(character, new object[] { lastKnownPosition, true });
                    }
                    else
                    {
                        // Fallback: just set position directly
                        Logger.LogWarning($"[WARP FALLBACK] WarpPlayerRPC method not found, using direct position set for {player.NickName}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[WARP ERROR] Failed to call WarpPlayerRPC for {player.NickName}: {ex.Message}");
                }

                Logger.LogInfo($"[INFINITY RESCUE] Rescued {player.NickName} from infinity coordinates to {lastKnownPosition}");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}was rescued from infinity coordinates!</color>", false, false, true);
            }
        }

        // Update player's last known coordinates (called from other systems)
        public static void UpdatePlayerCoordinates(int actorNumber, Vector3 position)
        {
            _playerLastKnownCoordinates[actorNumber] = position;
            _playerCoordinateTimestamps[actorNumber] = DateTime.Now;
        }

        // Track item held by blocked player to prevent duplication
        public static void TrackBlockedPlayerItem(int actorNumber, GameObject item)
        {
            if (item != null)
            {
                _blockedPlayerHeldItems[actorNumber] = item;
                Logger.LogInfo($"[ITEM TRACKING] Tracking item for blocked player {actorNumber}: {item.name}");
            }
        }

        // Clean up items when player is unblocked
        public static void CleanupBlockedPlayerItems(int actorNumber)
        {
            if (_blockedPlayerHeldItems.TryGetValue(actorNumber, out GameObject trackedItem))
            {
                if (trackedItem != null)
                {
                    Logger.LogInfo($"[ITEM CLEANUP] Destroying tracked item for unblocked player {actorNumber}: {trackedItem.name}");
                    
                    // Destroy the tracked item to prevent duplication
                    UnityEngine.Object.Destroy(trackedItem);
                }
                
                _blockedPlayerHeldItems.Remove(actorNumber);
            }
        }

        public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
            _knownPlayerIdentities.RemoveAll(p => p.ActorNumber == otherPlayer.ActorNumber);
            _playerLastHeldItems.Remove(otherPlayer.ActorNumber);
            _recentlySpawnedPlayers.Remove(otherPlayer.ActorNumber);
            _playerLastKnownCoordinates.Remove(otherPlayer.ActorNumber);
            _playerCoordinateTimestamps.Remove(otherPlayer.ActorNumber);
            _blockedPlayerHeldItems.Remove(otherPlayer.ActorNumber);
            _detectedCheatModUsers.Remove(otherPlayer.ActorNumber);
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

        public void OnJoinedRoom()
        {
            StartCoroutine(SendAntiCheatPingDelayed());

            // Add all current players to the player manager
            foreach (var player in PhotonNetwork.CurrentRoom.Players.Values)
            {
                PlayerManager.AddPlayer(player);
            }

            // --- CHEAT MOD DETECTION ---
            try
            {
                // Only run if not master client
                if (!PhotonNetwork.IsMasterClient)
                {
                    var detectedMods = new List<string>();
                    
                    // Check for Atlas and Cherry (trigger banana slip)
                    bool hasAtlas = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("synq.peak.atlas");
                    bool hasCherry = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("Cherry.Cherry");

                    if (hasAtlas || hasCherry)
                    {
                        string atlasCherryMods = "";
                        if (hasAtlas && hasCherry)
                            atlasCherryMods = "Atlas and Cherry";
                        else if (hasAtlas)
                            atlasCherryMods = "Atlas";
                        else
                            atlasCherryMods = "Cherry";

                        Logger.LogWarning($"[AntiCheat] {atlasCherryMods} detected on client, will send unauthorized banana slip RPC to trigger detection!");
                        SendBananaSlipRpcToMaster();
                    }

                    // Check for other cheat mods (visual warning only)
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.quackandcheese.ItemSpawner"))
                        detectedMods.Add("ItemSpawner mod");
                    
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("Flymod"))
                        detectedMods.Add("Fly mod");
                    
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.lamia.flymod"))
                        detectedMods.Add("Fly mod");
                    
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.mathis.infinitestamina"))
                        detectedMods.Add("Infinite Stamina mod");
                    
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("Everything"))
                        detectedMods.Add("Miscellaneous Cheat mod");
                    
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("BingBongMod"))
                        detectedMods.Add("Miscellaneous Cheat mod");
                    
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("UIOpener"))
                        detectedMods.Add("Console Unlocker mod");
                    
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.keklick1337.peak.advancedconsole"))
                        detectedMods.Add("Advanced Console mod");

                    // Send visual warnings to master for detected mods
                    if (detectedMods.Count > 0)
                    {
                        foreach (var mod in detectedMods)
                        {
                            PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.CheatModDetected, 
                                new object[] { PhotonNetwork.LocalPlayer.NickName, mod },
                                new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, 
                                SendOptions.SendReliable);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[AntiCheat] Error checking for cheat mods: {ex}");
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
                var whitelist = BlockingManager.GetWhitelistedSteamIDs().Select(id => (long)id).ToArray();

                object[] syncData = { blockList, detectionSettings, whitelist };
                RaiseEventOptions opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
                PhotonNetwork.RaiseEvent((byte)AntiCheatNetEvent.SyncResponse, syncData, opts, SendOptions.SendReliable);
            }
        }

        private IEnumerator SendUnauthorizedBananaSlipToMaster()
        {
            // Wait for characters to spawn
            yield return new WaitForSeconds(3f);

            // Find the master client's character
            Character masterCharacter = null;
            var allCharacters = UnityEngine.Object.FindObjectsOfType<Character>();

            foreach (var character in allCharacters)
            {
                var pv = character.GetComponent<PhotonView>();
                if (pv != null && pv.Owner != null && pv.Owner.IsMasterClient)
                {
                    masterCharacter = character;
                    break;
                }
            }

            if (masterCharacter == null)
            {
                Logger.LogWarning("[AntiCheat] Could not find master client's character for cheat mod detection");
                yield break;
            }

            var masterPV = masterCharacter.GetComponent<PhotonView>();
            if (masterPV == null)
            {
                Logger.LogWarning("[AntiCheat] Master client's character has no PhotonView");
                yield break;
            }

            // Find or spawn a banana peel (like cheaters do)
            BananaPeel bananaPeel = UnityEngine.Object.FindFirstObjectByType<BananaPeel>();
            if (bananaPeel == null)
            {
                // Spawn a banana peel at the master's position
                bananaPeel = PhotonNetwork.Instantiate("0_Items/Berrynana Peel Pink Variant", masterCharacter.Head, Quaternion.identity, 0, null).GetComponent<BananaPeel>();
                Logger.LogWarning("[AntiCheat] Spawned banana peel for cheat detection");
            }

            Logger.LogWarning($"[AntiCheat] Cheat mod detected - sending unauthorized banana slip RPC to banana peel (ViewID: {bananaPeel.GetComponent<PhotonView>().ViewID})");

            // Send banana slip RPC to trigger detection (like cheaters do)
            bananaPeel.GetComponent<PhotonView>().RPC("RPCA_TriggerBanana", RpcTarget.All, new object[] { masterPV.ViewID });

            Logger.LogWarning("[AntiCheat] Unauthorized banana slip RPC sent - should trigger detection on master client");
        }

        private void SendBananaSlipRpcToMaster()
        {
            // Start coroutine to wait for characters to spawn
            StartCoroutine(SendUnauthorizedBananaSlipToMaster());
        }

        private IEnumerator SendAntiCheatPingDelayed()
        {
            Logger.LogInfo($"[AntiCheat] SendAntiCheatPingDelayed started - waiting 1 second...");
            yield return new WaitForSeconds(1f);
            Logger.LogInfo($"[AntiCheat] SendAntiCheatPingDelayed finished waiting - calling SendAntiCheatPing");
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

            Logger.LogInfo($"[AntiCheat] About to send ping event with code {ANTICHEAT_PING_EVENT}");
            bool success = PhotonNetwork.RaiseEvent(ANTICHEAT_PING_EVENT, pingData, opts, SendOptions.SendReliable);
            Logger.LogInfo($"[AntiCheat] Sent anticheat detection ping to other players - Success: {success}");
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

            // Handle anticheat ping/pong events
            if (photonEvent.Code == ANTICHEAT_PING_EVENT)
            {
                HandleAntiCheatPing(photonEvent);
                return;
            }

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
                case AntiCheatNetEvent.CheatModDetected:
                    if (PhotonNetwork.IsMasterClient)
                        HandleCheatModDetected(photonEvent);
                    break;
            }
        }

        // Handle anticheat ping/pong events
        private void HandleAntiCheatPing(EventData photonEvent)
        {
            Logger.LogInfo($"[AntiCheat] HandleAntiCheatPing called - Event from actor {photonEvent.Sender}");
            if (photonEvent.CustomData is object[] pingData && pingData.Length >= 3)
            {
                string senderName = (string)pingData[0];
                string senderUserId = (string)pingData[1];
                string senderVersion = (string)pingData[2];

                var sender = PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender);
                if (sender != null)
                {
                    // Store that this player has anticheat
                    _anticheatUsers[sender.ActorNumber] = senderVersion;
                    
                    // Log visually for all clients (not just master)
                    LogVisually($"{{joinedColor}}{sender.NickName}</color> {{leftColor}}has anticheat installed (v{senderVersion})</color>", false, true, false, true);
                    
                    Logger.LogInfo($"[AntiCheat] Received ping from {sender.NickName} (v{senderVersion})");
                }
            }
            else
            {
                Logger.LogWarning($"[AntiCheat] Invalid ping data received from actor {photonEvent.Sender}");
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
            // SECURITY: Only accept sync responses from master client
            if (photonEvent.Sender != PhotonNetwork.MasterClient.ActorNumber)
            {
                Logger.LogWarning($"[SECURITY] Blocked fake sync response from non-master client (Actor #{photonEvent.Sender})");
                return;
            }

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
                BlockPlayer(player, reason, type);
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
            // SECURITY: Only accept block/unblock events from master client
            if (photonEvent.Sender != PhotonNetwork.MasterClient.ActorNumber)
            {
                Logger.LogWarning($"[SECURITY] Blocked fake block/unblock event from non-master client (Actor #{photonEvent.Sender})");
                return;
            }

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
            // SECURITY: Only accept detection settings from master client
            if (photonEvent.Sender != PhotonNetwork.MasterClient.ActorNumber)
            {
                Logger.LogWarning($"[SECURITY] Blocked fake detection settings event from non-master client (Actor #{photonEvent.Sender})");
                return;
            }

            object[] settings = (object[])photonEvent.CustomData;
            DetectionManager.DeserializeSettings(settings);
        }
        private void HandleCheatModDetected(EventData photonEvent)
        {
            if (photonEvent.CustomData is object[] data && data.Length >= 2)
            {
                string playerName = (string)data[0];
                string modName = (string)data[1];
                
                LogVisually($"{{userColor}}{playerName}</color> {{leftColor}}has {modName} - no action taken</color>", false, false, true);
                Logger.LogWarning($"[AntiCheat] {playerName} has {modName} - no action taken");
            }
        }

        private void HandleWhitelistUpdate(EventData photonEvent)
        {
            // SECURITY: Only accept whitelist updates from master client
            if (photonEvent.Sender != PhotonNetwork.MasterClient.ActorNumber)
            {
                Logger.LogWarning($"[SECURITY] Blocked fake whitelist event from non-master client (Actor #{photonEvent.Sender})");
                return;
            }

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