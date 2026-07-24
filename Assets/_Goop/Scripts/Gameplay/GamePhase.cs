namespace Goop.Gameplay
{
    /// <summary>
    /// Session flow per the Game Feel doc: the lobby is a real 3D room inside the arena scene where
    /// players hang out, practice painting, and pass the gun around. When the match starts, Hiders are
    /// teleported to the map while the Seeker physically stays behind in the lobby room (spatial fairness
    /// — no blindfold tricks). A short Transition beat moves the Seeker in, then the Hunt runs.
    /// </summary>
    public enum GamePhase
    {
        /// <summary>Everyone in the 3D lobby room: free movement, paint/pose practice, gun hand-off.</summary>
        LobbyIdle,
        /// <summary>Hiders on the map picking spots; the Seeker is still alone in the lobby room.</summary>
        Hide,
        /// <summary>Short ceremony beat ("the hunt begins") while the Seeker is moved onto the map.</summary>
        Transition,
        Hunt,
        Resolution,
        PostRound
    }
}
