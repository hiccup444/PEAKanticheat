using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using Photon.Realtime;
using Steamworks;
using System;
using System.Linq;
using Photon.Pun;

namespace AntiCheatMod
{
    public class AntiCheatUI : MonoBehaviour, IInRoomCallbacks
    {
        private bool _uiVisible = false;
        private const KeyCode TOGGLE_KEY = KeyCode.F1;
        private int _updateCounter = 0;
        private static Rect _windowRect = new Rect(200, 200, 900, 600);
        
        // UI state
        private Vector2 _scrollPosition = Vector2.zero;
        private Dictionary<string, int> _groupSliders = new Dictionary<string, int>(); // For Mod Detection and Spoofed Names
        private Dictionary<DetectionType, int> _individualSliders = new Dictionary<DetectionType, int>(); // For all other detections
        private List<PlayerInfo> _playerList = new List<PlayerInfo>();
        private List<BlockEntry> _blockedPlayers = new List<BlockEntry>();

        private void Start()
        {
            Debug.Log("[AntiCheatUI] Start method called");
            
            // Subscribe to events
            DetectionManager.OnDetectionSettingsChanged += OnDetectionSettingsChanged;
            DetectionManager.OnDetectionOccurred += OnDetectionOccurred;
            PlayerManager.OnPlayerAdded += OnPlayerAdded;
            PlayerManager.OnPlayerRemoved += OnPlayerRemoved;
            PlayerManager.OnPlayerStatusChanged += OnPlayerStatusChanged;
            BlockingManager.OnPlayerBlocked += OnPlayerBlocked;
            BlockingManager.OnPlayerUnblocked += OnPlayerUnblocked;

            // Initialize group sliders
            _groupSliders["ModDetection"] = 2; // Default to Block
            _groupSliders["SpoofedNames"] = 2; // Default to Block

            // Initialize individual sliders for all other detections
            foreach (DetectionType type in System.Enum.GetValues(typeof(DetectionType)))
            {
                if (type == DetectionType.CherryMod || type == DetectionType.AtlasMod ||
                    type == DetectionType.SteamNameMismatch || type == DetectionType.NameImpersonation || type == DetectionType.MidGameNameChange)
                    continue;
                _individualSliders[type] = DetectionManager.IsDetectionEnabled(type) ? 2 : 0;
            }

            Debug.Log($"[AntiCheatUI] UI initialized. GameObject name: {gameObject.name}, Active: {gameObject.activeInHierarchy}, Enabled: {enabled}");
        }

        private void OnEnable()
        {
            Debug.Log("[AntiCheatUI] OnEnable called");
            
            // Subscribe to master client switch events
            PhotonNetwork.AddCallbackTarget(this);
        }

        private void OnDisable()
        {
            Debug.Log("[AntiCheatUI] OnDisable called");
            
            // Unsubscribe from master client switch events
            PhotonNetwork.RemoveCallbackTarget(this);
        }

        // Handle master client switches
        public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
        {
            Debug.Log($"[AntiCheatUI] Master client switched to {newMasterClient.NickName}");
            
            // If we became the master client, ensure UI is available
            if (newMasterClient.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                Debug.Log("[AntiCheatUI] We are now the master client - UI should be available");
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
                        Debug.Log("[AntiCheatUI] We are the lobby owner - keeping UI available in case we take master client back");
                        return; // Don't hide UI yet
                    }
                }
                
                Debug.Log("[AntiCheatUI] We are no longer the master client - hiding UI");
                // Hide UI if we're no longer master client
                _uiVisible = false;
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        // Called when we successfully take master client back from a thief
        public void OnMasterClientRecovered()
        {
            Debug.Log("[AntiCheatUI] Master client recovered - UI should remain available");
            // UI will automatically become available on next Update() due to PhotonNetwork.IsMasterClient check
        }

