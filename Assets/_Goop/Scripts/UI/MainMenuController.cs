using System.Collections.Generic;
using Goop.Networking;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Goop.UI
{
    public class MainMenuController : MonoBehaviour
    {
        [SerializeField] private InputField joinCodeInput;
        [SerializeField] private Text statusText;
        [SerializeField] private Button hostButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Button browseButton;
        [SerializeField] private Transform browseListContainer;
        [SerializeField] private GameObject browseEntryPrefab;

        private void Start()
        {
            // MainMenu can end up loaded directly (MPPM virtual players, a misclicked Play, etc.) without
            // Bootstrap having run first, which means NetworkManager was never created. Self-heal by
            // routing back through Bootstrap instead of failing later with a cryptic "singleton not set".
            if (NetworkManager.Singleton == null)
            {
                SceneManager.LoadScene("Bootstrap", LoadSceneMode.Single);
            }
        }

        private void Awake()
        {
            hostButton.onClick.AddListener(OnHostClicked);
            joinButton.onClick.AddListener(OnJoinClicked);
            browseButton.onClick.AddListener(OnBrowseClicked);
        }

        private async void OnHostClicked()
        {
            SetStatus("Hosting...");
            SetInteractable(false);
            try
            {
                await GoopSessionManager.HostAsync("Goop Room", maxPlayers: 8, isPrivate: false, mode: "Normal");
                // Must go through NGO's own scene manager (not a plain SceneManager.LoadScene) — NetworkConfig
                // has EnableSceneManagement=true, so every scene transition from here on needs to be tracked
                // by NetworkManager.SceneManager or joining clients can never finish synchronizing into it.
                // Straight into the arena scene: its lobby room IS the lobby (Game Feel doc §2) — no
                // separate Lobby UI scene anymore.
                NetworkManager.Singleton.SceneManager.LoadScene("Arena_Greybox", LoadSceneMode.Single);
            }
            catch (System.Exception e)
            {
                SetStatus($"Host failed: {e.Message}");
                SetInteractable(true);
            }
        }

        private async void OnJoinClicked()
        {
            string code = joinCodeInput.text.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code))
            {
                SetStatus("Enter a join code first.");
                return;
            }

            SetStatus($"Joining {code}...");
            SetInteractable(false);
            try
            {
                await GoopSessionManager.JoinByCodeAsync(code);
                // Do NOT manually load a scene here — the host's networked scene load (see OnHostClicked)
                // is what NGO uses to synchronize this client into Lobby automatically once connected.
                SetStatus("Joined! Waiting for host...");
            }
            catch (System.Exception e)
            {
                SetStatus($"Join failed: {e.Message}");
                SetInteractable(true);
            }
        }

        private async void OnBrowseClicked()
        {
            SetStatus("Browsing public rooms...");
            foreach (Transform child in browseListContainer) Destroy(child.gameObject);

            try
            {
                IList<ISessionInfo> sessions = await GoopSessionManager.BrowsePublicSessionsAsync();
                SetStatus($"Found {sessions.Count} room(s).");
                foreach (var info in sessions)
                {
                    var entry = Instantiate(browseEntryPrefab, browseListContainer);
                    var label = entry.GetComponentInChildren<Text>();
                    if (label != null) label.text = $"{info.Name}  ({info.MaxPlayers} max)";

                    var button = entry.GetComponentInChildren<Button>();
                    if (button != null)
                    {
                        string sessionId = info.Id;
                        button.onClick.AddListener(async () =>
                        {
                            SetStatus($"Joining {info.Name}...");
                            await GoopSessionManager.JoinByIdAsync(sessionId);
                            // Same as OnJoinClicked: no manual scene load — NGO syncs us into Lobby once connected.
                            SetStatus("Joined! Waiting for host...");
                        });
                    }
                }
            }
            catch (System.Exception e)
            {
                SetStatus($"Browse failed: {e.Message}");
            }
        }

        private void SetStatus(string msg)
        {
            // Once a Host/Join succeeds, NGO's own scene sync can switch us away from MainMenu (destroying
            // this UI) before an async continuation gets a chance to run — guard every post-await UI touch.
            if (statusText == null) return;
            statusText.text = msg;
        }

        private void SetInteractable(bool interactable)
        {
            if (hostButton == null || joinButton == null || browseButton == null) return;
            hostButton.interactable = interactable;
            joinButton.interactable = interactable;
            browseButton.interactable = interactable;
        }
    }
}
