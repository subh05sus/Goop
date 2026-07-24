using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Goop.Player
{
    public enum Team
    {
        None,
        Hider,
        Seeker
    }

    public class NetworkPlayer : NetworkBehaviour
    {
        public NetworkVariable<FixedString32Bytes> DisplayName = new(
            writePerm: NetworkVariableWritePermission.Owner);

        public NetworkVariable<Team> CurrentTeam = new(
            Team.None,
            writePerm: NetworkVariableWritePermission.Server);

        public NetworkVariable<bool> IsAlive = new(
            true,
            writePerm: NetworkVariableWritePermission.Server);

        /// <summary>Seekers are frozen during the Prep phase (PRD 6.3) — camera-locked, no movement.</summary>
        public NetworkVariable<bool> IsFrozen = new(
            false,
            writePerm: NetworkVariableWritePermission.Server);

        /// <summary>Seeker-only, meaningful when the host's ammo mode is enabled (PRD 7.4).</summary>
        public NetworkVariable<int> AmmoRemaining = new(
            0,
            writePerm: NetworkVariableWritePermission.Server);

        /// <summary>Survival time (Hiders) or successful tags (Seekers) — basic round scoreboard (PRD 6/11).</summary>
        public NetworkVariable<int> Score = new(
            0,
            writePerm: NetworkVariableWritePermission.Server);

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                DisplayName.Value = $"Player{OwnerClientId}";
            }
        }

        /// <summary>Server-initiated teleport. The NetworkTransform is owner-authoritative, so a server-side
        /// transform write would just get overwritten by the owner's next update — instead the server asks
        /// the owner to move itself. CharacterController must be toggled or it snaps the transform back.</summary>
        [ClientRpc]
        public void TeleportClientRpc(Vector3 position, float yawDegrees)
        {
            if (!IsOwner) return;

            var attach = GetComponent<SurfaceAttachController>();
            if (attach != null && attach.IsAttached) attach.ForceDetach();

            var controller = GetComponent<CharacterController>();
            bool wasEnabled = controller != null && controller.enabled;
            if (controller != null) controller.enabled = false;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f));
            if (controller != null) controller.enabled = wasEnabled;
        }
    }
}
