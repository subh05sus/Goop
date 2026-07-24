# Goop — TODO

See `Docs/Goop_Build_Plan.md` for full milestone plan. Check items as completed.

## M0 — Foundation & package install
- [x] Install NGO, com.unity.services.multiplayer, multiplayer.tools, multiplayer.playmode
- [x] Create `Assets/_Goop/{Scripts/{Networking,Gameplay,Player,UI,Paint},Prefabs,Scenes,Materials,Art}`
- [x] Create scenes: Bootstrap, MainMenu, Lobby, Arena_Greybox (added to Build Settings)
- [x] Move `goop.fbx` into `Assets/_Goop/Art/` (19 pose AnimationClips confirmed: Pose1-Pose19, rig "metarig")
- [x] Create Docs/ files
- [x] Placeholder capsule Player prefab (`Assets/_Goop/Prefabs/Player.prefab`)
- [x] Verify: no compile errors (4 cosmetic NetVis overlay errors unrelated to project code, see Learnings)

## M1 — Netcode core (offline host/client)
- [x] NetworkManager in Bootstrap scene (Unity Transport)
- [x] PlayerController (owner-auth movement + 3rd person camera, Input System)
- [x] NetworkObject + NetworkTransform (owner-authoritative) on player prefab
- [x] NetworkPlayer NetworkBehaviour (name, team/role enum, alive NetworkVariables)
- [x] Debug Host/Client UI buttons (NetworkDebugUI)
- [x] Verify (editor, single instance): StartHost spawns Player(Clone), zero console errors
- [ ] **USER ACTION NEEDED**: Verify MPPM 2 players — movement replicates both ways (not scriptable via MCP)

## M2 — Relay + Lobby
- [x] Unity Cloud project already linked (org `subhadipsus`) — no user action was needed
- [x] ServicesBootstrap (UnityServices init + anonymous auth)
- [x] GoopSessionManager — unified Session API wraps Relay+Lobby+Auth (no separate LobbyManager needed)
- [x] MainMenu UI (Host / Join-by-code / Browse public) + Lobby UI (join code, player list, host Start/Leave)
- [x] BootstrapLoader — NetworkManager persists via DontDestroyOnLoad across Bootstrap→MainMenu→Lobby→Arena
- [x] EnableSceneManagement on NetworkConfig (host-driven synced scene loads for M3)
- [x] Verify (editor, real Unity Cloud services): Host Game → real Relay session created, real join code
      (`F9NJFQ` in test), Lobby UI shows correct role + live player list, zero errors
- [ ] **USER ACTION NEEDED**: Verify Join-by-code from a second real client (MPPM or standalone build) — not
      scriptable via MCP, same limitation as M1's multi-client check

