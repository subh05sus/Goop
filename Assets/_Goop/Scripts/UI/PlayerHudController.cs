using Goop.Gameplay;
using Goop.Networking;
using Goop.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.UI
{
    /// <summary>
    /// Owner-only HUD keys (reference-parity):
    ///   Tab (hold) — live scoreboard: name / role / alive / ammo / score for every player
    ///   3          — toggle nameplates above other players
    ///   Esc        — pause menu: look sensitivity slider, resume, leave match
    /// Sits on the Player prefab root next to PlayerController.
    /// </summary>
    public class PlayerHudController : NetworkBehaviour
    {
        private PlayerController _playerController;
        private bool _showScoreboard;
        private bool _showNameplates = true;
        private bool _pauseOpen;

        private void Awake()
        {
            _playerController = GetComponent<PlayerController>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) enabled = false;
        }

        private void Update()
        {
            if (Keyboard.current == null) return;
            if (GameStateManager.Instance == null)
            {
                if (_pauseOpen) ClosePause();
                _showScoreboard = false;
                return;
            }

            // While chat/paint/pose-wheel owns the input, Tab/3/Esc belong to them, not the HUD.
            bool inputBusy = !_pauseOpen && _playerController != null && _playerController.MovementLocked;
            if (inputBusy)
            {
                _showScoreboard = false;
                return;
            }

            _showScoreboard = Keyboard.current.tabKey.isPressed;

            if (Keyboard.current.digit3Key.wasPressedThisFrame)
            {
                _showNameplates = !_showNameplates;
            }

            if (Keyboard.current.escapeKey.wasPressedThisFrame && Time.frameCount != ChatController.LastEscConsumedFrame)
            {
                if (_pauseOpen) ClosePause();
                else OpenPause();
            }
        }

        private void OpenPause()
        {
            _pauseOpen = true;
            if (_playerController != null) _playerController.SetMovementLock(this, true);
        }

        private void ClosePause()
        {
            _pauseOpen = false;
            if (_playerController != null) _playerController.SetMovementLock(this, false);
        }

        private async void LeaveMatch()
        {
            ClosePause();
            await GoopSessionManager.LeaveAsync();
            if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient))
            {
                NetworkManager.Singleton.Shutdown();
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu", UnityEngine.SceneManagement.LoadSceneMode.Single);
        }

        private void OnGUI()
        {
            if (GameStateManager.Instance == null) return;

            if (_showNameplates) DrawNameplates();
            if (_showScoreboard) DrawScoreboard();
            if (_pauseOpen) DrawPauseMenu();
        }

        private void DrawNameplates()
        {
            Camera cam = _playerController != null ? _playerController.OwnerCamera : Camera.main;
            if (cam == null) return;

            foreach (var player in FindObjectsByType<NetworkPlayer>())
            {
                if (player.IsOwner) continue; // no nameplate on yourself

                Vector3 screen = cam.WorldToScreenPoint(player.transform.position + Vector3.up * 2.1f);
                if (screen.z <= 0f) continue;

                string name = player.DisplayName.Value.ToString();
                if (!player.IsAlive.Value) name += " (out)";
                // GUI space is y-down; WorldToScreenPoint is y-up.
                GUI.Label(new Rect(screen.x - 60, Screen.height - screen.y - 10, 120, 22), name, CenteredLabel());
            }
        }

        private static GUIStyle _centered;
        private static GUIStyle CenteredLabel()
        {
            _centered ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            return _centered;
        }

        private void DrawScoreboard()
        {
            float w = 460f;
            GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, 80f, w, 320f), GUI.skin.box);
            GUILayout.Label("SCOREBOARD", CenteredLabel());
            GUILayout.BeginHorizontal();
            GUILayout.Label("Player", GUILayout.Width(140));
            GUILayout.Label("Role", GUILayout.Width(80));
            GUILayout.Label("Status", GUILayout.Width(80));
            GUILayout.Label("Ammo", GUILayout.Width(60));
            GUILayout.Label("Score", GUILayout.Width(60));
            GUILayout.EndHorizontal();

            foreach (var player in FindObjectsByType<NetworkPlayer>())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(player.DisplayName.Value.ToString() + (player.IsOwner ? " (you)" : ""), GUILayout.Width(140));
                GUILayout.Label(player.CurrentTeam.Value.ToString(), GUILayout.Width(80));
                GUILayout.Label(player.IsAlive.Value ? "Alive" : "Out", GUILayout.Width(80));
                string ammo = player.CurrentTeam.Value == Team.Seeker && MatchSettings.AmmoModeEnabled
                    ? player.AmmoRemaining.Value.ToString() : "-";
                GUILayout.Label(ammo, GUILayout.Width(60));
                GUILayout.Label(player.Score.Value.ToString(), GUILayout.Width(60));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }

        private void DrawPauseMenu()
        {
            float w = 320f, h = 200f;
            GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h), GUI.skin.box);
            GUILayout.Label("PAUSED (Esc to resume)", CenteredLabel());
            GUILayout.Space(10);

            if (_playerController != null)
            {
                GUILayout.Label($"Look sensitivity: {_playerController.LookSensitivity:0.00}");
                _playerController.LookSensitivity = GUILayout.HorizontalSlider(_playerController.LookSensitivity, 0.02f, 0.5f);
            }

            GUILayout.Space(10);
            if (GUILayout.Button("Resume")) ClosePause();
            if (GUILayout.Button("Leave Match")) LeaveMatch();
            GUILayout.EndArea();
        }
    }
}
