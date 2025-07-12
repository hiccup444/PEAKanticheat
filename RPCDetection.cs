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

        // Dictionary to track RPC patterns and their meanings
        private static readonly Dictionary<string, RPCPattern> KnownRPCPatterns = new Dictionary<string, RPCPattern>
        {
            // Method ID 114 with Vector3 + bool = Teleport/Warp
            { "114", new RPCPattern {
                MethodId = "114",
                Description = "Teleport/Warp",
                ParameterCheck = (parameters) => parameters.Length == 2 && parameters[0] is Vector3 && parameters[1] is bool,
                RequiresMasterClient = true
            }},
            
            // Method ID 64 with bool = Revive
            { "64", new RPCPattern {
                MethodId = "64",
                Description = "Revive",
                ParameterCheck = (parameters) => parameters.Length == 1 && parameters[0] is bool,
                RequiresMasterClient = true
            }},
            
            // Method ID 69 with bool = Bee Swarm Control
            { "69", new RPCPattern {
                MethodId = "69",
                Description = "Bee Swarm Control",
                ParameterCheck = (parameters) => parameters.Length == 1 && parameters[0] is bool,
                RequiresMasterClient = true
            }},
            
            // Method ID 17 seems to be legitimate revive - don't flag this
            { "17", new RPCPattern {
                MethodId = "17",
                Description = "Legitimate Revive",
                ParameterCheck = (parameters) => true,
                RequiresMasterClient = false // This one seems OK for non-master clients
            }}
        };

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

        // Helper method to format parameters nicely
        private static string FormatParameters(object[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return "none";

            List<string> formattedParams = new List<string>();
            foreach (var param in parameters)
            {
                if (param is Vector3 vec)
                    formattedParams.Add($"({vec.x:F2}, {vec.y:F2}, {vec.z:F2})");
                else if (param is bool b)
                    formattedParams.Add(b.ToString());
                else
                    formattedParams.Add(param?.ToString() ?? "null");
            }

            return string.Join(", ", formattedParams);
        }

        // ===== NUMERIC RPC DETECTION =====

        [HarmonyPatch(typeof(PhotonNetwork), "ExecuteRpc")]
        [HarmonyPrefix]
        internal static bool PreExecuteRpc(Hashtable rpcData, Photon.Realtime.Player sender)
        {
            try
            {
                // Block ALL RPCs from soft-locked players for EVERYONE with the mod
                if (sender != null && AntiCheatPlugin.IsSoftLocked(sender.ActorNumber))
                {
                    if (AntiCheatPlugin.VerboseRPCLogging.Value)
                    {
                        AntiCheatPlugin.Logger.LogInfo($"[BLOCKED ALL] Blocked RPC from soft-locked player {sender.NickName} (#{sender.ActorNumber})");
                    }
                    return false; // Block for everyone with the mod
                }

                // Only do additional detection if we're the master client
                if (!PhotonNetwork.IsMasterClient || sender == null || sender.IsLocal)
                    return true;

                // Extract RPC info
                string methodName = "";
                int viewId = -1;
                object[] parameters = null;

                // Try to get method name from different possible keys
                if (rpcData.ContainsKey((byte)5)) methodName = rpcData[(byte)5]?.ToString() ?? "";
                else if (rpcData.ContainsKey((byte)3)) methodName = rpcData[(byte)3]?.ToString() ?? "";
                else if (rpcData.ContainsKey((byte)0)) methodName = rpcData[(byte)0]?.ToString() ?? "";

                // Get view ID
                if (rpcData.ContainsKey((byte)1)) viewId = (int)rpcData[(byte)1];

                // Get parameters
                if (rpcData.ContainsKey((byte)4)) parameters = rpcData[(byte)4] as object[];

                // Check for kill attempt (Method 108)
                if (methodName == "108" && parameters != null && parameters.Length == 1 && parameters[0] is Vector3)
                {
                    // Allow if sender is master client
                    if (sender.IsMasterClient)
                        return true;

                    AntiCheatPlugin.Logger.LogWarning($"[KILL BLOCKED] {sender.NickName} (#{sender.ActorNumber}) attempted to kill someone!");
                    AntiCheatPlugin.LogVisually($"{{userColor}}{sender.NickName}</color> {{leftColor}}attempted to illegally kill someone!</color>", false, false, true);
                    AntiCheatPlugin.SoftLockPlayer(sender, "Unauthorized kill attempt");
                    return false; // Block the RPC at network level
                }

                // Check for grow vine exploit (Method 107)
                if (methodName == "107" && parameters != null && parameters.Length == 3)
                {
                    // Vines should only be grown by master client or with magic beans
                    if (!sender.IsMasterClient)
                    {
                        AntiCheatPlugin.Logger.LogWarning($"[VINE GROW BLOCKED] {sender.NickName} (#{sender.ActorNumber}) attempted to grow vines!");
                        AntiCheatPlugin.LogVisually($"{{userColor}}{sender.NickName}</color> {{leftColor}}attempted to illegally grow vines!</color>", false, false, true);
                        AntiCheatPlugin.SoftLockPlayer(sender, "Unauthorized vine growth");
                        return false;
                    }
                }

                // Check for assign status exploit (Method 154)
                if (methodName == "154" && parameters != null && parameters.Length == 1 && parameters[0] is float[])
                {
                    // Status effects should only be assigned by master client
                    if (!sender.IsMasterClient)
                    {
                        AntiCheatPlugin.Logger.LogWarning($"[STATUS ASSIGN BLOCKED] {sender.NickName} (#{sender.ActorNumber}) attempted to assign status effects!");
                        AntiCheatPlugin.LogVisually($"{{userColor}}{sender.NickName}</color> {{leftColor}}attempted to illegally assign status effects!</color>", false, false, true);
                        AntiCheatPlugin.SoftLockPlayer(sender, "Unauthorized status assignment");
                        return false;
                    }
                }

                // Check for banana slip pattern (Method 164)
                if (methodName == "164" && parameters != null && parameters.Length == 1)
                {
                    if (int.TryParse(parameters[0]?.ToString(), out int targetViewId))
                    {
                        // Find the sender's character ViewID
                        Character senderCharacter = null;
                        var allCharacters = UnityEngine.Object.FindObjectsOfType<Character>();
                        foreach (var character in allCharacters)
                        {
                            var charPhotonView = character.GetComponent<PhotonView>();
                            if (charPhotonView != null && charPhotonView.Owner != null && charPhotonView.Owner.ActorNumber == sender.ActorNumber)
                            {
                                senderCharacter = character;
                                break;
                            }
                        }

                        // If the target ViewID is not the sender's character ViewID, it's a cheat
                        if (senderCharacter != null)
                        {
                            var senderViewId = senderCharacter.GetComponent<PhotonView>().ViewID;
                            if (targetViewId != senderViewId)
                            {
                                PhotonView targetView = PhotonView.Find(targetViewId);
                                string targetName = targetView?.Owner?.NickName ?? "Unknown";

                                AntiCheatPlugin.Logger.LogWarning($"[BANANA SLIP CHEAT] {sender.NickName} (#{sender.ActorNumber}) illegally slipped {targetName} (ViewID: {targetViewId})!");
                                AntiCheatPlugin.LogVisually($"{{userColor}}{sender.NickName}</color> {{leftColor}}attempted to illegally slip</color> {{userColor}}{targetName}!</color>", false, false, true);
                                AntiCheatPlugin.SoftLockPlayer(sender, "Unauthorized banana slip - targeting other player");
                                return false; // Block the RPC
                            }
                            // If they're slipping themselves, it's legitimate
                        }
                    }
                }

                // Special handling for revive (Method 64) - check if they have/had Scout Effigy
                if (methodName == "64" && parameters != null && parameters.Length == 1 && parameters[0] is bool)
                {
                    AntiCheatPlugin.Logger.LogInfo($"[DEBUG] Method 64 detected from {sender.NickName}");

                    // Check if they currently have OR recently had a Scout Effigy
                    bool hasOrHadScoutEffigy = false;

                    // First check current item
                    Character senderCharacter = null;
                    var allCharacters = UnityEngine.Object.FindObjectsOfType<Character>();
                    foreach (var character in allCharacters)
                    {
                        var charPhotonView = character.GetComponent<PhotonView>();
                        if (charPhotonView != null && charPhotonView.Owner != null && charPhotonView.Owner.ActorNumber == sender.ActorNumber)
                        {
                            senderCharacter = character;
                            break;
                        }
                    }

                    if (senderCharacter != null)
                    {
                        var characterData = senderCharacter.GetComponent<CharacterData>();
                        if (characterData != null && characterData.currentItem != null)
                        {
                            string itemName = characterData.currentItem.name.ToLower();
                            AntiCheatPlugin.Logger.LogInfo($"[DEBUG] Current item: {itemName}");
                            if (itemName.Contains("scout") && itemName.Contains("effigy"))
                            {
                                hasOrHadScoutEffigy = true;
                            }
                        }
                    }

                    // If not currently holding, check last held item
                    if (!hasOrHadScoutEffigy)
                    {
                        // Check with 2-second window
                        if (AntiCheatPlugin.PlayerHadItem(sender.ActorNumber, "scout", 2f) &&
                            AntiCheatPlugin.PlayerHadItem(sender.ActorNumber, "effigy", 2f))
                        {
                            hasOrHadScoutEffigy = true;
                            AntiCheatPlugin.Logger.LogInfo($"[DEBUG] Player had Scout Effigy within 2 seconds");
                        }
                    }

                    AntiCheatPlugin.Logger.LogInfo($"[DEBUG] hasOrHadScoutEffigy: {hasOrHadScoutEffigy}");

                    // Allow if master client OR has Scout Effigy
                    if (sender.IsMasterClient || hasOrHadScoutEffigy)
                    {
                        AntiCheatPlugin.Logger.LogInfo($"[DEBUG] Allowing revive - Master: {sender.IsMasterClient}, Scout Effigy: {hasOrHadScoutEffigy}");
                        return true;
                    }

                    // Block if not master client AND no Scout Effigy
                    AntiCheatPlugin.Logger.LogWarning($"[UNAUTHORIZED RPC] {sender.NickName} (#{sender.ActorNumber}) attempted to revive someone without Scout Effigy!");
                    AntiCheatPlugin.LogVisually($"{{userColor}}{sender.NickName}</color> {{leftColor}}attempted to illegally revive someone!</color>", false, false, true);
                    AntiCheatPlugin.SoftLockPlayer(sender, "Unauthorized revive - no Scout Effigy");
                    return false;
                }

                // Check for other numeric RPC patterns (excluding revive which we handle specially above)
                if (!string.IsNullOrEmpty(methodName) && KnownRPCPatterns.ContainsKey(methodName) && methodName != "64")
                {
                    var pattern = KnownRPCPatterns[methodName];
                    // Skip legitimate revive (method 17)
                    if (methodName == "17")
                        return true;
                    // Check if this RPC requires master client
                    if (pattern.RequiresMasterClient && !sender.IsMasterClient)
                    {
                        // Validate parameters match expected pattern
                        if (parameters != null && pattern.ParameterCheck(parameters))
                        {
                            string paramStr = FormatParameters(parameters);
                            AntiCheatPlugin.Logger.LogWarning($"[UNAUTHORIZED RPC] {sender.NickName} (#{sender.ActorNumber}) called {pattern.Description} (Method {methodName}) with params: {paramStr}");

                            // Updated visual log message based on pattern type
                            string visualMessage;
                            switch (pattern.Description)
                            {
                                case "Teleport/Warp":
                                    visualMessage = $"{{userColor}}{sender.NickName}</color> {{leftColor}}attempted to illegally teleport someone!</color>";
                                    break;
                                case "Bee Swarm Control":
                                    visualMessage = $"{{userColor}}{sender.NickName}</color> {{leftColor}}attempted to illegally control bee swarms!</color>";
                                    break;
                                default:
                                    visualMessage = $"{{userColor}}{sender.NickName}</color> {{leftColor}}illegally used {pattern.Description}!</color>";
                                    break;
                            }
                            AntiCheatPlugin.LogVisually(visualMessage, false, false, true);

                            // Determine punishment reason using traditional switch
                            string reason;
                            switch (pattern.Description)
                            {
                                case "Teleport/Warp":
                                    if (parameters[0] is Vector3 pos)
                                        reason = $"Unauthorized teleport to {pos}";
                                    else
                                        reason = "Unauthorized teleport";
                                    break;
                                case "Bee Swarm Control":
                                    reason = "Unauthorized bee swarm control";
                                    break;
                                default:
                                    reason = $"Unauthorized {pattern.Description}";
                                    break;
                            }
                            AntiCheatPlugin.SoftLockPlayer(sender, reason);
                            return false; // Block the RPC
                        }
                    }
                }

                // Verbose logging
                if (AntiCheatPlugin.VerboseRPCLogging.Value)
                {
                    string paramStr = parameters != null ? FormatParameters(parameters) : "none";
                    AntiCheatPlugin.Logger.LogInfo($"[HOST RPC RECEIVED] From {sender.NickName} (#{sender.ActorNumber}) -> '{methodName}' on ViewID:{viewId}");
                    if (parameters != null && parameters.Length > 0)
                    {
                        AntiCheatPlugin.Logger.LogInfo($"    Parameters: {paramStr}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AntiCheatPlugin.Logger.LogError($"Error in RPC detection: {ex.Message}");
                return true;
            }
        }

        [HarmonyPatch(typeof(PhotonNetwork), "OnEvent")]
        [HarmonyPrefix]
        public static bool PreOnEvent(EventData photonEvent)
        {
            int sender = photonEvent.Sender;

            // Block events from soft-locked players for EVERYONE with the mod
            if (sender > 0 && AntiCheatPlugin.IsSoftLocked(sender))
            {
                // Don't log event 201 as it spams the console
                if (photonEvent.Code != 201)
                {
                    AntiCheatPlugin.Logger.LogInfo($"[BLOCKED EVENT] Blocked event {photonEvent.Code} from soft-locked player (Actor #{sender})");
                }
                return false; // Block for everyone
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

                    // NEW: Rate limiting check
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

            // If this is our object and someone else is trying to claim it
            if (__instance.Owner != null && __instance.Owner.IsLocal && value != PhotonNetwork.LocalPlayer.ActorNumber)
            {
                // Log it for debugging but DON'T block legitimate players
                AntiCheatPlugin.Logger.LogInfo($"[OWNERSHIP] Actor #{value} taking ownership of view {__instance.ViewID} (was ours)");
            }

            return true; // Allow all legitimate ownership transfers
        }

        // ===== EXISTING DETECTIONS =====

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

        // Player killing
        [HarmonyPatch(typeof(Character), "RPCA_Die")]
        [HarmonyPrefix]
        public static bool PreCharacterRPCA_Die(Character __instance, PhotonMessageInfo info)
        {
            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Character.RPCA_Die called by {info.Sender?.NickName} on {__instance.GetComponent<PhotonView>()?.Owner?.NickName}");

            var photonView = __instance.GetComponent<PhotonView>();
            if (info.Sender == null || info.Sender.IsMasterClient || (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber))
                return true; // Allow legitimate kills

            AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} (#{info.Sender.ActorNumber}) attempted to kill {photonView?.Owner?.NickName} (#{photonView?.Owner?.ActorNumber})!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}attempted to kill</color> {{userColor}}{photonView?.Owner?.NickName}</color>{{leftColor}}!</color>", false, false, true);
            AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized kill attempt");
            return false; // Block the kill
        }

        [HarmonyPatch(typeof(PhotonNetwork), "RaiseEventInternal")]
        [HarmonyPrefix]
        internal static bool PreRaiseEventInternal(byte eventCode, object eventContent, RaiseEventOptions raiseEventOptions, SendOptions sendOptions)
        {
            var localPlayer = PhotonNetwork.LocalPlayer;

            // If sender is soft-locked, block all critical events
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
                        AntiCheatPlugin.Logger.LogWarning($"[EVENT BLOCKED] Blocked event {eventCode} from soft-locked player {localPlayer.NickName}");
                        return false;
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
    }
}
