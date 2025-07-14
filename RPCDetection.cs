using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using Photon.Realtime;

namespace AntiCheatMod
{
    // Harmony patches for detecting malicious RPC actions
    [HarmonyPatch]
    public static class RPCDetection
    {
        // Enum for ownership validation conditions
        public enum OwnershipCondition
        {
            IsMasterClient,
            IsViewOwner,
            IsMasterClientOrViewOwner
        }

        // RPC Pattern class for numeric RPC detection
        public class RPCPattern
        {
            public string MethodId { get; set; }
            public string Description { get; set; }
            public Func<object[], bool> ParameterCheck { get; set; }
            public bool RequiresMasterClient { get; set; }
        }

        private static Dictionary<int, DateTime> _lastOwnershipRequest = new Dictionary<int, DateTime>();
        private static Dictionary<int, int> _ownershipRequestCount = new Dictionary<int, int>();

        // Define the Photon hashtable keys
        private static readonly byte keyByte0 = 0; // Method name

        // Rate tracking for events
        private static Dictionary<int, Dictionary<int, int>> _eventRateCounts = new Dictionary<int, Dictionary<int, int>>();
        private static Dictionary<int, DateTime> _eventRateTimestamps = new Dictionary<int, DateTime>();
        private const int SUSPICIOUS_EVENT_THRESHOLD = 10; // Events per second to trigger logging

        // Helper method for RPC validation
        private static bool IsRpcValid(PhotonView view, Photon.Realtime.Player sender, OwnershipCondition ownershipCondition, Func<bool> validCondition = null)
        {
            if (sender == null)
                return true;

            switch (ownershipCondition)
            {
                case OwnershipCondition.IsMasterClient:
                    {
                        if (sender.IsMasterClient)
                            return true;
                        break;
                    }
                case OwnershipCondition.IsViewOwner:
                    {
                        if (view != null && view.Owner != null && sender.ActorNumber == view.Owner.ActorNumber)
                            return true;
                        break;
                    }
                case OwnershipCondition.IsMasterClientOrViewOwner:
                    {
                        if (sender.IsMasterClient || (view != null && view.Owner != null && sender.ActorNumber == view.Owner.ActorNumber))
                            return true;
                        break;
                    }
                default:
                    break;
            }

            return validCondition == null || validCondition();
        }

