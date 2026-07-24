using Goop.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Gameplay
{
    /// <summary>
    /// Seeker point-and-tag (PRD 7.4). The owning client raycasts to pick a candidate target for
    /// responsiveness, but a catch is only ever confirmed by an authoritative server-side range + line-of-
    /// sight check — the client's raycast result is never trusted directly (closes the "trust the client"
    /// exploit called out in PRD 9).
    /// </summary>
    public class SeekerTagController : NetworkBehaviour
    {
        [SerializeField] private float tagRange = 4f;
        [SerializeField] private float aimRayDistance = 30f;
        [SerializeField] private InputActionAsset inputActions;

        private NetworkPlayer _networkPlayer;
        private InputAction _attackAction;

        // Never cache Camera.main across scenes/phases — the player camera rig is enabled/disabled as we
        // move between lobby room and arena, and a cached reference can go stale (learned the hard way).
        private Camera OwnerCamera => Camera.main;

        public override void OnNetworkSpawn()
        {
            _networkPlayer = GetComponent<NetworkPlayer>();
            if (!IsOwner) return;

            var map = inputActions.FindActionMap("Player", throwIfNotFound: true);
            _attackAction = map.FindAction("Attack", throwIfNotFound: true);
            _attackAction.performed += OnAttackPerformed;
            _attackAction.Enable();
        }

        public override void OnNetworkDespawn()
        {
            if (_attackAction != null) _attackAction.performed -= OnAttackPerformed;
        }

        private void OnAttackPerformed(InputAction.CallbackContext ctx)
        {
            if (_networkPlayer.CurrentTeam.Value != Team.Seeker) return;
            Camera cam = OwnerCamera;
            if (cam == null) return;
            var gsm = GameStateManager.Instance;
            if (gsm == null || gsm.Phase.Value != GamePhase.Hunt) return;

            ulong targetId = 0;
            Vector2 screenCenter = new(Screen.width / 2f, Screen.height / 2f);
            Ray ray = cam.ScreenPointToRay(screenCenter);
            if (Physics.Raycast(ray, out RaycastHit hit, aimRayDistance))
            {
                var targetPlayer = hit.collider.GetComponentInParent<NetworkPlayer>();
                if (targetPlayer != null && targetPlayer != _networkPlayer)
                {
                    targetId = targetPlayer.NetworkObjectId;
                }
            }

            RequestTagServerRpc(targetId);
        }

        [ServerRpc]
        private void RequestTagServerRpc(ulong targetNetworkObjectId)
        {
            bool confirmedHit = false;

            if (targetNetworkObjectId != 0
                && NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkObjectId, out var targetObj))
            {
                var targetPlayer = targetObj.GetComponent<NetworkPlayer>();
                if (targetPlayer != null && targetPlayer.CurrentTeam.Value == Team.Hider && targetPlayer.IsAlive.Value)
                {
                    Vector3 origin = transform.position + Vector3.up * 1.5f;
                    Vector3 targetPos = targetObj.transform.position + Vector3.up * 1f;
                    Vector3 toTarget = targetPos - origin;
                    float distance = toTarget.magnitude;

                    if (distance <= tagRange)
                    {
                        // Server-side line-of-sight re-check — a wall between attacker and target blocks the tag.
                        bool blocked = Physics.Raycast(origin, toTarget.normalized, out RaycastHit blockHit, distance)
                                       && blockHit.collider.GetComponentInParent<NetworkPlayer>() != targetPlayer;
                        if (!blocked)
                        {
                            targetPlayer.IsAlive.Value = false;
                            _networkPlayer.Score.Value += 1;
                            confirmedHit = true;
                        }
                    }
                }
            }

            if (!confirmedHit)
            {
                RegisterMiss();
            }

            GameStateManager.Instance?.CheckEarlyRoundEnd();
        }

        private void RegisterMiss()
        {
            if (!MatchSettings.AmmoModeEnabled) return;
            if (_networkPlayer.AmmoRemaining.Value <= 0) return;
            _networkPlayer.AmmoRemaining.Value -= 1;
        }
    }
}
