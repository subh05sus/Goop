using Goop.Gameplay;
using Unity.Netcode;
using UnityEngine;

namespace Goop.Player
{
    /// <summary>
    /// Procedural aim IK for the Generic (non-humanoid) GoopChar rig: the WHOLE torso (spine-heavy
    /// weights) bends/twists toward the camera look direction, up to a clamp — past the clamp the owner's
    /// body itself turns to follow. Runs in LateUpdate after the Animator as additive world-space
    /// rotations. Aim angles replicate (owner-write) so every client sees the same body language.
    ///
    /// Accumulation guard: the Animator only rewrites bones that the CURRENT clip actually has curves
    /// for. Bones it doesn't touch would keep our last frame's offset and compound every frame (the
    /// "spinning head" bug). So per bone we remember what we set last frame — if the bone still has
    /// exactly that rotation, the Animator didn't write it and we restore the cached base first.
    /// </summary>
    public class AimIK : NetworkBehaviour
    {
        [SerializeField] private float maxYawOffset = 50f;
        [SerializeField] private float smoothing = 12f;
        [SerializeField] private float bodyTurnSpeed = 200f;

        // x = camera pitch, y = yaw offset from body facing
        public NetworkVariable<Vector2> AimAngles = new(
            Vector2.zero,
            writePerm: NetworkVariableWritePermission.Owner);

        // Spine + chest ONLY. The neck/head bones are left completely alone — they inherit the torso's
        // rotation through the hierarchy, so the head turns exactly with the torso and never on its own.
        // (They were also the drift source: no animation curves -> the Animator never rewrote them.)
        private static readonly float[] PitchWeights = { 0.5f, 0.5f };
        private static readonly float[] YawWeights = { 0.5f, 0.5f };
        private static readonly string[] BoneNames = { "Bone.001", "Bone.002" };

        private Transform[] _bones;
        private Quaternion[] _lastSet;   // rotation we wrote last frame
        private Quaternion[] _baseCache; // animator-authored rotation under our offset
        private PlayerController _playerController;
        private PoseController _poseController;
        private SurfaceAttachController _attach;
        private NetworkPlayer _networkPlayer;
        private Vector2 _current;

        private void Awake()
        {
            _playerController = GetComponentInParent<PlayerController>();
            _attach = GetComponentInParent<SurfaceAttachController>();
            _networkPlayer = GetComponentInParent<NetworkPlayer>();
            _poseController = GetComponent<PoseController>();

            _bones = new Transform[BoneNames.Length];
            _lastSet = new Quaternion[BoneNames.Length];
            _baseCache = new Quaternion[BoneNames.Length];
            foreach (var t in GetComponentsInChildren<Transform>())
            {
                for (int i = 0; i < BoneNames.Length; i++)
                {
                    if (t.name == BoneNames[i]) _bones[i] = t;
                }
            }
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] != null)
                {
                    _lastSet[i] = _bones[i].rotation;
                    _baseCache[i] = _bones[i].rotation;
                }
            }
        }

        private bool Suppressed =>
            (_poseController != null && _poseController.PoseIndex.Value != PoseController.IdlePoseIndex)
            || (_attach != null && _attach.IsAttached)
            || (_networkPlayer != null && !_networkPlayer.IsAlive.Value);

        private void Update()
        {
            if (!IsOwner || _playerController == null) return;

            Vector2 target = Vector2.zero;
            if (!Suppressed)
            {
                float rawOffset = Mathf.DeltaAngle(transform.root.eulerAngles.y, _playerController.CameraYaw);

                // Torso only twists so far — past the clamp the whole body turns to catch up.
                if (Mathf.Abs(rawOffset) > maxYawOffset && !_playerController.MovementOverridden)
                {
                    float excess = rawOffset - Mathf.Sign(rawOffset) * maxYawOffset;
                    float step = Mathf.Min(Mathf.Abs(excess), bodyTurnSpeed * Time.deltaTime) * Mathf.Sign(excess);
                    transform.root.rotation = Quaternion.Euler(0f, transform.root.eulerAngles.y + step, 0f);
                    rawOffset = Mathf.DeltaAngle(transform.root.eulerAngles.y, _playerController.CameraYaw);
                }

                target = new Vector2(_playerController.CameraPitch, Mathf.Clamp(rawOffset, -maxYawOffset, maxYawOffset));
            }

            if ((AimAngles.Value - target).sqrMagnitude > 0.25f)
            {
                AimAngles.Value = target;
            }
        }

        private void LateUpdate()
        {
            if (_bones == null) return;

            _current = Vector2.Lerp(_current, AimAngles.Value, smoothing * Time.deltaTime);

            Vector3 up = Vector3.up;
            Vector3 right = transform.root.right;

            for (int i = 0; i < _bones.Length; i++)
            {
                var bone = _bones[i];
                if (bone == null) continue;

                // If the bone still holds exactly what WE wrote last frame, the Animator didn't animate
                // it this frame — start from the cached base instead of compounding our own offset.
                Quaternion current = bone.rotation;
                Quaternion baseRot = QuaternionApproximately(current, _lastSet[i]) ? _baseCache[i] : current;

                Quaternion target =
                    Quaternion.AngleAxis(_current.y * YawWeights[i], up)
                    * Quaternion.AngleAxis(_current.x * PitchWeights[i], right)
                    * baseRot;

                bone.rotation = target;
                _baseCache[i] = baseRot;
                _lastSet[i] = target;
            }
        }

        private static bool QuaternionApproximately(Quaternion a, Quaternion b)
        {
            return Mathf.Abs(Quaternion.Dot(a, b)) > 0.999999f;
        }
    }
}