        [HarmonyPatch(typeof(PhotonNetwork), "OnEvent")]
        [HarmonyPrefix]
        public static bool PreOnEvent(EventData photonEvent)
        {
            int sender = photonEvent.Sender;

            // Block ALL events from blocked players silently
            if (sender > 0 && AntiCheatPlugin.IsBlocked(sender))
            {
                return false; // Block silently without any logging
            }

            // Track event rates for suspicious activity detection
            bool shouldLogEvent = false;
            if (sender > 0 && (photonEvent.Code == 204 || photonEvent.Code == 209 || photonEvent.Code == 210 || photonEvent.Code == 212))
            {
                // Initialize tracking for this sender if needed
                if (!_eventRateCounts.ContainsKey(sender))
                {
                    _eventRateCounts[sender] = new Dictionary<int, int>();
                    _eventRateTimestamps[sender] = DateTime.Now;
                }

                // Check if we need to reset the rate counter (1 second window)
                if ((DateTime.Now - _eventRateTimestamps[sender]).TotalSeconds >= 1.0)
                {
                    _eventRateCounts[sender].Clear();
                    _eventRateTimestamps[sender] = DateTime.Now;
                }

                // Increment counter for this event type
                if (!_eventRateCounts[sender].ContainsKey(photonEvent.Code))
                {
                    _eventRateCounts[sender][photonEvent.Code] = 0;
                }
                _eventRateCounts[sender][photonEvent.Code]++;

                // Check if rate is suspicious
                if (_eventRateCounts[sender][photonEvent.Code] >= SUSPICIOUS_EVENT_THRESHOLD)
                {
                    shouldLogEvent = true;
                    AntiCheatPlugin.Logger.LogWarning($"[SUSPICIOUS RATE] Actor #{sender} sent {_eventRateCounts[sender][photonEvent.Code]} events (code {photonEvent.Code}) in 1 second!");
                }
            }

            // Only log ownership/destroy events if we're master client AND rate is suspicious
            if (PhotonNetwork.IsMasterClient && shouldLogEvent)
            {
                switch (photonEvent.Code)
                {
                    case 204:
                        AntiCheatPlugin.Logger.LogWarning($"[HIGH RATE DESTROY EVENT] From actor #{sender}");
                        break;
                    case 209:
                        AntiCheatPlugin.Logger.LogWarning($"[HIGH RATE OWNERSHIP REQUEST] From actor #{sender}");
                        break;
                    case 210:
                        AntiCheatPlugin.Logger.LogWarning($"[HIGH RATE OWNERSHIP TRANSFER] From actor #{sender}");
                        break;
                    case 212:
                        AntiCheatPlugin.Logger.LogWarning($"[HIGH RATE OWNERSHIP UPDATE] From actor #{sender}");
                        break;
                }
            }

            // Additional protection for ownership theft (works for everyone)
            if (photonEvent.Code == 210) // OwnershipTransfer
            {
                int[] data = (int[])photonEvent.CustomData;
                if (data != null && data.Length >= 2)
                {
                    int viewId = data[0];
                    int newOwner = data[1];

                    // First check: Is the sender blocked? Block ALL their ownership attempts
                    if (AntiCheatPlugin.IsBlocked(sender))
                    {
                        AntiCheatPlugin.Logger.LogWarning($"[OWNERSHIP BLOCKED] Blocked player (Actor #{sender}) tried to transfer ownership of view {viewId}");
                        return false; // Block all ownership transfers from blocked players
                    }

                    // Rate limiting check
                    if (_ownershipRequestCount.ContainsKey(sender))
                    {
                        if (_lastOwnershipRequest.ContainsKey(sender) &&
                            (DateTime.Now - _lastOwnershipRequest[sender]).TotalSeconds < 1)
                        {
                            _ownershipRequestCount[sender]++;

                            // If more than 10 ownership requests per second
                            if (_ownershipRequestCount[sender] > 10)
                            {
                                Photon.Realtime.Player senderPlayer = PhotonNetwork.CurrentRoom?.GetPlayer(sender);
                                if (senderPlayer != null && !senderPlayer.IsLocal)
                                {
                                    AntiCheatPlugin.Logger.LogWarning($"[MASS OWNERSHIP DETECTED] {senderPlayer.NickName} attempted {_ownershipRequestCount[sender]} ownership transfers in 1 second!");

                                    // Only master client can punish
                                    if (PhotonNetwork.IsMasterClient)
                                    {
                                        AntiCheatPlugin.LogVisually($"{{userColor}}{senderPlayer.NickName}</color> {{leftColor}}attempted mass ownership theft!</color>", false, false, true);
                                        AntiCheatPlugin.BlockPlayer(senderPlayer, "Mass ownership theft attempt");
                                    }

                                    return false; // Block the ownership transfer
                                }
                            }
                        }
                        else
                        {
                            // Reset counter after 1 second
                            _ownershipRequestCount[sender] = 1;
                        }
                    }
                    else
                    {
                        _ownershipRequestCount[sender] = 1;
                    }

                    _lastOwnershipRequest[sender] = DateTime.Now;

                    // Second check: Is someone trying to steal YOUR character?
                    PhotonView targetView = PhotonView.Find(viewId);
                    if (targetView != null && targetView.Owner != null)
                    {
                        // If someone is trying to steal ownership of our character
                        if (targetView.Owner.IsLocal && newOwner != PhotonNetwork.LocalPlayer.ActorNumber)
                        {
                            Photon.Realtime.Player senderPlayer = PhotonNetwork.CurrentRoom?.GetPlayer(sender);
                            if (senderPlayer != null && !senderPlayer.IsMasterClient)
                            {
                                AntiCheatPlugin.Logger.LogWarning($"[OWNERSHIP THEFT BLOCKED] {senderPlayer.NickName} tried to steal ownership of your view {viewId}!");

                                // Only show visual logs and block if we're master client
                                if (PhotonNetwork.IsMasterClient)
                                {
                                    AntiCheatPlugin.LogVisually($"{{userColor}}{senderPlayer.NickName}</color> {{leftColor}}tried to steal your character!</color>", false, false, true);
                                    AntiCheatPlugin.BlockPlayer(senderPlayer, "Attempted ownership theft");
                                }

                                return false; // Block the theft
                            }
                        }
                    }

                    // Block destroy events targeting local player's objects
                    if (photonEvent.Code == 204) // Destroy
                    {
                        if (photonEvent.CustomData is Hashtable destroyData && destroyData.ContainsKey((byte)0))
                        {
                            int destroyViewId = (int)destroyData[(byte)0];
                            PhotonView destroyTargetView = PhotonView.Find(destroyViewId);

                            // Only protect local player's objects
                            if (destroyTargetView != null && destroyTargetView.IsMine)
                            {
                                // Block if someone else is trying to destroy our object
                                if (sender != PhotonNetwork.LocalPlayer.ActorNumber)
                                {
                                    Photon.Realtime.Player senderPlayer = PhotonNetwork.CurrentRoom?.GetPlayer(sender);
                                    AntiCheatPlugin.Logger.LogError($"[DESTROY BLOCKED] {senderPlayer?.NickName ?? $"Actor {sender}"} tried to destroy your object (ViewID: {destroyViewId})!");

                                    // Extra logging if it's a character
                                    if (destroyTargetView.GetComponent<Character>() != null)
                                    {
                                        AntiCheatPlugin.Logger.LogError($"[CHARACTER DESTROY BLOCKED] They tried to destroy YOUR CHARACTER!");

                                        if (PhotonNetwork.IsMasterClient && senderPlayer != null)
                                        {
                                            AntiCheatPlugin.LogVisually($"{{userColor}}{senderPlayer.NickName}</color> {{leftColor}}tried to destroy your character!</color>", false, false, true);
                                            AntiCheatPlugin.BlockPlayer(senderPlayer, "Attempted to destroy your character");
                                        }
                                    }

                                    return false; // Block the destroy event from processing
                                }
                            }
                        }
                    }

                    return true; // Allow other events
                }
            }
            return true;
        }

