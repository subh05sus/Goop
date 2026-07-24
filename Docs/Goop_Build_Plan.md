# Goop — Build Plan (Paint-to-Hide Multiplayer Party Game)

## Context

Greenfield build of "Goop" (PRD in chat): a session-based multiplayer party game — Hiders paint their
bodies to blend into the map, Seekers tag them before a timer runs out. Built on Unity 6 + Netcode for
GameObjects (NGO) + Unity Relay/Lobby (free, no dedicated servers).

**Current project state (verified):**
- Unity **6000.5.1f1** (Unity 6), URP 17.5.0, **new Input System only** (`activeInputHandler: 1`).
- **No multiplayer stack installed** — no NGO, Transport, Relay, Lobby, Services, or Multiplayer Tools.
  Only `com.unity.multiplayer.center` (guidance UI, not a netcode stack) is present.
- One scene: `Assets/Scenes/SampleScene.unity`. No gameplay scripts (only URP tutorial `Readme` scripts).
- Character `Assets/guy/source/meccha chameleon.glb` imports as **`DefaultAsset`** — Unity cannot read its
  mesh/animations without glTFast. **Decision: user converts glb→FBX in Blender** (best Animator/humanoid
  support). Until then, build on a **placeholder capsule** so netcode work isn't blocked.

**Locked decisions:** Character → FBX via Blender · Perspective → **third-person** · Testing → **Multiplayer
Play Mode (MPPM) for iteration + standalone builds for real-Relay verification**.

**Build order (user-requested):** netcode core → Relay/Lobby host+join → game state/round logic → dummy map
→ then painting, poses, seeker tag/ammo.

---

## Docs to create & maintain (in `Assets/../Docs/` at project root, i.e. `D:/gamedev/My project/Docs/`)

Created first, updated every milestone:
- **`Docs/Goop_Build_Plan.md`** — master build plan (this document, copied into repo).
- **`Docs/TODO.md`** — living checklist, grouped by milestone; check items as completed.
- **`Docs/Learnings.md`** — running log of gotchas/decisions (NGO quirks, Relay setup steps, Unity 6 API
  changes, MPPM behavior). Append-only, dated.
- **`Docs/ARCHITECTURE.md`** — network object map, ownership model, phase state machine (kept short).

---

## Prerequisite: Unity Gaming Services linking (USER manual step, one-time)

Relay + Lobby need a Unity Cloud project + signed-in editor. **I cannot do this — requires the user's Unity
account.** Steps for the user:
1. `Edit > Project Settings > Services` → sign in → create/link a Unity Cloud project (gives an Org + Project ID).
2. Enable **Relay** and **Lobby** in the Unity Cloud dashboard (both have free tiers).
Milestones M0–M1 don't need this; it's required starting **M2**.

---

## Milestone 0 — Foundation & package install

**I do:**
- Install packages via NGO/UPM (Unity MCP `PackageManager_ExecuteAction`, or edit `Packages/manifest.json`):
  - `com.unity.netcode.gameobjects` (NGO 2.x for Unity 6)
  - `com.unity.services.multiplayer` (Unity 6 unified: Relay + Lobby + Sessions + Authentication + Core)
  - `com.unity.multiplayer.tools` (network profiler/metrics)
  - `com.unity.multiplayer.playmode` (MPPM — virtual players)
- Create folder structure:
  `Assets/_Goop/{Scripts/{Networking,Gameplay,Player,UI,Paint},Prefabs,Scenes,Materials,Art}`
- Create scenes: `Bootstrap` (NetworkManager + services init), `MainMenu`, `Lobby`, `Arena_Greybox`.
- Create the Docs files above.
- Create a placeholder **Player prefab** from a capsule (real FBX swapped in at M4).

**Verify:** Unity compiles with no errors (`GetConsoleLogs`); NGO `NetworkManager` component available; MPPM
window opens under `Window > Multiplayer Play Mode`.

## Milestone 1 — Netcode core (offline host/client, no Relay yet)

**I do (`Scripts/Networking`, `Scripts/Player`):**
- `Bootstrap` scene: `NetworkManager` GameObject + Unity Transport, `NetworkManager` config (player prefab,
  tick rate). Keep it in a persistent scene loaded first.
- `PlayerController` (owner-authoritative movement + third-person camera, Input System driven) on the Player
  prefab with `NetworkObject` + `ClientNetworkTransform` (owner-write) or server-auth `NetworkTransform`.
  Start with **owner-auth** for responsiveness; note tradeoff in Learnings.
- `NetworkPlayer` (`NetworkBehaviour`) holding per-player state: display name, team/role enum, alive flag —
  all `NetworkVariable`s owned/written per PRD ownership rules.
- Temporary in-scene "Host / Client" debug buttons (`NetworkManager.StartHost/StartClient` on localhost).

**Verify:** MPPM 2 players (or two editor instances) — Start Host on one, Start Client on the other; both
capsules spawn and **movement replicates both ways**. Check network profiler for sane traffic.

## Milestone 2 — Relay + Lobby (real online host/join)

**Requires UGS linking (prereq above).**

**I do (`Scripts/Networking`, `Scripts/UI`):**
- `ServicesBootstrap`: `UnityServices.InitializeAsync()` + anonymous `AuthenticationService` sign-in.
- `RelayManager`: host creates allocation → `SetRelayServerData` on transport → gets **join code**; client
  joins by code. (Prefer the Unity 6 **Multiplayer Services `Session`** API which wraps Relay+Lobby together —
  simpler than wiring both by hand; note choice in Learnings.)
