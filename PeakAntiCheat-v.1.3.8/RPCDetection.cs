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

            // Block events from blocked players, but allow specific "leaving" events
            if (sender > 0 && AntiCheatPlugin.IsBlocked(sender))
            {
                // Allow specific events that indicate the player is leaving or cleaning up
                // These are typically Photon's internal events for player leaving
                if (photonEvent.Code == 1 || // Player left room event
                    photonEvent.Code == 2 || // Player properties changed (for leaving)
                    photonEvent.Code == 3)   // Room properties changed (for leaving)
                {
                    return true; // Allow these events to pass through
                }
                
                return false; // Block all other events silently without any logging
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

                            // If more than 5 ownership requests per second
                            if (_ownershipRequestCount[sender] > 10)
                            {
                                Photon.Realtime.Player senderPlayer = PhotonNetwork.CurrentRoom?.GetPlayer(sender);
                                if (senderPlayer != null && !senderPlayer.IsLocal)
                                {
                                    // Check detection settings
                                    bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.OwnershipTheft);
                                    bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.OwnershipTheft);
                                    bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.OwnershipTheft);

                                    // Log to console if enabled
                                    if (shouldLog)
                                    {
                                        AntiCheatPlugin.Logger.LogWarning($"[MASS OWNERSHIP DETECTED] {senderPlayer.NickName} attempted {_ownershipRequestCount[sender]} ownership transfers in 1 second!");
                                    }

                                    // Block player if auto-block is enabled
                                    if (shouldBlock && PhotonNetwork.IsMasterClient)
                                    {
                                        AntiCheatPlugin.BlockPlayer(senderPlayer, "Mass ownership theft attempt", DetectionType.OwnershipTheft);
                                    }

                                    // Record the detection
                                    DetectionManager.RecordDetection(DetectionType.OwnershipTheft, senderPlayer, "Mass ownership theft attempt");

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

                                // Check detection settings
                                bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.OwnershipTheft);
                                bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.OwnershipTheft);
                                bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.OwnershipTheft);

                                // Log to console if enabled
                                if (shouldLog)
                                {
                                    AntiCheatPlugin.Logger.LogWarning($"[OWNERSHIP THEFT BLOCKED] {senderPlayer.NickName} tried to steal ownership of your view {viewId}!");
                                }

                                // Block player if auto-block is enabled
                                if (shouldBlock && PhotonNetwork.IsMasterClient)
                                {
                                    AntiCheatPlugin.BlockPlayer(senderPlayer, "Attempted ownership theft", DetectionType.OwnershipTheft);
                                }

                                // Record the detection
                                DetectionManager.RecordDetection(DetectionType.OwnershipTheft, senderPlayer, "Attempted ownership theft");

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

                                        // Check detection settings
                                        bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedDestroy);
                                        bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedDestroy);
                                        bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedDestroy);

                                        // Log to console if enabled
                                        if (shouldLog)
                                        {
                                            AntiCheatPlugin.Logger.LogError($"[CHARACTER DESTROY BLOCKED] They tried to destroy YOUR CHARACTER!");
                                        }

                                        // Show visual log if enabled
                                        if (shouldShowVisual && senderPlayer != null)
                                        {
                                            AntiCheatPlugin.LogVisually($"{{userColor}}{senderPlayer.NickName}</color> {{leftColor}}tried to destroy your character!</color>", false, false, true);
                                        }

                                        // Block player if auto-block is enabled
                                        if (shouldBlock && PhotonNetwork.IsMasterClient && senderPlayer != null)
                                        {
                                            AntiCheatPlugin.BlockPlayer(senderPlayer, "Attempted to destroy your character", DetectionType.UnauthorizedDestroy);
                                        }

                                        // Record the detection
                                        if (senderPlayer != null)
                                        {
                                            DetectionManager.RecordDetection(DetectionType.UnauthorizedDestroy, senderPlayer, "Attempted to destroy your character");
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

                    // Check detection settings
                    bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.OwnershipTheft);
                    bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.OwnershipTheft);
                    bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.OwnershipTheft);

                    // Log to console if enabled
                    if (shouldLog)
                    {
                        AntiCheatPlugin.Logger.LogError($"[CHARACTER THEFT BLOCKED] {thief?.NickName ?? $"Actor {value}"} tried to steal your character!");
                    }

                                                            // Show visual log if enabled
                                        if (shouldShowVisual && thief != null)
                                        {
                                            AntiCheatPlugin.LogVisually($"{{userColor}}{thief.NickName}</color> {{leftColor}}tried to steal your character!</color>", false, false, true);
                                        }

                    // Block player if auto-block is enabled
                    if (shouldBlock && PhotonNetwork.IsMasterClient && thief != null)
                    {
                        AntiCheatPlugin.BlockPlayer(thief, "Character ownership theft attempt", DetectionType.OwnershipTheft);
                    }

                    // Record the detection
                    if (thief != null)
                    {
                        DetectionManager.RecordDetection(DetectionType.OwnershipTheft, thief, "Character ownership theft attempt");
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

        // Campfire extinguishing
        [HarmonyPatch(typeof(Campfire), "Extinguish_Rpc")]
        [HarmonyPrefix]
        public static bool PreCampfireExtinguish_Rpc(Campfire __instance, PhotonMessageInfo info)
        {
            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Campfire.Extinguish_Rpc called by {info.Sender?.NickName}");

            var photonView = __instance.GetComponent<PhotonView>();
            bool isValid = info.Sender == null || (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber);

            if (!isValid)
            {
                // Check detection settings
                bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedCampfireModification);
                bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedCampfireModification);
                bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedCampfireModification);

                // Log the unauthorized attempt
                if (shouldLog)
                {
                    AntiCheatPlugin.Logger.LogWarning($"[CAMPFIRE DETECTED] {info.Sender.NickName} (#{info.Sender.ActorNumber}) tried to extinguish the {__instance.advanceToSegment} campfire!");
                }

                // Block player if auto-block is enabled
                if (shouldBlock)
                {
                    AntiCheatPlugin.BlockPlayer(info.Sender, "Unauthorized campfire extinguish", DetectionType.UnauthorizedCampfireModification);
                }

                // Record the detection
                DetectionManager.RecordDetection(DetectionType.UnauthorizedCampfireModification, info.Sender, "Unauthorized campfire extinguish");

                return false; // Block the RPC
            }

            return isValid;
        }

        // Campfire lighting detection - only when not everyone is in range
        [HarmonyPatch(typeof(Campfire), "Light_Rpc")]
        [HarmonyPrefix]
        public static bool PreCampfireLight_Rpc(Campfire __instance, PhotonMessageInfo info)
        {
            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Campfire.Light_Rpc called by {info.Sender?.NickName}");

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Check if everyone is in range using the campfire's method
            string printout;
            bool everyoneInRange = __instance.EveryoneInRange(out printout);

            // If everyone is in range, allow the lighting
            if (everyoneInRange)
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"[LIGHT CAMPFIRE ALLOWED] {info.Sender.NickName} lit campfire - everyone in range");
                return true;
            }

            // If not everyone is in range, this is a cheat attempt
            AntiCheatPlugin.Logger.LogWarning($"[LIGHT CAMPFIRE DETECTED] {info.Sender.NickName} attempted to light {__instance.advanceToSegment} campfire with players out of range!");

            // Log who was out of range
            if (!string.IsNullOrEmpty(printout))
            {
                AntiCheatPlugin.Logger.LogWarning($"[LIGHT CAMPFIRE] Out of range players: {printout}");
            }

            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}tried to light {__instance.advanceToSegment} campfire with players out of range!</color>", false, false, true);

            // COMMENT OUT TO DISABLE PUNISHMENT
            // AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized campfire lighting - players out of range");

            return false; // Block the cheat attempt
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
                        return true;
                    }

                    // Block if they don't own it
                    AntiCheatPlugin.Logger.LogWarning($"[DESTROY BLOCKED] Blocked player tried to remove {go.name} owned by {photonView.Owner.NickName}");
                    return false;
                }
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

            // Add debug logging
            if (photonView != null && photonView.Owner != null)
            {
                AntiCheatPlugin.Logger.LogInfo($"[REVIVE DEBUG] Target: {victimName} (Actor #{photonView.Owner.ActorNumber}), Sender: {info.Sender.NickName} (Actor #{info.Sender.ActorNumber})");
                AntiCheatPlugin.Logger.LogInfo($"[REVIVE DEBUG] Is target in grace period? {AntiCheatPlugin.IsInSpawnGracePeriod(photonView.Owner.ActorNumber)}");
                AntiCheatPlugin.Logger.LogInfo($"[REVIVE DEBUG] Is sender in grace period? {AntiCheatPlugin.IsInSpawnGracePeriod(info.Sender.ActorNumber)}");
            }

            // Check if the TARGET is in spawn grace period
            if (photonView != null && photonView.Owner != null &&
                AntiCheatPlugin.IsInSpawnGracePeriod(photonView.Owner.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN TELEPORT ALLOWED] {info.Sender.NickName} teleported {victimName} who is in spawn grace period.");
                return true;
            }

            // Also check if the SENDER is in spawn grace period
            if (AntiCheatPlugin.IsInSpawnGracePeriod(info.Sender.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN TELEPORT ALLOWED] {info.Sender.NickName} (in spawn grace period) teleported {victimName} who is in spawn grace period.");
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

            // Check detection is enabled at all
            if (!DetectionManager.IsDetectionEnabled(DetectionType.UnauthorizedRevive))
            {
                return true; // Allow the RPC if detection is disabled
            }

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedRevive);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedRevive);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedRevive);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                AntiCheatPlugin.Logger.LogWarning($"[REVIVE DETECTED] {info.Sender.NickName} attempted unauthorized revive on {victimName}!");
            }

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized revive of {victimName}", DetectionType.UnauthorizedRevive);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedRevive, info.Sender, $"Unauthorized revive of {victimName}");

            return false; // Always block the RPC call itself
        }

        // Character.WarpPlayerRPC - Teleport detection
        [HarmonyPatch(typeof(Character), "WarpPlayerRPC")]
        [HarmonyPrefix]
        public static bool PreWarpPlayerRPC(Character __instance, Vector3 position, bool poof, PhotonMessageInfo info)
        {
            var photonView = __instance.GetComponent<PhotonView>();
            string victimName = photonView?.Owner?.NickName ?? "Unknown";

            // Always allow if sender is null (system) or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Add debug logging
            if (photonView != null && photonView.Owner != null)
            {
                AntiCheatPlugin.Logger.LogInfo($"[WARP DEBUG] Target: {victimName} (Actor #{photonView.Owner.ActorNumber}), Sender: {info.Sender.NickName} (Actor #{info.Sender.ActorNumber})");
                AntiCheatPlugin.Logger.LogInfo($"[WARP DEBUG] Is target in grace period? {AntiCheatPlugin.IsInSpawnGracePeriod(photonView.Owner.ActorNumber)}");
                AntiCheatPlugin.Logger.LogInfo($"[WARP DEBUG] Is sender in grace period? {AntiCheatPlugin.IsInSpawnGracePeriod(info.Sender.ActorNumber)}");
                AntiCheatPlugin.Logger.LogInfo($"[WARP DEBUG] Position: {position}");
            }

            // Check if position is infinity (black screen attempt)
            bool isInfinityWarp = float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z) ||
                                  float.IsNegativeInfinity(position.x) || float.IsNegativeInfinity(position.y) || float.IsNegativeInfinity(position.z);

            if (isInfinityWarp)
            {
                // NEVER allow infinity warps from non-master clients, even during spawn grace period
                AntiCheatPlugin.Logger.LogError($"[INFINITY WARP BLOCKED] {info.Sender.NickName} tried to warp {victimName} to infinity!");

                // Check detection settings
                bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.InfinityWarp);
                bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.InfinityWarp);

                // Log the unauthorized attempt
                if (shouldLog)
                {
                    AntiCheatPlugin.Logger.LogError($"[INFINITY WARP BLOCKED] {info.Sender.NickName} tried to warp {victimName} to infinity!");
                }

                // Block player if auto-block is enabled
                if (shouldBlock)
                {
                    AntiCheatPlugin.BlockPlayer(info.Sender, $"Infinity warp attempt on {victimName}", DetectionType.InfinityWarp);
                }

                // Record the detection
                DetectionManager.RecordDetection(DetectionType.InfinityWarp, info.Sender, $"Infinity warp attempt on {victimName}");

                return false; // Always block infinity warps
            }

            // Check if the TARGET is in spawn grace period
            if (photonView != null && photonView.Owner != null &&
                AntiCheatPlugin.IsInSpawnGracePeriod(photonView.Owner.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN TELEPORT ALLOWED] {info.Sender.NickName} teleported {victimName} who is in spawn grace period to {position}");
                return true;
            }

            // Also check if the SENDER is in spawn grace period
            if (AntiCheatPlugin.IsInSpawnGracePeriod(info.Sender.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN TELEPORT ALLOWED] {info.Sender.NickName} (in spawn grace period) teleported {victimName} to {position}");
                return true;
            }

            // For non-spawn warps, check if they have Scout Effigy
            if (AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "scout", 2f) &&
                AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "effigy", 2f))
            {
                AntiCheatPlugin.Logger.LogInfo($"[WARP ALLOWED] {info.Sender.NickName} used Scout Effigy to warp {victimName} to {position}");
                return true; // Allow Scout Effigy warps (except infinity)
            }

            // Check detection settings for unauthorized warp
            bool shouldBlockWarp = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedWarp);
            bool shouldShowVisualWarp = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedWarp);
            bool shouldLogWarp = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedWarp);

            // Log the unauthorized attempt
            if (shouldLogWarp)
            {
                AntiCheatPlugin.Logger.LogWarning($"[WARP DETECTED] {info.Sender.NickName} attempted to warp {victimName} without Scout Effigy!");
            }

            // Block player if auto-block is enabled
            if (shouldBlockWarp)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized warp of {victimName} - no Scout Effigy", DetectionType.UnauthorizedWarp);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedWarp, info.Sender, $"Unauthorized warp of {victimName} - no Scout Effigy");

            return false; // Always block the RPC call itself
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

            // Check if the TARGET is in spawn grace period
            if (photonView != null && photonView.Owner != null &&
                AntiCheatPlugin.IsInSpawnGracePeriod(photonView.Owner.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN TELEPORT ALLOWED] {info.Sender.NickName} teleported {victimName} who is in spawn grace period.");
                return true;
            }

            // Also check if the SENDER is in spawn grace period
            if (AntiCheatPlugin.IsInSpawnGracePeriod(info.Sender.ActorNumber))
            {
                AntiCheatPlugin.Logger.LogInfo($"[SPAWN TELEPORT ALLOWED] {info.Sender.NickName} (in spawn grace period) teleported {victimName} who is in spawn grace period.");
                return true;
            }

            // Check if detection is enabled at all
            if (!DetectionManager.IsDetectionEnabled(DetectionType.UnauthorizedRevive))
            {
                return true; // Allow the RPC if detection is disabled
            }

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedRevive);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedRevive);

            AntiCheatPlugin.Logger.LogWarning($"[REVIVE DETECTED] {info.Sender.NickName} attempted unauthorized revive on {victimName}!");

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized revive of {victimName}", DetectionType.UnauthorizedRevive);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedRevive, info.Sender, $"Unauthorized revive of {victimName}");

            return false; // Always block the RPC call itself
        }

        // Character.RPCA_Die - Kill detection
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

            // Check if detection is enabled at all
            if (!DetectionManager.IsDetectionEnabled(DetectionType.UnauthorizedKill))
            {
                return true; // Allow the RPC if detection is disabled
            }

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedKill);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedKill);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedKill);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                AntiCheatPlugin.Logger.LogWarning($"[KILL DETECTED] {info.Sender.NickName} attempted to kill {victimName}!");
            }

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized kill attempt on {victimName}", DetectionType.UnauthorizedKill);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedKill, info.Sender, $"Unauthorized kill attempt on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedStatusEffect);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedStatusEffect);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedStatusEffect);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                AntiCheatPlugin.Logger.LogWarning($"[STATUS DETECTED] {info.Sender.NickName} attempted to assign status effects to {targetName}!");
            }

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized status assignment to {targetName}", DetectionType.UnauthorizedStatusEffect);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedStatusEffect, info.Sender, $"Unauthorized status assignment to {targetName}");

            return false; // Always block the RPC call itself
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

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Check if sender is trying to slip someone else
            if (targetView.Owner != null && info.Sender != null &&
                targetView.Owner.ActorNumber != info.Sender.ActorNumber)
            {
                // Check detection settings
                bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedBananaSlip);
                bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedBananaSlip);
                bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedBananaSlip);

                // Log the unauthorized attempt
                if (shouldLog)
                {
                    AntiCheatPlugin.Logger.LogWarning($"[BANANA SLIP DETECTED] {info.Sender.NickName} tried to slip {victimName}!");
                }

                // Block player if auto-block is enabled
                if (shouldBlock)
                {
                    AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized banana slip on {victimName}", DetectionType.UnauthorizedBananaSlip);
                }

                // Record the detection
                DetectionManager.RecordDetection(DetectionType.UnauthorizedBananaSlip, info.Sender, $"Unauthorized banana slip on {victimName}");

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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                AntiCheatPlugin.Logger.LogWarning($"[PASS OUT DETECTED] {info.Sender.NickName} attempted to make {victimName} pass out!");
            }

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized pass out on {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized pass out on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                AntiCheatPlugin.Logger.LogWarning($"[UNPASS OUT DETECTED] {info.Sender.NickName} attempted to wake up {victimName}!");
            }

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized wake up of {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized wake up of {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                AntiCheatPlugin.Logger.LogWarning($"[FALL SHAKE DETECTED] {info.Sender.NickName} attempted to make {victimName} fall with screen shake!");
            }

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized fall with shake on {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized fall with shake on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                AntiCheatPlugin.Logger.LogWarning($"[JUMP DETECTED] {info.Sender.NickName} attempted to make {victimName} jump!");
            }

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized jump on {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized jump on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                string action = setCrouch ? "crouch" : "stand up";
                AntiCheatPlugin.Logger.LogWarning($"[CROUCH DETECTED] {info.Sender.NickName} attempted to make {victimName} {action}!");
            }

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized crouch control on {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized crouch control on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedItemDrop);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedItemDrop);
            bool shouldLog = DetectionManager.ShouldLogToConsole(DetectionType.UnauthorizedItemDrop);

            // Log the unauthorized attempt
            if (shouldLog)
            {
                AntiCheatPlugin.Logger.LogWarning($"[DROP ITEM DETECTED] {info.Sender.NickName} attempted to drop item from {victimName}'s slot {slotID}!");
            }


            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized item drop from {victimName}", DetectionType.UnauthorizedItemDrop);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedItemDrop, info.Sender, $"Unauthorized item drop from {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[CUSTOMIZATION PASS OUT DETECTED] {info.Sender.NickName} attempted to trigger pass out customization on {victimName}!");

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized pass out customization on {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized pass out customization on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[CUSTOMIZATION DEATH DETECTED] {info.Sender.NickName} attempted to render {victimName} dead!");

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized death customization on {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized death customization on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedEmote);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedEmote);

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[PLAY REMOVE DETECTED] {info.Sender.NickName} attempted to play emote on {victimName}!");

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized animation on {victimName}", DetectionType.UnauthorizedEmote);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedEmote, info.Sender, $"Unauthorized animation on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[STOP CLIMBING DETECTED] {info.Sender.NickName} attempted to stop {victimName}'s climbing!");

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized climbing stop on {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized climbing stop on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedMovement);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedMovement);

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[STOP ROPE CLIMBING DETECTED] {info.Sender.NickName} attempted to stop {victimName}'s rope climbing!");

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, $"Unauthorized rope climbing stop on {victimName}", DetectionType.UnauthorizedMovement);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedMovement, info.Sender, $"Unauthorized rope climbing stop on {victimName}");

            return false; // Always block the RPC call itself
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

            // Check detection settings
            bool shouldBlock = DetectionManager.ShouldAutoBlock(DetectionType.UnauthorizedFlareLighting);
            bool shouldShowVisual = DetectionManager.ShouldShowVisualWarning(DetectionType.UnauthorizedFlareLighting);

            // Log the unauthorized attempt
            AntiCheatPlugin.Logger.LogWarning($"[SET FLARE DETECTED] {info.Sender.NickName} attempted to light flare without having a flare!");

            // Block player if auto-block is enabled
            if (shouldBlock)
            {
                AntiCheatPlugin.BlockPlayer(info.Sender, "Unauthorized flare lighting - no flare item", DetectionType.UnauthorizedFlareLighting);
            }

            // Record the detection
            DetectionManager.RecordDetection(DetectionType.UnauthorizedFlareLighting, info.Sender, "Unauthorized flare lighting - no flare item");

            return false; // Always block the RPC call itself
        }
    }
}