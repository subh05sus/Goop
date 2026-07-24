using Goop.Gameplay;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Player
{
    /// <summary>
    /// Hider surface-attach (reference-parity, context-sensitive keys):
    ///   Space (free-moving, near a wall)  — attach: snap flat against the surface
    ///   Space / Ctrl (attached)           — slide up / down the surface
    ///   Right mouse + WASD (attached)     — slide along the surface plane
    ///   A / D (attached, no RMB)          — roll the body angle to match a prop's orientation
    ///   Shift (attached)                  — detach, drop back to free movement
    /// Position/rotation replicate through the existing owner-auth NetworkTransform. Also drives the
    /// automatic "clipping too deep" red-flash fairness warning.
    /// </summary>
    public class SurfaceAttachController : NetworkBehaviour
    {
        [SerializeField] private float attachSearchDistance = 1.2f;
        [SerializeField] private float surfaceOffset = 0.35f;
        [SerializeField] private float slideSpeed = 1.6f;
        [SerializeField] private float rollSpeed = 90f;
        [SerializeField] private float maxSurfaceNormalY = 0.4f; // walls & steep ramps only, not floors

        public bool IsAttached { get; private set; }

        private CharacterController _controller;
        private PlayerController _playerController;
        private NetworkPlayer _networkPlayer;
        private Vector3 _surfaceNormal;
        private float _roll;
        private float _overlapWarningAlpha;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _playerController = GetComponent<PlayerController>();
            _networkPlayer = GetComponent<NetworkPlayer>();
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
                if (IsAttached) Detach();
                return;
            }

            bool inputBusy = _playerController != null && _playerController.MovementLocked;
            bool isSeeker = _networkPlayer != null && _networkPlayer.CurrentTeam.Value == Team.Seeker;

            if (!IsAttached)
            {
                if (!inputBusy && !isSeeker && Keyboard.current.spaceKey.wasPressedThisFrame)
                {
                    TryAttach();
                }
                UpdateOverlapWarning();
                return;
            }

            // --- attached ---
            if (Keyboard.current.leftShiftKey.wasPressedThisFrame)
            {
                Detach();
                return;
            }

            if (inputBusy) return; // paint/chat/pose can still be used while stuck to a wall

            Vector3 up = Vector3.up;
            Vector3 right = Vector3.Cross(up, _surfaceNormal).normalized;
            if (right.sqrMagnitude < 0.001f) right = transform.right;
            Vector3 surfaceUp = Vector3.Cross(_surfaceNormal, right).normalized;

            Vector3 slide = Vector3.zero;
            if (Keyboard.current.spaceKey.isPressed) slide += surfaceUp;
            if (Keyboard.current.leftCtrlKey.isPressed) slide -= surfaceUp;

            bool rmb = Mouse.current != null && Mouse.current.rightButton.isPressed;
            if (rmb)
            {
                if (Keyboard.current.wKey.isPressed) slide += surfaceUp;
                if (Keyboard.current.sKey.isPressed) slide -= surfaceUp;
                if (Keyboard.current.dKey.isPressed) slide += right;
                if (Keyboard.current.aKey.isPressed) slide -= right;
            }
            else
            {
                // A/D without RMB = fine body-roll adjustment to match a prop's angle.
                float rollInput = (Keyboard.current.dKey.isPressed ? 1f : 0f) - (Keyboard.current.aKey.isPressed ? 1f : 0f);
                _roll += rollInput * rollSpeed * Time.deltaTime;
            }

            if (slide.sqrMagnitude > 0.001f)
            {
                Vector3 next = transform.position + slide.normalized * (slideSpeed * Time.deltaTime);
                // Stay glued: re-cast against the surface so sliding follows it and falls off at edges.
                if (Physics.Raycast(next + _surfaceNormal * surfaceOffset, -_surfaceNormal, out RaycastHit hit,
                        surfaceOffset * 2f, ~0, QueryTriggerInteraction.Ignore)
                    && hit.collider.transform.root != transform.root
                    && hit.normal.y <= maxSurfaceNormalY)
                {
                    _surfaceNormal = hit.normal;
                    transform.position = hit.point + _surfaceNormal * surfaceOffset;
                }
            }

            transform.rotation = Quaternion.LookRotation(_surfaceNormal, Vector3.up) * Quaternion.Euler(0f, 0f, _roll);
            UpdateOverlapWarning();
        }

        private void TryAttach()
        {
            // Look for a wall in the facing direction first, then the three other cardinal directions.
            Vector3 origin = transform.position + Vector3.up * 1f;
            Vector3[] directions = { transform.forward, -transform.forward, transform.right, -transform.right };
            foreach (var dir in directions)
            {
                if (!Physics.Raycast(origin, dir, out RaycastHit hit, attachSearchDistance, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (hit.collider.transform.root == transform.root) continue;
                if (hit.normal.y > maxSurfaceNormalY) continue; // floors don't count

                IsAttached = true;
                _surfaceNormal = hit.normal;
                _roll = 0f;
                _controller.enabled = false;
                if (_playerController != null) _playerController.MovementOverridden = true;
                transform.SetPositionAndRotation(
                    hit.point + _surfaceNormal * surfaceOffset,
                    Quaternion.LookRotation(_surfaceNormal, Vector3.up));
                return;
            }
        }

        /// <summary>External force-detach (teleports, round transitions).</summary>
        public void ForceDetach()
        {
            if (IsAttached) Detach();
        }

        private void Detach()
        {
            IsAttached = false;
            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            _controller.enabled = true;
            if (_playerController != null) _playerController.MovementOverridden = false;
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner && IsAttached) Detach();
        }

        /// <summary>Fairness signal: if the body is buried too deep inside world geometry the screen edge
        /// flashes red (visible to the hiding player so they know the spot won't be considered fair).</summary>
        private void UpdateOverlapWarning()
        {
            Vector3 chest = transform.position + Vector3.up * 0.9f;
            bool buried = false;
            foreach (var col in Physics.OverlapSphere(chest, 0.05f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (col.transform.root == transform.root) continue;
                buried = true;
                break;
            }
            _overlapWarningAlpha = Mathf.MoveTowards(_overlapWarningAlpha, buried ? 0.45f : 0f, Time.deltaTime * 2f);
        }

        private void OnGUI()
        {
            if (!IsOwner) return;

            if (_overlapWarningAlpha > 0.01f)
            {
                var prev = GUI.color;
                GUI.color = new Color(1f, 0f, 0f, Mathf.PingPong(Time.time * 2f, _overlapWarningAlpha));
                GUI.DrawTexture(new Rect(0, 0, Screen.width, 6), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(0, Screen.height - 6, Screen.width, 6), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(0, 0, 6, Screen.height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(Screen.width - 6, 0, 6, Screen.height), Texture2D.whiteTexture);
                GUI.Label(new Rect(Screen.width / 2f - 80, 12, 200, 22), "Clipping too deep!");
                GUI.color = prev;
            }

            if (IsAttached)
            {
                GUI.Label(new Rect(10, 40, 480, 22),
                    "ATTACHED — Space/Ctrl up/down · RMB+WASD slide · A/D tilt · Shift detach");
            }
        }
    }
}
