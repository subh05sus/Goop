# Goop — Learnings

Append-only log of gotchas, decisions, and Unity/NGO quirks discovered during the build.

## 2026-07-23 — M0 setup

- **Original character `.glb` unreadable**: `Assets/guy/source/meccha chameleon.glb` imported as
  `DefaultAsset` — Unity has no glTF importer without the glTFast package. Resolved by converting to FBX in
  Blender externally (`goop.fbx`), which imports as a proper Model with mesh/materials/animations.
- **`goop.fbx` import result**: rig root named `metarig` (raw Blender bone name, not yet Mecanim-Humanoid
  configured), one material `body.001`, meshes `Cube`/`Icosphere`, and **19 baked AnimationClips**
  (`Pose1`...`Pose19`). PRD needs 6-8 poses — plenty of raw material once we pick + trim. At M4, must set the
  model's Animation Type to Humanoid (or keep Generic + drive via enum->clip mapping, per PRD 7.2) and verify
  avatar configuration.
- **Unity MCP folder/asset ops need `AssetDatabase.Refresh()`**: creating folders on disk via shell `mkdir`
  first, then calling the Unity asset tool's `CreateFolder`/`Move`, fails with GUID-not-found errors because
  the Editor's AssetDatabase hasn't indexed the new paths yet. Fix: either let Unity's own `CreateFolder`
  action make the folder (don't pre-create via shell), or run `AssetDatabase.Refresh()` via `RunCommand`
  before asset ops. Also: after adding UPM packages, Unity briefly disconnects from the MCP relay while
  recompiling — wait and retry `GetState` rather than treating it as a hard failure.
- **`com.unity.multiplayer.tools` NetVis toolbar overlay throws editor UI errors on install** (missing
  UxmlElementAttribute / NullReferenceException in `PanelOverlayView`) — cosmetic bug in the overlay panel
  registration, not a project compile error (`isCompilationSuccessful: true`, no project code involved).
  Harmless; ignore unless the Network Visualization overlay itself is needed.
- **Package versions**: installed via Unity Package Manager registry resolve (not manual manifest.json
  pinning) — `com.unity.netcode.gameobjects`, `com.unity.services.multiplayer` (unified Relay+Lobby+Auth+Core
  for Unity 6), `com.unity.multiplayer.tools`, `com.unity.multiplayer.playmode` (MPPM). All resolved cleanly,
  no errors.

## 2026-07-23 — M1 netcode core

- **NGO 2.x `NetworkTransform` API changed**: no more `m_ServerAuthoritative` bool field. It's now an
  `AuthorityMode` enum (`Server` / `Owner`, index 0/1). Set via
  `SerializedObject.FindProperty("AuthorityMode").enumValueIndex = 1` for owner-authoritative. Discovered by
  dumping `SerializedObject.GetIterator()` field names at runtime rather than guessing from older NGO docs.
- **`PrefabUtility.LoadPrefabContents` + AddComponent order matters**: destroying an existing component
  (`CapsuleCollider`) and then immediately calling `AddComponent<CharacterController>()` on the same call
  chain intermittently threw `MissingComponentException`/`NullReferenceException` through the MCP RunCommand
  bridge on the first two attempts, with **no changes actually persisted** (`SaveAsPrefabAsset` never ran, so
  failures were safe/atomic). Fix was mechanical — same code, retried — works fine on a clean attempt. Always
  re-check `GetComponents` on the asset after a RunCommand failure before assuming corruption.
- **Player prefab (`Assets/_Goop/Prefabs/Player.prefab`)** now has: `CharacterController`, `NetworkObject`,
  `NetworkTransform` (owner-authoritative), `NetworkPlayer`, `PlayerController` (wired to the existing
  `Assets/InputSystem_Actions.inputactions` asset's `Player/Move` and `Player/Look` actions — reused instead
  of creating a new action asset, since it already has Move/Look/Attack/Interact/Crouch/Jump which map well
  onto movement, seeker-tag, and pose actions later).
- **Verified in-editor**: `Bootstrap` scene (`NetworkManager` + `UnityTransport` + `NetworkDebugUI` +
  `Main Camera` + `Ground` plane) — entering Play and calling `NetworkManager.Singleton.StartHost()` via
  RunCommand spawned `Player(Clone)` with zero console errors, then shut down cleanly. **Not yet verified:
  two real connected clients (MPPM)** — that requires driving the Editor's Multiplayer Play Mode windows,
  which isn't scriptable through the MCP tools; needs the user to open `Window > Multiplayer Play Mode`,
  add 1-2 virtual players, and confirm movement replicates both ways.

## 2026-07-23 — Character swap to goop_guy.fbx + pose system (pulled forward from M4)

- **CRITICAL: pose animation data is empty in both `goop.fbx` and `goop_guy.fbx`.** Confirmed at the FBX
  *import* level (not a Unity-side misread): Unity's own import log reports every one of the 19 pose takes
  as `"has length of 0 frames (start=0, end=0)"` for both files. Cross-checked with
  `AnimationUtility.GetCurveBindings(clip)` → 0 bindings on every real clip. The Editor's `__preview__*`
  auto-generated clips *do* show a valid single-frame snapshot (which is how the FBX preview thumbnail can
  still render a "posed" character), but that's just Unity baking the current pose at frame 0 for preview —
  it is **not** proof of a working animation range.
  **Root cause is the Blender export, not Unity/code**: each Pose action's frame range exported as 0-0. Fix
  in Blender before next export: for every Pose action, open the Action/Dope Sheet editor and confirm it has
  a real frame range (e.g. holds its pose across frames 1-2, not a single frame at 0 with no range set), and
  make sure "Bake Animation" is checked in the FBX export panel so the exporter writes real keyframe curves
  instead of an empty 0-length take. Once re-exported, no code changes are needed — the pipeline below reads
  clips by name and will just start working.
- **`goop_guy.fbx` also had a duplicate-take defect**: 38 raw takes instead of 19 — every pose appeared twice,
  once as `metarig|Pose1` and once as `metarig|metarig|Pose1` (nested-armature naming artifact from the
  Blender rig setup, likely an Armature modifier parented under an object with the same name). Deduped in the
  importer to the 19 `metarig|PoseN` takes and renamed via `ModelImporter.clipAnimations` to friendly
  `Pose1`..`Pose19` (so runtime code never needs the FBX take-path prefix).
- **`ModelImporter.avatarSetupType` doesn't exist in this Unity API surface** (compile error) — not needed
  anyway since we're using `ModelImporterAnimationType.Generic` (no humanoid retargeting required for a
  single fixed character).
