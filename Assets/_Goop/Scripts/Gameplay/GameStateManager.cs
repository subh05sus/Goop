using System.Collections;
using Goop.Player;
using Unity.Netcode;
using UnityEngine;

namespace Goop.Gameplay
{
    public enum RoundWinner
    {
        None,
        Hiders,
        Seekers
    }

    /// <summary>
    /// Server-authoritative session/round state machine (Game Feel doc). The whole session lives in one
    /// scene containing both the lobby room and the arena map:
    ///   LobbyIdle  — everyone hangs out in the lobby room; paint/pose practice; gun hand-off decides Seeker
    ///   Hide       — Hiders teleported to the map; the Seeker physically stays in the lobby (spatial fairness)
    ///   Transition — short ceremony beat, then the Seeker is teleported onto the map
    ///   Hunt       — Seeker sweeps; tag/ammo/early-end rules as before
    ///   Resolution/PostRound — winner + scoreboard, then everyone returns to the lobby room and it loops.
    /// </summary>
    public class GameStateManager : NetworkBehaviour
    {
        public static GameStateManager Instance { get; private set; }

        [SerializeField] private Transform[] lobbySpawnPoints;
        [SerializeField] private Transform[] hiderSpawnPoints;
        [SerializeField] private Transform[] seekerSpawnPoints;
        [SerializeField] private float transitionDuration = 3f;

        public NetworkVariable<GamePhase> Phase = new(GamePhase.LobbyIdle, writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<float> PhaseTimeRemaining = new(0f, writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<RoundWinner> Winner = new(RoundWinner.None, writePerm: NetworkVariableWritePermission.Server);

        // Host-configured, replicated so everyone in the lobby sees the settings live (Game Feel doc §4).
        public NetworkVariable<float> HideDuration = new(30f, writePerm: NetworkVariableWritePermission.Server);
        public NetworkVariable<float> HuntDuration = new(90f, writePerm: NetworkVariableWritePermission.Server);

        private bool _endHuntEarly;
        private bool _matchRunning;
        private bool _abortRound; // a whole team disconnected mid-round — skip straight to Resolution

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            Phase.Value = GamePhase.LobbyIdle;
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            StartCoroutine(PlaceInitialPlayers());
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            }
        }

        /// <summary>If an entire side vanishes mid-round (Seeker rage-quits, last Hider drops), abort to
        /// Resolution instead of letting a ghost round run its full timers.</summary>
        private void OnClientDisconnected(ulong clientId)
        {
            if (!IsServer || !_matchRunning) return;
            if (Phase.Value != GamePhase.Hide && Phase.Value != GamePhase.Transition && Phase.Value != GamePhase.Hunt) return;

            bool anySeeker = false, anyHider = false;
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                if (kvp.Key == clientId || kvp.Value.PlayerObject == null) continue;
                var netPlayer = kvp.Value.PlayerObject.GetComponent<NetworkPlayer>();
                if (netPlayer.CurrentTeam.Value == Team.Seeker) anySeeker = true;
                if (netPlayer.CurrentTeam.Value == Team.Hider && netPlayer.IsAlive.Value) anyHider = true;
            }

            if (!anySeeker || !anyHider)
            {
                Debug.LogWarning("[GameStateManager] A whole team disconnected — aborting round to Resolution.");
                _abortRound = true;
            }
        }

