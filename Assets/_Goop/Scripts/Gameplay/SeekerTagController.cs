using Goop.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Gameplay
{
    /// <summary>
    /// Seeker shooting (PRD 7.4 + Game Feel doc §8). The owning client aims with a center-screen
    /// crosshair and fires with Attack (LMB). The client walks ALL ray hits and skips its own colliders
    /// (the camera sits behind the player, so a naive single raycast always hit the Seeker's own capsule —
    /// this was why shooting "didn't work"). A catch is only confirmed by the server's own range +
    /// line-of-sight re-check; the client's ray is never trusted (PRD 9).
    /// Also draws the crosshair and hit/miss feedback for the gun holder.
    /// </summary>
    public class SeekerTagController : NetworkBehaviour
    {
        [SerializeField] private float tagRange = 18f;
        [SerializeField] private float aimRayDistance = 60f;
        [SerializeField] private float shotCooldown = 1f;
        [SerializeField] private InputActionAsset inputActions;

        private NetworkPlayer _networkPlayer;
        private InputAction _attackAction;
        private float _lastShotTime = -999f;
        private float _feedbackUntil;
        private bool _feedbackWasHit;

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

        private bool IsHoldingGun =>
            GunPickup.Instance != null
            && NetworkManager.Singleton != null
            && GunPickup.Instance.HolderClientId.Value == OwnerClientId;

        private void OnAttackPerformed(InputAction.CallbackContext ctx)
        {
            if (_networkPlayer.CurrentTeam.Value != Team.Seeker) return;
            var gsm = GameStateManager.Instance;
            if (gsm == null || gsm.Phase.Value != GamePhase.Hunt) return;
            if (Time.time - _lastShotTime < shotCooldown) return;

            Camera cam = OwnerCamera;
            if (cam == null) return;
            _lastShotTime = Time.time;

            // Fire from the crosshair: walk all hits, skip our own colliders, take the first thing hit.
            // If that first thing belongs to a player, that's our candidate target; if it's world
            // geometry, the shot is blocked and it's a miss.
            ulong targetId = 0;
            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            RaycastHit[] hits = Physics.RaycastAll(ray, aimRayDistance, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider.transform.root == transform.root) continue; // own body/camera rig

                var targetPlayer = hit.collider.GetComponentInParent<NetworkPlayer>();
                if (targetPlayer != null && targetPlayer != _networkPlayer)
                {
                    targetId = targetPlayer.NetworkObjectId;
                }
                break; // first non-self hit decides: player = candidate, world = blocked
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
                        // Server-side line-of-sight re-check — walk all hits, skip both bodies' own
                        // colliders, and see whether world geometry sits between shooter and target.
                        bool blocked = false;
                        RaycastHit[] hits = Physics.RaycastAll(origin, toTarget.normalized, distance, ~0, QueryTriggerInteraction.Ignore);
                        foreach (var hit in hits)
                        {
                            if (hit.collider.transform.root == transform.root) continue;
                            if (hit.collider.transform.root == targetObj.transform.root) continue;
                            blocked = true;
                            break;
                        }
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

            TagResultClientRpc(confirmedHit, RpcTargetOwner());
            GameStateManager.Instance?.CheckEarlyRoundEnd();
        }

        private ClientRpcParams RpcTargetOwner() => new()
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };

        [ClientRpc]
        private void TagResultClientRpc(bool hit, ClientRpcParams rpcParams = default)
        {
            _feedbackWasHit = hit;
            _feedbackUntil = Time.time + 0.6f;
        }

        private void RegisterMiss()
        {
            if (!MatchSettings.AmmoModeEnabled) return;
            if (_networkPlayer.AmmoRemaining.Value <= 0) return;
            _networkPlayer.AmmoRemaining.Value -= 1;
        }

        private void OnGUI()
        {
            if (!IsOwner || _networkPlayer == null) return;
            var gsm = GameStateManager.Instance;
            if (gsm == null) return;

            // Crosshair whenever this player is holding the gun (lobby practice aim included).
            if (!IsHoldingGun && _networkPlayer.CurrentTeam.Value != Team.Seeker) return;

            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            var prev = GUI.color;

            bool onCooldown = Time.time - _lastShotTime < shotCooldown && gsm.Phase.Value == GamePhase.Hunt;
            GUI.color = onCooldown ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
            GUI.DrawTexture(new Rect(cx - 9, cy - 1, 6, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx + 3, cy - 1, 6, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1, cy - 9, 2, 6), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1, cy + 3, 2, 6), Texture2D.whiteTexture);

            if (Time.time < _feedbackUntil)
            {
                GUI.color = _feedbackWasHit ? Color.green : Color.red;
                var style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold
                };
                GUI.Label(new Rect(cx - 60, cy + 20, 120, 30), _feedbackWasHit ? "HIT!" : "MISS", style);
            }
            GUI.color = prev;
        }
    }
}
