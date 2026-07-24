using UnityEngine;

namespace Goop.Player
{
    /// <summary>
    /// Drives the Animator's "Speed" float (0 idle, ~0.65 walk, 1 run) from the character's actual planar
    /// velocity. Runs identically on every client — remote players' movement arrives via NetworkTransform,
    /// so measuring transform delta gives correct walk/run animation everywhere without a NetworkAnimator.
    /// Sits on the visual child next to the Animator.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class LocomotionAnimator : MonoBehaviour
    {
        [SerializeField] private float runReferenceSpeed = 7.5f; // matches PlayerController.runSpeed

        private Animator _animator;
        private Vector3 _lastRootPos;
        private float _smoothedSpeed01;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _lastRootPos = transform.root.position;
        }

        private void Update()
        {
            if (Time.deltaTime <= 0f) return;

            Vector3 rootPos = transform.root.position;
            Vector3 delta = rootPos - _lastRootPos;
            _lastRootPos = rootPos;
            delta.y = 0f;

            float speed01 = Mathf.Clamp01(delta.magnitude / Time.deltaTime / runReferenceSpeed);
            // Teleports (round start etc.) produce one absurd delta — ignore spikes.
            if (delta.magnitude / Time.deltaTime > runReferenceSpeed * 3f) speed01 = _smoothedSpeed01;

            _smoothedSpeed01 = Mathf.Lerp(_smoothedSpeed01, speed01, 12f * Time.deltaTime);
            _animator.SetFloat("Speed", _smoothedSpeed01);
        }
    }
}
