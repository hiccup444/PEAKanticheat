using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using Photon.Realtime;
using Steamworks;
using System;
using System.Linq;
using Photon.Pun;
using BepInEx.Configuration;

namespace AntiCheatMod
{
    public class AntiCheatUI : MonoBehaviour, IInRoomCallbacks
    {
        private bool _uiVisible = false;
        private static KeyCode _toggleKey = KeyCode.F2; // Default to F2
        private int _updateCounter = 0;
        private static Rect _windowRect = new Rect(200, 200, 1280, 600);
        
        // UI state
        private Vector2 _scrollPosition = Vector2.zero;
        private Dictionary<string, int> _groupSliders = new Dictionary<string, int>(); // For Mod Detection and Spoofed Names
        private Dictionary<DetectionType, int> _individualSliders = new Dictionary<DetectionType, int>(); // For all other detections
        private List<PlayerInfo> _playerList = new List<PlayerInfo>();
        private List<BlockEntry> _blockedPlayers = new List<BlockEntry>();
        private bool _autoKickBlockedPlayers = false;
        private bool _autoBlockNoAnticheat = false;
        private bool _advancedModDetection = false;
        
        // Secondary window state
        private bool _secondaryWindowVisible = false;
        private static Rect _secondaryWindowRect = new Rect(400, 300, 400, 300);
        private PlayerInfo _selectedPlayer = null;
        private Vector2 _secondaryWindowScrollPosition = Vector2.zero;

        // Config entries for saving UI settings
        private static ConfigEntry<string> _uiGroupSlidersConfig;
        private static ConfigEntry<string> _uiIndividualSlidersConfig;
        private static ConfigEntry<bool> _uiAutoKickConfig;
        private static ConfigEntry<bool> _uiAutoBlockNoAnticheatConfig;
        private static ConfigEntry<string> _uiWindowPositionConfig;
        private static ConfigEntry<string> _uiLanguageConfig;
        private static ConfigEntry<bool> _uiAdvancedModDetectionConfig;

        private void Start()
        {
            // Initialize config entries for UI settings
            InitializeConfigEntries();
            
            // Initialize translation system with language config
            TranslationManager.Initialize(_uiLanguageConfig);
            
            // Initialize group sliders with defaults (will be overridden by loaded settings)
            _groupSliders["ModDetection"] = 2; // Default to Block
            _groupSliders["SpoofedNames"] = 2; // Default to Block
            
            // Initialize auto-kick setting (will be overridden by loaded settings)
            _autoKickBlockedPlayers = AntiCheatPlugin.AutoKickBlockedPlayers;
            _autoBlockNoAnticheat = AntiCheatPlugin.AutoBlockNoAnticheat;

            // Initialize individual sliders with defaults (will be overridden by loaded settings)
            foreach (DetectionType type in System.Enum.GetValues(typeof(DetectionType)))
            {
                if (type == DetectionType.CherryMod || type == DetectionType.AtlasMod ||
                    type == DetectionType.SteamNameMismatch || type == DetectionType.NameImpersonation || type == DetectionType.MidGameNameChange)
                    continue;
                _individualSliders[type] = 2; // Default to Block
            }
            
            // Load saved UI settings (this will override the defaults)
            LoadUISettings();

            // Subscribe to events
            DetectionManager.OnDetectionSettingsChanged += OnDetectionSettingsChanged;
            DetectionManager.OnDetectionOccurred += OnDetectionOccurred;
            PlayerManager.OnPlayerAdded += OnPlayerAdded;
            PlayerManager.OnPlayerRemoved += OnPlayerRemoved;
            PlayerManager.OnPlayerStatusChanged += OnPlayerStatusChanged;
            BlockingManager.OnPlayerBlocked += OnPlayerBlocked;
            BlockingManager.OnPlayerUnblocked += OnPlayerUnblocked;
            AntiCheatEvents.OnPlayerKicked += OnPlayerKicked;

            // Set the keybind from config if available
            if (AntiCheatPlugin.UIToggleKey != null)
            {
                _toggleKey = AntiCheatPlugin.UIToggleKey.Value.MainKey;
            }
        }

