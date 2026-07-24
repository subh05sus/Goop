using Goop.Gameplay;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Player
{
    /// <summary>
    /// Owner-authoritative third-person controller: mouse-orbit camera, camera-relative WASD at a single
    /// fixed speed (no sprint — deliberate design choice), jump, crouch. The owner creates its own camera
    /// rig as a child of the player so it survives NGO scene migration and never depends on a scene camera.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float runSpeed = 7.5f;
        [SerializeField] private float crouchSpeed = 2.5f;
        [SerializeField] private float rotationSpeed = 720f;
        [SerializeField] private float gravity = -20f;
        [SerializeField] private float jumpHeight = 1.1f;
        [SerializeField] private InputActionAsset inputActions;

        [Header("Crouch")]
        [SerializeField] private float standingHeight = 1.8f;
        [SerializeField] private float crouchedHeight = 0.9f;

        [Header("Third-person camera")]
        [SerializeField] private float cameraDistance = 4.5f;
        [SerializeField] private float cameraShoulderHeight = 1.6f;
        [SerializeField] private float gunShoulderOffset = 0.6f; // lateral shift while holding the gun
        [SerializeField] private float lookSensitivity = 0.12f;
        [SerializeField] private float minPitch = -55f;
        [SerializeField] private float maxPitch = 75f;
        [SerializeField] private float cameraCollisionRadius = 0.25f;

        /// <summary>True while any UI system (paint mode, chat, pause menu, pose wheel) owns the
        /// mouse/keyboard. Gravity still applies; look + move input are ignored, cursor is released.
        /// Lock ownership is per-source so overlapping systems can't stomp each other's lock.</summary>
        public bool MovementLocked => _movementLocks.Count > 0;

        private readonly System.Collections.Generic.HashSet<object> _movementLocks = new();

        public void SetMovementLock(object source, bool locked)
        {
            if (locked) _movementLocks.Add(source);
            else _movementLocks.Remove(source);
        }

        /// <summary>Paint mode pulls the camera in close so the player can inspect their own paint job;
        /// orbiting is then driven explicitly via OrbitCamera (middle-mouse drag) instead of free look.</summary>
        public bool PaintViewActive { get; set; }

        /// <summary>Set while surface-attached: another system drives the transform directly, so normal
        /// WASD movement, gravity, and jump are all skipped. Mouse look stays live.</summary>
        public bool MovementOverridden { get; set; }

        public bool IsCrouching { get; private set; }

        /// <summary>Prone: replicated so every client tilts the visual the same way. X toggles.</summary>
        public NetworkVariable<bool> IsProne = new(
            false,
            writePerm: NetworkVariableWritePermission.Owner);

        /// <summary>Camera angles exposed for AimIK (torso/head look direction).</summary>
        public float CameraPitch => _pitch;
        public float CameraYaw => _yaw;

        /// <summary>Exposed for the pause menu's sensitivity slider.</summary>
        public float LookSensitivity
        {
            get => lookSensitivity;
            set => lookSensitivity = Mathf.Clamp(value, 0.01f, 1f);
        }

        public Camera OwnerCamera => _camera;

        private CharacterController _controller;
        private NetworkPlayer _networkPlayer;
        private InputAction _moveAction;
        private InputAction _lookAction;
        private InputAction _jumpAction;
        private InputAction _crouchAction;
        private InputAction _sprintAction;
        private Goop.Gameplay.PoseController _poseController;
        private Transform _cameraRig;
        private Camera _camera;
        private AudioListener _listener;
        private Vector3 _verticalVelocity;
        private float _yaw;
        private float _pitch = 15f;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _networkPlayer = GetComponent<NetworkPlayer>();
        }

        public override void OnNetworkSpawn()
        {
            IsProne.OnValueChanged += OnProneChanged;
            ApplyProneVisual(IsProne.Value);

            if (!IsOwner)
            {
                // Remote players must NOT present a capsule to raycasts/collisions — their hitbox is the
                // baked mesh collider on the visual (pose-accurate). Position comes from NetworkTransform,
                // so the CharacterController does nothing useful here anyway.
                _controller.enabled = false;
                enabled = false;
                return;
            }

            InputActionMap map = inputActions.FindActionMap("Player", throwIfNotFound: true);
            _moveAction = map.FindAction("Move", throwIfNotFound: true);
            _lookAction = map.FindAction("Look", throwIfNotFound: true);
            _jumpAction = map.FindAction("Jump", throwIfNotFound: true);
            _crouchAction = map.FindAction("Crouch", throwIfNotFound: true);
            _sprintAction = map.FindAction("Sprint", throwIfNotFound: true);
            map.Enable();

            _poseController = GetComponentInChildren<Goop.Gameplay.PoseController>();
            _yaw = transform.eulerAngles.y;
            CreateCameraRig();
        }

        public override void OnNetworkDespawn()
        {
            IsProne.OnValueChanged -= OnProneChanged;
            if (!IsOwner) return;
            inputActions.FindActionMap("Player")?.Disable();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (_cameraRig != null) Destroy(_cameraRig.gameObject);
        }

        /// <summary>Own camera, parented under the player: survives networked scene loads with the player
        /// object and never depends on a scene having (or keeping) a Camera.main of its own.</summary>
        private void CreateCameraRig()
        {
            var rigGo = new GameObject("PlayerCameraRig");
            // Tagged MainCamera so existing Camera.main-based raycasts (paint brush, seeker tag,
            // eyedropper) resolve to this rig once the arena's own cameras are disabled.
            rigGo.tag = "MainCamera";
            rigGo.transform.SetParent(transform, worldPositionStays: false);
            _camera = rigGo.AddComponent<Camera>();
            // Starts disabled — the player object also exists in the Lobby scene, where the scene's own
            // camera/UI should keep rendering. Update() enables it once we're actually in the arena.
            _camera.enabled = false;
            _listener = rigGo.AddComponent<AudioListener>();
            _listener.enabled = false;
            _cameraRig = rigGo.transform;
        }

        private bool InArena => GameStateManager.Instance != null;

        private void Update()
        {
            if (!IsOwner) return;

            // Outside the arena (lobby/menu scenes) the player object exists but shouldn't fight the UI
            // for the mouse — camera off, cursor free, no movement.
            if (!InArena)
            {
                if (_camera != null && _camera.enabled) _camera.enabled = false;
                SetCursorLocked(false);
                return;
            }

            if (_camera != null && !_camera.enabled)
            {
                // Entering the arena: take over rendering. Disable any stray scene camera/listener.
                foreach (var other in FindObjectsByType<Camera>())
                {
                    if (other != _camera) other.enabled = false;
                }
                foreach (var listener in FindObjectsByType<AudioListener>())
                {
                    if (listener.gameObject != _camera.gameObject) listener.enabled = false;
                }
                _camera.enabled = true;
                _listener.enabled = true;
            }

            bool inputBlocked = MovementLocked;
            SetCursorLocked(!inputBlocked);

            if (!inputBlocked)
            {
                Vector2 look = _lookAction.ReadValue<Vector2>();
                _yaw += look.x * lookSensitivity;
                _pitch = Mathf.Clamp(_pitch - look.y * lookSensitivity, minPitch, maxPitch);
            }

            bool frozen = _networkPlayer != null && _networkPlayer.IsFrozen.Value;

            if (MovementOverridden)
            {
                // Surface attach drives the transform; nothing to do but keep looking around.
                return;
            }

            HandleCrouch(inputBlocked || frozen);

            Vector3 moveDir = Vector3.zero;
            if (!inputBlocked && !frozen)
            {
                Vector2 move = _moveAction.ReadValue<Vector2>();
                // Camera-relative: input is rotated by the camera yaw so W always means "away from camera".
                Quaternion camYaw = Quaternion.Euler(0f, _yaw, 0f);
                moveDir = camYaw * new Vector3(move.x, 0f, move.y);
                moveDir = Vector3.ClampMagnitude(moveDir, 1f);

                if (moveDir.sqrMagnitude > 0.0001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(moveDir, Vector3.up);
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, rotationSpeed * Time.deltaTime);
                }

                if (_jumpAction.WasPressedThisFrame() && _controller.isGrounded && !IsCrouching && !IsProne.Value)
                {
                    _verticalVelocity.y = Mathf.Sqrt(2f * -gravity * jumpHeight);
                }
            }

            if (_controller.isGrounded && _verticalVelocity.y < 0f)
            {
                _verticalVelocity.y = -1f;
            }
            _verticalVelocity.y += gravity * Time.deltaTime;

            bool running = !inputBlocked && !frozen && !IsCrouching && !IsProne.Value
                           && _sprintAction != null && _sprintAction.IsPressed();
            float speed = IsProne.Value ? 1.2f : (IsCrouching ? crouchSpeed : (running ? runSpeed : moveSpeed));
            _controller.Move((moveDir * speed + _verticalVelocity) * Time.deltaTime);

            // Walking keeps the pose (you shuffle around frozen in the silhouette — no walk animation,
            // the pose state ignores the Speed param). Only RUNNING (Shift) breaks the pose.
            if (running && moveDir.sqrMagnitude > 0.001f && _poseController != null
                && _poseController.PoseIndex.Value != Goop.Gameplay.PoseController.IdlePoseIndex)
            {
                _poseController.SetPose(Goop.Gameplay.PoseController.IdlePoseIndex);
            }
        }

        private void HandleCrouch(bool inputBlocked)
        {
            // X toggles prone (lowest stance, slowest). Ctrl-crouch is ignored while prone.
            if (!inputBlocked && UnityEngine.InputSystem.Keyboard.current != null
                && UnityEngine.InputSystem.Keyboard.current.xKey.wasPressedThisFrame)
            {
                IsProne.Value = !IsProne.Value;
            }

            bool wantCrouch = !inputBlocked && !IsProne.Value && _crouchAction.IsPressed();
            if (wantCrouch != IsCrouching)
            {
                IsCrouching = wantCrouch;
            }

            float height = IsProne.Value ? 0.5f : (IsCrouching ? crouchedHeight : standingHeight);
            if (!Mathf.Approximately(_controller.height, height))
            {
                _controller.height = height;
                _controller.center = new Vector3(0f, height * 0.5f, 0f);
            }
        }

        private void OnProneChanged(bool previous, bool current) => ApplyProneVisual(current);

        /// <summary>Greybox prone visual: tilt the whole visual child face-down. Runs on every client
        /// (driven by the replicated IsProne), no prone animation clip needed.</summary>
        private void ApplyProneVisual(bool prone)
        {
            Transform visual = transform.Find("Visual_GoopGuy");
            if (visual == null) return;
            if (prone)
            {
                visual.SetLocalPositionAndRotation(new Vector3(0f, 0.35f, -0.4f), Quaternion.Euler(80f, 0f, 0f));
            }
            else
            {
                visual.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            }
        }

        /// <summary>Externally-driven camera orbit (paint mode middle-mouse drag).</summary>
        public void OrbitCamera(Vector2 pixelDelta)
        {
            _yaw += pixelDelta.x * lookSensitivity;
            _pitch = Mathf.Clamp(_pitch - pixelDelta.y * lookSensitivity, minPitch, maxPitch);
        }

        private void LateUpdate()
        {
            if (!IsOwner || _cameraRig == null || _camera == null || !_camera.enabled) return;

            float distance = PaintViewActive ? cameraDistance * 0.55f : cameraDistance;
            Quaternion camRot = Quaternion.Euler(_pitch, _yaw, 0f);
            float pivotHeight = PaintViewActive ? standingHeight * 0.55f
                : (IsCrouching ? cameraShoulderHeight * 0.6f : cameraShoulderHeight);
            Vector3 pivot = transform.position + Vector3.up * pivotHeight;

            // Gun holder gets an over-the-shoulder view: character shifts left of center so the
            // crosshair has a clear line — proper aiming view instead of shooting "through" yourself.
            bool holdingGun = Goop.Gameplay.GunPickup.Instance != null
                && Goop.Gameplay.GunPickup.Instance.HolderClientId.Value == OwnerClientId;
            if (holdingGun && !PaintViewActive)
            {
                pivot += Quaternion.Euler(0f, _yaw, 0f) * new Vector3(gunShoulderOffset, 0f, 0f);
            }
            Vector3 desiredPos = pivot + camRot * new Vector3(0f, 0f, -distance);

            // Pull the camera in front of anything solid between the player and its desired position so it
            // never clips through walls; ignore the player's own colliders.
            float targetDistance = distance;
            Vector3 toCamera = (desiredPos - pivot).normalized;
            var hits = Physics.SphereCastAll(pivot, cameraCollisionRadius, toCamera, distance, ~0, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                if (hit.collider.transform.root == transform.root) continue;
                if (hit.distance > 0f && hit.distance < targetDistance) targetDistance = hit.distance;
            }

            _cameraRig.SetPositionAndRotation(pivot + camRot * new Vector3(0f, 0f, -targetDistance), camRot);
        }

        private void SetCursorLocked(bool locked)
        {
            if (locked && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (!locked && Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }
}