        // Required IInRoomCallbacks interface methods
        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer) { }
        public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer) { }
        public void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged) { }
        public void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps) { }

        private void Update()
        {
            if (Input.GetKeyDown(TOGGLE_KEY))
            {
                Debug.Log($"[AntiCheatUI] F1 key pressed. IsMasterClient: {PhotonNetwork.IsMasterClient}, GameObject active: {gameObject.activeInHierarchy}");
                ToggleUI();
            }
        }

        private void ToggleUI()
        {
            Debug.Log($"[AntiCheatUI] ToggleUI called. IsMasterClient: {PhotonNetwork.IsMasterClient}");
            
            if (!PhotonNetwork.IsMasterClient)
            {
                Debug.Log("[AntiCheatUI] UI only available for master client");
                return;
            }

            _uiVisible = !_uiVisible;
            Debug.Log($"[AntiCheatUI] UI toggled to {_uiVisible}");
            
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
            }
        }

        private void OnGUI()
        {
            if (!_uiVisible || !PhotonNetwork.IsMasterClient)
                return;

            // Create the main window
            _windowRect = GUI.Window(0, _windowRect, DrawWindow, "PEAK AntiCheat [F1]");
        }

        private void DrawWindow(int windowID)
        {
            GUILayout.BeginHorizontal();

            // --- Left: Detections ---
            GUILayout.BeginVertical(GUILayout.Width(280));
            GUILayout.Label("Detections", GUI.skin.box, GUILayout.Height(30));
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(500));

            // Mod Detection (Cherry + Atlas)
            DrawGroupSlider("ModDetection", "Cheat Detection");
            // Spoofed Names (all name spoofing)
            DrawGroupSlider("SpoofedNames", "Spoofed Names");

            // All other detections as individual sliders
            foreach (DetectionType type in System.Enum.GetValues(typeof(DetectionType)))
            {
                if (type == DetectionType.CherryMod || type == DetectionType.AtlasMod ||
                    type == DetectionType.SteamNameMismatch || type == DetectionType.NameImpersonation || type == DetectionType.MidGameNameChange)
                    continue;
                DrawDetectionSlider(type, GetDetectionLabel(type));
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            // --- Divider ---
            GUILayout.Box("", GUILayout.Width(2), GUILayout.ExpandHeight(true));

            // --- Middle: Player List ---
            GUILayout.BeginVertical(GUILayout.Width(340));
            GUILayout.Label("Players", GUI.skin.box, GUILayout.Height(30));
            var playerScroll = GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(500));
            foreach (var player in _playerList)
            {
                GUILayout.BeginHorizontal();
                string displayText;
                if (player.Status == PlayerStatus.MasterClient)
                {
                    displayText = $"{player.PhotonName} (MasterClient)";
                }
                else
                {
                    displayText = $"{player.PhotonName} #{player.ActorNumber}";
                }
                GUILayout.Label(displayText, GUILayout.Width(250));
                bool isBlocked = BlockingManager.IsBlocked(player.ActorNumber);
                string buttonText = isBlocked ? "Unblock" : "Block";
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
                            BlockingManager.BlockPlayer(photonPlayer, "Manual block from UI", BlockReason.Manual);
                            AntiCheatPlugin.BroadcastBlockListUpdate(player.ActorNumber, true);
                        }
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            // --- Divider ---
            GUILayout.Box("", GUILayout.Width(2), GUILayout.ExpandHeight(true));

            // --- Right: Blocked Players ---
            GUILayout.BeginVertical(GUILayout.Width(270));
            GUILayout.Label("Blocked Players", GUI.skin.box, GUILayout.Height(30));
            var blockedScroll = GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(500));
            foreach (var blockEntry in _blockedPlayers)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{blockEntry.PlayerName} - {blockEntry.SpecificReason}", GUILayout.Width(200));
                if (GUILayout.Button("Unblock", GUILayout.Width(60)))
                {
                    BlockingManager.UnblockPlayer(blockEntry.ActorNumber);
                    AntiCheatPlugin.BroadcastBlockListUpdate(blockEntry.ActorNumber, false);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
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
            }
            string settingText = newValue == 0 ? "Off" : newValue == 1 ? "Warn" : "Block";
            GUILayout.Label(settingText, GUILayout.Width(40));
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
            }
            string settingText = newValue == 0 ? "Off" : newValue == 1 ? "Warn" : "Block";
            GUILayout.Label(settingText, GUILayout.Width(40));
            GUILayout.EndHorizontal();
        }

        private string GetDetectionLabel(DetectionType type)
        {
            switch (type)
            {
                case DetectionType.OwnershipTheft: return "Ownership Theft";
                case DetectionType.UnauthorizedDestroy: return "Unauthorized Destroy";
                case DetectionType.RateLimitExceeded: return "Rate Limit Exceeded";
                case DetectionType.UnauthorizedKill: return "Unauthorized Kill";
                case DetectionType.UnauthorizedRevive: return "Unauthorized Revive";
                case DetectionType.UnauthorizedWarp: return "Unauthorized Warp";
                case DetectionType.UnauthorizedStatusEffect: return "Unauthorized Status Effect";
                case DetectionType.UnauthorizedMovement: return "Unauthorized Movement";
                case DetectionType.UnauthorizedEmote: return "Unauthorized Emote";
                case DetectionType.UnauthorizedItemDrop: return "Unauthorized Item Drop";
                case DetectionType.UnauthorizedCampfireModification: return "Unauthorized Campfire";
                case DetectionType.UnauthorizedFlareLighting: return "Unauthorized Flare";
                case DetectionType.UnauthorizedBananaSlip: return "Unauthorized Banana";
                case DetectionType.MasterClientTheft: return "Master Client Theft";
                case DetectionType.SteamIDSpoofing: return "Steam ID Spoofing";
                case DetectionType.InfinityWarp: return "Infinity Warp";
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

        private void OnDestroy()
        {
            Debug.Log("[AntiCheatUI] OnDestroy called");
            
            // Unsubscribe from events
            DetectionManager.OnDetectionSettingsChanged -= OnDetectionSettingsChanged;
            DetectionManager.OnDetectionOccurred -= OnDetectionOccurred;
            PlayerManager.OnPlayerAdded -= OnPlayerAdded;
            PlayerManager.OnPlayerRemoved -= OnPlayerRemoved;
            PlayerManager.OnPlayerStatusChanged -= OnPlayerStatusChanged;
            BlockingManager.OnPlayerBlocked -= OnPlayerBlocked;
            BlockingManager.OnPlayerUnblocked -= OnPlayerUnblocked;
        }
    }
}