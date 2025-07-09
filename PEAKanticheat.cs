using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
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

namespace AntiCheatMod
{
    [BepInPlugin("com.yourname.anticheat", "Anti-Cheat Protection", "2.1.0")]
    public class AntiCheatPlugin : BaseUnityPlugin, IConnectionCallbacks, IMatchmakingCallbacks, IInRoomCallbacks
    {
        public static new ManualLogSource Logger;
        private static AntiCheatPlugin Instance;
        private static new ConfigFile Config;

        // Config entries
        private static ConfigEntry<bool> ShowVisualLogs;
        private static ConfigEntry<bool> CheckSteamNames;
        public static ConfigEntry<bool> VerboseRPCLogging;

        // Connection log for visual messages
        private static PlayerConnectionLog _connectionLog;
        private static readonly Queue<(string message, bool onlySendOnce, bool sfxJoin, bool sfxLeave)> _queuedLogs = new Queue<(string, bool, bool, bool)>(8);

        // Track soft-locked players for current session only
        private static readonly HashSet<int> _softLockedPlayers = new HashSet<int>();

        // Reflection methods/fields
        private static MethodInfo _getColorTagMethod;
        private static MethodInfo _addMessageMethod;
        private static FieldInfo _currentLogField;

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;

            // Initialize config
            Config = new ConfigFile(Path.Combine(Paths.ConfigPath, "com.yourname.anticheat.cfg"), true);
            ShowVisualLogs = Config.Bind("General", "ShowVisualLogs", true, "Show anti-cheat messages in the connection log");
            CheckSteamNames = Config.Bind("General", "CheckSteamNames", true, "Check if Photon names match Steam names");
            VerboseRPCLogging = Config.Bind("Debug", "VerboseRPCLogging", false, "Log all RPC calls for debugging");

            Logger.LogInfo("Anti-cheat protection active!");

            // Apply Harmony patches
            var harmony = new Harmony("com.yourname.anticheat");
            harmony.PatchAll();

            // Subscribe to Photon callbacks
            PhotonNetwork.AddCallbackTarget(this);

            // Start checking for cheat mods
            StartCoroutine(CheckPlayersForCheats());
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

        public static void SoftLockPlayer(Photon.Realtime.Player cheater, string reason)
        {
            // Only work if we're master client
            if (!PhotonNetwork.IsMasterClient)
                return;

            // Never soft-lock ourselves
            if (cheater.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                Logger.LogInfo($"Not soft-locking local player");
                return;
            }

            // Check if already soft-locked
            if (_softLockedPlayers.Contains(cheater.ActorNumber))
            {
                return;
            }

            Logger.LogWarning($"CHEATER DETECTED: {cheater.NickName} - Reason: {reason}");
            LogVisually($"{{userColor}}{cheater.NickName}</color> {{leftColor}}softlocked - {reason}</color>", false, false, true);

            // Try to find AirportCheckInKiosk (only exists in airport scene)
            var kiosk = FindObjectOfType<AirportCheckInKiosk>();
            if (kiosk != null)
            {
                var photonView = kiosk.GetComponent<PhotonView>();
                if (photonView != null)
                {
                    photonView.RPC("BeginIslandLoadRPC", cheater, new object[]
                    {
                        "Pretitle",
                        7
                    });
                    Logger.LogInfo($"Soft-locked cheater via kiosk: {cheater.NickName}");
                    _softLockedPlayers.Add(cheater.ActorNumber);
                    return;
                }
            }

            // If not in airport scene, use alternative methods
            Logger.LogInfo($"Not in airport scene, using alternative soft-lock for: {cheater.NickName}");

            // Find the cheater's character
            var allCharacters = FindObjectsOfType<Character>();
            Character cheaterCharacter = null;

            foreach (var character in allCharacters)
            {
                var characterPhotonView = character.GetComponent<PhotonView>();
                if (characterPhotonView != null && characterPhotonView.Owner != null && characterPhotonView.Owner.ActorNumber == cheater.ActorNumber)
                {
                    cheaterCharacter = character;
                    break;
                }
            }

            if (cheaterCharacter != null)
            {
                var cheaterPhotonView = cheaterCharacter.GetComponent<PhotonView>();
                if (cheaterPhotonView != null)
                {
                    try
                    {
                        // Black screen the cheater
                        cheaterPhotonView.RPC("WarpPlayerRPC", RpcTarget.All, new object[]
                        {
                            new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity),
                            true
                        });
                        Logger.LogInfo($"Black screened cheater: {cheater.NickName}");

                        // Try to destroy the cheater's character
                        cheaterPhotonView.OwnershipTransfer = OwnershipOption.Request;
                        cheaterPhotonView.OwnerActorNr = PhotonNetwork.LocalPlayer.ActorNumber;
                        cheaterPhotonView.ControllerActorNr = PhotonNetwork.LocalPlayer.ActorNumber;
                        cheaterPhotonView.RequestOwnership();
                        cheaterPhotonView.TransferOwnership(PhotonNetwork.LocalPlayer);

                        // Wait a frame then destroy
                        Instance.StartCoroutine(DestroyPlayerDelayed(cheaterPhotonView));

                        Logger.LogInfo($"Initiated destruction of cheater: {cheater.NickName}");
                    }
                    catch (System.Exception ex)
                    {
                        Logger.LogError($"Error applying alternative soft-lock to {cheater.NickName}: {ex.Message}");
                    }
                }
            }
            else
            {
                Logger.LogWarning($"Could not find character for cheater: {cheater.NickName}");
            }

