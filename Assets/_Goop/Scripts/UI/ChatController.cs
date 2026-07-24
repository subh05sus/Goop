using System.Collections.Generic;
using Goop.Player;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.UI
{
    /// <summary>
    /// Networked text chat: T opens the input line, Enter sends (ServerRpc -> ClientRpc broadcast),
    /// Esc cancels. Movement is locked while typing. The last few messages fade in the lower-left.
    /// Works in both the lobby and the arena. Sits on the Player prefab root.
    /// </summary>
    public class ChatController : NetworkBehaviour
    {
        private const int MaxVisibleMessages = 8;
        private const float MessageLifetime = 12f;

        // Client-local chat log, shared across all player instances on this client.
        private static readonly List<(string text, float time)> Messages = new();

        /// <summary>Frame on which chat consumed an Esc press — lets the pause menu skip that same press.</summary>
        public static int LastEscConsumedFrame = -1;

        private Goop.Player.PlayerController _playerController;
        private bool _typing;
        private string _draft = "";

        private void Awake()
        {
            _playerController = GetComponent<Goop.Player.PlayerController>();
        }

        private void Update()
        {
            if (!IsOwner || Keyboard.current == null) return;

            bool inputBusy = _playerController != null && _playerController.MovementLocked;

            if (!_typing && !inputBusy && Keyboard.current.tKey.wasPressedThisFrame)
            {
                _typing = true;
                _draft = "";
                if (_playerController != null) _playerController.SetMovementLock(this, true);
            }
            else if (_typing && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                LastEscConsumedFrame = Time.frameCount;
                StopTyping();
            }
            else if (_typing && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame))
            {
                string message = _draft.Trim();
                if (message.Length > 0)
                {
                    var netPlayer = GetComponent<NetworkPlayer>();
                    string sender = netPlayer != null ? netPlayer.DisplayName.Value.ToString() : $"Player{OwnerClientId}";
                    SendChatServerRpc(new FixedString128Bytes(Truncate($"{sender}: {message}", 125)));
                }
                StopTyping();
            }
        }

        private void StopTyping()
        {
            _typing = false;
            _draft = "";
            if (_playerController != null) _playerController.SetMovementLock(this, false);
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        [ServerRpc]
        private void SendChatServerRpc(FixedString128Bytes message)
        {
            ReceiveChatClientRpc(message);
        }

        [ClientRpc]
        private void ReceiveChatClientRpc(FixedString128Bytes message)
        {
            Messages.Add((message.ToString(), Time.time));
            while (Messages.Count > MaxVisibleMessages) Messages.RemoveAt(0);
        }

        private void OnGUI()
        {
            if (!IsOwner) return;

            float y = Screen.height - 90f;
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                var (text, time) = Messages[i];
                if (!_typing && Time.time - time > MessageLifetime) continue; // old messages hidden unless typing
                GUI.Label(new Rect(10, y, 500, 22), text);
                y -= 22f;
            }

            if (_typing)
            {
                GUI.SetNextControlName("ChatInput");
                _draft = GUI.TextField(new Rect(10, Screen.height - 60f, 400, 24), _draft, 125);
                GUI.FocusControl("ChatInput");
                GUI.Label(new Rect(415, Screen.height - 60f, 200, 24), "Enter = send · Esc = cancel");
            }
            else
            {
                GUI.Label(new Rect(10, Screen.height - 60f, 200, 22), "T = chat");
            }
        }
    }
}
