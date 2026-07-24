using Goop.Gameplay;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Player
{
    /// <summary>
    /// Hider surface-attach, v2 (smooth):
    ///   Space (free-moving, near a wall) — attach: glide flat against the surface, staying fully upright
    ///   Space / Ctrl (attached)          — glide up / down the wall
    ///   A / D (attached)                 — glide sideways along the wall
    ///   Shift (attached)                 — detach cleanly (no rotation pop)
    /// The character only ever yaws to face away from the wall — never tilts or rolls, so there is no
    /// pivot-around-the-feet spin. All motion is velocity-smoothed. Position/rotation replicate through the
    /// existing owner-auth NetworkTransform. Also drives the "clipping too deep" red-flash warning.
    /// </summary>
    public class SurfaceAttachController : NetworkBehaviour
    {
        [SerializeField] private float attachSearchDistance = 1.2f;
        [SerializeField] private float surfaceOffset = 0.35f;
        [SerializeField] private float slideSpeed = 1.8f;
        [SerializeField] private float smoothing = 12f;      // higher = snappier glide
        [SerializeField] private float attachBlendTime = 0.18f;
        [SerializeField] private float maxSurfaceNormalY = 0.4f; // walls & steep ramps only, not floors

        public bool IsAttached { get; private set; }

        private CharacterController _controller;
        private PlayerController _playerController;
        private NetworkPlayer _networkPlayer;
        private Vector3 _surfaceNormal;   // horizontal, points away from the wall
        private Vector3 _velocity;        // smoothed slide velocity
        private float _attachBlend;       // 0..1 ease-in on attach
        private Vector3 _blendFrom;
        private Vector3 _blendTo;
        private Quaternion _blendFromRot;
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

        /// <summary>External force-detach (teleports, round transitions).</summary>
        public void ForceDetach()
        {
            if (IsAttached) Detach();
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

            // Short ease-in glide onto the wall so attaching never pops.
            if (_attachBlend < 1f)
            {
                _attachBlend = Mathf.Min(1f, _attachBlend + Time.deltaTime / attachBlendTime);
                float t = Mathf.SmoothStep(0f, 1f, _attachBlend);
                transform.SetPositionAndRotation(
                    Vector3.Lerp(_blendFrom, _blendTo, t),
                    Quaternion.Slerp(_blendFromRot, WallRotation(), t));
                UpdateOverlapWarning();
                return;
            }

            Vector3 targetVelocity = Vector3.zero;
            if (!inputBusy)
            {
                Vector3 right = Vector3.Cross(Vector3.up, _surfaceNormal).normalized;

                float vertical = (Keyboard.current.spaceKey.isPressed ? 1f : 0f)
                               - (Keyboard.current.leftCtrlKey.isPressed ? 1f : 0f);
                float horizontal = (Keyboard.current.dKey.isPressed ? 1f : 0f)
                                 - (Keyboard.current.aKey.isPressed ? 1f : 0f);

                targetVelocity = (Vector3.up * vertical + right * horizontal).normalized * slideSpeed;
                if (vertical == 0f && horizontal == 0f) targetVelocity = Vector3.zero;
            }

            // Velocity smoothing = the "really smooth" glide (no per-frame snapping).
            _velocity = Vector3.Lerp(_velocity, targetVelocity, smoothing * Time.deltaTime);
            Vector3 next = transform.position + _velocity * Time.deltaTime;

            // Stay glued: re-cast at the new spot so sliding follows the wall around gentle bends.
            Vector3 probeOrigin = next + Vector3.up * 0.9f + _surfaceNormal * surfaceOffset;
            if (Physics.Raycast(probeOrigin, -_surfaceNormal, out RaycastHit hit, surfaceOffset * 2.5f, ~0, QueryTriggerInteraction.Ignore)
                && hit.collider.transform.root != transform.root
                && hit.normal.y <= maxSurfaceNormalY)
            {
                _surfaceNormal = FlattenNormal(hit.normal);
                next = new Vector3(
                    hit.point.x + _surfaceNormal.x * surfaceOffset,
                    next.y,
                    hit.point.z + _surfaceNormal.z * surfaceOffset);
            }
            else
            {
                // Ran off the edge of the surface — stop instead of drifting into thin air.
                _velocity = Vector3.zero;
                next = transform.position;
            }

            // Don't glide below the floor.
            if (next.y < 0.02f) next.y = 0.02f;

            transform.SetPositionAndRotation(next, WallRotation());
            UpdateOverlapWarning();
        }

        private static Vector3 FlattenNormal(Vector3 n)
        {
            Vector3 flat = new(n.x, 0f, n.z);
            return flat.sqrMagnitude > 0.001f ? flat.normalized : Vector3.forward;
        }

        /// <summary>Upright, yaw-only: face away from the wall. Never tilts, never rolls.</summary>
        private Quaternion WallRotation() => Quaternion.LookRotation(_surfaceNormal, Vector3.up);

        private void TryAttach()
        {
            // Look for a wall in the facing direction first, then the three other cardinal directions.
            Vector3 origin = transform.position + Vector3.up * 0.9f;
            Vector3[] directions = { transform.forward, -transform.forward, transform.right, -transform.right };
            foreach (var dir in directions)
            {
                Vector3 flatDir = FlattenNormal(dir);
                if (!Physics.Raycast(origin, flatDir, out RaycastHit hit, attachSearchDistance, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (hit.collider.transform.root == transform.root) continue;
                if (hit.normal.y > maxSurfaceNormalY) continue; // floors don't count

                IsAttached = true;
                _surfaceNormal = FlattenNormal(hit.normal);
                _velocity = Vector3.zero;
                _controller.enabled = false;
                if (_playerController != null) _playerController.MovementOverridden = true;

                // Keep the feet at their current height — only pull in horizontally against the wall.
                _blendFrom = transform.position;
                _blendFromRot = transform.rotation;
                _blendTo = new Vector3(
                    hit.point.x + _surfaceNormal.x * surfaceOffset,
                    transform.position.y,
                    hit.point.z + _surfaceNormal.z * surfaceOffset);
                _attachBlend = 0f;
                return;
            }
        }

        private void Detach()
        {
            IsAttached = false;
            _velocity = Vector3.zero;
            // Rotation is already upright yaw-only — nothing to correct, no pop.
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
                    "ATTACHED — Space up · Ctrl down · A/D sideways · Shift detach");
            }
        }
    }
}