        private void InitializeConfigEntries()
        {
            // Create config entries for UI settings
            _uiGroupSlidersConfig = AntiCheatPlugin.Config.Bind("UI", "GroupSliders", "", "Saved group slider settings");
            _uiIndividualSlidersConfig = AntiCheatPlugin.Config.Bind("UI", "IndividualSliders", "", "Saved individual slider settings");
            _uiAutoKickConfig = AntiCheatPlugin.Config.Bind("UI", "AutoKickBlockedPlayers", false, "Auto-kick blocked players setting");
            _uiAutoBlockNoAnticheatConfig = AntiCheatPlugin.Config.Bind("UI", "AutoBlockNoAnticheat", false, "Auto-block no anticheat setting");
            _uiWindowPositionConfig = AntiCheatPlugin.Config.Bind("UI", "WindowPosition", "200,200,1240,600", "UI window position and size");
            _uiLanguageConfig = AntiCheatPlugin.Config.Bind("UI", "Language", "en", "Language code for UI translations (e.g., 'en' for English, 'es' for Spanish, 'fr' for French).");
            _uiAdvancedModDetectionConfig = AntiCheatPlugin.Config.Bind("UI", "AdvancedModDetection", false, "Enable advanced mod detection features (clickable player names)");
        }

        private void LoadUISettings()
        {
            try
            {
                // Load group sliders
                string groupSlidersStr = _uiGroupSlidersConfig.Value;
                if (!string.IsNullOrEmpty(groupSlidersStr))
                {
                    var groupSliders = ParseSliderDictionary(groupSlidersStr);
                    foreach (var kvp in groupSliders)
                    {
                        _groupSliders[kvp.Key] = kvp.Value;
                    }
                }

                // Load individual sliders
                string individualSlidersStr = _uiIndividualSlidersConfig.Value;
                if (!string.IsNullOrEmpty(individualSlidersStr))
                {
                    var individualSliders = ParseSliderDictionary(individualSlidersStr);
                    foreach (var kvp in individualSliders)
                    {
                        if (Enum.TryParse<DetectionType>(kvp.Key, out DetectionType type))
                        {
                            _individualSliders[type] = kvp.Value;
                        }
                    }
                }

                // Load auto-kick setting
                _autoKickBlockedPlayers = _uiAutoKickConfig.Value;
                AntiCheatPlugin.AutoKickBlockedPlayers = _autoKickBlockedPlayers;

                // Load auto-block no anticheat setting
                _autoBlockNoAnticheat = _uiAutoBlockNoAnticheatConfig.Value;
                AntiCheatPlugin.AutoBlockNoAnticheat = _autoBlockNoAnticheat;

                // Load advanced mod detection setting
                _advancedModDetection = _uiAdvancedModDetectionConfig.Value;

                // Load window position
                string windowPosStr = _uiWindowPositionConfig.Value;
                if (!string.IsNullOrEmpty(windowPosStr))
                {
                    var parts = windowPosStr.Split(',');
                    if (parts.Length == 4 && float.TryParse(parts[0], out float x) &&
                        float.TryParse(parts[1], out float y) && float.TryParse(parts[2], out float width) &&
                        float.TryParse(parts[3], out float height))
                    {
                        _windowRect = new Rect(x, y, width, height);
                    }
                }
                
                // Update translation manager with current language config
                TranslationManager.UpdateLanguageConfig(_uiLanguageConfig);
                
                // Apply loaded settings to DetectionManager
                ApplyLoadedSettings();
                
                // Update UI slider values to match loaded settings
                UpdateUISlidersFromLoadedSettings();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AntiCheatUI] Error loading UI settings: {ex.Message}");
            }
        }

        private void ApplyLoadedSettings()
        {
            // Apply group slider settings
            foreach (var kvp in _groupSliders)
            {
                bool isEnabled = kvp.Value > 0;
                bool autoBlock = kvp.Value == 2;
                var settings = new DetectionSettings(isEnabled, autoBlock, true, true);
                
                switch (kvp.Key)
                {
                    case "ModDetection":
                        DetectionManager.SetDetectionSettings(DetectionType.CherryMod, settings);
                        DetectionManager.SetDetectionSettings(DetectionType.AtlasMod, settings);
                        break;
                    case "SpoofedNames":
                        DetectionManager.SetDetectionSettings(DetectionType.SteamNameMismatch, settings);
                        DetectionManager.SetDetectionSettings(DetectionType.NameImpersonation, settings);
                        DetectionManager.SetDetectionSettings(DetectionType.MidGameNameChange, settings);
                        break;
                }
            }

            // Apply individual slider settings
            foreach (var kvp in _individualSliders)
            {
                bool isEnabled = kvp.Value > 0;
                bool autoBlock = kvp.Value == 2;
                var settings = new DetectionSettings(isEnabled, autoBlock, true, true);
                DetectionManager.SetDetectionSettings(kvp.Key, settings);
            }
        }