        /// <summary>Players who were already connected when this scene loaded get moved into the lobby room
        /// once their player objects have finished migrating in.</summary>
        private IEnumerator PlaceInitialPlayers()
        {
            yield return new WaitUntil(AllPlayerObjectsReady);
            int i = 0;
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                SendToLobbySpawn(kvp.Value.PlayerObject, i++);
            }
        }

        /// <summary>Late joiners (mid-lobby or even mid-round) always land in the lobby room as spectators.</summary>
        private void OnClientConnected(ulong clientId)
        {
            StartCoroutine(PlaceLateJoiner(clientId));
        }

        private IEnumerator PlaceLateJoiner(ulong clientId)
        {
            float timeout = Time.time + 30f;
            yield return new WaitUntil(() =>
                Time.time > timeout
                || (NetworkManager.Singleton != null
                    && NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var c)
                    && c.PlayerObject != null));

            if (NetworkManager.Singleton == null) yield break;
            if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject == null) yield break;
            SendToLobbySpawn(client.PlayerObject, (int)clientId);
        }

        private void SendToLobbySpawn(NetworkObject playerObj, int index)
        {
            if (playerObj == null || lobbySpawnPoints == null || lobbySpawnPoints.Length == 0) return;
            Transform spawn = lobbySpawnPoints[index % lobbySpawnPoints.Length];
            playerObj.GetComponent<NetworkPlayer>()?.TeleportClientRpc(spawn.position, spawn.eulerAngles.y);
        }

        /// <summary>Host presses Start in the lobby panel. Requires someone to be holding the gun —
        /// can't start a match Seeker-less (Game Feel doc §4).</summary>
        public bool TryStartMatch()
        {
            if (!IsServer || _matchRunning || Phase.Value != GamePhase.LobbyIdle) return false;
            if (GunPickup.Instance == null || !GunPickup.Instance.HasHolder)
            {
                Debug.LogWarning("[GameStateManager] Can't start: nobody is holding the gun.");
                return false;
            }
            if (NetworkManager.Singleton.ConnectedClients.Count < 2)
            {
                Debug.LogWarning("[GameStateManager] Can't start: need at least 2 players (1 Seeker + 1 Hider).");
                return false;
            }

            StartCoroutine(RunMatch());
            return true;
        }

        private IEnumerator RunMatch()
        {
            _matchRunning = true;
            _abortRound = false;
            ulong seekerClientId = GunPickup.Instance.HolderClientId.Value;

            AssignRoles(seekerClientId);

            // Hiders go to the map; the Seeker stays behind in the lobby room. Spatial fairness — the
            // Seeker never sees the map (or the Hiders settling in) until the Transition beat.
            TeleportTeamToSpawns(Team.Hider, hiderSpawnPoints);

            Phase.Value = GamePhase.Hide;
            Winner.Value = RoundWinner.None;
            Debug.Log("[GameStateManager] Phase -> Hide (Hiders on map, Seeker waiting in lobby)");
            yield return CountDown(HideDuration.Value, () => _abortRound);

            if (!_abortRound)
            {
                Phase.Value = GamePhase.Transition;
                PhaseTimeRemaining.Value = transitionDuration;
                Debug.Log("[GameStateManager] Phase -> Transition (the hunt begins...)");
                yield return CountDown(transitionDuration, () => _abortRound);
            }

            if (!_abortRound)
            {
                TeleportTeamToSpawns(Team.Seeker, seekerSpawnPoints);
                Phase.Value = GamePhase.Hunt;
                _endHuntEarly = false;
                Debug.Log("[GameStateManager] Phase -> Hunt");
                yield return CountDown(HuntDuration.Value, () => _endHuntEarly || _abortRound);
            }

            Phase.Value = GamePhase.Resolution;
            Winner.Value = EvaluateWinner();
            AwardSurvivalScore();
            Debug.Log($"[GameStateManager] Phase -> Resolution, Winner={Winner.Value}");
            yield return new WaitForSeconds(1f);

            Phase.Value = GamePhase.PostRound;
            Debug.Log("[GameStateManager] Phase -> PostRound");
            yield return CountDown(MatchSettings.PostRoundDuration);

            // Back to the lobby room — everyone, roles cleared, gun on its stand, ready to go again.
            ReturnEveryoneToLobby();
            Phase.Value = GamePhase.LobbyIdle;
            _matchRunning = false;
            Debug.Log("[GameStateManager] Round complete, back to LobbyIdle");
        }

        private void ReturnEveryoneToLobby()
        {
            int i = 0;
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                var netObj = kvp.Value.PlayerObject;
                if (netObj == null) continue;
                var netPlayer = netObj.GetComponent<NetworkPlayer>();
                netPlayer.CurrentTeam.Value = Team.None;
                netPlayer.IsAlive.Value = true;
                SendToLobbySpawn(netObj, i++);
            }
            if (GunPickup.Instance != null) GunPickup.Instance.ResetToHome();
        }

        private IEnumerator CountDown(float seconds, System.Func<bool> earlyExit = null)
        {
            PhaseTimeRemaining.Value = seconds;
            while (PhaseTimeRemaining.Value > 0f)
            {
                if (earlyExit != null && earlyExit()) yield break;
                yield return null;
                PhaseTimeRemaining.Value = Mathf.Max(0f, PhaseTimeRemaining.Value - Time.deltaTime);
            }
        }

        /// <summary>Called by SeekerTagController after every tag attempt (hit or miss) — checks the two
        /// early-round-end conditions from PRD 7.4/8: all Hiders caught, or (ammo mode) all Seekers dry.</summary>
        public void CheckEarlyRoundEnd()
        {
            if (!IsServer || Phase.Value != GamePhase.Hunt) return;

            bool anyHiderAlive = false;
            bool anySeekerHasAmmo = !MatchSettings.AmmoModeEnabled; // ammo mode off -> this check never ends it early
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                if (kvp.Value.PlayerObject == null) continue;
                var netPlayer = kvp.Value.PlayerObject.GetComponent<NetworkPlayer>();
                if (netPlayer.CurrentTeam.Value == Team.Hider && netPlayer.IsAlive.Value) anyHiderAlive = true;
                if (netPlayer.CurrentTeam.Value == Team.Seeker && netPlayer.AmmoRemaining.Value > 0) anySeekerHasAmmo = true;
            }

            if (!anyHiderAlive || !anySeekerHasAmmo)
            {
                _endHuntEarly = true;
            }
        }

        private bool AllPlayerObjectsReady()
        {
            if (NetworkManager.Singleton == null) return false;
            if (NetworkManager.Singleton.ConnectedClients.Count == 0) return false;
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                if (kvp.Value.PlayerObject == null) return false;
            }
            return true;
        }

        /// <summary>Gun holder = the one Seeker; everyone else hides (Game Feel doc §3).</summary>
        private void AssignRoles(ulong seekerClientId)
        {
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                var netObj = kvp.Value.PlayerObject;
                if (netObj == null) continue;
                var netPlayer = netObj.GetComponent<NetworkPlayer>();
                bool isSeeker = kvp.Key == seekerClientId;
                netPlayer.CurrentTeam.Value = isSeeker ? Team.Seeker : Team.Hider;
                netPlayer.IsAlive.Value = true;
                netPlayer.AmmoRemaining.Value = isSeeker ? MatchSettings.AmmoCount : 0;
            }
        }

        private void TeleportTeamToSpawns(Team team, Transform[] points)
        {
            int index = 0;
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                var netObj = kvp.Value.PlayerObject;
                if (netObj == null) continue;
                var netPlayer = netObj.GetComponent<NetworkPlayer>();
                if (netPlayer.CurrentTeam.Value != team) continue;

                Transform spawn = PickSpawn(points, ref index);
                if (spawn != null)
                {
                    netPlayer.TeleportClientRpc(spawn.position, spawn.eulerAngles.y);
                }
            }
        }

        private Transform PickSpawn(Transform[] points, ref int index)
        {
            if (points == null || points.Length == 0) return null;
            Transform t = points[index % points.Length];
            index++;
            return t;
        }

        private void AwardSurvivalScore()
        {
            float huntElapsed = HuntDuration.Value - PhaseTimeRemaining.Value;
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                if (kvp.Value.PlayerObject == null) continue;
                var netPlayer = kvp.Value.PlayerObject.GetComponent<NetworkPlayer>();
                if (netPlayer.CurrentTeam.Value == Team.Hider && netPlayer.IsAlive.Value)
                {
                    netPlayer.Score.Value += Mathf.RoundToInt(huntElapsed);
                }
            }
        }

        private RoundWinner EvaluateWinner()
        {
            foreach (var kvp in NetworkManager.Singleton.ConnectedClients)
            {
                if (kvp.Value.PlayerObject == null) continue;
                var netPlayer = kvp.Value.PlayerObject.GetComponent<NetworkPlayer>();
                if (netPlayer.CurrentTeam.Value == Team.Hider && netPlayer.IsAlive.Value)
                {
                    return RoundWinner.Hiders;
                }
            }
            return RoundWinner.Seekers;
        }
    }
}
