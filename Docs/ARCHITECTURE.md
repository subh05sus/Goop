# Goop — Architecture (living doc)

Kept short. Updated as milestones land.

## Scenes
- **Bootstrap** — first-loaded, persistent. Holds `NetworkManager` + services init. Never unloaded.
- **MainMenu** — Host / Join-by-code / Browse public lobbies.
- **Lobby** — pre-round player list, host settings (mode/map/timers/cap/ammo), ready-up.
- **Arena_Greybox** — the MVP map (dummy geometry + spawn points).

## Networking / Session layer
- `Scripts/Networking/ServicesBootstrap.cs` — `UnityServices.InitializeAsync()` + anonymous auth, idempotent.
- `Scripts/Networking/GoopSessionManager.cs` — wraps `Unity.Services.Multiplayer`'s unified Session API
  (Relay + Lobby + Auth in one). Host/Join-by-code/Join-by-id/Browse/Leave. No separate Relay/Lobby manager.
- `Scripts/Networking/BootstrapLoader.cs` — on `NetworkManager` in `Bootstrap`; `DontDestroyOnLoad` + services
  init, then loads `MainMenu`. Keeps `NetworkManager`/session state alive across the whole scene chain.
- `Scripts/UI/MainMenuController.cs` / `LobbyController.cs` — drive the Session flow from UI; host's
  `Start Round` uses `NetworkManager.Singleton.SceneManager.LoadScene` (requires `EnableSceneManagement`,
  set on the `NetworkManager` asset) to synchronize the scene change to all connected clients.

## NetworkObjects
- **Player prefab** — `NetworkObject` + `NetworkTransform` (owner-authoritative movement) + `PlayerController`
  + `NetworkPlayer` (name, team/role, alive — all owner-written `NetworkVariable`s).
- **GameStateManager** — single server-authoritative `NetworkBehaviour`: phase enum, round timer, mode,
  round settings (all `NetworkVariable`s, server-write).
- **`PaintableSkin`** (per player, on `Visual_GoopGuy`) — owner-write `NetworkList<PaintStroke>` (not
  `ServerRpc`/`ClientRpc`; NGO's list replication handles batching and late-joiner full-history sync itself).
  Each client bakes a `MeshCollider` from their own `SkinnedMeshRenderer` for UV-accurate paint raycasts and
  owns a private `Texture2D` + material instance so painting one player never touches another's texture.
- **`SeekerTagController`** (Player root) — owner raycasts client-side to *pick* a target only; the actual
  hit is confirmed by the server re-deriving target/range/line-of-sight from its own authoritative state
  (never trusts the client's raycast result, per PRD 9's anti-"trust the client" requirement). Misses cost
  ammo only when `MatchSettings.AmmoModeEnabled`.

## Ownership model
- Each player owns and writes their own movement, paint strokes, and pose state.
- Server is authoritative for: round/phase transitions, role assignment, win evaluation, and **tag/catch
  confirmation** (never trust a client's local raycast for a hit).

## Phase state machine (server-driven)
`Lobby -> Prep (15-30s, Seekers frozen) -> Hunt (60-120s, can end early) -> Resolution -> PostRound ->
(next round | Lobby)`

Hunt can end before its timer via `GameStateManager.CheckEarlyRoundEnd()`: all Hiders caught, or (ammo mode)
all Seekers out of ammo.

Implemented in `Scripts/Gameplay/GameStateManager.cs`, a scene-placed `NetworkObject` in `Arena_Greybox`
(auto-spawns server-side on scene load). Server runs a single coroutine (`RunRound`) driving
`NetworkVariable<GamePhase> Phase` and `NetworkVariable<float> PhaseTimeRemaining`; clients only read them.
`MatchSettings` (`Scripts/Gameplay/MatchSettings.cs`) is host-set plain static state (prep/hunt/post-round
durations, ammo toggle) read once when the round starts — not networked itself, only its effects are.
`RoundHudUI` (`Scripts/UI`) renders phase/timer/role/winner from those NetworkVariables.
Role assignment splits connected clients into `Team.Hider`/`Team.Seeker` (alternating for MVP) and moves
them to `Arena_Greybox`'s `HiderSpawns`/`SeekerSpawns` transforms; Seekers get `NetworkPlayer.IsFrozen=true`
during Prep, checked in `PlayerController.Update` to block movement.

## Character
- `Assets/_Goop/Art/goop_guy.fbx` — active character model (mesh `Cube`, material `body.002`, rig `metarig`,
  Generic animation type). 19 deduped/renamed pose clips `Pose1`-`Pose19`. **Pose clips currently carry no
  keyframe data (0-frame export defect) — see Learnings.md.** `Assets/_Goop/Art/goop.fbx` is the earlier,
  unused first model (same defect, kept for reference).
- **Player prefab** (`Assets/_Goop/Prefabs/Player.prefab`): root has `CharacterController` + netcode
  components; child `Visual_GoopGuy` (instantiated `goop_guy.fbx`) carries `Animator` (controller:
  `Assets/_Goop/Prefabs/GoopCharacterAnimator.controller`, param `PoseIndex` int), `PoseController`, and
  `PoseSelectorUI`.
- **Pose selection flow**: owner presses Previous/Next (or Crouch to reset) → `PoseController.CyclePose`/
  `SetPose` → writes owner-authoritative `NetworkVariable<int> PoseIndex` → replicates to all clients →
  each client's own `Animator.SetInteger("PoseIndex", ...)` → instant `AnyState` transition to that pose
  state (no interpolation/frame streaming, per PRD 7.2).