        private void UpdateUISlidersFromLoadedSettings()
        {
            // Update group sliders based on loaded DetectionManager settings
            bool cherryEnabled = DetectionManager.IsDetectionEnabled(DetectionType.CherryMod);
            bool cherryAutoBlock = DetectionManager.ShouldAutoBlock(DetectionType.CherryMod);
            bool atlasEnabled = DetectionManager.IsDetectionEnabled(DetectionType.AtlasMod);
            bool atlasAutoBlock = DetectionManager.ShouldAutoBlock(DetectionType.AtlasMod);
            
            if (cherryEnabled && cherryAutoBlock && atlasEnabled && atlasAutoBlock)
            {
                _groupSliders["ModDetection"] = 2; // Block
            }
            else if (cherryEnabled || atlasEnabled)
            {
                _groupSliders["ModDetection"] = 1; // Warn
            }
            else
            {
                _groupSliders["ModDetection"] = 0; // Off
            }
            
            bool steamNameEnabled = DetectionManager.IsDetectionEnabled(DetectionType.SteamNameMismatch);
            bool steamNameAutoBlock = DetectionManager.ShouldAutoBlock(DetectionType.SteamNameMismatch);
            bool nameImpersonationEnabled = DetectionManager.IsDetectionEnabled(DetectionType.NameImpersonation);
            bool nameImpersonationAutoBlock = DetectionManager.ShouldAutoBlock(DetectionType.NameImpersonation);
            bool midGameNameEnabled = DetectionManager.IsDetectionEnabled(DetectionType.MidGameNameChange);
            bool midGameNameAutoBlock = DetectionManager.ShouldAutoBlock(DetectionType.MidGameNameChange);
            
            if (steamNameEnabled && steamNameAutoBlock && nameImpersonationEnabled && nameImpersonationAutoBlock && midGameNameEnabled && midGameNameAutoBlock)
            {
                _groupSliders["SpoofedNames"] = 2; // Block
            }
            else if (steamNameEnabled || nameImpersonationEnabled || midGameNameEnabled)
            {
                _groupSliders["SpoofedNames"] = 1; // Warn
            }
            else
            {
                _groupSliders["SpoofedNames"] = 0; // Off
            }
            
            // Update individual sliders based on loaded DetectionManager settings
            foreach (DetectionType type in System.Enum.GetValues(typeof(DetectionType)))
            {
                if (type == DetectionType.CherryMod || type == DetectionType.AtlasMod ||
                    type == DetectionType.SteamNameMismatch || type == DetectionType.NameImpersonation || type == DetectionType.MidGameNameChange)
                    continue;
                
                bool isEnabled = DetectionManager.IsDetectionEnabled(type);
                bool autoBlock = DetectionManager.ShouldAutoBlock(type);
                
                if (isEnabled && autoBlock)
                {
                    _individualSliders[type] = 2; // Block
                }
                else if (isEnabled)
                {
                    _individualSliders[type] = 1; // Warn
                }
                else
                {
                    _individualSliders[type] = 0; // Off
                }
            }
        }

