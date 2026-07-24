using Goop.Gameplay;
using Goop.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Goop.UI
{
    public class LobbyController : MonoBehaviour
    {
        [SerializeField] private Text joinCodeText;
        [SerializeField] private Text playerListText;
        [SerializeField] private Text roleText;
        [SerializeField] private Button startButton;
        [SerializeField] private Button leaveButton;
        [SerializeField] private Toggle ammoModeToggle;

        private float _refreshTimer;
        private readonly System.Collections.Generic.HashSet<ulong> _syncedClients = new();

        private void Awake()
        {
            startButton.onClick.AddListener(OnStartClicked);
            leaveButton.onClick.AddListener(OnLeaveClicked);
        }

        private void Start()
        {
            var session = GoopSessionManager.CurrentSession;
            joinCodeText.text = session != null ? $"Join code: {session.Code}" : "Join code: (none)";
            roleText.text = NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost ? "You are the Host" : "You are a Client";
            bool isHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;
            startButton.gameObject.SetActive(isHost);
            if (ammoModeToggle != null)
            {
                ammoModeToggle.gameObject.SetActive(isHost);
                ammoModeToggle.isOn = MatchSettings.AmmoModeEnabled;
                ammoModeToggle.onValueChanged.AddListener(v => MatchSettings.AmmoModeEnabled = v);
            }

            if (isHost && NetworkManager.Singleton.SceneManager != null)
            {
                // A client having a spawned PlayerObject only means the SERVER has spawned it — not that the
                // client has actually finished receiving/processing that spawn data. OnSynchronizeComplete is
                // NGO's real "this client is fully caught up" signal; only trust that for the Start guard.
                _syncedClients.Add(NetworkManager.ServerClientId);
                NetworkManager.Singleton.SceneManager.OnSynchronizeComplete += OnClientSynchronizeComplete;
            }

            RefreshPlayerList();
        }

        private void OnDestroy()
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnSynchronizeComplete -= OnClientSynchronizeComplete;
            }
        }

        private void OnClientSynchronizeComplete(ulong clientId)
        {
            _syncedClients.Add(clientId);
        }

        private void Update()
        {
            _refreshTimer += Time.deltaTime;
            if (_refreshTimer >= 1f)
            {
                _refreshTimer = 0f;
                RefreshPlayerList();
            }
        }

        private void RefreshPlayerList()
        {
            var session = GoopSessionManager.CurrentSession;
            if (session == null)
            {
                playerListText.text = "(no session)";
                return;
            }

            playerListText.text = $"Players ({session.Players.Count}):\n";
            foreach (var player in session.Players)
            {
                playerListText.text += $" - {player.Id}\n";
            }
        }

        private void OnStartClicked()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost) return;

            // Guard against starting the scene transition while a just-joined client's initial connection
            // sync is still in flight — racing that with a scene-migration event causes NGO's
            // "NetworkObjectId was not spawned or no longer exists" scene-sync error. A non-null
            // PlayerObject only proves the SERVER spawned it; OnSynchronizeComplete is what proves the
            // client itself has actually finished catching up.
            foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
            {
                if (!_syncedClients.Contains(clientId))
                {
                    Debug.LogWarning($"[LobbyController] Client {clientId} hasn't finished synchronizing yet — try Start again in a moment.");
                    return;
                }
            }

            NetworkManager.Singleton.SceneManager.LoadScene("Arena_Greybox", UnityEngine.SceneManagement.LoadSceneMode.Single);
        }

        private async void OnLeaveClicked()
        {
            await GoopSessionManager.LeaveAsync();
            if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient))
            {
                NetworkManager.Singleton.Shutdown();
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu", UnityEngine.SceneManagement.LoadSceneMode.Single);
        }
    }
}
