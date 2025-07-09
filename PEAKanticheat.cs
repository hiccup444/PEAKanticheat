using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace AntiCheatMod
{
    [BepInPlugin("com.yourname.anticheat", "Anti-Cheat Protection", "2.0.0")]
    public class AntiCheatPlugin : BaseUnityPlugin, IConnectionCallbacks, IMatchmakingCallbacks
    {
        public static new ManualLogSource Logger;
        private static AntiCheatPlugin Instance;
        private static new ConfigFile Config;

        // Config entries
        private static ConfigEntry<bool> ShowVisualLogs;

        // Connection log for visual messages
        private static PlayerConnectionLog _connectionLog;
        private static readonly Queue<(string message, bool onlySendOnce, bool sfxJoin, bool sfxLeave)> _queuedLogs = new Queue<(string, bool, bool, bool)>(8);

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
                }
            }

            if (!_connectionLog || _getColorTagMethod == null || _addMessageMethod == null)
            {
                _queuedLogs.Enqueue((message, onlySendOnce, sfxJoin, sfxLeave));
                return false;
            }

            StringBuilder sb = new StringBuilder(message);

            // Use reflection to call GetColorTag
            string joinedColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { _connectionLog.joinedColor });
            string leftColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { _connectionLog.leftColor });
            string userColorTag = (string)_getColorTagMethod.Invoke(_connectionLog, new object[] { _connectionLog.userColor });

            sb.Replace("{joinedColor}", joinedColorTag);
            sb.Replace("{leftColor}", leftColorTag);
            sb.Replace("{userColor}", userColorTag);
            message = sb.ToString();

            if (onlySendOnce && _currentLogField != null)
            {
                string currentLog = (string)_currentLogField.GetValue(_connectionLog);
                if (currentLog != null && currentLog.Contains(message))
                {
                    return true;
                }
            }

            // Use reflection to call AddMessage
            _addMessageMethod.Invoke(_connectionLog, new object[] { message });

            if (sfxJoin && _connectionLog.sfxJoin)
            {
                _connectionLog.sfxJoin.Play(Vector3.zero);
            }

            if (sfxLeave && _connectionLog.sfxLeave)
            {
                _connectionLog.sfxLeave.Play(Vector3.zero);
            }

            return true;
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

            Logger.LogWarning($"CHEATER DETECTED: {cheater.NickName} - Reason: {reason}");
            LogVisually($"{{userColor}}{cheater.NickName}</color> {{leftColor}}softlocked - {reason}</color>", false, false, true);

            var kiosk = FindObjectOfType<AirportCheckInKiosk>();
            if (kiosk != null)
            {
                // Get the photonView property using reflection if needed
                var photonView = kiosk.GetComponent<PhotonView>();
                if (photonView != null)
                {
                    photonView.RPC("BeginIslandLoadRPC", cheater, new object[]
                    {
                        "Pretitle",
                        7
                    });
                    Logger.LogInfo($"Soft-locked cheater: {cheater.NickName}");
                }
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

            // Check for Cherry cheat mod
            if (player.CustomProperties.ContainsKey("CherryUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Cherry cheat mod!</color>", true, false, true);
                SoftLockPlayer(player, "Cherry cheat mod user");
            }

            if (player.CustomProperties.ContainsKey("CherryOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Cherry cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Cherry cheat mod!</color>", true, false, true);
                SoftLockPlayer(player, "Cherry cheat mod owner");
            }

            // Check for Atlas cheat mod
            if (player.CustomProperties.ContainsKey("AtlUser"))
            {
                Logger.LogWarning($"{player.NickName} is using the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is using the Atlas cheat mod!</color>", true, false, true);
                SoftLockPlayer(player, "Atlas cheat mod user");
            }

            if (player.CustomProperties.ContainsKey("AtlOwner"))
            {
                Logger.LogWarning($"{player.NickName} is the Owner of the Atlas cheat mod!");
                LogVisually($"{{userColor}}{player.NickName}</color> {{leftColor}}is the Owner of the Atlas cheat mod!</color>", true, false, true);
                SoftLockPlayer(player, "Atlas cheat mod owner");
            }
        }

        public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
        {
            Logger.LogInfo($"{newMasterClient.NickName} (#{newMasterClient.ActorNumber}) is the new master client!");

            // Only protect master client if we were previously master client and someone took it
            if (newMasterClient.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber && PhotonNetwork.LocalPlayer.IsMasterClient == false)
            {
                Logger.LogWarning($"Master client stolen by: {newMasterClient.NickName}");
                LogVisually($"{{userColor}}{newMasterClient.NickName}</color> {{leftColor}}tried to take master client</color>", false, false, true);
                PhotonNetwork.SetMasterClient(PhotonNetwork.LocalPlayer);
                SoftLockPlayer(newMasterClient, "Stole master client");
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
        public void OnLeftRoom() { }
    }

    // Harmony patches for detecting malicious actions
    [HarmonyPatch]
    public static class AntiCheatPatches
    {
        // Detect illegal campfire manipulation
        [HarmonyPatch(typeof(Campfire), "SetFireWoodCount")]
        [HarmonyPrefix]
        public static bool BlockIllegalCampfireLogChange(Campfire __instance, int count, ref PhotonMessageInfo info)
        {
            // Only check if we're master client
            if (!PhotonNetwork.IsMasterClient)
                return true;

            // Get photonView using reflection or GetComponent
            var photonView = __instance.GetComponent<PhotonView>();
            if (photonView == null)
                return true;

            bool isLegitimate = __instance.state == Campfire.FireState.Spent ||
                                info.Sender == null ||
                                info.Sender.ActorNumber == photonView.Owner.ActorNumber;

            if (!isLegitimate)
            {
                AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} tried to illegally set campfire logs!");
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to manipulate campfire</color>", false, false, true);
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Illegal campfire manipulation");
                __instance.FireWoodCount = 3; // Reset to default
            }

            return isLegitimate;
        }

        // Detect illegal campfire extinguishing
        [HarmonyPatch(typeof(Campfire), "Extinguish_Rpc")]
        [HarmonyPrefix]
        public static bool BlockUnauthorizedCampfireExtinguish(Campfire __instance, ref PhotonMessageInfo info)
        {
            // Only check if we're master client
            if (!PhotonNetwork.IsMasterClient)
                return true;

            // Get photonView using reflection or GetComponent
            var photonView = __instance.GetComponent<PhotonView>();
            if (photonView == null)
                return true;

            bool isOwner = info.Sender == null ||
                           info.Sender.ActorNumber == photonView.Owner.ActorNumber;

            if (!isOwner)
            {
                AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} tried to illegally extinguish campfire!");
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to extinguish campfire illegally</color>", false, false, true);
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Illegal campfire extinguish");
            }

            return isOwner;
        }

        // Detect unauthorized player killing
        [HarmonyPatch(typeof(Character), "RPCA_Die")]
        [HarmonyPrefix]
        public static void DetectIllegalPlayerKill(Character __instance, ref PhotonMessageInfo info)
        {
            // Only check if we're master client
            if (!PhotonNetwork.IsMasterClient)
                return;

            // Get photonView using reflection or GetComponent
            var photonView = __instance.GetComponent<PhotonView>();
            if (photonView == null)
                return;

            // Allow if sender is null, master client, killing themselves, or is local player
            if (info.Sender == null || info.Sender.IsMasterClient ||
                info.Sender.ActorNumber == photonView.Owner.ActorNumber ||
                info.Sender.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
                return;

            AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} illegally killed {photonView.Owner.NickName}!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}illegally killed {{userColor}}{photonView.Owner.NickName}</color>", false, false, true);
            AntiCheatPlugin.SoftLockPlayer(info.Sender, "Illegal player kill");
        }

        // Detect unauthorized player reviving
        [HarmonyPatch(typeof(Character), "RPCA_ReviveAtPosition")]
        [HarmonyPrefix]
        public static void DetectIllegalPlayerRevive(Character __instance, ref PhotonMessageInfo info)
        {
            // Only check if we're master client
            if (!PhotonNetwork.IsMasterClient)
                return;

            // Get photonView using reflection or GetComponent
            var photonView = __instance.GetComponent<PhotonView>();
            if (photonView == null)
                return;

            // Allow if sender is null, master client, or is local player
            if (info.Sender == null || info.Sender.IsMasterClient ||
                info.Sender.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
                return;

            AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} illegally revived {photonView.Owner.NickName}!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}illegally revived {{userColor}}{photonView.Owner.NickName}</color>", false, false, true);
            AntiCheatPlugin.SoftLockPlayer(info.Sender, "Illegal player revive");
        }

        // Detect suspicious prefab spawning
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
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (!instantiateEvent || __result == null)
                return;

            string[] suspiciousPrefabs = {
                "BeeSwarm",
                "Bingbong_Grab",
                "Bingbong_Blow",
                "Bingbong_Suck",
                "Bingbong_Push",
                "Bingbong_Push_Gentle",
                "BingBongVoiceRelay"
            };

            foreach (string prefab in suspiciousPrefabs)
            {
                if (__result.name == prefab)
                {
                    AntiCheatPlugin.Logger.LogWarning($"{parameters.creator.NickName} spawned suspicious prefab: {prefab}");
                    AntiCheatPlugin.LogVisually($"{{userColor}}{parameters.creator.NickName}</color> {{leftColor}}spawned suspicious prefab: {prefab}</color>", false, false, true);
                    AntiCheatPlugin.SoftLockPlayer(parameters.creator, $"Spawned suspicious prefab: {prefab}");
                    break;
                }
            }
        }

        // Detect tick attachment abuse
        [HarmonyPatch(typeof(Bugfix), "AttachBug")]
        [HarmonyPostfix]
        public static void DetectIllegalTickAttachment(int targetID, ref PhotonMessageInfo info)
        {
            // Only check if we're master client
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (info.Sender != null && PhotonView.Find(targetID).Owner.ActorNumber != info.Sender.ActorNumber)
            {
                AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} illegally attached a tick to another player!");
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}attached a tick to another player</color>", false, false, true);
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Illegal tick attachment");
            }
        }
    }
}
