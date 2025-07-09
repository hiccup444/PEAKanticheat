using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace AntiCheatMod
{
    // Harmony patches for detecting malicious RPC actions
    [HarmonyPatch]
    public static class RPCDetection
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

        // Campfire lighting (commented out)
        /*[HarmonyPatch(typeof(Campfire), "Light_Rpc")]
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
        }*/

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

        // Player reviving
        [HarmonyPatch(typeof(Character), "RPCA_ReviveAtPosition")]
        [HarmonyPrefix]
        public static void PreCharacterRPCA_ReviveAtPosition(Character __instance, PhotonMessageInfo info)
        {
            if (AntiCheatPlugin.VerboseRPCLogging.Value)
                AntiCheatPlugin.Logger.LogInfo($"Character.RPCA_ReviveAtPosition called by {info.Sender?.NickName} on {__instance.GetComponent<PhotonView>()?.Owner?.NickName}");

            var photonView = __instance.GetComponent<PhotonView>();
            bool isValid = info.Sender == null || info.Sender.IsMasterClient || (photonView != null && info.Sender.ActorNumber == photonView.Owner.ActorNumber);

            if (isValid)
                return;

            // Unauthorized revive attempt
            AntiCheatPlugin.Logger.LogWarning($"{info.Sender.NickName} (#{info.Sender.ActorNumber}) revived {photonView?.Owner?.NickName} (#{photonView?.Owner?.ActorNumber}) without permission!");
            AntiCheatPlugin.LogVisually($"{{userColor}}{info.Sender.NickName}</color> {{leftColor}}revived</color> {{userColor}}{photonView?.Owner?.NickName}</color>{{leftColor}} without permission!</color>", false, false, true);
            AntiCheatPlugin.SoftLockPlayer(info.Sender, "Unauthorized revive");
        }
    }
}
