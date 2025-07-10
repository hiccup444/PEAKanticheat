using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;

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
                // Only process if we're the master client and sender is another player
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
                                AntiCheatPlugin.LogVisually($"{{userColor}}{sender.NickName}</color> {{leftColor}}illegally slipped</color> {{userColor}}{targetName}!</color>", false, false, true);
                                AntiCheatPlugin.SoftLockPlayer(sender, "Unauthorized banana slip - targeting other player");
                                return false; // Block the RPC
                            }
                            // If they're slipping themselves, it's legitimate
                        }
                    }
                }


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
                    AntiCheatPlugin.LogVisually($"{{userColor}}{sender.NickName}</color> {{leftColor}}attempted to revive someone!</color>", false, false, true);
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
                            AntiCheatPlugin.LogVisually($"{{userColor}}{sender.NickName}</color> {{leftColor}}illegally used {pattern.Description}!</color>", false, false, true);

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