- **Pose pipeline built (works mechanically, blocked only on the empty-clip data above)**:
  - `Assets/_Goop/Prefabs/GoopCharacterAnimator.controller` — built via `AnimatorController` scripting API:
    one `Idle` default state + one state per `Pose1`-`Pose19`, each wired from `AnyState` with an instant
    (`hasExitTime=false`, `duration=0`) transition gated on an `int` parameter `PoseIndex` — a direct snap,
    matching PRD 7.2 ("don't stream animation frames").
  - `Scripts/Gameplay/PoseController.cs` — `NetworkBehaviour` with `NetworkVariable<int> PoseIndex`
    (owner-write, matches PRD 9 ownership model), pushes to `Animator.SetInteger("PoseIndex", ...)` on
    change. `CyclePose(delta)` / `SetPose(index)` are owner-gated.
  - `Scripts/UI/PoseSelectorUI.cs` — reuses the project's existing `Assets/InputSystem_Actions.inputactions`
    (`Previous`/`Next` cycle poses, `Crouch` resets to idle) instead of a new radial-menu asset; a proper
    radial UI is deferred as a polish pass (PRD says radial menu, this is the MVP substitute). Only
    initializes on the owning client (hooked from `PoseController.OnNetworkSpawn` when `IsOwner`).
  - **Player prefab visual swapped**: placeholder capsule mesh/renderer removed from the root; `goop_guy.fbx`
    instantiated as a child `Visual_GoopGuy` (keeps `CharacterController` + netcode components on the root,
    matches the pattern of physics-object-separate-from-visual-mesh). `Animator` + `PoseController` +
    `PoseSelectorUI` live on the visual child, controller assigned, `Previous`/`Next` input wired.
  - **Verified in Play mode**: `StartHost()` → found the spawned `PoseController` → `CyclePose(1)` moved
    `PoseIndex.Value` from `0` to `1` with zero console errors. Confirms the networked plumbing is correct;
    visually nothing will move until the source clips carry real keyframes (see root cause above).

## 2026-07-23/24 — M2 Relay + Lobby

- **Unity Cloud project was already linked** (org `subhadipsus`, verified via `CloudProjectSettings.projectId`/
  `organizationId` being non-empty) — the plan's "user must link UGS" prerequisite was already satisfied,
  saved a round trip.
- **Used the unified `Unity.Services.Multiplayer` Session API** (`MultiplayerService.Instance`, `ISession`,
  `SessionOptions.WithRelayNetwork()`, `CreateSessionAsync`/`JoinSessionByCodeAsync`/`JoinSessionByIdAsync`/
  `QuerySessionsAsync`) instead of wiring Relay and Lobby separately, per the plan's stated preference. Wrote
  the whole surface (`GoopSessionManager.cs`) from documented API knowledge with zero reflection access
  (the MCP `RunCommand` sandbox blocks `System.Reflection`) — it compiled clean on the first try, so the
  plan's original separate "LobbyManager" is unnecessary; the Session already models the room, its players,
  and its properties. Simplified `ARCHITECTURE.md`/`TODO.md` accordingly.
