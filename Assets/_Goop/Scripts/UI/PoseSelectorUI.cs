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
        [SerializeField] private float wheelRadius = 160f;
        [SerializeField] private float deadzoneRadius = 40f;

        private PoseController _poseController;
        private PlayerController _playerController;
        private bool _wheelOpen;
        private int _hoveredIndex = -1;

        private const int SegmentCount = PoseController.PoseCount + 1; // + idle

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

            if (!_wheelOpen && !inputBusy && Keyboard.current.rKey.wasPressedThisFrame)
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
                int index = _poseController.PoseIndex.Value;
                string label = index == PoseController.IdlePoseIndex ? "Idle" : $"Pose {index}";
                GUI.Label(new Rect(10, Screen.height - 30, 300, 25), $"Pose: {label}  (hold R for pose wheel)");
                return;
            }

            Vector2 center = new(Screen.width / 2f, Screen.height / 2f);
            GUI.Label(new Rect(center.x - 60, center.y - 10, 120, 20),
                _hoveredIndex < 0 ? "(release = keep)" : (_hoveredIndex == 0 ? "Idle" : $"Pose {_hoveredIndex}"));

            for (int i = 0; i < SegmentCount; i++)
            {
                float segAngle = (i + 0.5f) / SegmentCount * 360f * Mathf.Deg2Rad;
                Vector2 pos = center + new Vector2(Mathf.Sin(segAngle), -Mathf.Cos(segAngle)) * wheelRadius;

                var prev = GUI.backgroundColor;
                GUI.backgroundColor = i == _hoveredIndex ? Color.yellow : Color.white;
                string label = i == 0 ? "Idle" : i.ToString();
                GUI.Box(new Rect(pos.x - 20, pos.y - 14, 40, 28), label);
                GUI.backgroundColor = prev;
            }
        }
    }
}
