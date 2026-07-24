using Goop.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Gameplay
{
    /// <summary>
    /// Seeker assist tools (reference-parity):
    ///   1 — taunt: plays a whistle at the player's position for everyone (networked, any role)
    ///   2 — X-ray scan toggle (Seeker only): screen-space markers over living Hiders, visible through
    ///       walls/clutter. Deliberately surfaced with an explicit HUD indicator so other players know
    ///       it's a legitimate built-in tool, not a cheat.
    /// Sits on the Player prefab root.
    /// </summary>
    public class SeekerToolsController : NetworkBehaviour
    {
        [SerializeField] private AudioClip[] tauntClips;
        [SerializeField] private float tauntCooldown = 4f;

        private NetworkPlayer _networkPlayer;
        private PlayerController _playerController;
        private bool _xrayActive;
        private float _lastTauntTime = -999f;

        private void Awake()
        {
            _networkPlayer = GetComponent<NetworkPlayer>();
            _playerController = GetComponent<PlayerController>();
        }

        public override void OnNetworkSpawn()
        {
            // Stays enabled on non-owners: the taunt ClientRpc must run on every client's instance.
        }

        private void Update()
        {
            if (!IsOwner || Keyboard.current == null) return;
            if (GameStateManager.Instance == null)
            {
                _xrayActive = false;
                return;
            }

            // Number keys belong to chat/paint/pause while those own the input.
            if (_playerController != null && _playerController.MovementLocked) return;

            if (Keyboard.current.digit1Key.wasPressedThisFrame && Time.time - _lastTauntTime >= tauntCooldown)
            {
                _lastTauntTime = Time.time;
                TauntServerRpc(Random.Range(0, Mathf.Max(1, tauntClips?.Length ?? 0)));
            }

            if (Keyboard.current.digit2Key.wasPressedThisFrame && _networkPlayer.CurrentTeam.Value == Team.Seeker)
            {
                _xrayActive = !_xrayActive;
            }
        }

        [ServerRpc]
        private void TauntServerRpc(int clipIndex)
        {
            TauntClientRpc(clipIndex);
        }

        [ClientRpc]
        private void TauntClientRpc(int clipIndex)
        {
            if (tauntClips == null || tauntClips.Length == 0) return;
            AudioClip clip = tauntClips[Mathf.Clamp(clipIndex, 0, tauntClips.Length - 1)];
            if (clip != null) AudioSource.PlayClipAtPoint(clip, transform.position, 1f);
        }

        private void OnGUI()
        {
            if (!IsOwner || GameStateManager.Instance == null) return;

            if (_networkPlayer.CurrentTeam.Value == Team.Seeker)
            {
                GUI.Label(new Rect(Screen.width - 210, 10, 200, 22),
                    _xrayActive ? "X-RAY SCAN: ON  (2 to toggle)" : "X-ray scan: off  (2 to toggle)");
            }

            if (!_xrayActive || _networkPlayer.CurrentTeam.Value != Team.Seeker) return;
            // Markers only render during the Hunt — during Hide the Seeker is waiting in the lobby and
            // through-wall markers would leak every Hider's position on the map (spatial-fairness break).
            if (GameStateManager.Instance.Phase.Value != GamePhase.Hunt) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            foreach (var player in FindObjectsByType<NetworkPlayer>())
            {
                if (player.IsOwner) continue;
                if (player.CurrentTeam.Value != Team.Hider || !player.IsAlive.Value) continue;

                Vector3 screen = cam.WorldToScreenPoint(player.transform.position + Vector3.up * 1f);
                if (screen.z <= 0f) continue;

                float dist = Vector3.Distance(transform.position, player.transform.position);
                var prev = GUI.color;
                GUI.color = Color.red;
                GUI.Box(new Rect(screen.x - 22, Screen.height - screen.y - 22, 44, 44), "");
                GUI.Label(new Rect(screen.x - 30, Screen.height - screen.y + 24, 60, 20), $"{dist:0}m");
                GUI.color = prev;
            }
        }
    }
}