## M3 — Game state & round flow (Normal mode)
- [x] GameStateManager phase state machine (Lobby/Prep/Hunt/Resolution/PostRound) as scene-placed NetworkObject
- [x] RoleAssignment (Hider/Seeker split via NetworkPlayer.CurrentTeam) + spawn points (Arena_Greybox)
- [x] Seeker freeze during Prep (NetworkPlayer.IsFrozen, checked in PlayerController)
- [x] Win eval (Normal mode: any Hider alive at Hunt end -> Hiders win)
- [x] RoundHudUI (phase banner, countdown, role, winner)
- [x] Fixed real bug: player-object migration race on scene load (see Learnings.md 2026-07-24)
- [x] Verify (editor): Host -> Start Round -> scene transitions Lobby -> Arena_Greybox cleanly, zero errors
- [ ] **USER ACTION NEEDED**: manual click-through of a full round to confirm Prep->Hunt->Resolution->
      PostRound->Lobby timer loop visually (MCP polling can't reliably observe this, see Learnings.md)

## M4 — Dummy map + character + poses
- [ ] Arena_Greybox greybox geometry + spawn points
- [x] Swap capsule → goop_guy.fbx (Generic rig; visual child `Visual_GoopGuy` under Player root)
- [x] PoseController NetworkVariable<int> PoseIndex (owner-write) → Animator "PoseIndex" param
- [x] AnimatorController with Idle + Pose1-19 states, instant AnyState transitions
- [x] Pose cycle UI (Previous/Next/Crouch via existing Input Actions) — radial menu visuals deferred
- [x] Verify (editor, single instance): PoseIndex replicates via NetworkVariable, zero console errors
- [ ] **BLOCKED on source asset**: pose clips import with 0 frames (Blender export defect, not Unity/code —
      see Learnings.md 2026-07-23). Re-export goop_guy.fbx with real keyframe ranges + Bake Animation on;
      no code changes needed after that.
- [ ] Verify visually once re-exported: poses actually animate the mesh, silhouette/collider changes

## M5 — Painting system
- [x] Paintable Texture2D per player + local brush raycast->UV (baked MeshCollider, RaycastHit.textureCoord)
- [x] PaintStroke struct (IEquatable, INetworkSerializable) + owner-write NetworkList<PaintStroke> (list
      replication does the batching; late joiners get full history for free via NGO's normal list sync)
- [x] Stroke cap (400/round, client-side) — server-side re-validation NOT implemented (documented risk, PRD
      frames as post-launch monitoring item, not MVP-blocking)
- [x] Palette UI: 12 preset color swatches + eyedropper (samples world color under crosshair)
- [ ] Full HSV/color-wheel picker (deferred polish, presets satisfy PRD's "at least 12" requirement)
- [ ] "Wet paint" mid-hunt repaint penalty/shimmer (deferred polish)
- [x] Verify (editor): PaintableSkin.OnNetworkSpawn (texture/material/collider/NetworkList setup) runs with
      zero errors in a live networked session
- [ ] **USER ACTION NEEDED**: manual click-through — Host, Start Round, hold Interact looking at self to
      paint, try swatches/eyedropper, verify cross-client stroke sync with a 2nd client (MPPM/build)

## M6 — Seeker tag + ammo + polish
- [x] Server-authoritative tag confirm (SeekerTagController: server re-derives target/range/line-of-sight,
      never trusts client's own raycast result)
- [x] Ammo mode (host toggle in Lobby UI, default 5 via MatchSettings.AmmoCount, per-seeker AmmoRemaining)
- [x] Early round-end: all Hiders caught OR (ammo mode) all Seekers dry -> ends Hunt phase early
- [x] Scoring (Seeker +1/tag, Hider +survival seconds) + basic scoreboard in RoundHudUI
- [x] Verify (editor): SeekerTagController + Lobby ammo toggle compile clean, wired into Player.prefab/Lobby
- [ ] **USER ACTION NEEDED**: needs 2 real clients (1 Hider, 1 Seeker) to verify tag/ammo/early-end/scoring
      end-to-end — beyond single-instance MCP testing, same as every other multi-client feature so far

## Post-MVP polish backlog (not blocking, from PRD "later" items)
- [ ] Full HSV/color-wheel palette picker (currently 12 presets + eyedropper only)
- [ ] "Wet paint" mid-Hunt repaint penalty/shimmer
- [ ] Radial pose-selection menu (currently Previous/Next/Crouch cycle)
- [ ] Server-side stroke sanity-checks / anti-cheat hardening (PRD 9 risk item)
- [ ] Interest management (don't send full paint texture until Seeker in range) — PRD 9 anti-cheat item
- [ ] Infection + Double modes, dedicated-server path, 2nd map (all explicitly post-MVP per PRD)

## M7 — Full control scheme (reference-parity, 2026-07-24)
- [x] ROOT CAUSE FIX: no camera in Arena (scene had none; Camera.main cached at spawn died on scene switch;
      Look action was read but never used). Owner now spawns own camera rig as child of player.
- [x] Third-person orbit camera: mouse look (yaw/pitch clamp), wall-collision spherecast, no lag/choppiness
- [x] Camera-relative WASD, single fixed speed (no sprint — deliberate), jump (Space), crouch (Ctrl,
      shrinks CharacterController + slower move)
- [x] Paint mode (F): cursor freed, camera pulls in, LMB paint, RMB-drag brush size, MMB-drag self-orbit,
      Space 3D eyedropper (world + own body), palette panel; Hider/None only
- [x] Pose wheel: hold R -> radial menu (Idle + 19 poses), mouse dir selects, release confirms
- [x] Tab (hold) scoreboard, 3 nameplate toggle, Esc pause menu (sensitivity slider, resume, leave match)
- [x] Chat (T): ServerRpc->ClientRpc broadcast, movement locked while typing, hotkeys gated during typing
- [x] Seeker: 2 X-ray scan toggle (through-wall hider markers + explicit HUD indicator), 1 taunt whistle
      (whistle_1/2.mp3, networked, 4s cooldown)
- [x] Surface attach: Space near wall attaches (Hider), Space/Ctrl up/down, RMB+WASD slide, A/D tilt,
      Shift detach; overlap "clipping too deep" red-flash warning
- [x] Movement-lock ownership system (HashSet per-source) so paint/chat/pause/wheel can't stomp each other
- [ ] SKIPPED by user decision: Clone feature (Q/X)
- [ ] Deferred: voice PTT (V, needs Vivox), character size/shape pre-round select, pose pages
- [ ] **USER ACTION NEEDED**: fresh restart + live 2-client test of all new controls in a round

## M8 — Session flow rework per Game Feel doc (2026-07-24)
- [x] GamePhase: LobbyIdle / Hide / Transition / Hunt / Resolution / PostRound (Prep+freeze removed —
      spatial fairness replaces it)
- [x] Lobby is a real 3D room INSIDE Arena_Greybox (at x=200): floor/walls/practice crates/spawns.
      Host now loads straight into Arena_Greybox; Lobby.unity scene retired from flow
- [x] The Gun: scene-placed NetworkObject in lobby room. E pickup (2.5m), E aiming at player = hand-off
      (3.5m), G drop, holder marker over head, holder disconnect returns gun to stand. Server-validated.
- [x] Gun holder at Start = the one Seeker; everyone else Hider
- [x] Match start: Hiders teleport to map, Seeker physically stays in lobby room for whole Hide phase
- [x] Owner-teleport RPC (NetworkPlayer.TeleportClientRpc) — server transform writes don't stick with
      owner-auth NetworkTransform, owner must move itself (CharacterController toggled around the set)
- [x] Transition beat: 3s "THE HUNT BEGINS" full-screen overlay for everyone, then Seeker teleports in
- [x] In-lobby host panel (LobbyPanelUI): join code, players, map label, Hide/Hunt duration sliders
      (replicated NetworkVariables — everyone sees live), ammo toggle, Start (needs gun holder + 2+
      players), Leave. Old LobbyController UI scene no longer used
- [x] PostRound: everyone teleported back to lobby room, roles cleared, gun reset — loops to LobbyIdle
- [x] X-ray markers gated to Hunt phase only (would leak Hider positions during Hide)
- [x] Paint/pose fully live in lobby (practice space — Team None can paint)
- [x] Decisions on doc's open questions: host CAN be Seeker (MVP), lobby paint carries into match (MVP),
      Hiders hear Seeker only via 3D taunt whistles, gun uses existing tag raycast + optional ammo
- [ ] **USER ACTION NEEDED**: live 2-client run of full loop: lobby hangout -> pass gun -> Start ->
      Hide/Transition/Hunt -> PostRound -> back to lobby

## M9 — Hitboxes/IK/stances + resilience + menu polish (2026-07-24)
- [x] Mesh-accurate hitboxes: remote players' CharacterController capsules disabled — only the baked
      mesh collider is hit by aim/paint rays; collider REBAKES on every pose change (0.35s post-transition)
- [x] Seeker cannot use the pose wheel; stances = stand / crouch (Ctrl) / prone (X toggle, replicated
      visual tilt, slow crawl, no jump)
- [x] Gun rides the holder's hand.R bone (hip fallback), body-facing rotation
- [x] AimIK: spine chain (Bone.001-004) bends to camera pitch + head/torso yaw offset (±65°), additive
      world-space (Generic-rig safe), replicated Vector2, suppressed while posed/attached/dead
- [x] Walking keeps pose (shuffle frozen); only Shift-run breaks it
- [x] ConnectionWatchdog on NetworkManager: host quits/leaves, kick, transport failure -> tolerant session
      leave + shutdown + MainMenu with reason banner
- [x] Server: whole-team disconnect mid-round aborts to Resolution (no ghost rounds)
- [x] MainMenu revamp: scene had NO camera (root cause of "weird") — added camera/light/stage backdrop,
      3 posed colored GoopChar statues, restyled panel/title/buttons
- [ ] **USER ACTION NEEDED**: fresh VP launch + test: shooting posed hiders (mesh hitbox), prone, gun in
      hand, torso/head aim on remote players, host-quit while client in round

## M10 — Paint mechanism deep-dive parity (2026-07-24)
- [x] PaintStroke carries Metallic + Roughness bytes; painted into a second metallic-gloss texture
      (URP Lit _MetallicGlossMap: R=metallic, A=smoothness) — sheen replicates like color does
- [x] Palette: hue wheel + SV square + RGB sliders + HSV sliders + metallic/roughness sliders + brush
      slider + 12 presets, all two-way synced with the eyedropper
- [x] Per-map saved swatches (6 slots, PlayerPrefs keyed by scene; click empty=save, filled=load)
- [x] Undo stroke / Clear all — server-side list ops (RemoveAt/Clear), all clients rebuild both textures
      from the authoritative stroke list
- [x] Cast-shadow toggle — ServerRpc-validated NetworkVariable, only ever flips shadowCastingMode On/Off
- [x] Eyedropper anti-cheat: 0.25s rate limit + imperceptible ±2/255 sample jitter (Paint doc §5.5)
- [x] Already in place from earlier: freehand UV strokes, instant local prediction + server-validated
      stroke relay, MMB self-inspect orbit, feet frozen in paint mode, stroke cap 400
- [ ] Deferred: interest management (don't replicate paint state until Seeker within range) — PRD 9
