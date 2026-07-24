using Goop.Gameplay;
using Goop.Player;
using Unity.Netcode;
using UnityEngine;

namespace Goop.UI
{
    /// <summary>
    /// Round HUD for the in-match phases: phase banner + countdown, role/status, the Transition "hunt
    /// begins" ceremony overlay, the Seeker's waiting-in-lobby message during Hide, and the end-of-round
    /// winner/scoreboard. LobbyIdle is handled by LobbyPanelUI instead.
    /// </summary>
    public class RoundHudUI : MonoBehaviour
    {
        private void OnGUI()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || gsm.Phase.Value == GamePhase.LobbyIdle) return;

            if (gsm.Phase.Value == GamePhase.Transition)
            {
                DrawTransitionOverlay(gsm);
                return;
            }

            GUILayout.BeginArea(new Rect(Screen.width / 2 - 150, 10, 300, 150));
            GUILayout.Box($"Phase: {PhaseLabel(gsm.Phase.Value)}   Time: {gsm.PhaseTimeRemaining.Value:F0}s");

            var localPlayer = FindLocalPlayer();
            if (localPlayer != null)
            {
                string role = localPlayer.CurrentTeam.Value.ToString();
                string alive = localPlayer.IsAlive.Value ? "Alive" : "Caught";
                GUILayout.Box($"Role: {role}   Status: {alive}   Score: {localPlayer.Score.Value}");

                if (localPlayer.CurrentTeam.Value == Team.Seeker && MatchSettings.AmmoModeEnabled)
                {
                    GUILayout.Box($"Ammo: {localPlayer.AmmoRemaining.Value}");
                }

                // The Seeker's Hide-phase view is the empty lobby — tell them what's happening so the
                // wait reads as "the round is working", not "the game broke" (Game Feel doc §6).
                if (gsm.Phase.Value == GamePhase.Hide && localPlayer.CurrentTeam.Value == Team.Seeker)
                {
                    GUILayout.Box("The Hiders are hiding... you'll be sent in when the timer ends.\nWarm up your aim — try your scan tool (2) or taunt (1).");
                }
            }

            if (gsm.Phase.Value == GamePhase.Resolution || gsm.Phase.Value == GamePhase.PostRound)
            {
                GUILayout.Box($"Winner: {gsm.Winner.Value}");
                DrawScoreboard();
            }
            GUILayout.EndArea();
        }

        private static string PhaseLabel(GamePhase phase) => phase switch
        {
            GamePhase.Hide => "HIDE",
            GamePhase.Hunt => "HUNT",
            GamePhase.Resolution => "ROUND OVER",
            GamePhase.PostRound => "SCOREBOARD",
            _ => phase.ToString()
        };

        /// <summary>The ceremony beat (Game Feel doc §7): a dark full-screen pulse with "THE HUNT BEGINS"
        /// shown to everyone while the Seeker is being moved onto the map. Sells the tension spike.</summary>
        private void DrawTransitionOverlay(GameStateManager gsm)
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.red;
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 42,
                fontStyle = FontStyle.Bold
            };
            GUI.Label(new Rect(0, Screen.height / 2f - 60, Screen.width, 80), "THE HUNT BEGINS", style);
            GUI.color = Color.white;
            var sub = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20 };
            GUI.Label(new Rect(0, Screen.height / 2f + 20, Screen.width, 40), $"{gsm.PhaseTimeRemaining.Value:F0}...", sub);
            GUI.color = prev;
        }

        private NetworkPlayer FindLocalPlayer()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null) return null;
            return nm.LocalClient.PlayerObject.GetComponent<NetworkPlayer>();
        }

        private void DrawScoreboard()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            foreach (var kvp in nm.ConnectedClients)
            {
                if (kvp.Value.PlayerObject == null) continue;
                var netPlayer = kvp.Value.PlayerObject.GetComponent<NetworkPlayer>();
                GUILayout.Label($"{netPlayer.DisplayName.Value} ({netPlayer.CurrentTeam.Value}): {netPlayer.Score.Value} pts");
            }
        }
    }
}