        private void SaveUISettings()
        {
            try
            {
                // Save group sliders
                string groupSlidersStr = SerializeSliderDictionary(_groupSliders);
                _uiGroupSlidersConfig.Value = groupSlidersStr;

                // Save individual sliders
                var individualSlidersStr = SerializeSliderDictionary(_individualSliders.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value));
                _uiIndividualSlidersConfig.Value = individualSlidersStr;

                // Save auto-kick setting
                _uiAutoKickConfig.Value = _autoKickBlockedPlayers;

                // Save auto-block no anticheat setting
                _uiAutoBlockNoAnticheatConfig.Value = _autoBlockNoAnticheat;

                // Save window position
                string windowPosStr = $"{_windowRect.x},{_windowRect.y},{_windowRect.width},{_windowRect.height}";
                _uiWindowPositionConfig.Value = windowPosStr;

                // Save the config file
                AntiCheatPlugin.Config.Save();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AntiCheatUI] Error saving UI settings: {ex.Message}");
            }
        }

        private string SerializeSliderDictionary(Dictionary<string, int> dict)
        {
            return string.Join(";", dict.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }

        private Dictionary<string, int> ParseSliderDictionary(string str)
        {
            var dict = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(str)) return dict;

            var pairs = str.Split(';');
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=');
                if (parts.Length == 2 && int.TryParse(parts[1], out int value))
                {
                    dict[parts[0]] = value;
                }
            }
            return dict;
        }

        private void OnEnable()
        {
            // Subscribe to master client switch events
            PhotonNetwork.AddCallbackTarget(this);
        }

        private void OnDisable()
        {
            // Save UI settings when disabled
            SaveUISettings();
            
            // Unsubscribe from master client switch events
            PhotonNetwork.RemoveCallbackTarget(this);
        }

        // Handle master client switches
        public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
        {
            // If we became the master client, ensure UI is available
            if (newMasterClient.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                // The UI will automatically become available on next Update() due to PhotonNetwork.IsMasterClient check
            }
            else
            {
                // Check if this might be a theft that we're about to take back
                var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();
                if (lobbyHandler != null && lobbyHandler.InSteamLobby(out CSteamID currentLobby))
                {
                    CSteamID lobbyOwner = SteamMatchmaking.GetLobbyOwner(currentLobby);
                    
                    // If we're the lobby owner (original master), don't hide UI yet - we might take it back
                    if (lobbyOwner == SteamUser.GetSteamID())
                    {
                        return; // Don't hide UI yet
                    }
                }
                
                // Hide UI if we're no longer master client
                _uiVisible = false;
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        // Called when we successfully take master client back from a thief
        public void OnMasterClientRecovered()
        {
            // UI will automatically become available on next Update() due to PhotonNetwork.IsMasterClient check
        }

        // Required IInRoomCallbacks interface methods
        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer) { }
        public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer) { }
        public void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged) { }
        public void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps) { }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey))
            {
                ToggleUI();
            }
        }

        private void ToggleUI()
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            _uiVisible = !_uiVisible;
            
            if (_uiVisible)
            {
                // Show cursor when UI opens
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                RefreshPlayerList();
                RefreshBlockedPlayersList();
            }
            else
            {
                // Hide cursor when UI closes
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
                // Close secondary window when main UI closes
                _secondaryWindowVisible = false;
                _selectedPlayer = null;
            }
        }

        private void OnGUI()
        {
            if (!_uiVisible || !PhotonNetwork.IsMasterClient)
                return;

            // Always render the secondary window first (invisible when not needed)
            if (_selectedPlayer != null)
            {
                _secondaryWindowRect = GUI.Window(999, _secondaryWindowRect, DrawSecondaryWindow, $"Player: {_selectedPlayer.PhotonName}");
            }
            
            // Create the main window last (drawn on top)
            _windowRect = GUI.Window(0, _windowRect, DrawWindow, $"{TranslationManager.GetTranslation("UI_WINDOW_TITLE")} [{_toggleKey}]");
        }

        private void DrawWindow(int windowID)
        {
            GUILayout.BeginHorizontal();

            // --- Left: Detections ---
            GUILayout.BeginVertical(GUILayout.Width(360));
            GUILayout.Box(TranslationManager.GetTranslation("UI_DETECTIONS"), GUILayout.Width(360), GUILayout.Height(30));
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(420)); // Reduced height to make room for buttons

            // Mod Detection (Cherry + Atlas)
            DrawGroupSlider("ModDetection", TranslationManager.GetTranslation("DETECTION_CHEAT_DETECTION"));
            // Spoofed Names (all name spoofing)
            DrawGroupSlider("SpoofedNames", TranslationManager.GetTranslation("DETECTION_SPOOFED_NAMES"));

            // All other detections as individual sliders
            foreach (DetectionType type in System.Enum.GetValues(typeof(DetectionType)))
            {
                if (type == DetectionType.CherryMod || type == DetectionType.AtlasMod ||
                    type == DetectionType.SteamNameMismatch || type == DetectionType.NameImpersonation || type == DetectionType.MidGameNameChange)
                    continue;
                DrawDetectionSlider(type, GetDetectionLabel(type));
            }

            GUILayout.EndScrollView();

            // Auto Kick checkbox
            GUILayout.BeginHorizontal();
            bool newAutoKickValue = GUILayout.Toggle(_autoKickBlockedPlayers, TranslationManager.GetTranslation("SETTING_AUTOKICK_BLOCKED_PLAYERS"));
            if (newAutoKickValue != _autoKickBlockedPlayers)
            {
                _autoKickBlockedPlayers = newAutoKickValue;
                AntiCheatPlugin.AutoKickBlockedPlayers = _autoKickBlockedPlayers;
                SaveUISettings(); // Save the new setting
            }
            GUILayout.EndHorizontal();

            // Auto Block No Anticheat checkbox
            GUILayout.BeginHorizontal();
            bool newAutoBlockNoAnticheatValue = GUILayout.Toggle(_autoBlockNoAnticheat, TranslationManager.GetTranslation("SETTING_AUTOBLOCK_NO_ANTICHEAT"));
            if (newAutoBlockNoAnticheatValue != _autoBlockNoAnticheat)
            {
                _autoBlockNoAnticheat = newAutoBlockNoAnticheatValue;
                AntiCheatPlugin.AutoBlockNoAnticheat = _autoBlockNoAnticheat;
                SaveUISettings(); // Save the new setting
            }
            GUILayout.EndHorizontal();

            // Advanced Mod Detection checkbox
            GUILayout.BeginHorizontal();
            bool newAdvancedModDetectionValue = GUILayout.Toggle(_advancedModDetection, TranslationManager.GetTranslation("SETTING_ADVANCED_MOD_DETECTION"));
            if (newAdvancedModDetectionValue != _advancedModDetection)
            {
                _advancedModDetection = newAdvancedModDetectionValue;
                SaveUISettings(); // Save the new setting
            }
            GUILayout.EndHorizontal();

            // Add the three buttons
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(TranslationManager.GetTranslation("BUTTON_ALL_BLOCK"), GUILayout.Width(100)))
            {
                SetAllSliders(2);
            }
            if (GUILayout.Button(TranslationManager.GetTranslation("BUTTON_ALL_WARN"), GUILayout.Width(100)))
            {
                SetAllSliders(1);
            }
            if (GUILayout.Button(TranslationManager.GetTranslation("BUTTON_ALL_OFF"), GUILayout.Width(100)))
            {
                SetAllSliders(0);
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            // --- Divider ---
            GUILayout.Box("", GUILayout.Width(2), GUILayout.ExpandHeight(true));

            // --- Middle: Player List ---
            GUILayout.BeginVertical(GUILayout.Width(440)); // Increased from 360 to 440
            GUILayout.Box(TranslationManager.GetTranslation("UI_PLAYERS"), GUILayout.Width(440), GUILayout.Height(30));
            var playerScroll = GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(500));
            foreach (var player in _playerList)
            {
                GUILayout.BeginHorizontal();
                string displayText;
                if (player.Status == PlayerStatus.MasterClient)
                {
                    displayText = $"{player.PhotonName} ({TranslationManager.GetTranslation("PLAYER_MASTER_CLIENT")})";
                }
                else
                {
                    displayText = $"{player.PhotonName} {TranslationManager.GetTranslation("PLAYER_ACTOR_NUMBER", player.ActorNumber)}";
                }
                // Make player name clickable only if advanced mod detection is enabled
                if (_advancedModDetection)
                {
                    if (GUILayout.Button(displayText, GUILayout.Width(280)))
                    {
                        Debug.Log($"[AntiCheatUI] Player button clicked: {player.PhotonName} (Actor: {player.ActorNumber})");
                        Debug.Log($"[AntiCheatUI] Current selected player: {(_selectedPlayer?.PhotonName ?? "null")} (Actor: {_selectedPlayer?.ActorNumber ?? -1})");
                        Debug.Log($"[AntiCheatUI] Secondary window visible: {_secondaryWindowVisible}");
                        
                        // If clicking the same player, re-instance the window (force on top)
                        if (_selectedPlayer != null && _selectedPlayer.ActorNumber == player.ActorNumber)
                        {
                            Debug.Log($"[AntiCheatUI] Same player clicked - re-instancing window");
                            _secondaryWindowVisible = false; // Close first
                            _selectedPlayer = null; // Clear the player to force complete re-instance
                            StartCoroutine(ReopenWindowAfterFrame(player)); // Re-open after a frame
                            Debug.Log($"[AntiCheatUI] Window closed, will reopen after frame");
                        }
                        else
                        {
                            Debug.Log($"[AntiCheatUI] Different player clicked - switching windows");
                            // If clicking a different player, close current and open new
                            _secondaryWindowVisible = false; // Close current window
                            _selectedPlayer = null; // Clear current player
                            StartCoroutine(ReopenWindowAfterFrame(player)); // Re-open with new player
                            Debug.Log($"[AntiCheatUI] Window closed, will reopen with new player after frame");
                        }
                    }
                }
                else
                {
                    GUILayout.Label(displayText, GUILayout.Width(280));
                }
                bool isBlocked = BlockingManager.IsBlocked(player.ActorNumber);
                string buttonText = isBlocked ? TranslationManager.GetTranslation("BUTTON_UNBLOCK") : TranslationManager.GetTranslation("BUTTON_BLOCK");
                if (GUILayout.Button(buttonText, GUILayout.Width(60)))
                {
                    if (isBlocked)
                    {
                        BlockingManager.UnblockPlayer(player.ActorNumber);
                        AntiCheatPlugin.BroadcastBlockListUpdate(player.ActorNumber, false);
                    }
                    else
                    {
                        var photonPlayer = PhotonNetwork.CurrentRoom?.GetPlayer(player.ActorNumber);
                        if (photonPlayer != null)
                        {
                            BlockingManager.BlockPlayer(photonPlayer, TranslationManager.GetTranslation("MESSAGE_MANUAL_BLOCK"), BlockReason.Manual);
                            AntiCheatPlugin.BroadcastBlockListUpdate(player.ActorNumber, true);
                        }
                    }
                }

                // Show Kick button only for players with anticheat installed (and not for master client)
                if (player.Status != PlayerStatus.MasterClient && AntiCheatPlugin.HasAnticheat(player.ActorNumber))
                {
                    if (GUILayout.Button(TranslationManager.GetTranslation("BUTTON_KICK"), GUILayout.Width(50)))
                    {
                        AntiCheatPlugin.KickPlayer(player.ActorNumber);
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            // --- Divider ---
            GUILayout.Box("", GUILayout.Width(2), GUILayout.ExpandHeight(true));

            // --- Right: Blocked Players ---
            GUILayout.BeginVertical(GUILayout.Width(380)); // Reduced from 420 to 380
            GUILayout.Box(TranslationManager.GetTranslation("UI_BLOCKED_PLAYERS"), GUILayout.Width(380), GUILayout.Height(30));
            var blockedScroll = GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(500));
            foreach (var blockEntry in _blockedPlayers)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{blockEntry.PlayerName} - {blockEntry.SpecificReason}", GUILayout.Width(300)); // Adjusted to fit new column width
                if (GUILayout.Button(TranslationManager.GetTranslation("BUTTON_UNBLOCK"), GUILayout.Width(60)))
                {
                    BlockingManager.UnblockPlayer(blockEntry.ActorNumber);
                    AntiCheatPlugin.BroadcastBlockListUpdate(blockEntry.ActorNumber, false);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            // Save window position when dragged
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
        }

        private void DrawSecondaryWindow(int windowID)
        {
            // Only show content if the window is supposed to be visible
            if (!_secondaryWindowVisible)
            {
                Debug.Log($"[AntiCheatUI] DrawSecondaryWindow called but window not visible. Selected player: {_selectedPlayer?.PhotonName ?? "null"}");
                return;
            }
            
            Debug.Log($"[AntiCheatUI] Drawing secondary window for player: {_selectedPlayer?.PhotonName ?? "null"}");

            // Draw solid background
            GUI.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.95f);
            GUI.Box(new Rect(0, 0, _secondaryWindowRect.width, _secondaryWindowRect.height), "");
            GUI.backgroundColor = Color.white;

            // Close button at top right
            if (GUI.Button(new Rect(_secondaryWindowRect.width - 25, 5, 20, 20), "X"))
            {
                _secondaryWindowVisible = false;
                _selectedPlayer = null;
                return;
            }

            // Content area
            GUILayout.BeginArea(new Rect(10, 30, _secondaryWindowRect.width - 20, _secondaryWindowRect.height - 40));
            
            GUILayout.Label($"Player: {_selectedPlayer.PhotonName}");
            GUILayout.Label($"Actor Number: {_selectedPlayer.ActorNumber}");
            GUILayout.Label($"Status: {_selectedPlayer.Status}");
            
            // Check if player has anticheat installed
            if (AntiCheatPlugin.HasAnticheat(_selectedPlayer.ActorNumber))
            {
                // Check if player has opted out of mod sharing
                if (AntiCheatPlugin.HasPlayerOptedOutOfModSharing(_selectedPlayer.ActorNumber))
                {
                    GUILayout.Space(10);
                    GUILayout.Label(TranslationManager.GetTranslation("MESSAGE_OPTED_OUT", _selectedPlayer.PhotonName));
                }
                else
                {
                    GUILayout.Space(10);
                    GUILayout.Label(TranslationManager.GetTranslation("MESSAGE_MODS_TITLE"), GUILayout.Width(380));
                    
                    string[] mods = AntiCheatPlugin.GetPlayerModList(_selectedPlayer.ActorNumber);
                    if (mods.Length > 0)
                    {
                        _secondaryWindowScrollPosition = GUILayout.BeginScrollView(_secondaryWindowScrollPosition, GUILayout.Height(200));
                        foreach (string mod in mods)
                        {
                            GUILayout.Label($"• {mod}");
                        }
                        GUILayout.EndScrollView();
                    }
                    else
                    {
                        GUILayout.Label(TranslationManager.GetTranslation("MESSAGE_NO_MODS"));
                    }
                }
            }
            else
            {
                GUILayout.Space(10);
                GUILayout.Label(TranslationManager.GetTranslation("MESSAGE_NO_ANTICHEAT"));
            }
            
            GUILayout.EndArea();

            // Make window draggable
            GUI.DragWindow(new Rect(0, 0, _secondaryWindowRect.width, 20));
        }

        private void SetAllSliders(int value)
        {
            // Set all group sliders
            foreach (var key in _groupSliders.Keys.ToList())
            {
                _groupSliders[key] = value;
            }

            // Set all individual sliders
            foreach (var key in _individualSliders.Keys.ToList())
            {
                _individualSliders[key] = value;
            }

            // Apply the settings to DetectionManager
            bool isEnabled = value > 0;
            bool autoBlock = value == 2;
            var settings = new DetectionSettings(isEnabled, autoBlock, true, true);

            // Apply to all detection types
            foreach (DetectionType type in System.Enum.GetValues(typeof(DetectionType)))
            {
                DetectionManager.SetDetectionSettings(type, settings);
            }

            // Broadcast the settings update and save
            AntiCheatPlugin.BroadcastDetectionSettingsUpdate();
            SaveUISettings();
        }

        private void DrawGroupSlider(string groupKey, string label)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(140));
            int currentValue = _groupSliders.ContainsKey(groupKey) ? _groupSliders[groupKey] : 0;
            int newValue = (int)GUILayout.HorizontalSlider(currentValue, 0, 2, GUILayout.Width(80));
            if (newValue != currentValue)
            {
                _groupSliders[groupKey] = newValue;
                bool isEnabled = newValue > 0;
                bool autoBlock = newValue == 2;
                var settings = new DetectionSettings(isEnabled, autoBlock, true, true);
                switch (groupKey)
                {
                    case "ModDetection":
                        DetectionManager.SetDetectionSettings(DetectionType.CherryMod, settings);
                        DetectionManager.SetDetectionSettings(DetectionType.AtlasMod, settings);
                        break;
                    case "SpoofedNames":
                        DetectionManager.SetDetectionSettings(DetectionType.SteamNameMismatch, settings);
                        DetectionManager.SetDetectionSettings(DetectionType.NameImpersonation, settings);
                        DetectionManager.SetDetectionSettings(DetectionType.MidGameNameChange, settings);
                        break;
                }
                AntiCheatPlugin.BroadcastDetectionSettingsUpdate();
                SaveUISettings(); // Save the new setting
            }
            string settingText = newValue == 0 ? TranslationManager.GetTranslation("SETTING_OFF") : newValue == 1 ? TranslationManager.GetTranslation("SETTING_WARN") : TranslationManager.GetTranslation("SETTING_BLOCK");
            GUILayout.Label(settingText, GUILayout.Width(80));
            GUILayout.EndHorizontal();
        }

        private void DrawDetectionSlider(DetectionType type, string label)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(140));
            int currentValue = _individualSliders.ContainsKey(type) ? _individualSliders[type] : 0;
            int newValue = (int)GUILayout.HorizontalSlider(currentValue, 0, 2, GUILayout.Width(80));
            if (newValue != currentValue)
            {
                _individualSliders[type] = newValue;
                bool isEnabled = newValue > 0;
                bool autoBlock = newValue == 2;
                var settings = new DetectionSettings(isEnabled, autoBlock, true, true);
                DetectionManager.SetDetectionSettings(type, settings);
                AntiCheatPlugin.BroadcastDetectionSettingsUpdate();
                SaveUISettings(); // Save the new setting
            }
            string settingText = newValue == 0 ? TranslationManager.GetTranslation("SETTING_OFF") : newValue == 1 ? TranslationManager.GetTranslation("SETTING_WARN") : TranslationManager.GetTranslation("SETTING_BLOCK");
            GUILayout.Label(settingText, GUILayout.Width(80));
            GUILayout.EndHorizontal();
        }

        private string GetDetectionLabel(DetectionType type)
        {
            switch (type)
            {
                case DetectionType.OwnershipTheft: return TranslationManager.GetTranslation("DETECTION_OWNERSHIP_THEFT");
                case DetectionType.UnauthorizedDestroy: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_DESTROY");
                case DetectionType.RateLimitExceeded: return TranslationManager.GetTranslation("DETECTION_RATE_LIMIT_EXCEEDED");
                case DetectionType.UnauthorizedKill: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_KILL");
                case DetectionType.UnauthorizedRevive: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_REVIVE");
                case DetectionType.UnauthorizedWarp: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_WARP");
                case DetectionType.UnauthorizedStatusEffect: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_STATUS_EFFECT");
                case DetectionType.UnauthorizedMovement: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_MOVEMENT");
                case DetectionType.UnauthorizedEmote: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_EMOTE");
                case DetectionType.UnauthorizedItemDrop: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_ITEM_DROP");
                case DetectionType.UnauthorizedCampfireModification: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_CAMPFIRE");
                case DetectionType.UnauthorizedFlareLighting: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_FLARE");
                case DetectionType.UnauthorizedBananaSlip: return TranslationManager.GetTranslation("DETECTION_UNAUTHORIZED_BANANA");
                case DetectionType.MasterClientTheft: return TranslationManager.GetTranslation("DETECTION_MASTER_CLIENT_THEFT");
                case DetectionType.SteamIDSpoofing: return TranslationManager.GetTranslation("DETECTION_STEAM_ID_SPOOFING");
                case DetectionType.InfinityWarp: return TranslationManager.GetTranslation("DETECTION_INFINITY_WARP");
                default: return type.ToString();
            }
        }

        private void RefreshPlayerList()
        {
            _playerList = PlayerManager.GetAllPlayers().Where(p => !BlockingManager.IsBlocked(p.ActorNumber)).ToList();
        }

        private void RefreshBlockedPlayersList()
        {
            _blockedPlayers = BlockingManager.GetAllBlockedPlayers();
        }

        // Event handlers
        private void OnDetectionSettingsChanged(DetectionType type, DetectionSettings settings)
        {
            // This method is called when settings change from outside the UI
            // We don't need to update the UI sliders here since they're controlled by the user
            // The sliders will maintain their state until the user changes them
        }

        private void OnDetectionOccurred(DetectionResult result)
        {
            RefreshPlayerList();
        }

        private void OnPlayerAdded(PlayerInfo playerInfo)
        {
            if (_uiVisible)
            {
                RefreshPlayerList();
            }
        }

        private void OnPlayerRemoved(PlayerInfo playerInfo)
        {
            RefreshPlayerList();
            RefreshBlockedPlayersList();
        }

        private void OnPlayerStatusChanged(PlayerInfo playerInfo)
        {
            if (_uiVisible)
            {
                RefreshPlayerList();
                RefreshBlockedPlayersList();
            }
        }

        private void OnPlayerBlocked(BlockEntry blockEntry)
        {
            if (_uiVisible)
            {
                RefreshPlayerList();
                RefreshBlockedPlayersList();
            }
        }

        private void OnPlayerUnblocked(int actorNumber)
        {
            if (_uiVisible)
            {
                RefreshPlayerList();
                RefreshBlockedPlayersList();
            }
        }

        private void OnPlayerKicked(Photon.Realtime.Player player)
        {
            if (_uiVisible)
            {
                RefreshPlayerList();
                RefreshBlockedPlayersList();
            }
        }

        // Static method to set the keybind
        public static void SetKeybind(KeyCode key)
        {
            _toggleKey = key;
        }

        private void OnApplicationQuit()
        {
            SaveUISettings();
        }

        private System.Collections.IEnumerator ReopenWindowAfterFrame(PlayerInfo player)
        {
            yield return null; // Wait one frame
            _selectedPlayer = player;
            _secondaryWindowVisible = true;
            Debug.Log($"[AntiCheatUI] Window reopened after frame. Visible: {_secondaryWindowVisible}");
        }

        private void OnDestroy()
        {
            // Save UI settings before destroying
            SaveUISettings();
            
            // Unsubscribe from events
            DetectionManager.OnDetectionSettingsChanged -= OnDetectionSettingsChanged;
            DetectionManager.OnDetectionOccurred -= OnDetectionOccurred;
            PlayerManager.OnPlayerAdded -= OnPlayerAdded;
            PlayerManager.OnPlayerRemoved -= OnPlayerRemoved;
            PlayerManager.OnPlayerStatusChanged -= OnPlayerStatusChanged;
            BlockingManager.OnPlayerBlocked -= OnPlayerBlocked;
            BlockingManager.OnPlayerUnblocked -= OnPlayerUnblocked;
            AntiCheatEvents.OnPlayerKicked -= OnPlayerKicked;
        }
    }
}