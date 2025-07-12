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
        private static readonly byte keyByte1 = 1; // View ID
        private static readonly byte keyByte4 = 4; // Parameters

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

            // Block events from soft-locked players silently
            if (sender > 0 && AntiCheatPlugin.IsSoftLocked(sender))
            {
                return false; // Block without logging
            }

            // Log ownership events if we're master client
            if (PhotonNetwork.IsMasterClient && (photonEvent.Code == 209 || photonEvent.Code == 210 || photonEvent.Code == 212))
            {
                AntiCheatPlugin.Logger.LogInfo($"[OWNERSHIP EVENT] Code {photonEvent.Code} from actor #{sender}, soft-locked: {AntiCheatPlugin.IsSoftLocked(sender)}");
            }

            // Additional protection for ownership theft (works for everyone)
            if (photonEvent.Code == 210) // OwnershipTransfer
            {
                int[] data = (int[])photonEvent.CustomData;
                if (data != null && data.Length >= 2)
                {
                    int viewId = data[0];
                    int newOwner = data[1];

                    // First check: Is the sender soft-locked? Block ALL their ownership attempts
                    if (AntiCheatPlugin.IsSoftLocked(sender))
                    {
                        AntiCheatPlugin.Logger.LogWarning($"[OWNERSHIP BLOCKED] Soft-locked player (Actor #{sender}) tried to transfer ownership of view {viewId}");
                        return false; // Block all ownership transfers from soft-locked players
                    }

                    // Rate limiting check
                    if (_ownershipRequestCount.ContainsKey(sender))
                    {
                        if (_lastOwnershipRequest.ContainsKey(sender) &&
                            (DateTime.Now - _lastOwnershipRequest[sender]).TotalSeconds < 1)
                        {
                            _ownershipRequestCount[sender]++;

                            // If more than 5 ownership requests per second
                            if (_ownershipRequestCount[sender] > 5)
                            {
                                Photon.Realtime.Player senderPlayer = PhotonNetwork.CurrentRoom?.GetPlayer(sender);
                                if (senderPlayer != null && !senderPlayer.IsLocal)
                                {
                                    AntiCheatPlugin.Logger.LogWarning($"[MASS OWNERSHIP DETECTED] {senderPlayer.NickName} attempted {_ownershipRequestCount[sender]} ownership transfers in 1 second!");

                                    // Only master client can punish
                                    if (PhotonNetwork.IsMasterClient)
                                    {
                                        AntiCheatPlugin.LogVisually($"{{userColor}}{senderPlayer.NickName}</color> {{leftColor}}attempted mass ownership theft!</color>", false, false, true);
                                        AntiCheatPlugin.SoftLockPlayer(senderPlayer, "Mass ownership theft attempt");
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

                                // Only show visual logs and soft-lock if we're master client
                                if (PhotonNetwork.IsMasterClient)
                                {
                                    AntiCheatPlugin.LogVisually($"{{userColor}}{senderPlayer.NickName}</color> {{leftColor}}tried to steal your character!</color>", false, false, true);
                                    AntiCheatPlugin.SoftLockPlayer(senderPlayer, "Attempted ownership theft");
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
                                            AntiCheatPlugin.SoftLockPlayer(senderPlayer, "Attempted to destroy your character");
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

            // Check if the player trying to take ownership is soft-locked
            if (AntiCheatPlugin.IsSoftLocked(value))
            {
                AntiCheatPlugin.Logger.LogWarning($"[BLOCKED] Soft-locked player (actor #{value}) tried to take ownership of view {__instance.ViewID}");
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
                        AntiCheatPlugin.SoftLockPlayer(thief, "Character ownership theft attempt");
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
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Campfire log manipulation");
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
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized campfire extinguish");
                return false; // Block the RPC
            }

            return isValid;
        }

        [HarmonyPatch(typeof(PhotonNetwork), "RaiseEventInternal")]
        [HarmonyPrefix]
        internal static bool PreRaiseEventInternal(byte eventCode, object eventContent, RaiseEventOptions raiseEventOptions, SendOptions sendOptions)
        {
            var localPlayer = PhotonNetwork.LocalPlayer;

            // If sender is soft-locked, block all critical events silently
            if (localPlayer != null && AntiCheatPlugin.IsSoftLocked(localPlayer.ActorNumber))
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

                // If a soft-locked player is trying to destroy something
                if (localPlayer != null && AntiCheatPlugin.IsSoftLocked(localPlayer.ActorNumber))
                {
                    // Allow if they own it
                    if (photonView.Owner.ActorNumber == localPlayer.ActorNumber)
                    {
                        AntiCheatPlugin.Logger.LogInfo($"[DESTROY ALLOWED] Soft-locked player destroying their own object {go.name}");
                        return true;
                    }

                    // Block if they don't own it
                    AntiCheatPlugin.Logger.LogWarning($"[DESTROY BLOCKED] Soft-locked player tried to remove {go.name} owned by {photonView.Owner.NickName}");
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

            // Always allow if sender is null or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Allow if they're reviving themselves
            if (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber)
                return true;

            // Check if they have/had Scout Effigy before flagging as unauthorized (with 2-second window)
            if (AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "scout", 2f) &&
                AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "effigy", 2f))
            {
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    AntiCheatPlugin.Logger.LogInfo($"{info.Sender.NickName} used Scout Effigy to revive - legitimate");
                return true; // Allow the revive
            }

            // Unauthorized revive attempt
            AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} (#{info.Sender.ActorNumber}) attempted to revive {photonView?.Owner?.NickName} (#{photonView?.Owner?.ActorNumber}) without permission!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}attempted to revive</color> {{userColor}}{photonView?.Owner?.NickName}</color>{{leftColor}} without permission!</color>", false, false, true);
            AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized revive");
            return false; // Block the revive
        }

        [HarmonyPatch(typeof(Character), "WarpPlayerRPC")]
        [HarmonyPrefix]
        public static bool PreWarpPlayerRPC(Character __instance, Vector3 position, bool poof, PhotonMessageInfo info)
        {
            // Always allow if sender is null (system) or master client
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Check if position is infinity (black screen attempt)
            bool isInfinityWarp = float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z) ||
                                  float.IsNegativeInfinity(position.x) || float.IsNegativeInfinity(position.y) || float.IsNegativeInfinity(position.z);

            if (isInfinityWarp)
            {
                // NEVER allow infinity warps from non-master clients, even with Scout Effigy (Revive)
                AntiCheatPlugin.Logger.LogError($"[BLACK SCREEN BLOCKED] {info.Sender.NickName} tried to warp {__instance.name} to infinity!");

                var photonView = __instance.GetComponent<PhotonView>();
                if (photonView != null && photonView.IsMine)
                {
                    AntiCheatPlugin.Logger.LogError($"[PROTECTED] Blocked black screen attempt on local player!");
                }

                if (PhotonNetwork.IsMasterClient)
                {
                    AntiCheatPlugin.SoftLockPlayer(info.Sender, "Black screen attempt");
                }

                return false; // Always block infinity warps
            }

            // For non-infinity warps, check if they have Scout Effigy
            if (AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "scout", 2f) &&
                AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "effigy", 2f))
            {
                AntiCheatPlugin.Logger.LogInfo($"[WARP ALLOWED] {info.Sender.NickName} used Scout Effigy to warp player to {position}");
                return true; // Allow Scout Effigy warps (except infinity)
            }

            // Block unauthorized warps
            var targetView = __instance.GetComponent<PhotonView>();
            AntiCheatPlugin.Logger.LogWarning($"[WARP BLOCKED] {info.Sender.NickName} attempted to warp {targetView?.Owner?.NickName} without Scout Effigy!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized warp - no Scout Effigy");
            }

            return false;
        }

        // Revive protection
        [HarmonyPatch(typeof(Character), "RPCA_Revive")]
        [HarmonyPrefix]
        public static bool PreRPCA_Revive(Character __instance, PhotonMessageInfo info)
        {
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            // Check for Scout Effigy
            if (AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "scout", 2f) &&
                AntiCheatPlugin.PlayerHadItem(info.Sender.ActorNumber, "effigy", 2f))
            {
                return true; // Legitimate revive with item
            }

            AntiCheatPlugin.Logger.LogWarning($"[REVIVE BLOCKED] {info.Sender.NickName} attempted unauthorized revive!");
            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized revive");
            }
            return false;
        }

        // Kill protection
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

            AntiCheatPlugin.Logger.LogWarning($"[KILL BLOCKED] {info.Sender.NickName} attempted to kill {photonView?.Owner?.NickName}!");
            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized kill");
            }
            return false;
        }

        [HarmonyPatch(typeof(CharacterAfflictions), "ApplyStatusesFromFloatArrayRPC")]
        [HarmonyPrefix]
        public static bool PreAssignStatusRPC(Character __instance, float[] deserializedData, PhotonMessageInfo info)
        {
            // Allow system or host
            if (info.Sender == null || info.Sender.IsMasterClient)
                return true;

            var photonView = __instance.GetComponent<PhotonView>();

            // Allow if the sender is modifying their own character
            if (photonView != null && photonView.Owner != null &&
                info.Sender.ActorNumber == photonView.Owner.ActorNumber)
            {
                AntiCheatPlugin.Logger.LogInfo($"[STATUS ALLOWED] {info.Sender.NickName} applying status to themselves");
                return true;
            }

            // Block if trying to modify another player
            string targetName = photonView?.Owner?.NickName ?? "Unknown";
            AntiCheatPlugin.Logger.LogWarning($"[STATUS BLOCKED] {info.Sender.NickName} attempted to assign status effects to {targetName}!");

            if (PhotonNetwork.IsMasterClient)
            {
                AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized status assignment to another player");
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

            // Check if sender is trying to slip someone else
            if (targetView.Owner != null && info.Sender != null &&
                targetView.Owner.ActorNumber != info.Sender.ActorNumber)
            {
                AntiCheatPlugin.Logger.LogWarning($"[BANANA SLIP BLOCKED] {info.Sender.NickName} tried to slip {targetView.Owner.NickName}!");
                if (PhotonNetwork.IsMasterClient)
                {
                    AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized banana slip - targeting other player");
                }
                return false;
            }

            return true; // Allow self-slips or slips where sender owns the target
        }
    }
}
