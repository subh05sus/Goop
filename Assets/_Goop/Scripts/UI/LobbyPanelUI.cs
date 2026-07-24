using Goop.Gameplay;
using Goop.Networking;
using Unity.Netcode;
using UnityEngine;

namespace Goop.UI
{
    /// <summary>
    /// In-world lobby panel (Game Feel doc §4) — an overlay you use while standing in the 3D lobby room,
    /// not a separate menu scene. Everyone sees the join code, player list, and the host's current match
    /// settings live; only the host can edit them and press Start. Start requires a gun holder.
    /// Lives in the arena scene next to RoundHudUI; only draws during LobbyIdle.
    /// </summary>
    public class LobbyPanelUI : MonoBehaviour
    {
        private void OnGUI()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || gsm.Phase.Value != GamePhase.LobbyIdle) return;
            if (NetworkManager.Singleton == null) return;

            bool isHost = NetworkManager.Singleton.IsHost;

            GUILayout.BeginArea(new Rect(Screen.width - 330, 10, 320, 420), GUI.skin.box);
            GUILayout.Label("LOBBY — practice paint (F), poses (R), pass the gun (E)");

            var session = GoopSessionManager.CurrentSession;
            GUILayout.Label(session != null ? $"Join code: {session.Code}" : "Join code: (none)");

            int playerCount = NetworkManager.Singleton.ConnectedClientsIds.Count;
            GUILayout.Label($"Players: {playerCount}");

            GUILayout.Space(6);
            GUILayout.Label("— Match settings —");
            GUILayout.Label("Map: Greybox Arena");

            if (isHost)
            {
                GUILayout.Label($"Hide time: {gsm.HideDuration.Value:0}s");
                gsm.HideDuration.Value = Mathf.Round(GUILayout.HorizontalSlider(gsm.HideDuration.Value, 10f, 120f));
                GUILayout.Label($"Hunt time: {gsm.HuntDuration.Value:0}s");
                gsm.HuntDuration.Value = Mathf.Round(GUILayout.HorizontalSlider(gsm.HuntDuration.Value, 30f, 300f));
                MatchSettings.AmmoModeEnabled = GUILayout.Toggle(MatchSettings.AmmoModeEnabled, $" Ammo mode ({MatchSettings.AmmoCount} shots)");
            }
            else
            {
                // Non-hosts see the live values (replicated NetworkVariables) but can't change them.
                GUILayout.Label($"Hide time: {gsm.HideDuration.Value:0}s   Hunt time: {gsm.HuntDuration.Value:0}s");
                GUILayout.Label(MatchSettings.AmmoModeEnabled ? "Ammo mode: on" : "Ammo mode: off");
            }

            GUILayout.Space(6);
            bool gunHeld = GunPickup.Instance != null && GunPickup.Instance.HasHolder;
            GUILayout.Label(gunHeld ? "Gun is held — Seeker is decided." : "Nobody is holding the gun yet!");

            if (isHost)
            {
                GUI.enabled = gunHeld && playerCount >= 2;
                if (GUILayout.Button("START MATCH"))
                {
                    gsm.TryStartMatch();
                }
                GUI.enabled = true;
                if (!gunHeld) GUILayout.Label("(Start needs a gun holder)");
                else if (playerCount < 2) GUILayout.Label("(Need at least 2 players)");
            }
            else
            {
                GUILayout.Label("Waiting for host to start...");
            }

            GUILayout.Space(10);
            if (GUILayout.Button("Leave Session"))
            {
                Leave();
            }
            GUILayout.EndArea();
        }

        private async void Leave()
        {
            await GoopSessionManager.LeaveAsync();
            if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient))
            {
                NetworkManager.Singleton.Shutdown();
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu", UnityEngine.SceneManagement.LoadSceneMode.Single);
        }
    }
}