        [HarmonyPatch(typeof(PhotonView), "TransferOwnership", typeof(Photon.Realtime.Player))]
        [HarmonyPrefix]
        public static bool PreTransferOwnership(PhotonView __instance, Photon.Realtime.Player newOwner)
        {
            // Never allow transferring ownership of characters to non-owners
            if (__instance.GetComponent<Character>() != null)
            {
                var currentOwner = __instance.Owner;

                // If someone is trying to steal a character they don't own
                if (currentOwner != null &&
                    currentOwner.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber &&
                    newOwner.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
                {
                    AntiCheatPlugin.Logger.LogError($"[BLOCKED] Attempted to steal character ownership from {currentOwner.NickName}!");
                    return false; // Block the transfer
                }
            }

            return true;
        }

        [HarmonyPatch(typeof(PhotonView), "RequestOwnership")]
        [HarmonyPrefix]
        public static bool PreRequestOwnership(PhotonView __instance)
        {
            // Never allow ownership requests on characters unless you already own it
            if (__instance.GetComponent<Character>() != null)
            {
                if (!__instance.IsMine && !PhotonNetwork.IsMasterClient)
                {
                    AntiCheatPlugin.Logger.LogError($"[BLOCKED] Attempted to request ownership of character owned by {__instance.Owner?.NickName}!");
                    return false; // Block the request
                }
            }
            return true;
        }

        [HarmonyPatch(typeof(PhotonNetwork), "Destroy", typeof(PhotonView))]
        [HarmonyPrefix]
        public static bool PrePhotonDestroy(PhotonView targetView)
        {
            if (targetView == null) return true;

            // Never allow destroying characters you don't own
            if (targetView.GetComponent<Character>() != null)
            {
                if (!targetView.IsMine && !PhotonNetwork.LocalPlayer.IsMasterClient)
                {
                    AntiCheatPlugin.Logger.LogError($"[BLOCKED] Attempted to destroy character owned by {targetView.Owner?.NickName}!");
                    return false;
                }
            }

            return true;
        }

        [HarmonyPatch(typeof(PhotonView), "OwnerActorNr", MethodType.Setter)]
        [HarmonyPrefix]
        public static bool PreOwnerActorNrSetter(PhotonView __instance, int value)
        {
            // Always allow during spawn grace period
            if (AntiCheatPlugin.IsInSpawnGracePeriod(value))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN GRACE] Allowing ownership change to actor #{value} during spawn period");
                return true;
            }

            // Check if the player trying to take ownership is blocked
            if (AntiCheatPlugin.IsBlocked(value))
            {
                AntiCheatPlugin.Logger.LogWarning($"[BLOCKED] Blocked player (actor #{value}) tried to take ownership of view {__instance.ViewID}");
                return false;
            }

            // CRITICAL: Always protect local player's character from ownership theft
            if (__instance.GetComponent<Character>() != null && __instance.IsMine)
            {
                // Check if this is a fresh spawn (character has no owner yet or is being initialized)
                var character = __instance.GetComponent<Character>();

                // Allow ownership changes during character initialization/spawning
                if (__instance.CreatorActorNr == 0 || // New object
                    __instance.Owner == null || // No owner yet
                    (character != null && !character.isActiveAndEnabled)) // Character not fully initialized
                {
                    AntiCheatPlugin.Logger.LogInfo($"[SPAWN] Allowing ownership change for uninitialized character to actor #{value}");
                    return true;
                }

                if (value != PhotonNetwork.LocalPlayer.ActorNumber)
                {
                    var thief = PhotonNetwork.CurrentRoom?.GetPlayer(value);
                    AntiCheatPlugin.Logger.LogError($"[CHARACTER THEFT BLOCKED] {thief?.NickName ?? $"Actor {value}"} tried to steal your character!");

                    // Log visual warning
                    if (PhotonNetwork.IsMasterClient && thief != null)
                    {
                        AntiCheatPlugin.LogVisually($"{{userColor}}{thief.NickName}</color> {{leftColor}}tried to steal your character!</color>", false, false, true);
                        AntiCheatPlugin.BlockPlayer(thief, "Character ownership theft attempt");
                    }

                    return false; // Block the ownership change
                }
            }

