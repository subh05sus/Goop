namespace Goop.Gameplay
{
    /// <summary>
    /// Host-chosen round settings, set by LobbyController before loading the Arena, read by
    /// GameStateManager.OnNetworkSpawn on the server. Plain static state (not networked) — only the
    /// server/host that started the match needs it; GameStateManager replicates the derived NetworkVariables.
    /// </summary>
    public static class MatchSettings
    {
        public static float PrepDuration = 20f;
        public static float HuntDuration = 90f;
        public static float PostRoundDuration = 8f;
        public static bool AmmoModeEnabled = false;
        public static int AmmoCount = 5;
    }
}
