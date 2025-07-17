using Steamworks;
using System;

namespace AntiCheatMod
{
    public enum AntiCheatNetEvent : byte
    {
        DetectionReport = 100,
        BlockListUpdate = 101,
        DetectionSettingsUpdate = 102,
        WhitelistUpdate = 103,
        SyncRequest = 104,
        SyncResponse = 105
    }

    public static class AntiCheatEvents
    {
        // Keep the original event for the banning mod
        public static event Action<Photon.Realtime.Player, string, CSteamID, string> OnCheaterDetected;
        
        // New events for the UI system
        public static event Action<DetectionType, Photon.Realtime.Player, string> OnDetectionTriggered;
        public static event Action<Photon.Realtime.Player, bool> OnPlayerBlockStatusChanged;

        public static void NotifyCheaterDetected(Photon.Realtime.Player player, string reason, CSteamID steamID)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            OnCheaterDetected?.Invoke(player, reason, steamID, timestamp);
        }

        public static void NotifyDetectionTriggered(DetectionType type, Photon.Realtime.Player player, string reason)
        {
            OnDetectionTriggered?.Invoke(type, player, reason);
        }

        public static void NotifyPlayerBlockStatusChanged(Photon.Realtime.Player player, bool isBlocked)
        {
            OnPlayerBlockStatusChanged?.Invoke(player, isBlocked);
        }
    }
}
