using Goop.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Gameplay
{
    /// <summary>
    /// The one gun (Game Feel doc §3): a physical world object in the lobby room. Whoever is holding it
    /// when the host presses Start becomes the Seeker. Role selection is social — pick it up, pass it,
    /// duck out of it — not a menu.
    ///   E — pick up (near the gun) / hand off (holder, aiming at a player within pass range)
    ///   G — drop (holder, fallback to hand-off)
    /// Server-authoritative holder state; the gun visually snaps to its holder on every client, with a
    /// clearly readable "has the gun" marker over the holder's head.
    /// </summary>
    public class GunPickup : NetworkBehaviour
    {
        public const ulong NoHolder = ulong.MaxValue;

        [SerializeField] private float pickupRange = 2.5f;
        [SerializeField] private float passRange = 3.5f;

        public static GunPickup Instance { get; private set; }

        public NetworkVariable<ulong> HolderClientId = new(
            NoHolder,
            writePerm: NetworkVariableWritePermission.Server);

        public bool HasHolder => HolderClientId.Value != NoHolder;

        private Vector3 _homePosition;
        private Quaternion _homeRotation;

        private void Awake()
        {
            Instance = this;
            _homePosition = transform.position;
            _homeRotation = transform.rotation;
        }

        /// <summary>Server: return the gun to its lobby stand (round reset).</summary>
        public void ResetToHome()
        {
            if (!IsServer) return;
            HolderClientId.Value = NoHolder;
        }

        private void Update()
        {
            UpdateVisualAttachment();
            HandleLocalInput();
        }

        private void UpdateVisualAttachment()
        {
            if (!HasHolder)
            {
                transform.SetPositionAndRotation(_homePosition, _homeRotation);
                return;
            }

            Transform holder = FindPlayerTransform(HolderClientId.Value);
            if (holder == null) return;
            // Held at the right hip, pointing forward — readable from a distance.
            transform.SetPositionAndRotation(
                holder.position + holder.right * 0.4f + Vector3.up * 1.1f + holder.forward * 0.2f,
                holder.rotation);
        }

        private static Transform FindPlayerTransform(ulong clientId)
        {
            foreach (var player in FindObjectsByType<NetworkPlayer>())
            {
                if (player.OwnerClientId == clientId) return player.transform;
            }
            return null;
        }

        private void HandleLocalInput()
        {
            if (Keyboard.current == null || NetworkManager.Singleton == null) return;
            var localObj = NetworkManager.Singleton.LocalClient?.PlayerObject;
            if (localObj == null) return;

            var pc = localObj.GetComponent<PlayerController>();
            if (pc != null && pc.MovementLocked) return; // chat/paint/pause owns the keys

            // Gun changes hands only in the lobby phase — mid-round the Seeker keeps it.
            var gsm = GameStateManager.Instance;
            if (gsm == null || gsm.Phase.Value != GamePhase.LobbyIdle) return;

            ulong myId = NetworkManager.Singleton.LocalClientId;
            bool iAmHolder = HolderClientId.Value == myId;

            if (Keyboard.current.eKey.wasPressedThisFrame)
            {
                if (!iAmHolder && !HasHolder
                    && Vector3.Distance(localObj.transform.position, transform.position) <= pickupRange)
                {
                    RequestPickupServerRpc();
                }
                else if (iAmHolder)
                {
                    ulong target = FindPassTarget(localObj.transform);
                    if (target != NoHolder) RequestPassServerRpc(target);
                }
            }
            else if (Keyboard.current.gKey.wasPressedThisFrame && iAmHolder)
            {
                RequestDropServerRpc();
            }
        }

        private ulong FindPassTarget(Transform me)
        {
            Camera cam = Camera.main;
            if (cam == null) return NoHolder;

            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            if (!Physics.Raycast(ray, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore)) return NoHolder;

            var target = hit.collider.GetComponentInParent<NetworkPlayer>();
            if (target == null || target.transform == me.root || target.transform == me) return NoHolder;
            if (Vector3.Distance(me.position, target.transform.position) > passRange) return NoHolder;
            return target.OwnerClientId;
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestPickupServerRpc(ServerRpcParams rpcParams = default)
        {
            if (HasHolder) return;
            if (GameStateManager.Instance != null && GameStateManager.Instance.Phase.Value != GamePhase.LobbyIdle) return;

            ulong requester = rpcParams.Receive.SenderClientId;
            Transform player = FindPlayerTransform(requester);
            if (player == null) return;
            if (Vector3.Distance(player.position, transform.position) > pickupRange + 1f) return; // slack for latency

            HolderClientId.Value = requester;
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestPassServerRpc(ulong targetClientId, ServerRpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != HolderClientId.Value) return;
            if (GameStateManager.Instance != null && GameStateManager.Instance.Phase.Value != GamePhase.LobbyIdle) return;

            Transform holder = FindPlayerTransform(HolderClientId.Value);
            Transform target = FindPlayerTransform(targetClientId);
            if (holder == null || target == null) return;
            if (Vector3.Distance(holder.position, target.position) > passRange + 1f) return;

            HolderClientId.Value = targetClientId;
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestDropServerRpc(ServerRpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != HolderClientId.Value) return;
            HolderClientId.Value = NoHolder;
        }

        /// <summary>Server: if the holder disconnects, the gun goes back to the stand.</summary>
        public override void OnNetworkSpawn()
        {
            if (IsServer && NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            }
        }

        private void OnClientDisconnect(ulong clientId)
        {
            if (HolderClientId.Value == clientId) HolderClientId.Value = NoHolder;
        }

        private void OnGUI()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            // "Who has Seeker duty" must read clearly from a distance (Game Feel doc §3).
            if (HasHolder)
            {
                Transform holder = FindPlayerTransform(HolderClientId.Value);
                if (holder == null) return;
                Vector3 screen = cam.WorldToScreenPoint(holder.position + Vector3.up * 2.5f);
                if (screen.z <= 0f) return;
                var prev = GUI.color;
                GUI.color = Color.yellow;
                bool isMe = NetworkManager.Singleton != null && HolderClientId.Value == NetworkManager.Singleton.LocalClientId;
                string label = isMe ? "YOU HAVE THE GUN (E aim at player = pass · G drop)" : "HAS THE GUN — will be Seeker";
                GUI.Label(new Rect(screen.x - 150, Screen.height - screen.y - 10, 300, 22), label,
                    new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
                GUI.color = prev;
            }
            else
            {
                Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * 0.8f);
                if (screen.z <= 0f) return;
                GUI.Label(new Rect(screen.x - 100, Screen.height - screen.y - 10, 200, 22), "GUN — E to pick up",
                    new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
            }
        }
    }
}