- **CRITICAL MCP workflow gotcha: every `RunCommand` call that compiles new code triggers a Unity domain
  reload, even mid-Play-Mode.** This resets ALL static C# state (`ServicesBootstrap.IsReady`,
  `GoopSessionManager.CurrentSession`, `UnityServices.State` itself reverts to `Uninitialized`, etc.) but
  *preserves* already-running scene objects and their serialized field values (Unity's normal
  "recompile during play" behavior). Practical consequences:
  - An `async void` `RunCommand` script that does `await SomeUnityServicesCall(); result.Log(...)` will
    often report success with the log call **never reached** — `Execute()` returns synchronously at the
    first `await`, and the tool captures/returns before the continuation runs. Don't trust a "clean success
    with fewer log lines than expected" from an async `RunCommand` — it likely means the async chain got cut
    off, not that later steps didn't need to run.
  - To actually verify async gameplay flows (like a full Host/Join), invoke them via a button/method already
    running in the live scene (e.g. `hostButton.onClick.Invoke()`) so the async continuation runs on Unity's
    normal frame loop in whatever domain exists *after* that RunCommand call returns, then verify with
    **non-compiling** tools only (`GetConsoleLogs`, `ManageScene GetActive`, or a `RunCommand` that only
    *reads* already-rendered UI `Text.text` values — reading persisted component state is safe even though
    the read call itself triggers another reload, since `UnityEngine.Object` field values survive reload,
    only bare `static` fields don't).
- **Verified for real, in Play Mode, against live Unity Cloud services**: clicked `Host Game` in the actual
  `MainMenuController` UI → `GoopSessionManager.HostAsync` → real `CreateSessionAsync(...WithRelayNetwork())`
  → scene auto-transitioned `MainMenu` → `Lobby` (matches `LobbyController`'s host-only flow) → `Lobby` UI
  showed a genuine Relay join code (`F9NJFQ`), `"You are the Host"`, and a real player list with 1 entry
  (an actual Unity Cloud player ID). Zero console errors. **This confirms Relay + Lobby are correctly enabled
  in the Unity Cloud dashboard for this project** — the M2 prerequisite risk in the plan is fully resolved.
- **Not yet verified: Join-by-code from a second real client.** Same limitation as M1's MPPM check — driving
  two simultaneous Editor/Player instances isn't scriptable through MCP. Needs the user to run a second
  instance (MPPM virtual player or a standalone build) and join the code a hosted instance prints.
- **`EnableSceneManagement`** was enabled on `NetworkManager.NetworkConfig` (found via
  `SerializedObject.FindProperty("NetworkConfig.EnableSceneManagement")` — not visible via plain field
  iteration, same hidden-inspector quirk as `AuthorityMode` earlier) so the host's `Start Round` button can
  use `NetworkManager.Singleton.SceneManager.LoadScene(...)` to synchronize the Arena scene load across all
  connected clients — this is what M3's round-start flow will build on.
- **`BootstrapLoader.cs`** added to the `NetworkManager` GameObject in `Bootstrap`: calls
  `DontDestroyOnLoad(gameObject)` before awaiting `ServicesBootstrap.InitializeAsync()`, then loads
  `MainMenu` (single mode). This is what makes `NetworkManager` (and the whole services/session stack)
  survive the `MainMenu → Lobby → Arena_Greybox` scene chain without needing additive scene loading.

## 2026-07-24 — M3 game state & round flow (Normal mode)

- **Real bug found and fixed: player-object migration race on networked scene load.** `GameStateManager`
  (a scene-placed `NetworkObject` in `Arena_Greybox`) ran `AssignRoles()`/`SpawnPlayers()` synchronously
  inside `OnNetworkSpawn()`, which NGO calls from `NetworkSceneManager.OnSessionOwnerLoadedScene` — i.e.
  mid-way through the host's own scene-load completion callback. At that exact instant, a connected client's
  `PlayerObject` reference could still be mid-migration from the old scene (`Lobby`) into the new one,
  so `NetworkManager.Singleton.ConnectedClients[id].PlayerObject.GetComponent<...>()` threw
  `MissingReferenceException` on a destroyed `NetworkObject`. **Fix**: `yield return new
  WaitUntil(AllPlayerObjectsReady)` at the top of the round coroutine before touching any player object, plus
  defensive null-checks in every method that iterates `ConnectedClients` (`AssignRoles`, `SpawnPlayers`,
  `SetSeekersFrozen`, `EvaluateWinner`) — cheap insurance against the same class of timing race elsewhere.
  Confirmed fixed: re-ran the Host→Start Round flow, scene transitioned `Lobby → Arena_Greybox` cleanly with
  zero console errors (previously crashed every time).
- **MCP `RunCommand` polling is fundamentally unreliable for verifying anything that spans more than one
  call** (extends the M2 finding). Every `RunCommand` invocation compiles new code → domain reload →
  running `Coroutine`s are silently dropped (they're not serializable, so `StartCoroutine(RunRound())`
  simply stops advancing) and `NetworkVariable` fields declared with initializers (`= new(GamePhase.Lobby,
  ...)`) get **reconstructed to their default value** since Unity re-runs field initializers on reload. A
  static singleton reference (`GameStateManager.Instance`) is also wiped the same way `GoopSessionManager.
  CurrentSession` was in M2 — use `GameObject.Find(...).GetComponent<T>()` instead of a static `Instance`
  when reading live state from a `RunCommand` script. Net effect: I could confirm the crash was fixed and the
  scene-transition/role-assignment path is sound, but could **not** reliably observe the full
  Prep→Hunt→Resolution→PostRound→Lobby timer progression through automated polling — each poll call risked
  restarting or freezing the very coroutine being observed. Added `Debug.Log` phase-transition markers to
  `GameStateManager` for whenever a human (or a future non-RunCommand-based test harness) watches the
  Console during a real play session.
- **USER ACTION NEEDED**: do one manual click-through of a full round (Host → Start Round in Lobby → watch
  the HUD phase/timer banner cycle through Prep → Hunt → Resolution → PostRound → back to Lobby) to confirm
  the timer loop and win evaluation feel right. With default settings that's ~20s Prep + 90s Hunt + ~9s
  Resolution/PostRound (~2 minutes) — safe to temporarily lower `MatchSettings.PrepDuration`/`HuntDuration`
  in code for a quick manual check, as was done (and reverted) during this session's testing.
- Win evaluation is intentionally naive for M3: without the tag system (M6) every Hider is still `IsAlive`
  at Hunt's end, so Hiders always win right now — expected, not a bug; M6 wires the real tag/catch flow that
  can flip `NetworkPlayer.IsAlive`.

## 2026-07-24 — M5 painting system (stroke-sync)

- **`NetworkList<T>` requires `T : IEquatable<T>`, not just `INetworkSerializable`.** `PaintStroke` needed an
  explicit `IEquatable<PaintStroke>` implementation (`CS0315` otherwise) — a real compile error caught by the
  normal edit/wait/check loop, not a tool artifact.
- **Design**: `PaintStroke` (UV, brush size, RGB — PRD 7.1's "compact stroke list") lives in a per-player
  owner-write `NetworkList<PaintStroke>` on `PaintableSkin`. Owner paints locally first (instant feedback,
  `Texture2D.SetPixel` in a soft-circle brush around the UV hit point) then `Strokes.Add(stroke)` — NGO's
  normal list replication (not hand-rolled RPC batching) pushes it to everyone else, and **late joiners get
  the whole history for free** via NGO's full-list sync on spawn, so `OnNetworkSpawn` just replays every
  existing entry once. Non-owners apply new strokes via `Strokes.OnListChanged`, guarded to skip the owner
  (who already painted locally) to avoid double-drawing. `MaxStrokesPerRound = 400` caps local paint input;
  no server-side re-validation of list contents yet (PRD flags this as a v1 anti-cheat *risk to monitor*, not
  a blocking MVP requirement — noted, not implemented).
- **UV picking**: each player's `SkinnedMeshRenderer` gets a runtime-baked `MeshCollider`
  (`renderer.BakeMesh()` + `MeshCollider.sharedMesh`) so a normal `Physics`/`Collider.Raycast` against their
  *own* character returns `RaycastHit.textureCoord` directly — no manual UV math needed. Paint input is
  gated on the existing `Interact` (Hold) action from the project's Input Actions asset, reusing it rather
  than adding new bindings.
- **Palette**: 12 preset color swatches (satisfies PRD's "at least 12 quick colors") + an eyedropper that
  raycasts world geometry under the screen center and reads the hit renderer's `_Color`. A full HSV/color-
  wheel picker and the "wet paint" mid-hunt repaint penalty are deferred polish, not MVP-blocking.
- **Confirmed at the code level**: `PaintableSkin.OnNetworkSpawn()` (texture allocation, material
  instancing, `NetworkList` init, `MeshCollider` bake) ran with zero console errors in a real Play session
  after Host → Start Round reached `Arena_Greybox` with a live player spawned — this is the same session
  referenced in the M3 entry above.
- **New structural finding, sharper than the M2/M3 "domain reload wipes statics" note**: a `RunCommand` call
  that recompiles **while a real NGO/Relay connection is active** doesn't just wipe C# statics/coroutines —
  it appears to kill the underlying Unity Transport (UTP) socket state itself, since that's native/unmanaged
  state that can't survive a domain reload. Symptom observed: clicking `Start Round` via a *second*
  `RunCommand` call (after an earlier successful `RunCommand` click on `Host`) led to `NetworkManager.
  Singleton.ConnectedClients` reading as **empty** by the time `GameStateManager`'s round coroutine ran —
  the scene still transitioned to `Arena_Greybox` (that part is local, not dependent on the live socket), but
  no player object existed, and the `AllPlayerObjectsReady()` empty-list case was returning `true`
  (vacuous truth) and letting the round proceed with nobody there. **Both problems fixed**: added an
  explicit `ConnectedClients.Count == 0 → false` guard, and confirmed (by not issuing any further
  `RunCommand` calls after the `Start Round` click, only non-compiling `GetConsoleLogs`/`GetHierarchy`/
  `GetState`) that the round now correctly sits waiting rather than silently completing.
- **Practical consequence for future testing**: at most **one** `RunCommand` call should touch an
  established multiplayer session per Play session — after that, verification must go through non-compiling
  tools only (`GetConsoleLogs`, `ManageScene GetHierarchy`/`GetActive`, `ManageEditor GetState`), or through
  a real user click-through. This is stricter than the earlier M2/M3 guidance and supersedes it.
- **USER ACTION NEEDED**: manually click through Host → Start Round → once spawned in the arena, hold
  `Interact` while looking at your own character to paint, try the palette swatches and eyedropper. This is
  the only way left to verify the actual paint-on-mesh visual result and the cross-client stroke sync (needs
  a second real client, same limitation as every other multi-client check so far).

## 2026-07-24 — M6 Seeker tag + ammo + polish

- **Server-authoritative tag** (`SeekerTagController.cs`, PRD 7.4/9): owner raycasts from their own camera to
  pick a candidate target (pure UX responsiveness — client never decides a hit), sends only the target's
  `NetworkObjectId` via `ServerRpc`. Server re-derives everything itself: looks up the target's *actual*
  spawned `NetworkObject` (not trusting any client-supplied position), checks team/alive state, checks
  distance against `tagRange`, and does its own line-of-sight raycast between attacker and target — a wall
  in between blocks the tag even if the client's own raycast (perhaps through a wallhack) said otherwise.
  A miss (no valid target, out of range, or blocked) only costs ammo when the host's ammo mode is on.
- **Ammo mode**: `NetworkPlayer.AmmoRemaining` (server-write), seeded to `MatchSettings.AmmoCount` for
  Seekers in `GameStateManager.AssignRoles`. `MatchSettings.AmmoModeEnabled` is now host-toggleable from the
  Lobby UI (`LobbyController`'s new `ammoModeToggle`, hooked straight to the static field — simplest wiring
  since it only needs to be read once when the round starts).
  Hand-built a `UnityEngine.UI.Toggle` (background `Image` + checkmark `Image` + label `Text`) via script
  since there's no toggle prefab in this UGUI-only (no TMP) project — same `UnityEngine.UI.Image` vs
  ambiguous-`Image`-namespace compile gotcha as the earlier Host/Join buttons; fully-qualifying the type
  fixes it every time this comes up.
- **Early round-end conditions** (PRD 7.4/8: "if all Seekers exhaust ammo, Hiders win"; also the standing
  "all Hiders caught -> round over" rule): `GameStateManager.CheckEarlyRoundEnd()` is called by
  `SeekerTagController` after *every* tag attempt (hit or miss) and sets a private `_endHuntEarly` flag that
  the Hunt-phase `CountDown` coroutine polls once per frame via an `earlyExit` predicate parameter (added to
  `CountDown` rather than writing a second bespoke loop).
- **Scoring** (PRD 6/11, kept intentionally minimal for MVP): `NetworkPlayer.Score` — Seekers get `+1` per
  confirmed tag (awarded inline in the `ServerRpc`), Hiders get their elapsed Hunt survival time in seconds
  added once at Resolution (`GameStateManager.AwardSurvivalScore`). `RoundHudUI` shows the local player's
  running score/ammo and a simple full scoreboard once the round reaches Resolution/PostRound.
- **Not yet possible to verify live**: tag/ammo/scoring fundamentally need *two* real connected players (a
  Seeker to tag a Hider) — beyond what a single Editor instance or my MCP tools can exercise, on top of the
  already-established finding that a second `RunCommand` mid-session risks killing the live transport anyway.
  Compiles clean and the prefab wiring was verified (`SeekerTagController` added to `Player.prefab` with its
  `inputActions` reference set); full functional verification is a **USER ACTION** — needs 2 clients
  (MPPM or two builds), one Hider and one Seeker, to actually tag and confirm ammo depletion / early round end.

## 2026-07-24 — Fixed two real "cannot Host" bugs the user hit

- **Bug 1: pressing Play from any scene other than `Bootstrap` skips `BootstrapLoader` entirely**, so
  `NetworkManager` is never created — any Relay/Session call then fails because there's no `NetworkManager.
  Singleton` for the Session's network integration to attach to. This is exactly what the user hit (they had
  `MainMenu` open in the Editor and pressed Play directly). **Fix**: set `EditorSceneManager.
  playModeStartScene = Bootstrap.unity` via script, so pressing Play always starts from `Bootstrap` regardless
  of which scene tab is open in the Editor. This is a **local Editor preference** (stored per-machine, not a
  committed project file) — visible/editable in Unity's own UI at `File > Build Profiles` is not it either;
  it's set by selecting `Bootstrap.unity` in the Project window and using the small "Play Button" icon that
  appears at the top of its Inspector (Unity 6) to pin it as the Play Mode start scene. If it ever reverts,
  the simplest fallback is just to have `Bootstrap.unity` open/active before pressing Play.
- **Bug 2: `ServicesBootstrap.IsReady` was a cached `bool` that could go stale relative to the real Unity
  Services SDK state.** Caught a live repro where `UnityServices.State` read `Uninitialized` while `IsReady`
  still read `true`, which short-circuited `InitializeAsync()` into skipping re-initialization and then
  crashing on `AuthenticationService.Instance` with "Singleton is not initialized." However this specific
  mismatch was only ever observed immediately after one of my own MCP `RunCommand` calls (which force a
  domain reload) — **not confirmed to be reachable in a normal user session with no tooling interference**.
  Fixed anyway, since it's a correctness improvement regardless of root cause: `IsReady` now always
  *re-derives* from the live SDK (`UnityServices.State == Initialized && AuthenticationService.Instance.
  IsSignedIn`) instead of trusting a cached flag, so it self-heals from any state mismatch rather than
  papering over one specific reproduction.
- Re-verified after both fixes: fresh Play session (starting correctly from `Bootstrap`), one `Host Game`
  click, clean transition to `Lobby`, zero errors.

## 2026-07-24 — "Join failed: singleton not set" (MPPM 2nd client) — wrong fix, then right fix

- **First hypothesis was WRONG and had to be reverted**: guessed `JoinSessionOptions` needed
  `.WithRelayNetwork()` like `SessionOptions` does. This does not compile —
  `SessionOptionsExtensions.WithRelayNetwork<T>` is generic-constrained to `T : SessionOptions` specifically,
  and `JoinSessionOptions` is an unrelated type (no implicit conversion). The user's own Unity Editor caught
  this immediately (`CS0311` on both call sites) even though my own `GetConsoleLogs` check right after the
  edit came back clean — **don't trust a single post-edit console check as proof of a clean compile**; ask
  for/expect the user's own compiler output too when they're actively running the project, since their
  Editor instance recompiles independently of whichever one MCP is attached to. Reverted both call sites back
  to plain `new JoinSessionOptions()`.
- **Real fix**: this is the same root-cause *class* as the earlier Host bug (M2 "singleton not set"), just
  hitting a different entry point. MPPM virtual players can end up loaded directly into `MainMenu` without
  ever running `Bootstrap`'s `BootstrapLoader` first (unconfirmed exactly why — MPPM's scene "coherence"
  behavior mirroring the main Editor's current scene is the leading theory, but not confirmed since VP
  processes aren't independently inspectable through the MCP connection, which only talks to the primary
  Editor instance). Rather than fight MPPM's exact scene-sync semantics, made the game **self-healing**:
  `MainMenuController.Start()` now checks `NetworkManager.Singleton == null` and, if so, reloads `Bootstrap`
  (which then correctly re-runs `BootstrapLoader` and lands back on `MainMenu` with a real `NetworkManager`
  this time). This fixes the bug for MPPM, for a misclicked Play-from-any-scene, and for any future entry
  point — no reliance on the local-only `playModeStartScene` Editor preference from the earlier Host fix.
- Not yet re-verified live (needs the user to retry Join via MPPM) — compiles clean.

## 2026-07-24 — NGO scene-migration sync error + Leave crash (real bugs, both fixed)

- **`[Object Scene Migration] Trying to synchronize NetworkObjectId (2) but it was not spawned or no longer
  exists!!`**: real timing bug, not stale MPPM state. If the host clicks **Start Round** immediately after a
  client joins, the Lobby→Arena scene-migration event can race that client's still-in-flight initial
  connection sync — NGO tries to tell the client "this NetworkObjectId is moving to the new scene" before the
  client has even spawned that object locally yet. **Fix**: `LobbyController.OnStartClicked` now checks every
  `ConnectedClients[...].PlayerObject != null` before allowing the scene load (same pattern already used
  server-side in `GameStateManager.AllPlayerObjectsReady`) — if a client hasn't finished connecting, Start is
  a no-op with a warning log instead of triggering the race.
- **`SessionException: lobby not found` crashing the Leave button**: downstream symptom of the above (or any
  connection drop) — the remote Lobby/session can legitimately already be gone by the time the local player
  clicks Leave, and `ISession.LeaveAsync()` throwing in that case broke the whole `OnLeaveClicked` flow
  (never reached the `NetworkManager.Shutdown()` / return-to-`MainMenu` lines). **Fix**: `GoopSessionManager.
  LeaveAsync()` now catches and logs instead of propagating — a session that's already gone counts as
  successfully left from the local player's perspective. `CurrentSession` is cleared in a `finally` block
  regardless of outcome.
- Both compile clean; not yet re-verified live (needs the user to retry the full Host→Join→Start Round→Leave
  flow via MPPM).

## 2026-07-24 — THE actual root cause of every Host/Join/sync issue today

- After the Start-guard fix above, the user still got stuck forever on "Not all clients finished connecting"
  even with the Lobby session showing 2 players. Traced it to the `com.unity.services.multiplayer` package's
  own source (`NetworkManagerSession.cs`), which has this comment verbatim: *"Currently clients are not
  considered fully connected to the NetworkManager until after they have finished synchronizing.
  Synchronizing includes loading any loaded scenes."* — i.e. a joining client only counts as connected once
  NGO's own scene-sync machinery has caught it up to the host's current scene.
- **The actual bug**: `MainMenuController.OnHostClicked`/`OnJoinClicked`/browse-join all called plain
  `UnityEngine.SceneManagement.SceneManager.LoadScene("Lobby", ...)` directly. But `NetworkConfig.
  EnableSceneManagement = true` (set back in M2 so the host's `Start Round` button could sync the Arena load
  to clients) means **every** scene transition after the host starts must go through `NetworkManager.
  Singleton.SceneManager.LoadScene`, not a plain local scene load — otherwise NGO's own scene tracking never
  learns the host moved to `Lobby`, so a joining client can never "finish synchronizing" into a scene NGO
  doesn't know exists. This is almost certainly the true root cause of the original scene-migration
  `NetworkObjectId` error too, not a timing race — the earlier "wait for all PlayerObjects before Start"
  guard (`LobbyController.OnStartClicked`) was a reasonable defensive addition but was treating a symptom.
- **Fix**: `OnHostClicked` now calls `NetworkManager.Singleton.SceneManager.LoadScene("Lobby", ...)` instead
  of the plain `SceneManager.LoadScene`. `OnJoinClicked` and the browse-join handler no longer manually load
  any scene at all after joining — the client just sets a "Joined! Waiting for host..." status and lets NGO's
  own scene-sync pull it into `Lobby` automatically once connected (which is the whole point of
  `EnableSceneManagement`). The `Bootstrap → MainMenu` transition stays a plain scene load since no
  `NetworkManager` connection exists yet at that point — only post-connection transitions need the networked
  path.
- Compiles clean. **Not yet re-verified live** — and since prior test sessions (host + VPs) were running with
  the broken version, the user needs a **fully fresh restart** (stop Play in the main Editor, close/reopen
  MPPM virtual players so they pick up the new compiled code, no leftover sessions) before retrying
  Host → wait for player to show in list → Start Round.

## 2026-07-24 — Scene-migration error recurred at Start Round — found the actual gap

- After the MainMenu→Lobby networked-scene-load fix, the user hit the *same* `NetworkObjectId was not
  spawned` error again, this time for **both** player IDs (1 and 2) together with `[Deferred OnSpawn]`
  timeout warnings — at the `Lobby → Arena_Greybox` transition specifically (`LobbyController.OnStartClicked`
  already correctly used `NetworkManager.Singleton.SceneManager.LoadScene`, so that transition was never the
  bug). Root cause was the **Start guard itself**: it checked
  `NetworkManager.Singleton.ConnectedClients[id].PlayerObject != null`, which only proves the *server* has
  spawned that client's player object locally — it says nothing about whether the *remote client* has
  actually received and processed that spawn message yet. The SDK's own TODO comment (quoted above) about
  clients only being "fully connected" after finishing synchronization was the correct signal all along; my
  guard was checking the wrong side of that sync.
- **Fix**: `LobbyController` now subscribes to `NetworkManager.Singleton.SceneManager.OnSynchronizeComplete`
  (a real NGO event, `Action<ulong>`, fired once a specific client has genuinely finished its initial
  scene/object synchronization) and tracks a `_syncedClients` set (host's own `ServerClientId` is added
  immediately since the host never needs to sync to itself). `OnStartClicked` now blocks until every
  currently connected client ID appears in that set, instead of the flawed `PlayerObject != null` check.
- Compiles clean (`OnSynchronizeComplete` confirmed to exist on `NetworkSceneManager`). Not yet re-verified
  live — needs the same fresh-restart-of-all-instances treatment as every fix in this session, plus this
  time genuinely waiting after a client joins (the warning log will say so) before clicking Start.

## 2026-07-24 — "No movement or camera in the round" root cause
Three stacked causes, all real:
1. **Arena_Greybox had no camera at all.** Nothing in the scene file matched "Camera".
2. **PlayerController cached `Camera.main` once at OnNetworkSpawn** (which happens in the Lobby scene when
   the player object spawns). The Lobby camera is destroyed by the Single-mode scene switch to the arena,
   so `_followCamera` was a dead reference and the null-guard made LateUpdate silently do nothing.
3. **The Look input action was read but never applied** — mouse look had never actually been implemented;
   the old camera was a fixed follow-offset, which is why it felt like a "choppy free 3d cam".
Fix pattern: the owner creates its OWN camera rig as a child of the player object. Children of a
NetworkObject migrate scenes with it, so the camera can never be destroyed by a scene load, and no scene
needs to provide a camera. Rig is tagged MainCamera so all existing `Camera.main` raycast code still works.

## 2026-07-24 — Single shared MovementLocked bool doesn't survive multiple UI systems
Paint mode, chat, pause menu, and the pose wheel all want to lock movement/free the cursor. With a plain
bool, closing one system unlocks movement even while another is still open (chat opens -> pose wheel
closes -> movement unlocked mid-typing). Fix: `SetMovementLock(object source, bool)` backed by a
HashSet<object>; `MovementLocked => count > 0`. Every hotkey entry point also checks MovementLocked before
activating so typing "r2f3" in chat doesn't open the pose wheel/X-ray/paint mode. Esc is double-bound
(cancel chat vs open pause): chat records `LastEscConsumedFrame` and the pause menu skips that frame.

## 2026-07-24 — Unity 6000.5 deprecation: FindObjectsByType(FindObjectsSortMode)
CS0618: the `FindObjectsByType<T>(FindObjectsSortMode.None)` overload is now deprecated — use the
parameterless `FindObjectsByType<T>()` instead.

## 2026-07-24 — Owner-auth NetworkTransform: server-side transform writes silently don't stick
GameStateManager.SpawnPlayers() was setting player positions directly on the server. With an
owner-authoritative NetworkTransform the owner's next update just snaps it back — no error, position
"randomly" doesn't apply. Correct pattern: a ClientRpc (TeleportClientRpc) that runs on the OWNER, which
disables its CharacterController, sets the transform, re-enables. All teleports (lobby placement, match
start, seeker entry, post-round return) now go through this.

## 2026-07-24 — Spatial fairness > freeze: one scene, two places
Game Feel doc flow implemented without any multi-scene tricks: the lobby room is plain geometry 200 units
away from the arena inside the same scene. "Seeker can't see the map during Hide" is just distance +
walls. Avoids NGO additive-scene visibility management entirely. Consequence: any through-wall tool
(X-ray) MUST be phase-gated or it trivially defeats the separation.

## 2026-07-24 — THE paint-mode bug: BakeMesh + x100 import scale = collider 100x too big
goop_guy's SkinnedMeshRenderer sits under a x100 transform scale (Blender FBX unit compensation).
Legacy `SkinnedMeshRenderer.BakeMesh(mesh)` bakes vertices WITH that scale applied, and the MeshCollider
on the same GameObject then inherits the x100 transform ON TOP — measured collider world size was
116 x 57 x 210 METERS. Every paint ray / eyedropper ray / seeker aim ray / camera spherecast interacted
with an invisible building-sized collider. Fix: `BakeMesh(mesh, useScale: true)` bakes unscaled vertices
(verified empirically: bounds 0.0117 vs 1.167), letting the transform apply scale exactly once.
Lesson: after ANY runtime-generated collider on imported models, sanity-check collider bounds vs
renderer.bounds — a silent scale mismatch produces "raycasts just don't work" with zero errors.

## 2026-07-24 — Eyedropper single-raycast died on own capsule; pink = shader OOM
- Third-person camera sits behind the player, so a single center-screen Physics.Raycast almost always hits
  the player's own CharacterController capsule first. Eyedropper now RaycastAll-sorts and skips own
  non-paintable colliders; paintable bodies (own or other players') sample their live paint Texture2D.
- MPPM virtual player showing an all-pink character: console had "Shader error 'URP/Lit': out of memory
  during compilation" — the VP ran out of memory compiling shader variants, magenta fallback. Not a
  material bug per se, but PaintableSkin now builds its material from Shader.Find("Universal Render
  Pipeline/Lit") explicitly instead of cloning the FBX import material (which can reference non-URP
  shaders on some clients). Restarting the VP clears the OOM case.
- UnityTransport MaxPacketQueueSize raised 128 -> 512 (persistent "Receive queue is full" warnings).

## 2026-07-24 — Editor asset-database wedged into Read Only mid-session
During the goop_character.fbx swap, interrupted RunCommand calls ("User interactions are not supported for
MCP tool calls") left the asset pipeline in "Asset Database is set to Read Only, but it has found
out-of-date assets. This should not happen!" — after that, EVERY import silently produced 0 sub-assets and
every AssetDatabase.CreateAsset failed. StopAssetEditing unwind didn't help (counter was already 0).
Only recovery: restart the editor. Consequences + mitigations:
- Asset-creating editor automation now lives in a committed MenuItem tool (Goop > Complete Character Swap)
  instead of throwaway RunCommand scripts — rerunnable after any restart, survives the wedge.
- AnimatorController.CreateAnimatorControllerAtPath is fragile in a wedged/half-imported state (object
  destroyed mid-call); REBUILDING an existing controller asset in place (RemoveState/RemoveParameter, then
  re-add) kept working when creation didn't.

## 2026-07-24 — Seeker shooting never registered: own-capsule raycast (same class as eyedropper bug)
SeekerTagController used one Physics.Raycast from the camera — which sits BEHIND the Seeker, so the first
hit was almost always the Seeker's own CharacterController capsule → targetId 0 → server-registered miss
on every shot. Fixed with RaycastAll + skip-own-root + first-non-self-hit-decides (player = candidate,
world = blocked). Server LOS re-check got the same treatment (skips both shooter's and target's own
colliders instead of a fragile single-hit comparison). Standing rule: ANY center-screen ray from a
third-person camera must skip the local player's colliders first.
