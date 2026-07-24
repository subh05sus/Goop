using Goop.UI;
using Unity.Netcode;
using UnityEngine;

namespace Goop.Gameplay
{
    /// <summary>
    /// Networked pose selection per PRD 7.2: a synced enum/int state drives a local Animator directly
    /// (no animation frame streaming). Owner selects; NetworkVariable replicates the index; every client's
    /// own Animator snaps to the matching state via the "PoseIndex" int parameter.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PoseController : NetworkBehaviour
    {
        public const int PoseCount = 19;
        public const int IdlePoseIndex = 0;

        public NetworkVariable<int> PoseIndex = new(
            IdlePoseIndex,
            writePerm: NetworkVariableWritePermission.Owner);

        private Animator _animator;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
        }

        public override void OnNetworkSpawn()
        {
            PoseIndex.OnValueChanged += OnPoseIndexChanged;
            ApplyPose(PoseIndex.Value);

            if (IsOwner)
            {
                var selector = GetComponent<PoseSelectorUI>();
                if (selector != null) selector.Initialize(this);
            }
        }

        public override void OnNetworkDespawn()
        {
            PoseIndex.OnValueChanged -= OnPoseIndexChanged;
        }

        private void OnPoseIndexChanged(int previous, int current)
        {
            ApplyPose(current);
        }

        private void ApplyPose(int index)
        {
            _animator.SetInteger("PoseIndex", index);
        }

        /// <summary>Owner-only: cycle to the next/previous pose. Index wraps within [0, PoseCount].</summary>
        public void CyclePose(int delta)
        {
            if (!IsOwner) return;

            int next = PoseIndex.Value + delta;
            int wrapped = ((next % (PoseCount + 1)) + (PoseCount + 1)) % (PoseCount + 1);
            PoseIndex.Value = wrapped;
        }

        /// <summary>Owner-only: jump straight to a specific pose (0 = idle/no pose).</summary>
        public void SetPose(int index)
        {
            if (!IsOwner) return;
            if (index < 0 || index > PoseCount) return;

            PoseIndex.Value = index;
        }
    }
}
