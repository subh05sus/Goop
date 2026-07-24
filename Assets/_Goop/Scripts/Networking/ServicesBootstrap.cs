using System;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace Goop.Networking
{
    /// <summary>Initializes Unity Gaming Services + anonymous auth. Must complete before Relay/Lobby calls.</summary>
    public static class ServicesBootstrap
    {
        public static bool IsReady =>
            UnityServices.State == ServicesInitializationState.Initialized && AuthenticationService.Instance.IsSignedIn;

        public static string PlayerId => AuthenticationService.Instance.PlayerId;

        private static Task _initTask;

        public static Task InitializeAsync()
        {
            // Always re-derive readiness from the real SDK state rather than trusting a cached flag — a
            // domain reload (or any other event that resets UnityServices without us knowing) can leave
            // a stale "ready" flag pointing at a dead session, so this self-heals instead of trusting it.
            if (IsReady) return Task.CompletedTask;
            _initTask = InitializeInternalAsync();
            return _initTask;
        }

        private static async Task InitializeInternalAsync()
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                await UnityServices.InitializeAsync();
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            Debug.Log($"[ServicesBootstrap] Ready. PlayerId={PlayerId}");
        }
    }
}
