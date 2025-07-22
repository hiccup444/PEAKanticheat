using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

namespace AntiCheatMod
{
    // Automatic invite link generation when entering a room
    public static class InviteLinkGenerator
    {
        private static bool _linkGenerated = false;

        public static void OnJoinedRoom()
        {
            // Reset the flag when joining a new room
            _linkGenerated = false;
            
            // Start the coroutine to generate invite link
            if (AntiCheatPlugin.Instance != null)
            {
                AntiCheatPlugin.Instance.StartCoroutine(GenerateInviteLinkDelayed());
            }
        }

        private static IEnumerator GenerateInviteLinkDelayed()
        {
            // Wait for room state to be properly established
            yield return new WaitForSeconds(2f);

            // Check if we're still in a room
            if (!PhotonNetwork.InRoom)
            {
                Debug.LogWarning("[PEAK AntiCheat] No longer in room, skipping invite link generation");
                yield break;
            }

            // Check if we're the master client
            if (!PhotonNetwork.IsMasterClient)
            {
                Debug.Log("[PEAK AntiCheat] Not master client, skipping invite link generation");
                yield break;
            }

            // Check if we've already generated a link for this room
            if (_linkGenerated)
            {
                Debug.Log("[PEAK AntiCheat] Invite link already generated for this room");
                yield break;
            }

            try
            {
                // Check if Steam is initialized
                if (!SteamManager.Initialized)
                {
                    Debug.LogWarning("[PEAK AntiCheat] Steam not initialized, cannot create invite link");
                    yield break;
                }

                // Get the Steam lobby handler
                var lobbyHandler = GameHandler.GetService<SteamLobbyHandler>();
                if (lobbyHandler == null)
                {
                    Debug.LogWarning("[PEAK AntiCheat] SteamLobbyHandler not found");
                    yield break;
                }

                // Check if we're in a Steam lobby
                CSteamID steamIDLobby;
                if (!lobbyHandler.InSteamLobby(out steamIDLobby))
                {
                    Debug.LogWarning("[PEAK AntiCheat] Not in a Steam lobby");
                    yield break;
                }

                // Get the lobby owner's Steam ID
                CSteamID lobbyOwner = SteamMatchmaking.GetLobbyOwner(steamIDLobby);
                if (lobbyOwner == CSteamID.Nil)
                {
                    Debug.LogWarning("[PEAK AntiCheat] Could not get lobby owner");
                    yield break;
                }

                // Construct the Steam join link
                string lobbyLink = $"steam://joinlobby/3527290/{steamIDLobby}/{lobbyOwner}";
                
                // Copy to clipboard
                GUIUtility.systemCopyBuffer = lobbyLink;
                
                // Mark as generated
                _linkGenerated = true;
                
                Debug.Log($"[PEAK AntiCheat] Invite link copied to clipboard: {lobbyLink}");
                
                // Optional: Show a visual notification
                AntiCheatPlugin.LogVisually("Invite link copied to clipboard!", false, false, false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PEAK AntiCheat] Error creating invite link: {ex.Message}");
            }
        }
    }


}
