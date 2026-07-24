using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Goop.Networking
{
    /// <summary>
    /// Global disconnect safety net. Lives on the NetworkManager object (Bootstrap, DontDestroyOnLoad).
    /// Covers every "the session died under us" case:
    ///   - host closes the app or their editor -> clients lose the connection
    ///   - host clicks Leave -> server shuts down -> clients disconnected
    ///   - transport failure (relay hiccup, network drop) on anyone
    /// On any of those, the local player is cleanly torn down (tolerant session leave + NGO shutdown) and
    /// returned to the MainMenu with a human-readable reason instead of being stranded in a dead scene.
    /// </summary>
    public class ConnectionWatchdog : MonoBehaviour
    {
        /// <summary>MainMenu shows this on load so the player knows why they're back there.</summary>
        public static string LastDisconnectReason;

        private bool _handling;

        private void Start()
        {
            if (NetworkManager.Singleton == null) return;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
            NetworkManager.Singleton.OnTransportFailure += OnTransportFailure;
        }

        private void OnDestroy()
        {
            if (NetworkManager.Singleton == null) return;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            NetworkManager.Singleton.OnTransportFailure -= OnTransportFailure;
        }

        private void OnClientDisconnect(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            // On the server this fires for REMOTE clients leaving — that's handled by gameplay systems
            // (gun reset, round re-evaluation), not a local teardown.
            if (nm.IsServer) return;

            // On a client this fires when WE are disconnected (host quit, kicked, connection lost).
            if (clientId == nm.LocalClientId || clientId == NetworkManager.ServerClientId)
            {
                HandleDisconnect("Disconnected — the host left or the connection was lost.");
            }
        }

        private void OnTransportFailure()
        {
            HandleDisconnect("Network transport failure — returning to the main menu.");
        }

        private async void HandleDisconnect(string reason)
        {
            if (_handling) return;
            // A voluntary leave already routes through MainMenu — don't double-handle it.
            if (SceneManager.GetActiveScene().name == "MainMenu") return;
            _handling = true;
            LastDisconnectReason = reason;
            Debug.LogWarning($"[ConnectionWatchdog] {reason}");

            try
            {
                await GoopSessionManager.LeaveAsync(); // tolerant of the session already being gone
            }
            finally
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && (nm.IsClient || nm.IsServer))
                {
                    nm.Shutdown();
                }
                SceneManager.LoadScene("MainMenu", LoadSceneMode.Single);
                _handling = false;
            }
        }
    }
}
