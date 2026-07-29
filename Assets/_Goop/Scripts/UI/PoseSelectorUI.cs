using Goop.Gameplay;
using Goop.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.UI
{
    /// <summary>
    /// Radial pose wheel (PRD 7.2, reference-parity controls): hold R to open, move the mouse toward a
    /// segment to select, release R to confirm. Center deadzone = keep current pose. Segment 0 is Idle,
    /// segments 1..19 are the pose clips. Movement is locked while the wheel is open.
    /// (Class name kept from the old cycle-selector so the Player prefab's component reference survives.)
    /// </summary>
    public class PoseSelectorUI : MonoBehaviour
    {
        [SerializeField] private float wheelRadius = 200f;
        [SerializeField] private float deadzoneRadius = 45f;

        private PoseController _poseController;
        private PlayerController _playerController;
        private bool _wheelOpen;
        private int _hoveredIndex = -1;
        private Texture2D[] _previews; // index 1..PoseCount; [0] unused (idle has no thumbnail)

        private const int SegmentCount = PoseController.PoseCount + 1; // + idle

        private static string PoseLabel(int index) => PoseCatalog.Display(index);

        /// <summary>Thumbnails are pre-rendered at edit time into Resources/PosePreviews (pose_01..pose_18)
        /// — zero runtime rendering cost, just a texture load on first wheel open.</summary>
        private void EnsurePreviews()
        {
            if (_previews != null) return;
            _previews = new Texture2D[SegmentCount];
            for (int i = 1; i < SegmentCount; i++)
            {
                _previews[i] = Resources.Load<Texture2D>($"PosePreviews/pose_{i:00}");
            }
        }

        public void Initialize(PoseController poseController)
        {
            _poseController = poseController;
            _playerController = GetComponentInParent<PlayerController>();
        }

        private void Update()
        {
            if (_poseController == null || Keyboard.current == null || Mouse.current == null) return;
            if (GameStateManager.Instance == null)
            {
                if (_wheelOpen) CloseWheel(confirm: false);
                return;
            }

            // Don't open over chat/paint/pause — any active input lock means R belongs to someone else.
            bool inputBusy = _playerController != null && _playerController.MovementLocked;

            // Poses are a Hider tool — the Seeker's stances are stand/crouch(Ctrl)/prone(X) only.
            var netPlayer = GetComponentInParent<NetworkPlayer>();
            bool isSeeker = netPlayer != null && netPlayer.CurrentTeam.Value == Team.Seeker;
            if (isSeeker && _wheelOpen) CloseWheel(confirm: false);

            if (!_wheelOpen && !inputBusy && !isSeeker && Keyboard.current.rKey.wasPressedThisFrame)
            {
                _wheelOpen = true;
                _hoveredIndex = -1;
                if (_playerController != null) _playerController.SetMovementLock(this, true);
            }
            else if (_wheelOpen && Keyboard.current.rKey.wasReleasedThisFrame)
            {
                CloseWheel(confirm: true);
            }

            if (_wheelOpen)
            {
                UpdateHoveredSegment();
            }
        }

        private void CloseWheel(bool confirm)
        {
            _wheelOpen = false;
            if (confirm && _hoveredIndex >= 0)
            {
                _poseController.SetPose(_hoveredIndex);
            }
            if (_playerController != null) _playerController.SetMovementLock(this, false);
        }

        private void UpdateHoveredSegment()
        {
            Vector2 center = new(Screen.width / 2f, Screen.height / 2f);
            Vector2 toMouse = Mouse.current.position.ReadValue() - center;

            if (toMouse.magnitude < deadzoneRadius)
            {
                _hoveredIndex = -1; // deadzone: no change on release
                return;
            }

            // Angle 0 = straight up, clockwise. Each segment owns an equal slice of the circle.
            float angle = Mathf.Atan2(toMouse.x, toMouse.y) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            _hoveredIndex = Mathf.FloorToInt(angle / 360f * SegmentCount) % SegmentCount;
        }

        private void OnGUI()
        {
            if (_poseController == null) return;

            if (!_wheelOpen)
            {
                GUI.Label(new Rect(10, Screen.height - 30, 300, 25),
                    $"Pose: {PoseLabel(_poseController.PoseIndex.Value)}  (hold R for pose wheel)");
                return;
            }

            EnsurePreviews();
            Vector2 center = new(Screen.width / 2f, Screen.height / 2f);
            GUI.Label(new Rect(center.x - 70, center.y - 10, 140, 20),
                _hoveredIndex < 0 ? "(release = keep)" : PoseLabel(_hoveredIndex),
                new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold });

            for (int i = 0; i < SegmentCount; i++)
            {
                float segAngle = (i + 0.5f) / SegmentCount * 360f * Mathf.Deg2Rad;
                Vector2 pos = center + new Vector2(Mathf.Sin(segAngle), -Mathf.Cos(segAngle)) * wheelRadius;
                bool hovered = i == _hoveredIndex;
                float size = hovered ? 62f : 48f;

                var prev = GUI.backgroundColor;
                GUI.backgroundColor = hovered ? Color.yellow : Color.white;
                Rect box = new(pos.x - size / 2f, pos.y - size / 2f - 6f, size, size);
                GUI.Box(box, "");
                if (i > 0 && _previews[i] != null)
                {
                    GUI.DrawTexture(new Rect(box.x + 3, box.y + 3, box.width - 6, box.height - 6),
                        _previews[i], ScaleMode.ScaleToFit, true);
                }
                var labelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = hovered ? 12 : 10
                };
                GUI.Label(new Rect(pos.x - 45, box.yMax - 2, 90, 16), PoseLabel(i), labelStyle);
                GUI.backgroundColor = prev;
            }
        }
    }
}
