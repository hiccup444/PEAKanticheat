using Steamworks;
using System;

public static class AntiCheatEvents
{
    public static event Action<Photon.Realtime.Player, string, CSteamID, string> OnCheaterDetected;

    public static void NotifyCheaterDetected(Photon.Realtime.Player player, string reason, CSteamID steamID)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        OnCheaterDetected?.Invoke(player, reason, steamID, timestamp);
    }
}