            // If this is our object and someone else is trying to claim it
            if (__instance.Owner != null && __instance.Owner.IsLocal && value != PhotonNetwork.LocalPlayer.ActorNumber)
            {
                // Log it for debugging but DON'T block legitimate players
                AntiCheatPlugin.Logger.LogInfo($"[OWNERSHIP] Actor #{value} taking ownership of view {__instance.ViewID} (was ours)");
            }

            return true; // Allow all legitimate ownership transfers
        }

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
                AntiCheatPlugin.BlockPlayer(info.Sender, "Campfire log manipulation");
                __instance.FireWoodCount = 3; // Reset to default
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
                AntiCheatPlugin.BlockPlayer(info.Sender, "Unauthorized campfire extinguish");
                return false; // Block the RPC
            }

            return isValid;
        }

        [HarmonyPatch(typeof(PhotonNetwork), "RaiseEventInternal")]
        [HarmonyPrefix]
        internal static bool PreRaiseEventInternal(byte eventCode, object eventContent, RaiseEventOptions raiseEventOptions, SendOptions sendOptions)
        {
            var localPlayer = PhotonNetwork.LocalPlayer;

            // If sender is blocked, block all critical events silently
            if (localPlayer != null && AntiCheatPlugin.IsBlocked(localPlayer.ActorNumber))
            {
                switch (eventCode)
                {
                    case 200: // RPC
                    case 202: // Instantiate
                    case 204: // Destroy
                    case 207: // Destroy player objects
                    case 209: // Ownership request
                    case 210: // Ownership transfer  
                    case 212: // Ownership update
                        return false; // Block without logging
                }
            }

            // Special handling for destroy events - check if they're trying to destroy something they don't own
            if (eventCode == 204 && eventContent is Hashtable destroyData)
            {
                if (destroyData.ContainsKey((byte)0)) // PhotonNetwork.keyByteZero contains the view ID
                {
                    int viewId = (int)destroyData[(byte)0];
                    PhotonView targetView = PhotonView.Find(viewId);

                    if (targetView != null && targetView.Owner != null && localPlayer != null)
                    {
                        // Block if they're trying to destroy something they don't own
                        if (targetView.Owner.ActorNumber != localPlayer.ActorNumber && !localPlayer.IsMasterClient)
                        {
                            AntiCheatPlugin.Logger.LogWarning($"[DESTROY BLOCKED] {localPlayer.NickName} tried to destroy view {viewId} owned by {targetView.Owner.NickName}");
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        [HarmonyPatch(typeof(PhotonNetwork), "RemoveInstantiatedGO")]
        [HarmonyPrefix]
        internal static bool PreRemoveInstantiatedGO(GameObject go, bool localOnly)
        {
            if (go == null) return true;

            var photonView = go.GetComponent<PhotonView>();
            if (photonView != null && photonView.Owner != null)
            {
                var localPlayer = PhotonNetwork.LocalPlayer;

                // If a blocked player is trying to destroy something
                if (localPlayer != null && AntiCheatPlugin.IsBlocked(localPlayer.ActorNumber))
                {
                    // Allow if they own it
                    if (photonView.Owner.ActorNumber == localPlayer.ActorNumber)
                    {
                        AntiCheatPlugin.Logger.LogInfo($"[DESTROY ALLOWED] Blocked player destroying their own object {go.name}");
                        return true;
                    }

                    // Block if they don't own it
                    AntiCheatPlugin.Logger.LogWarning($"[DESTROY BLOCKED] Blocked player tried to remove {go.name} owned by {photonView.Owner.NickName}");
                    return false;
                }

                // Log normal destroys for debugging
                AntiCheatPlugin.Logger.LogInfo($"[DESTROY] {localPlayer?.NickName ?? "System"} removing {go.name} owned by {photonView.Owner.NickName}");
            }

            return true;
        }

        // Player reviving
        [HarmonyPatch(typeof(Character), "RPCA_ReviveAtPosition")]
        [HarmonyPrefix]
        public static bool PreCharacterRPCA_ReviveAtPosition(Character __instance, PhotonMessageInfo info)
        {
            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Character.RPCA_ReviveAtPosition called by {info.Sender?.NickName} on {__instance.GetComponent<PhotonView>()?.Owner?.NickName}");

            var photonView = __instance.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Check if they're reviving themselves
            bool isRevivingSelf = photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber;

            // If reviving self during spawn grace period, allow it
            if (isRevivingSelf && AntiCheatPlugin.IsInSpawnGracePeriod(info.Sender.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN REVIVE ALLOWED] {info.Sender.NickName} revived during spawn grace period");
                return true;
            }

            // Check if they have/had Scout Effigy before flagging as unauthorized (with 2-second window)
            if (AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "scout", 2f) &&
                AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "effigy", 2f))
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"{info.Sender.NickName} used Scout Effigy to revive {victimName} - legitimate");
                return true; // Allow the revive
            }

            // Unauthorized revive attempt
            AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} (#{info.Sender.ActorNumber}) attempted to revive {victimName} (#{photonView?.Owner?.ActorNumber}) without permission!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to revive</color> {{userColor}}{victimName}</color> {{leftColor}}without permission!</color>", false, false, true);
            AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized revive of {victimName}");
            return false; // Block the revive
        }

        [HarmonyPatch(typeof(Character), "WarpPlayerRPC")]
        [HarmonyPrefix]
        public static bool PreWarpPlayerRPC(Character __instance, Vector3 position, bool poof, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null (system) or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Check if position is infinity (black screen attempt)
            bool isInfinityWarp = float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z) ||
                                  float.IsNegativeInfinity(position.x) || float.IsNegativeInfinity(position.y) || float.IsNegativeInfinity(position.z);

            if (isInfinityWarp)
            {
                // NEVER allow infinity warps from non-master clients, even with Scout Effigy (Revive)
                AntiCheatPlugin.Logger.LogError($"[BLACK SCREEN BLOCKED] {info.Sender.NickName} tried to warp {victimName} to infinity!");

                if (photonView != null && photonView.IsMine)
                {
                    AntiCheatPlugin.Logger.LogError($"[PROTECTED] Blocked black screen attempt on local player!");
                    AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to black screen YOU!</color>", false, false, true);
                }
                else
                {
                    AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to black screen</color> {{userColor}}{victimName}</color>{{leftColor}}!</color>", false, false, true);
                }

                if (PhotonNetwork.IsMasterClient)
                {
                    AntiCheatPlugin.BlockPlayer(info.Sender, $"Black screen attempt on {victimName}");
                }

                return false; // Always block infinity warps
            }

            // Check if they're warping themselves
            bool isWarpingSelf = photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber;

            // If warping self during spawn grace period, always allow
            if (isWarpingSelf && AntiCheatPlugin.IsInSpawnGracePeriod(info.Sender.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN TELEPORT ALLOWED] {info.Sender.NickName} teleported during spawn grace period to {position}");
                return true;
            }

            // For non-spawn warps, check if they have Scout Effigy
            if (AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "scout", 2f) &&
                AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "effigy", 2f))
            {
                AntiCheatPlugin.Logger.LogInfo($"[WARP ALLOWED] {info.Sender.NickName} used Scout Effigy to warp {victimName} to {position}");
                return true; // Allow Scout Effigy warps (except infinity)
            }

            // Block unauthorized warps
            AntiCheatPlugin.Logger.LogWarning($"[WARP BLOCKED] {info.Sender.NickName} attempted to warp {victimName} without Scout Effigy!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to warp</color> {{userColor}}{victimName}</color> {{leftColor}}without Scout Effigy!</color>", false, false, true);

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized warp of {victimName} - no Scout Effigy");
            }

            return false;
        }

        // Revive protection
        [HarmonyPatch(typeof(Character), "RPCA_Revive")]
        [HarmonyPrefix]
        public static bool PreRPCA_Revive(Character __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Check for Scout Effigy
            if (AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "scout", 2f) &&
                AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "effigy", 2f))
            {
                return true; // Legitimate revive with item
            }

            AntiCheatPlugin.Logger.LogWarning($"[REVIVE BLOCKED] {info.Sender.NickName} attempted unauthorized revive on {victimName}!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to revive</color> {{userColor}}{victimName}</color> {{leftColor}}without permission!</color>", false, false, true);
            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized revive of {victimName}");
            }
            return false;
        }

        [HarmonyPatch(typeof(Character), "RPCA_Die")]
        [HarmonyPrefix]
        public static bool PreRPCA_Kill(Character __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();

            // Allow self-kills and master client kills
            if (info.Sender == null || info.Sender.IsMasterClient ||
                (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber))
            {
                return true;
            }

            // Get the victim's name
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            AntiCheatPlugin.Logger.LogWarning($"[KILL BLOCKED] {info.Sender.NickName} attempted to kill {victimName}!");

            if (PhotonNetwork.IsMasterClient)
            {
                // Update the visual log to show both names
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to kill</color> {{userColor}}{victimName}</color>{{leftColor}}!</color>", false, false, true);
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized kill attempt on {victimName}");
            }
            return false;
        }

        [HarmonyPatch(typeof(CharacterAfflictions), "ApplyStatusesFromFloatArrayRPC")]
        [HarmonyPrefix]
        public static bool PreAssignStatusRPC(CharacterAfflictions __instance, float[] deserializedData, PhotonMessageInfo info)
        {
            // Allow system or host
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string targetName = photonView?.Owner?.NickName ?? "Unknown";

            // Allow if the sender is modifying their own character
            if (photonView != null && photonView.Owner != null &&
                info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                AntiCheatPlugin.Logger.LogInfo($"[STATUS ALLOWED] {info.Sender.NickName} applying status to themselves");
                return true;
            }

            // Block if trying to modify another player
            AntiCheatPlugin.Logger.LogWarning($"[STATUS BLOCKED] {info.Sender.NickName} attempted to assign status effects to {targetName}!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to assign status effects to</color> {{userColor}}{targetName}</color>{{leftColor}}!</color>", false, false, true);

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized status assignment to {targetName}");
            }

            return false; // Cancel the original method
        }

        [HarmonyPatch(typeof(BananaPeel), "RPCA_TriggerBanana")]
        [HarmonyPrefix]
        public static bool PreSlipOnBananaRPC(BananaPeel __instance, int viewID, PhotonMessageInfo info)
        {
            // Find the character that's being targeted by viewID
            PhotonView targetView = PhotonView.Find(viewID);
            if (targetView == null)
                return true;

            Character targetCharacter = targetView.GetComponent<Character>();
            if (targetCharacter == null)
                return true;

            string victimName = targetView.Owner?.NickName ?? "Unknown";

            // Check if sender is trying to slip someone else
            if (targetView.Owner != null && info.Sender != null &&
                targetView.Owner.ActorNumber != info.Sender.ActorNumber)
            {
                AntiCheatPlugin.Logger.LogWarning($"[BANANA SLIP BLOCKED] {info.Sender.NickName} tried to slip {victimName}!");
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to slip</color> {{userColor}}{victimName}</color> {{leftColor}}with a banana!</color>", false, false, true);
                if (PhotonNetwork.IsMasterClient)
                {
                    AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized banana slip on {victimName}");
                }
                return false;
            }

            return true; // Allow self-slips or slips where sender owns the target
        }

        // NEW DETECTIONS - TESTING //
        // All punishments replaced with BlockPlayer //

        [HarmonyPatch(typeof(Character), "RPCA_PassOut")]
        [HarmonyPrefix]
        public static bool PreRPCA_PassOut(Character __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're passing out themselves
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[PASS OUT ALLOWED] {info.Sender.NickName} passed out themselves");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[PASS OUT DETECTED] {info.Sender.NickName} attempted to make {victimName} pass out!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to make</color> {{userColor}}{victimName}</color> {{leftColor}}pass out!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized pass out on {victimName}");
            }

            return false; // Block the attempt
        }

        [HarmonyPatch(typeof(Character), "RPCA_UnPassOut")]
        [HarmonyPrefix]
        public static bool PreRPCA_UnPassOut(Character __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're waking up themselves
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[UNPASS OUT ALLOWED] {info.Sender.NickName} woke themselves up");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[UNPASS OUT DETECTED] {info.Sender.NickName} attempted to wake up {victimName}!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to wake up</color> {{userColor}}{victimName}</color> {{leftColor}}without permission!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized wake up of {victimName}");
            }

            return false; // Block the attempt
        }

        // Character fall with screen shake detection
        [HarmonyPatch(typeof(Character), "RPCA_FallWithScreenShake")]
        [HarmonyPrefix]
        public static bool PreRPCA_FallWithScreenShake(Character __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're making themselves fall
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[FALL SHAKE ALLOWED] {info.Sender.NickName} made themselves fall with shake");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[FALL SHAKE DETECTED] {info.Sender.NickName} attempted to make {victimName} fall with screen shake!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to make</color> {{userColor}}{victimName}</color> {{leftColor}}fall with screen shake!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized fall with shake on {victimName}");
            }

            return false; // Block the attempt
        }

        // Jump detection
        [HarmonyPatch(typeof(CharacterMovement), "JumpRpc")]
        [HarmonyPrefix]
        public static bool PreJumpRpc(CharacterMovement __instance, PhotonMessageInfo info)
        {
            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're making themselves jump
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[JUMP ALLOWED] {info.Sender.NickName} jumped");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[JUMP DETECTED] {info.Sender.NickName} attempted to make {victimName} jump!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to make</color> {{userColor}}{victimName}</color> {{leftColor}}jump!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized jump on {victimName}");
            }

            return false; // Block the attempt
        }

        // Crouch detection
        [HarmonyPatch(typeof(CharacterMovement), "RPCA_SetCrouch")]
        [HarmonyPrefix]
        public static bool PreRPCA_SetCrouch(CharacterMovement __instance, bool setCrouch, PhotonMessageInfo info)
        {
            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're setting their own crouch
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[CROUCH ALLOWED] {info.Sender.NickName} set crouch to {setCrouch}");
                return true;
            }

            // Log the unauthorized attempt
            string action = setCrouch ? "crouch" : "stand up";
            AntiCheatPlugin.Logger.LogWarning($"[CROUCH DETECTED] {info.Sender.NickName} attempted to make {victimName} {action}!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to make</color> {{userColor}}{victimName}</color> {{leftColor}}{action}!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized crouch control on {victimName}");
            }

            return false; // Block the cheat attempt
        }

        // Drop item detection
        [HarmonyPatch(typeof(CharacterItems), "DropItemFromSlotRPC")]
        [HarmonyPrefix]
        public static bool PreDropItemFromSlotRPC(CharacterItems __instance, byte slotID, UnityEngine.Vector3 spawnPosition, PhotonMessageInfo info)
        {
            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're dropping their own item
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[DROP ITEM ALLOWED] {info.Sender.NickName} dropped item from slot {slotID}");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[DROP ITEM DETECTED] {info.Sender.NickName} attempted to drop item from {victimName}'s slot {slotID}!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to drop item from</color> {{userColor}}{victimName}</color>{{leftColor}}'s inventory!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized item drop from {victimName}");
            }

            return false; // Block the cheat attempt
        }

        // Remove item detection
        [HarmonyPatch(typeof(Player), "RPCRemoveItemFromSlot")]
        [HarmonyPrefix]
        public static bool PreRPCRemoveItemFromSlot(Player __instance, byte slotID, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're removing their own item
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[REMOVE ITEM ALLOWED] {info.Sender.NickName} removed item from slot {slotID}");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[REMOVE ITEM DETECTED] {info.Sender.NickName} attempted to remove item from {victimName}'s slot {slotID}!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to remove item from</color> {{userColor}}{victimName}</color>{{leftColor}}'s slot {slotID}!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized item removal from {victimName}");
            }

            return false; // Block the cheat attempt
        }

        // Character passed out customization detection
        [HarmonyPatch(typeof(CharacterCustomization), "CharacterPassedOut")]
        [HarmonyPrefix]
        public static bool PreCharacterPassedOut(CharacterCustomization __instance, PhotonMessageInfo info)
        {
            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if it's their own character
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[CUSTOMIZATION PASS OUT ALLOWED] {info.Sender.NickName}'s character passed out");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[CUSTOMIZATION PASS OUT DETECTED] {info.Sender.NickName} attempted to trigger pass out customization on {victimName}!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to trigger pass out customization on</color> {{userColor}}{victimName}</color>{{leftColor}}!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized pass out customization on {victimName}");
            }

            return false; // Block the attempt
        }

        // Character died customization detection
        [HarmonyPatch(typeof(CharacterCustomization), "CharacterDied")]
        [HarmonyPrefix]
        public static bool PreCharacterDied(CharacterCustomization __instance, PhotonMessageInfo info)
        {
            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if it's their own character
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[CUSTOMIZATION DEATH ALLOWED] {info.Sender.NickName}'s character died");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[CUSTOMIZATION DEATH DETECTED] {info.Sender.NickName} attempted to trigger death customization on {victimName}!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to trigger death customization on</color> {{userColor}}{victimName}</color>{{leftColor}}!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized death customization on {victimName}");
            }

            return false; // Block the attempt
        }

        // Play remove animation detection
        [HarmonyPatch(typeof(CharacterAnimations), "RPCA_PlayRemove")]
        [HarmonyPrefix]
        public static bool PreRPCA_PlayRemove(CharacterAnimations __instance, PhotonMessageInfo info)
        {
            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're playing animation on themselves
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[PLAY REMOVE ALLOWED] {info.Sender.NickName} played remove animation on themselves");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[PLAY REMOVE DETECTED] {info.Sender.NickName} attempted to play remove animation on {victimName}!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to play remove animation on</color> {{userColor}}{victimName}</color>{{leftColor}}!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized animation on {victimName}");
            }

            return false; // Block the cheat attempt
        }

        // Stop climbing detection
        [HarmonyPatch(typeof(CharacterClimbing), "StopClimbingRpc")]
        [HarmonyPrefix]
        public static bool PreStopClimbingRpc(CharacterClimbing __instance, PhotonMessageInfo info)
        {
            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're stopping their own climbing
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[STOP CLIMBING ALLOWED] {info.Sender.NickName} stopped climbing");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[STOP CLIMBING DETECTED] {info.Sender.NickName} attempted to stop {victimName}'s climbing!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to stop</color> {{userColor}}{victimName}</color>{{leftColor}}'s climbing!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized climbing stop on {victimName}");
            }

            return false; // Block the cheat attempt
        }

        // Stop rope climbing detection
        [HarmonyPatch(typeof(CharacterRopeHandling), "StopRopeClimbingRpc")]
        [HarmonyPrefix]
        public static bool PreStopRopeClimbingRpc(CharacterRopeHandling __instance, PhotonMessageInfo info)
        {
            var character = __instance.GetComponent<Character>();
            var photonView = character?.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're stopping their own rope climbing
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[STOP ROPE CLIMBING ALLOWED] {info.Sender.NickName} stopped rope climbing");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[STOP ROPE CLIMBING DETECTED] {info.Sender.NickName} attempted to stop {victimName}'s rope climbing!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to stop</color> {{userColor}}{victimName}</color>{{leftColor}}'s rope climbing!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized rope climbing stop on {victimName}");
            }

            return false; // Block the cheat attempt
        }

        // Shake rock detection
        [HarmonyPatch(typeof(ShakyIcicleIce2), "ShakeRock_Rpc")]
        [HarmonyPrefix]
        public static bool PreShakeRock_Rpc(ShakyIcicleIce2 __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they own the rock
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[SHAKE ROCK ALLOWED] {info.Sender.NickName} shook their own rock");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[SHAKE ROCK DETECTED] {info.Sender.NickName} attempted to shake rock!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to shake a rock/icicle!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, "Unauthorized rock shake");
            }

            return false; // Block the cheat attempt
        }

        // Shake bridge detection
        [HarmonyPatch(typeof(BreakableBridge), "ShakeBridge_Rpc")]
        [HarmonyPrefix]
        public static bool PreShakeBridge_Rpc(BreakableBridge __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they own the bridge
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[SHAKE BRIDGE ALLOWED] {info.Sender.NickName} shook their own bridge");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[SHAKE BRIDGE DETECTED] {info.Sender.NickName} attempted to shake bridge!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to shake a bridge!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, "Unauthorized bridge shake");
            }

            return false; // Block the cheat attempt
        }

        // Fire arrow detection
        [HarmonyPatch(typeof(ArrowShooter), "FireArrow_RPC")]
        [HarmonyPrefix]
        public static bool PreFireArrow_RPC(ArrowShooter __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they own the arrow shooter
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[FIRE ARROW ALLOWED] {info.Sender.NickName} fired their own arrow");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[FIRE ARROW DETECTED] {info.Sender.NickName} attempted to fire arrow from trap!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to fire an arrow trap!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, "Unauthorized arrow fire");
            }

            return false; // Block the cheat attempt
        }

        // Set flare lit detection (with item check)
        [HarmonyPatch(typeof(Flare), "SetFlareLitRPC")]
        [HarmonyPrefix]
        public static bool PreSetFlareLitRPC(Flare __instance, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Check if they have/had a flare within last 2 seconds
            bool hasFlare = AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "flare", 2f);

            // Also check if they currently hold a flare
            var allCharacters = UnityEngine.Object.FindObjectsOfType<Character>();
            foreach (var character in allCharacters)
            {
                var charPhotonView = character.GetComponent<PhotonView>();
                if (charPhotonView != null && charPhotonView.Owner != null &&
                    charPhotonView.Owner.ActorNumber == info.Sender.ActorNumber)
                {
                    var characterData = character.GetComponent<CharacterData>();
                    if (characterData != null && characterData.currentItem != null &&
                        characterData.currentItem.name.ToLower().Contains("flare"))
                    {
                        hasFlare = true;
                        break;
                    }
                }
            }

            if (hasFlare)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[SET FLARE ALLOWED] {info.Sender.NickName} lit flare (has flare item)");
                return true;
            }

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[SET FLARE DETECTED] {info.Sender.NickName} attempted to light flare without having a flare!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to light a flare without having one!</color>", false, false, true);

                // COMMENT OUT TO DISABLE PUNISHMENT
                // AntiCheatPlugin.BlockPlayer(info.Sender, "Unauthorized flare lighting - no flare item");
            }

            return false; // Block the cheat attempt
        }
    }
}