            _softLockedPlayers.Add(cheater.ActorNumber);
        }

        private static IEnumerator DestroyPlayerDelayed(PhotonView cheaterPhotonView)
        {
            yield return new WaitForSeconds(0.1f); // Wait a frame for ownership transfer
            try
            {
                if (cheaterPhotonView != null && cheaterPhotonView.IsMine)
                {
                    PhotonNetwork.Destroy(cheaterPhotonView);
                    Logger.LogInfo($"Successfully destroyed cheater's character");
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error destroying cheater's character: {ex.Message}");
            }
        }

        private IEnumerator CheckPlayersForCheats()
        {
            while (true)
            {
                yield return new WaitForSeconds(5f);

                // Only check if we're master client
                if (PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
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

            // Skip if already soft-locked
            if (_softLockedPlayers.Contains(player.ActorNumber))
                return;

            // Check for Cherry cheat mod
            if (player.CustomProperties.ContainsKey("CherryUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Cherry cheat mod!</color>", true, false, true);
                SoftLockPlayer(player, "Cherry cheat mod user");
                return;
            }

            if (player.CustomProperties.ContainsKey("CherryOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Cherry cheat mod!</color>", true, false, true);
                SoftLockPlayer(player, "Cherry cheat mod owner");
                return;
            }

            // Check for Atlas cheat mod
            if (player.CustomProperties.ContainsKey("AtlUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Atlas cheat mod!</color>", true, false, true);
                SoftLockPlayer(player, "Atlas cheat mod user");
                return;
            }

            if (player.CustomProperties.ContainsKey("AtlOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Atlas cheat mod!</color>", true, false, true);
                SoftLockPlayer(player, "Atlas cheat mod owner");
                return;
            }

            // Check if Steam name matches Photon name
            if (CheckSteamNames.Value)
            {
                CheckSteamNameMatch(player);
            }
        }

        private void CheckSteamNameMatch(Photon.Realtime.Player player)
        {
            // Get current Steam lobby
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
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}has mismatched Steam/Photon name!</color>", true, false, true);
                SoftLockPlayer(player, "Name mismatch - possible spoofer");
            }
        }

        public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
        {
            Logger.LogInfo($"{newMasterClient.NickName} (#{newMasterClient.ActorNumber}) is the new master client!");

            // Only protect master client if we were previously master client and someone took it
            if (newMasterClient.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber && PhotonNetwork.LocalPlayer.IsMasterClient == false)
            {
                var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();

                if (lobbyHandler != null && lobbyHandler.InSteamLobby(out CSteamID currentLobby))
                {
                    if (SteamMatchmaking.GetLobbyOwner(currentLobby) == SteamUser.GetSteamID())
                    {
                        Logger.LogWarning($"Master client stolen by: {newMasterClient.NickName}");
                        LogVisually($"{{userColor}}{newMasterClient.NickName}</color> {{leftColor}}tried to take master client</color>", false, false, true);
                        PhotonNetwork.SetMasterClient(PhotonNetwork.LocalPlayer);
                        SoftLockPlayer(newMasterClient, "Stole master client");
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
        public void OnJoinedRoom() { }
        public void OnJoinRoomFailed(short returnCode, string message) { }
        public void OnJoinRandomFailed(short returnCode, string message) { }
        public void OnLeftRoom()
        {
            // Clear soft-locked players when leaving room
            _softLockedPlayers.Clear();
        }

        // IInRoomCallbacks implementations
        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
        {
            // Check new players immediately when they join
            if (PhotonNetwork.IsMasterClient)
            {
                StartCoroutine(CheckNewPlayerDelayed(newPlayer));
            }
        }

        private IEnumerator CheckNewPlayerDelayed(Photon.Realtime.Player player)
        {
            // Wait a moment for Steam data to sync
            yield return new WaitForSeconds(2f);
            CheckPlayerForCheatMods(player);
        }

        public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
            // Remove from soft-locked list when they leave
            if (_softLockedPlayers.Contains(otherPlayer.ActorNumber))
            {
                _softLockedPlayers.Remove(otherPlayer.ActorNumber);
                Logger.LogInfo($"Removed {otherPlayer.NickName} from soft-lock list (disconnected)");
            }
        }

        public void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer, Hashtable changedProps) { }
        public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged) { }
    }

    // Harmony patches for detecting malicious actions
    [HarmonyPatch]
    public static class AntiCheatPatches
    {
        // Campfire log count manipulation
        [HarmonyPatch(typeof(Campfire), "SetFireWoodCount")]
        [HarmonyPrefix]
        public static bool PreCampfireSetFireWoodCount(Campfire __instance, int count, PhotonMessageInfo info)
        {
            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Campfire.SetFireWoodCount called by {info.Sender?.NickName} with count: {count}");

            var photonView = __instance.GetComponent<PhotonView>();
            bool isValid = __instance.state == Campfire.FireState.Spent || info.Sender == null || (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber);

            if (!isValid && PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} (#{info.Sender.ActorNumber}) tried to set the log count to {count} for the {__instance.advanceToSegment} campfire!");
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}attempted to modify campfire logs!</color>", false, false, true);
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Campfire log manipulation");
                __instance.FireWoodCount = 3; // Reset to default
                return false; // Block the RPC
            }

            return isValid;
        }

        // Campfire lighting
        [HarmonyPatch(typeof(Campfire), "Light_Rpc")]
        [HarmonyPrefix]
        public static bool PreCampfireLight_Rpc(Campfire __instance, PhotonMessageInfo info)
        {
            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Campfire.Light_Rpc called by {info.Sender?.NickName}");

            var photonView = __instance.GetComponent<PhotonView>();
            bool isValid = info.Sender == null || (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber);

            if (!isValid)
            {
                AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} (#{info.Sender.ActorNumber}) tried to light the {__instance.advanceToSegment} campfire!");
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}attempted to light the {__instance.advanceToSegment} campfire!</color>", false, false, true);
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized campfire lighting");
                return false; // Block the RPC
            }

            return isValid;
        }

