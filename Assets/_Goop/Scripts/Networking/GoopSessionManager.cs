using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Goop.Networking
{
    /// <summary>
    /// Wraps Unity's unified Session API (Relay + Lobby + Auth) so callers don't touch Relay/Lobby
    /// separately. A Session is a hosted-or-joined multiplayer room; NGO connection is handled by the
    /// Session's network integration.
    /// </summary>
    public static class GoopSessionManager
    {
        public const string JoinCodePropertyKey = "joinCode";
        public const string ModePropertyKey = "mode";

        public static ISession CurrentSession { get; private set; }

        public static async Task<ISession> HostAsync(string sessionName, int maxPlayers, bool isPrivate, string mode)
        {
            await ServicesBootstrap.InitializeAsync();

            var options = new SessionOptions
            {
                Name = sessionName,
                MaxPlayers = maxPlayers,
                IsPrivate = isPrivate,
                PlayerProperties = new Dictionary<string, PlayerProperty>
                {
                    { "name", new PlayerProperty($"Host-{ServicesBootstrap.PlayerId[..6]}") }
                },
                SessionProperties = new Dictionary<string, SessionProperty>
                {
                    { ModePropertyKey, new SessionProperty(mode) }
                }
            }.WithRelayNetwork();

            CurrentSession = await MultiplayerService.Instance.CreateSessionAsync(options);
            Debug.Log($"[GoopSessionManager] Hosted session '{CurrentSession.Id}' code={CurrentSession.Code}");
            return CurrentSession;
        }

        public static async Task<ISession> JoinByCodeAsync(string joinCode)
        {
            await ServicesBootstrap.InitializeAsync();

            var options = new JoinSessionOptions
            {
                PlayerProperties = new Dictionary<string, PlayerProperty>
                {
                    { "name", new PlayerProperty($"Player-{ServicesBootstrap.PlayerId[..6]}") }
                }
            };

            CurrentSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(joinCode, options);
            Debug.Log($"[GoopSessionManager] Joined session '{CurrentSession.Id}' via code {joinCode}");
            return CurrentSession;
        }

        public static async Task<IList<ISessionInfo>> BrowsePublicSessionsAsync()
        {
            await ServicesBootstrap.InitializeAsync();

            QuerySessionsResults results = await MultiplayerService.Instance.QuerySessionsAsync(new QuerySessionsOptions());
            return results.Sessions;
        }

        public static async Task<ISession> JoinByIdAsync(string sessionId)
        {
            await ServicesBootstrap.InitializeAsync();

            CurrentSession = await MultiplayerService.Instance.JoinSessionByIdAsync(sessionId, new JoinSessionOptions());
            return CurrentSession;
        }

        public static async Task LeaveAsync()
        {
            if (CurrentSession == null) return;
            try
            {
                await CurrentSession.LeaveAsync();
            }
            catch (Exception e)
            {
                // The remote session/lobby can already be gone (host ended it, connection dropped, etc.) —
                // that's still a successful "leave" from the local player's point of view.
                Debug.LogWarning($"[GoopSessionManager] LeaveAsync: remote session already gone ({e.Message})");
            }
            finally
            {
                CurrentSession = null;
            }
        }
    }
}
