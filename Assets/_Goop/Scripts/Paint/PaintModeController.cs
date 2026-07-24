using Goop.Gameplay;
using Goop.Player;
using Goop.UI;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Paint
{
    /// <summary>
    /// Hider paint mode (F key), mirroring the reference control scheme:
    ///   F            — enter/exit paint mode (frees the cursor, locks movement, pulls camera in close)
    ///   Left mouse   — paint at the cursor (handled by PaintableSkin while PaintingEnabled)
    ///   Right mouse  — hold + move mouse horizontally to adjust brush size live
    ///   Middle mouse — hold + drag to orbit the camera around yourself to inspect the paint job
    ///   Space        — eyedropper: sample the world (or your own body) color under the cursor
    /// Sits on the same GameObject as PaintableSkin (the visual child of the player prefab).
    /// </summary>
    [RequireComponent(typeof(PaintableSkin))]
    public class PaintModeController : NetworkBehaviour
    {
        [SerializeField] private float brushSizePerPixel = 0.0004f;

        public bool InPaintMode { get; private set; }

        private PaintableSkin _skin;
        private PlayerController _playerController;
        private PaletteUI _palette;
        private LineRenderer _brushRing;

        private void Awake()
        {
            _skin = GetComponent<PaintableSkin>();
            _playerController = GetComponentInParent<PlayerController>();
            _palette = GetComponent<PaletteUI>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) enabled = false;
        }

        private bool CanUsePaintMode()
        {
            if (GameStateManager.Instance == null) return false;
            var netPlayer = GetComponentInParent<NetworkPlayer>();
            // Painting is a Hider tool. Team.None (pre-role-assignment) is allowed so it can be tested.
            return netPlayer == null || netPlayer.CurrentTeam.Value != Team.Seeker;
        }

        private void Update()
        {
            if (!IsOwner || Keyboard.current == null || Mouse.current == null) return;

            if (Keyboard.current.fKey.wasPressedThisFrame)
            {
                if (InPaintMode) ExitPaintMode();
                // Don't enter if another system (chat, pause, pose wheel) already owns the input.
                else if (CanUsePaintMode() && (_playerController == null || !_playerController.MovementLocked)) EnterPaintMode();
            }

            if (!InPaintMode) return;

            // Leaving the arena (round ended) force-exits paint mode.
            if (GameStateManager.Instance == null)
            {
                ExitPaintMode();
                return;
            }

            // Brush size: hold RMB, drag horizontally.
            if (Mouse.current.rightButton.isPressed)
            {
                float dx = Mouse.current.delta.ReadValue().x;
                _skin.BrushSize += dx * brushSizePerPixel;
            }

            // Self-inspect orbit: hold MMB, drag.
            if (Mouse.current.middleButton.isPressed && _playerController != null)
            {
                _playerController.OrbitCamera(Mouse.current.delta.ReadValue());
            }

            UpdateBrushRing();

            // Eyedropper: Space samples whatever is under the cursor; routed through the palette so the
            // wheel follows the sample.
            if (Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                if (_skin.TrySampleWorldColor(Mouse.current.position.ReadValue(), out Color32 sampled))
                {
                    if (_palette != null) _palette.SetColor(sampled);
                    else _skin.CurrentColor = sampled;
                }
            }
        }

        /// <summary>Brush-size preview: a ring hugging the mesh at the cursor, oriented perpendicular to
        /// the surface normal, radius matching the brush footprint. Hidden when the cursor isn't on the
        /// body or is over the palette.</summary>
        private void UpdateBrushRing()
        {
            if (_brushRing == null) return;

            RaycastHit hit = default;
            bool show = !PaletteUI.PointerOverPaintUI
                        && _skin.TryGetBrushPoint(Mouse.current.position.ReadValue(), out hit);
            _brushRing.gameObject.SetActive(show);
            if (!show) return;

            // UV brush size -> approximate world radius: the UV atlas spans roughly the ~2m body,
            // so world radius ≈ brushSize * 2. Close enough for an aiming aid.
            float worldRadius = Mathf.Max(0.02f, _skin.BrushSize * 2f);
            var t = _brushRing.transform;
            t.SetPositionAndRotation(hit.point + hit.normal * 0.01f, Quaternion.LookRotation(hit.normal));
            t.localScale = Vector3.one * worldRadius;
            _brushRing.startColor = _brushRing.endColor = (Color)_skin.CurrentColor;
        }

        private void CreateBrushRing()
        {
            var go = new GameObject("BrushRing");
            go.transform.SetParent(null);
            _brushRing = go.AddComponent<LineRenderer>();
            _brushRing.loop = true;
            _brushRing.useWorldSpace = false;
            _brushRing.widthMultiplier = 0.012f;
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            _brushRing.material = unlit != null ? new Material(unlit) : new Material(Shader.Find("Sprites/Default"));
            _brushRing.material.color = Color.white;

            const int segments = 40;
            _brushRing.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                // Circle in local XY — LookRotation(normal) makes local Z the surface normal, so the
                // ring lies flat on (perpendicular to) the surface.
                _brushRing.SetPosition(i, new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f));
            }
            go.SetActive(false);
        }

        private void EnterPaintMode()
        {
            InPaintMode = true;
            _skin.PaintingEnabled = true;
            if (_brushRing == null) CreateBrushRing();
            if (_palette != null) _palette.Visible = true;
            if (_playerController != null)
            {
                _playerController.SetMovementLock(this, true);
                _playerController.PaintViewActive = true;
            }
        }

        private void ExitPaintMode()
        {
            InPaintMode = false;
            _skin.PaintingEnabled = false;
            if (_brushRing != null) _brushRing.gameObject.SetActive(false);
            if (_palette != null) _palette.Visible = false;
            if (_playerController != null)
            {
                _playerController.SetMovementLock(this, false);
                _playerController.PaintViewActive = false;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner && InPaintMode) ExitPaintMode();
            if (_brushRing != null) Destroy(_brushRing.gameObject);
        }
    }
}