- `LobbyManager`: create lobby (public/private), heartbeat (host), poll for members, list public lobbies,
  join-by-code, leave/cleanup. Store the Relay join code in lobby data.
- **UI:** `MainMenu` (Host / Join-by-Code / Browse Public), `Lobby` screen (player list, ready toggle, host
  settings: mode/map/timers/player-cap/ammo, Start button host-only).

**Verify:** Two **standalone builds** (or build + editor) on the same/different machines — host creates room,
client joins by code AND from public list; both connect **over Relay** (confirm no localhost). Player list
syncs; host Start transitions everyone to the arena.

## Milestone 3 — Game state & round flow (Normal mode MVP)

**I do (`Scripts/Gameplay`):**
- `GameStateManager` (`NetworkBehaviour`, server-authoritative singleton): `NetworkVariable`s for phase enum
  (`Lobby, Prep, Hunt, Resolution, PostRound`), round timer, selected mode, host round settings.
- Phase state machine on the server: Prep (15–30s, Seekers frozen/camera-locked) → Hunt (60–120s) →
  Resolution (evaluate win) → PostRound (scoreboard) → next round (role swap) or back to Lobby.
- `RoleAssignment` (server): split players into Hiders/Seekers for Normal mode; spawn at role spawn points.
- Win eval: Normal = ≥1 Hider alive at Hunt end → Hiders win; all caught → Seekers win.
- **UI:** phase banner, countdown timer, role indicator, end-of-round scoreboard.

**Verify:** MPPM 3–4 players run a full round in the greybox arena: roles assigned, Seekers frozen in Prep,
timer counts down synced on all clients, correct win/lose result, scoreboard shows, loop returns to lobby.

## Milestone 4 — Dummy map + character + poses

**I do:**
- `Arena_Greybox` scene: floor, walls, clutter primitives, Hider/Seeker spawn points, lighting. (Human
  players → no NavMesh needed.)
- Swap placeholder capsule → **real FBX character** (once user provides it): humanoid Avatar, Animator
  Controller, third-person rig. If FBX not ready, stay on capsule and proceed.
- **Pose system** (`Scripts/Gameplay`): `NetworkVariable<PoseState>` (crouch, lie flat, curl, lean, prop) →
  drives local Animator (per PRD, no frame streaming). Radial selection menu. Poses adjust collider
  size/silhouette.

**Verify:** MPPM — pose changes replicate to all clients via the enum→Animator path; collider/silhouette
changes; character animates correctly.

## Milestone 5 — Painting system (stroke-sync)

**I do (`Scripts/Paint`):**
- Per-player paintable `RenderTexture` on a material instance; local brush raycasts hit point → UV → paint
  (immediate local feedback).
- `Stroke` struct (UV pos, color, brush size, timestamp); batch ~100ms; `ServerRpc`→`ClientRpc`; each client
  reconstructs texture from stroke list. Cap strokes/player/round. Server sanity-checks (reject oversized/
  offscreen strokes).
- **Palette UI:** color wheel + HSV sliders + eyedropper (sample world geometry color) + ≥12 quick-color
  presets. Mid-Hunt repaint allowed with a soft "wet paint" penalty.
- (Anti-cheat, later pass) interest management so full paint texture isn't sent until Seeker in range.

**Verify:** MPPM — paint on one client shows identically on others within ~100ms; profiler confirms low
bandwidth (strokes, not textures); stroke cap enforced.

## Milestone 6 — Seeker tag + ammo + polish

**I do (`Scripts/Gameplay`):**
- Server-authoritative tag: Seeker click → `ServerRpc` → **server-side** raycast/range check confirms catch
  (never trust client raycast). Caught Hider eliminated (Normal mode).
- Ammo mode (host toggle, default 5, 1–99): miss costs 1, hit/flee doesn't; all Seekers dry → Hiders win.
- Score for survival time / close calls; wire into scoreboard.

**Verify:** MPPM/builds — tags only confirm server-side (test a client faking a hit → rejected); ammo
decrements correctly; auto-win when ammo exhausted; full match loop end-to-end.

---

## Testing strategy (every milestone)

- **Fast loop:** MPPM virtual players (`Window > Multiplayer Play Mode`), 2–4 players in one editor.
- **Real-network gate:** before closing M2/M6, run **standalone builds** over actual Relay.
- After each code change: check `GetConsoleLogs` for compile/runtime errors; use Multiplayer Tools **Network
  Profiler** to watch bandwidth (esp. paint at M5).
- I drive Unity via the unity-mcp tools (create scripts, manage scene/GameObjects, enter play mode, read
  console) so I can build and self-verify inside the editor.

## Key risks / notes

- **glb blocker:** character unusable until FBX conversion — capsule keeps all netcode/gameplay unblocked.
- **UGS linking is a user step** (M2); can't be automated from here.
- **Unity 6 Session API:** prefer `com.unity.services.multiplayer` Session wrapper over hand-wiring Relay+
  Lobby separately — fewer moving parts; confirm at M2 and record in Learnings.
- Paint bandwidth needs profiling before raising player cap past MVP (PRD §12).

## Out of scope for this plan (post-MVP, per PRD)

Infection & Double modes, dedicated-server path, second map, voice chat, monetization, ranked.