        // Campfire extinguishing
        [HarmonyPatch(typeof(Campfire), "Extinguish_Rpc")]
        [HarmonyPrefix]
        public static bool PreCampfireExtinguish_Rpc(Campfire __instance, PhotonMessageInfo info)
        {
            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Campfire.Extinguish_Rpc called by {info.Sender?.NickName}");

            var photonView = __instance.GetComponent<PhotonView>();
            bool isValid = info.Sender == null || (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber);

            if (!isValid)
            {
                AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} (#{info.Sender.ActorNumber}) tried to extinguish the {__instance.advanceToSegment} campfire!");
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}attempted to extinguish the {__instance.advanceToSegment} campfire!</color>", false, false, true);
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized campfire extinguish");
                return false; // Block the RPC
            }

            return isValid;
        }

        // Player killing
        [HarmonyPatch(typeof(Character), "RPCA_Die")]
        [HarmonyPrefix]
        public static void PreCharacterRPCA_Die(Character __instance, PhotonMessageInfo info)
        {
            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Character.RPCA_Die called by {info.Sender?.NickName} on {__instance.GetComponent<PhotonView>()?.Owner?.NickName}");

            var photonView = __instance.GetComponent<PhotonView>();
            if (info.Sender == null || info.Sender.IsMasterClient || (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber))
                return;

            AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} (#{info.Sender.ActorNumber}) killed {photonView?.Owner?.NickName} (#{photonView?.Owner?.ActorNumber})!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}killed</color> {{userColor}}{photonView?.Owner?.NickName}</color>{{leftColor}}!</color>", false, false, true);
            AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized kill");
        }

        // Suspicious prefab spawning (exact names from the other mod)
        [HarmonyPatch(typeof(PhotonNetwork), "NetworkInstantiate", new Type[]
        {
            typeof(Photon.Pun.InstantiateParameters),
            typeof(bool),
            typeof(bool)
        })]
        [HarmonyPostfix]
        public static void DetectSuspiciousPrefabSpawn(ref Photon.Pun.InstantiateParameters parameters, ref GameObject __result, bool instantiateEvent)
        {
            // Only check if we're master client
            if (!PhotonNetwork.IsMasterClient || !instantiateEvent || __result == null)
                return;

            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"{parameters.creator.NickName} (#{parameters.creator.ActorNumber}) instantiated the '{parameters.prefabName}' prefab.");

            string prefabName = __result.name;

            // Only check for BingBong prefabs since BeeSwarm doesn't work
            string[] suspiciousPrefabs = {
                "Bingbong_Grab",
                "Bingbong_Blow",
                "Bingbong_Suck",
                "Bingbong_Push",
                "Bingbong_Push_Gentle",
                "BingBongVoiceRelay"
            };

            foreach (string suspiciousPrefab in suspiciousPrefabs)
            {
                if (prefabName == suspiciousPrefab)
                {
                    AntiCheatPlugin.Logger.LogWarning($"{parameters.creator.NickName} spawned suspicious prefab: {prefabName}");
                    AntiCheatPlugin.LogVisually($"{{userColor}}{parameters.creator.NickName}</color> {{leftColor}}spawned suspicious prefab: {prefabName}!</color>", false, false, true);
                    AntiCheatPlugin.SoftLockPlayer(parameters.creator, $"Spawned suspicious prefab: {prefabName}");
                    break;
                }
            }
        }
    }
}
