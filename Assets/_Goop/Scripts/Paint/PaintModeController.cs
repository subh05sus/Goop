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

            // Eyedropper: Space samples whatever is under the cursor. Routed through the palette so the
            // hue wheel jumps to the sampled color too.
            if (Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                if (_skin.TrySampleWorldColor(Mouse.current.position.ReadValue(), out Color32 sampled))
                {
                    if (_palette != null) _palette.SetColor(sampled);
                    else _skin.CurrentColor = sampled;
                }
            }
        }

        private void EnterPaintMode()
        {
            InPaintMode = true;
            _skin.PaintingEnabled = true;
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
        }
    }
}
