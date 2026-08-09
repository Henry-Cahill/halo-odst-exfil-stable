# Project Dropshock — Architecture & Setup

> `Dropshock` is a placeholder codename/namespace.
>
> **This is a non-commercial Halo fan project.** It uses Microsoft's IP under the
> Microsoft **Game Content Usage Rules**: never sold, never monetised, no implied
> endorsement. That is the only lawful footing for it, and it is a hard constraint
> on the whole design, not a disclaimer — see "Non-commercial constraints".

## Locked decisions

| Decision | Choice | Why |
|---|---|---|
| IP | **Halo (Microsoft), non-commercial fan use** | Permitted only under Microsoft's Game Content Usage Rules. Cannot be sold, monetised, or funded by donations |
| Engine | Unity `6000.5.6f1` (tech stream) | Deliberate: team accepts mid-production upgrades to stay on newer features |
| Render pipeline | HDRP | Already set up; **needs a perf budget review before BR scale** |
| Netcode | **Photon Fusion 2** (`2.1.1 Stable 2177`), Server mode | Built-in area-of-interest + tick-aligned prediction; proven at 100+ CCU |
| Authority | **Server-authoritative, always** | Extraction shooters die to duping/aimbots without it |
| Build order | Co-op vertical slice → scale same core to BR | Shared core, cheapest path to a playable thing |

## This document supersedes the code

**Decision: where this document and the code disagree about what the game *should*
do, the document wins and the code is wrong.** Design intent lives here. An
implementation that contradicts a decision recorded above is a bug to be fixed, not
a decision to be back-filled into the document.

This matters because the alternative is how design drifts: someone implements
something slightly different, the document is quietly amended to match, and after
enough rounds the design is whatever the code happened to become. Deciding in
writing first, and holding the code to it, is what keeps this a design rather than
an archaeology.

**One necessary exception: statements of fact about the code.** This document
contains two kinds of claim, and only one of them supersedes:

| Kind of claim | Authority |
|---|---|
| **Intent** — what the game should do, what a system is for, what is forbidden | **The document wins.** Code disagreeing with it is a defect |
| **Reported state** — "X is implemented", "the default is 45s", "this is verified" | **The code wins.** The document is merely describing it, and can be stale |

A design decision cannot be falsified by the code. A status marker can be, and
routinely is — see the correction in "Status / caveats" below, where a section
headed `DOWNED IMPLEMENTED` survived a playtest that showed bleed-out and revive do
not work. Keeping the two kinds of claim distinct is what makes the rule usable
instead of a licence to record fiction.

## Non-commercial constraints

The IP decision is not cosmetic. It removes every revenue path while the
architecture requires permanent running costs:

| Cost | Why the design needs it |
|---|---|
| Central SQL Server | Per-account stash, base state, production timers |
| Always-on backend service | Gates the first playable frame; validates every stash mutation |
| Dedicated Fusion servers | Raids and base-defence sessions, server-authoritative |
| Photon Fusion licensing | CCU-based, scales with concurrent players |

**Every player-hour costs money, forever, with no permitted income.** Consequences
worth designing around now rather than discovering later:

- Success is a financial emergency, not a win. Capacity planning means deciding a
  ceiling you can personally fund, and what happens when it is reached.
- Prefer designs that degrade to fewer/cheaper servers over designs that assume
  elastic scale.
- Keep the option of self-hosted or friends-only deployment open; a
  single-operator instance is the sustainable shape for this.
- Read the current Game Content Usage Rules directly before any public release.
  The terms are Microsoft's to change, and this summary is not a substitute.

**Nothing technical in this document depends on the IP decision.** Lag
compensation, authority, inventory, persistence and session design are unchanged
whichever way it goes.
| Engine | Unity `6000.5.6f1` (tech stream) | Deliberate: team accepts mid-production upgrades to stay on newer features |
| Render pipeline | HDRP | Already set up; **needs a perf budget review before BR scale** |
| Netcode | **Photon Fusion 2** (`2.1.1 Stable 2177`), Server mode | Built-in area-of-interest + tick-aligned prediction; proven at 100+ CCU |
| Authority | **Server-authoritative, always** | Extraction shooters die to duping/aimbots without it |
| Build order | Co-op vertical slice → scale same core to BR | Shared core, cheapest path to a playable thing |

## Why server authority is non-negotiable

This game has **persistent gear that players can lose**. That makes every client
a financially-motivated attacker. Any system where the client reports its own
position, its own hits, or its own inventory becomes a dupe/teleport/aimbot vector
on day one. This cannot be retrofitted later — it dictates the shape of every
gameplay script, so it is enforced from the first commit:

- Clients send **input only** (`NetInput`), never state.
- The server simulates movement, fires weapons, and applies damage.
- Hit registration uses **Fusion lag compensation** — the server rewinds hitboxes
  to the shooter's view of the world, so the client never asserts a hit.
- Inventory/loot mutations will be server-side RPC requests, never client writes.

## Two modes, one core

Co-op extraction (3–4p, PvE, persistence) and battle royale (100p, PvP, no
persistence) share the character controller, weapons, and damage model — and
almost nothing else. The divergence is deliberate:

| | Co-op Extraction | Battle Royale |
|---|---|---|
| Players | 3–4 | ~100 |
| Interest management | Off (everyone sees everyone) | **Required** — AOI radius per player |
| Persistence | Gear carried in/out, saved | None |
| Session end | Extract or die | Last squad standing |
| AI load | Heavy | Light/none |

`SessionConfig` (a ScriptableObject) is the single switch between them, so BR
scaling work lands in the shared core rather than in a fork.

The compound (the persistent base) is neither of these — it is a solo, non-networked
space with no session at all. See "The compound (base)".

## Manual setup steps (I cannot do these for you)

1. **Engine version.** The project runs on `6000.5.6f1`, a tech-stream release
   rather than the `6000.0.x` LTS line. This is an accepted, deliberate tradeoff:
   newer features in exchange for a forced upgrade roughly every 6 months once the
   release drops out of support. Budget for those upgrades as scheduled work, not
   as surprises — the risky ones are HDRP and Fusion compatibility. Pin the exact
   editor version in source control and upgrade the whole team at once.
2. **Import Fusion 2.** Download the SDK from the Photon dashboard
   (`dashboard.photonengine.com`) — it is a `.unitypackage`, not available on the
   public UPM registry.
3. **Create a Fusion App ID** in the Photon dashboard and paste it into
   `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset`.
4. **Set the simulation tick rate** in `NetworkProjectConfig`. Start at 30Hz for
   co-op. BR at 100 players will likely need it lower — measure, don't guess.
5. **Layers.** Create a `Hitbox` layer and assign it to character hitboxes; the
   weapon code raycasts against it.
6. **Import the SimpleKCC addon.** It is a *separate* download from the Fusion 2
   SDK (Photon site → Fusion → Addons → Simple KCC). `NetworkPlayer` will not
   compile without it.
7. **Enable lag compensation** in `NetworkProjectConfig`. It ships **off**, and off
   means no hitbox snapshot history is recorded, so every server-side shot misses
   silently. See "Hit registration" below — this is the single highest-cost
   misconfiguration in the project.
8. **Add `FUSION_ENABLE_MPPM`** to Scripting Define Symbols if you use Multiplayer
   Play Mode. Fusion's MPPM integration is compiled out without it
   (`Fusion.Unity.cs:2017`): `FusionMppm.MainEditor` stays null, so each virtual
   player tries to write the AssetDatabase itself and you get *"Asset Database is
   set to Read Only, but it has found out-of-date assets"* on loop. This is
   permanent config, not a debug toggle.

   Note it does not make virtual players able to import source changes — nothing
   does. **Stop the virtual players before editing anything under `Assets/`**, let
   the main editor finish compiling, then restart them. Only the main editor may
   write the AssetDatabase; editing scripts while peers are live produces the same
   read-only error on a loop.

## Client prediction

Movement is client-predicted and server-authoritative. The owning client runs the
same movement code the server will run, so input feels instant; the server's
result always wins and overrides any disagreement.

Three rules make prediction correct. Break any one and you get jitter that is
extremely hard to diagnose later:

1. **All carried state must be `[Networked]`.** Fusion re-simulates past ticks
   when a correcting snapshot arrives. A plain C# field does not roll back, so the
   replayed tick starts from the wrong value and drifts. This is why
   `MoveVelocity` and `PreviousButtons` are networked properties, not fields.
2. **Edge detection must compare against networked previous buttons.**
   `input.Buttons.WasPressed(PreviousButtons, ...)`. Using a local `_wasJumpDown`
   field is the most common Fusion prediction bug — during resimulation it already
   holds a future value and the press is silently lost.
3. **Never use `Random` in predicted code.** Client and server would compute
   different results for the same tick. Weapon spread is hashed off `Runner.Tick`
   for exactly this reason.

**Prediction never extends to authority.** The client predicts its own movement,
its own ammo count, and its own muzzle flash. It does *not* predict damage — only
the state authority resolves hits and applies damage. A client that predicts a
kill simply sees it un-happen.

**SimpleKCC replaces `NetworkTransform`.** It replicates its own transform state.
Do not put both on the player prefab; they will fight.

## Hit registration

The server rewinds every player's hitboxes to the shooter's view of the world at
the tick their input was generated, then raycasts against that rewound state
(`NetworkWeapon.ResolveShot`). The client never asserts a hit.

**Every failure mode in this system is silent.** There is no exception, no
warning, and no visual difference — damage simply never happens. That is worth
stating plainly because it has already cost this project real debugging time.
The known ways to break it:

| Cause | Symptom |
|---|---|
| `LagCompensation.Enabled: false` in `NetworkProjectConfig` | Every shot misses. No history is recorded, so the rewind has nothing to rewind to. |
| No `HitboxRoot` on the player prefab | Lag comp rewinds Fusion `Hitbox` components, not PhysX colliders. |
| `HitboxRoot.Hitboxes` array empty | Assigning `Hitbox.Root` is *not* enough; the root's own array must list them. |
| `NetworkWeapon._hitMask` excludes the `Hitbox` layer | Identical symptom to having no hitboxes at all. |

`Dropshock ▸ Setup ▸ Validate Setup` checks all four. Run it before assuming the
problem is in gameplay code. It also checks the AI pipeline (missing enemy prefab,
missing or empty `HitboxRoot` on it, and an `EnemySpawner` with no prefab assigned
— which returns every tick and spawns nothing, silently) and the weaver allowlist.


Note that `Runner.LagCompensation` is **non-null even when the feature is
disabled**, so a null check alone does not detect the first row. `NetworkWeapon`
checks `Runner.Config.LagCompensation.Enabled` as well and logs once.

### Testing it

**Lag compensation cannot be tested in Host mode.** The host player *is* the
server, so their shots never cross the network and the conditioner does not apply
to them. Hits will register whether or not rewind works — a passing test in Host
mode tells you nothing. The shooter must be a genuine remote **Client**.

Nor can it be tested at zero latency, for the same reason: with no displacement
between the client's view and the server's, there is nothing to compensate for.

The procedure:

1. `NetworkConditions.Enabled: true` in `NetworkProjectConfig` (values are
   preset to 150ms + 5% loss). **Revert this after testing** — it is a harness,
   and leaving it on silently degrades everyone's play mode.
2. Add `DROPSHOCK_TRACE_SHOTS` to Scripting Define Symbols for per-shot server
   logging. Remove it afterwards; it logs on every shot.
3. Run three peers: main editor as **Server**, two virtual players as **Client**.
   Start the server first. Uncheck `_autoStartHostInEditor` to get the picker.
4. On the target client, press **F8** to auto-strafe. Only the focused window
   receives keyboard input, so a human cannot drive a moving target and a shooter
   at once — and a stationary target registers hits even when rewind is broken.
5. Shoot from the other client, aiming where the target *appears*. Needing to
   lead the target means rewind is not working.

Read the **server** console; `ResolveShot` is authority-only and clients log
nothing. `HIT` while strafing is the pass condition. Toggling F8 off and seeing
misses become hits is the clearest signal that rewind specifically is broken.

## Building the test setup

After importing Fusion 2 + SimpleKCC:

1. **Dropshock ▸ Setup ▸ Build Test Scene** — creates both scenes, the player
   prefab, the config assets, the `Hitbox` layer, and the build settings order.
2. **Dropshock ▸ Setup ▸ Validate Setup** — checks the failure modes that are
   silent at runtime (lag compensation disabled, missing `HitboxRoot`, empty
   `Hitboxes` array, a weapon hit mask that omits the `Hitbox` layer,
   `NetworkTransform` fighting SimpleKCC, unassigned weapon config, wrong scene
   build order).
3. Confirm the player prefab is registered in `NetworkProjectConfig`.
4. Open `Assets/Scenes/Bootstrap.unity` and press Play, then choose Server, Client
   or Host from the in-play picker. The generated scene leaves
   `_autoStartHostInEditor` off, because auto-host cannot exercise lag
   compensation (see "Hit registration"). Tick it on for solo iteration.

The setup is generated by editor scripts rather than committed as binary `.prefab`
and `.unity` assets. That keeps the wiring reviewable in a diff, re-runnable after
an SDK upgrade, and free of hand-authored GUIDs.

**Two scenes, deliberately.** `Bootstrap` owns the `NetworkRunner` and persists;
`DropTest` is the map Fusion loads over it. Putting the runner in the gameplay
scene works fine in the editor and breaks the moment you need to change maps
between matches.

**"Persists" requires an explicit call.** Fusion loads the gameplay scene in
Single mode, which unloads `Bootstrap` — taking the `NetworkRig` GameObject, and
the `NetworkRunner` running the in-flight `StartGame`, with it. The start
operation is then cancelled and reported as `ShutdownReason.OperationCanceled`,
which names the symptom and not the cause. `NetworkLauncher` therefore calls
`_runner.MakeDontDestroyOnLoad(gameObject)` before `StartGame`. Use the runner's
method rather than `DontDestroyOnLoad` directly; it is also correct under
multi-peer, where plain DDOL is wrong.

For the same reason **no camera lives in `Bootstrap`** — it would be destroyed on
connect, giving "Display 1 No cameras rendering". The gameplay scene provides one,
and `PlayerCamera` creates a fallback for the input authority if `Camera.main` is
null.

**Only the authority specifies a scene.** `StartGameArgs.Scene` is set for
Server/Host and left unset for Client — the client receives the scene from the
server, and specifying one makes it race the server's own load.

## Testing prediction

Prediction bugs are invisible on a local connection and obvious at 100ms. Test in
Fusion's network conditioner with latency **and** packet loss enabled, or the test
tells you nothing. What to look for:

- Movement that feels instant locally but rubber-bands on correction → networked
  state missing somewhere in the movement path.
- Jump occasionally not registering → edge detection reading a non-networked field.
- Character jitter that only appears under latency → `NetworkTransform` and
  SimpleKCC both writing the transform.

## Known environment hazard: truncated .meta files

Something outside the Unity editor writes **truncated `.cs.meta` files** into this
project — 59 bytes containing only `fileFormatVersion` and `guid`, with the entire
`MonoImporter:` block missing. Unity then fails to import those scripts, and the
symptom is misleading: you get `CS0234 "namespace Core does not exist in namespace
Dropshock"` for files that have no dependencies and should compile trivially.

Deleting them does not help — they regenerate truncated. The fix is to write a
complete meta, preserving the existing GUID:

```yaml
fileFormatVersion: 2
guid: <existing 32-char guid — do not change it>
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

**Always preserve the GUID.** Once prefabs and scenes reference these scripts,
changing a GUID silently detaches every component that used it.

If `Dropshock.Core` ever "does not exist" again, check the meta files before
touching the code.

## App ID setup (Fusion 2.1.1)

`Tools ▸ Fusion ▸ Realtime Settings` — creates `PhotonAppSettings` if missing,
then selects it. Paste the App ID into **App Id Fusion** in the Inspector.

There is **no Fusion Hub window** in 2.1.1. The asset is generated on first access,
not shipped with the package, so it will not exist under
`Assets/Photon/Fusion/Resources/` until you run that menu item.

## Known SDK issue: FusionPluginProjectSettings assert on script reload

Every script reload logs:

```
Exception thrown while invoking [DidReloadScripts] method
'Fusion.Editor.FusionPluginProjectSettings:ScriptsReloaded()' : AssertException: attribute != null
```

**This is a Fusion 2.1.1 SDK bug, not a project defect.**
`FusionPluginProjectSettings` derives from `FusionGlobalScriptableObject<T>` but
declares no `[FusionGlobalScriptableObjectResource]` attribute, so the global
lookup asserts. The affected handler only auto-exports assemblies for Fusion's
server-plugin code-export feature, which this project does not use.

**Harmless.** Optional workaround: create the asset via
`Assets ▸ Create ▸ Fusion ▸ Plugin Project Settings` and label it
`FusionDefaultGlobal`, matching how `NetworkProjectConfig` and `PhotonAppSettings`
are registered.

Re-check this after any Fusion upgrade — it may simply disappear.

## Offline typecheck (Linux, no Unity editor)

`Tools/setup-typecheck.sh` then `Tools/typecheck.sh` compiles `Assets/Scripts/`
with Roslyn from a user-local **.NET 8 SDK**, so C# errors can be caught without
opening the editor. No root needed.

**The language version is 9.0, not 8.0.** Unity's generated `Assembly-CSharp.csproj`
declares `<LangVersion>9.0</LangVersion>`, and the project already relies on it —
`PlayerSpawner.cs:24` and `NetworkPlayer.cs:71` both use target-typed `new()`.
Running `./Tools/typecheck.sh 8.0` fails on exactly those two lines with CS8400.
The argument exists so that stays visible; do not "fix" it by downgrading the code.

### What it does not check

A clean run means the C# is well-formed. It does **not** mean the project builds
in Unity:

| Gap | Consequence |
|---|---|
| Reference assemblies are Unity **2021.3.33** (newest on NuGet), not 6000.5.6f1 | APIs added after 2021.3 may be missing. Errors naming them are filtered and reported separately, never silently dropped |
| `Assets/Editor/` is not compiled | Needs `UnityEditor.dll`, which is not redistributable and not on NuGet |
| Fusion's ILWeaver does not run | `[Networked]` property rewriting is unverified — this checks C#, not weaving |

The Unity engine DLLs are not in this repo; `Assembly-CSharp.csproj` points at
`C:\Program Files\Unity\Hub\Editor\6000.5.6f1\...`, i.e. the editor install on the
Windows machine. Copying that `Editor/Data/Managed/UnityEngine/` folder into
`$DROPSHOCK_TOOLS/refs` would remove the first gap entirely and is the single
highest-value improvement available to this tool.

`Fusion.Unity.dll` and the other package assemblies are read from
`Library/ScriptAssemblies/`, which Unity populates on its last successful compile.
**A clean `Library/` therefore breaks the typecheck**, and the error will name
`NetworkSceneManagerDefault` rather than the missing directory.

### Two environment hazards, both fatal and both silent-ish

- **No `libicu`, and no root to install it.** Without
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, `csc` FailFasts inside
  `GlobalizationMode`'s static constructor *before parsing a single file*, and
  emits nothing resembling a compiler diagnostic. A script that greps for
  `: error` reports success on a compile that never ran. This was hit while
  building the tool, which is why `typecheck.sh` checks the **exit code** and
  refuses to interpret "no error lines" as "no errors".
- **Unresolvable localtime.** Every `dotnet` invocation dies in
  `TimeZoneInfo`'s static constructor without `TZ=UTC`.

Both are exported by the script. They are recorded here because they are the same
failure shape the rest of this document keeps warning about: the tool reports
success while doing nothing.

## Input

Input is read by polling `Keyboard.current` / `Mouse.current` from the Input System
package directly. The project has **Input System (New)** active, so the legacy
`UnityEngine.Input` API throws at runtime — do not reintroduce it.

Devices are null-checked because a **headless dedicated server has no keyboard or
mouse**, and an unchecked null there crashes the server on startup.

**Known gap:** bindings are hardcoded rather than driven by the
`InputSystem_Actions.inputactions` asset, so there is no key rebinding and no
gamepad support. Migrate before UI or tutorial content starts referencing specific
keys.

## Inventory and loot — DESIGN ONLY, NOT IMPLEMENTED

Nothing in this section exists yet. `Assets/Scripts/Core/` holds only `Damage.cs`
and `GameModeId.cs`; there is no item, container, or inventory type. This is the
intended shape, recorded before implementation because the storage model is as
hard to retrofit as server authority was.

**Decision: spatial grid containers** (Tarkov-style). Items occupy a `W×H`
footprint and must be placed somewhere they fit. Chosen over a flat slot list
deliberately — grid management *is* gameplay in this genre, and the flat model
cannot be upgraded to it without rewriting every container, RPC and UI.

### Item definitions

`ItemDef` is a ScriptableObject: a **stable numeric `ItemId`**, display name,
`GridWidth`/`GridHeight`, `MaxStack`, and a world prefab.

The id must be assigned explicitly and never reused — not derived from an array
index or asset order, both of which shift when someone reorders a folder and would
silently turn every stored rifle into a bandage. A registry resolves `ItemId →
ItemDef` on each peer.

**Only the id crosses the network.** ScriptableObject references are not
networkable, and sending item *properties* would let a client claim its pistol is
a 1×1 stack of 500.

### Container representation

```csharp
struct ItemStack {              // plain C#, NOT INetworkStruct
    ushort ItemId;      // 0 = empty
    ushort Count;       // stack size
    byte   X, Y;        // top-left cell in the container
    byte   Rotated;     // 90° rotation flag
    uint   InstanceId;  // server-assigned, unique, for dupe tracing
}
```

A container is a **list of placed items, not a grid of cells**. Occupancy is
derived by walking the list; replicating `W×H` cells would cost more and duplicate
information already present.

**Container contents are NOT `[Networked]`.** They live in plain server-side
memory and are sent to one player by targeted RPC when that player opens the
container. This is the single most important rule in this section, for two
reasons:

- **It closes a cheat class.** Networked state reaches every client whose area of
  interest includes the object, and a modified client can read anything it
  receives. Replicated corpse contents mean loot-ESP: seeing which bodies and
  crates are worth pushing, through walls. Nothing sent is ever secret.
- **It is cheaper.** One targeted payload on open, instead of continuous
  replication to everyone in range.

What *is* networked is only what other players must legitimately see:

| State | Replication |
|---|---|
| Equipped weapon / visible gear | `[Networked]` — everyone renders it |
| Carried container contents | Server memory; targeted RPC to the owner |
| Corpse / world container contents | Server memory; targeted RPC on open |
| "This corpse has been looted" flag | `[Networked]` — drives world visuals |

Because contents are not replicated, `NetworkArray`'s compile-time
`[Capacity(N)]` limit does not apply to them, and containers can be sized from
design rather than from a networking constraint. The limit still applies to
anything that *is* networked, such as equipment slots.

### Authority model

Clients send **requests**, never state — consistent with the rest of the project,
and non-negotiable here because gear has real value:

```csharp
[Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
public void RpcRequestMove(NetworkId fromContainer, byte fromX, byte fromY,
                           NetworkId toContainer,   byte toX,   byte toY,
                           NetworkBool rotated);
```

The server validates every request and silently rejects failures:

1. Does the requesting player have access to **both** containers?
2. Is the target container within interaction range of that player *now*?
3. Does the source cell actually hold an item?
4. Does the destination fit — inside bounds, no overlap with other stacks?
5. Are stack limits respected?

The request carries **coordinates only**. The server reads item identity from its
own state; a client that lies about what it is moving changes nothing.

### Death and disconnect

On death the server spawns a corpse container at the death position, moves every
stack into it, and clears the player's inventory — one atomic server-side
operation within a single tick. This is the payoff of the whole genre and the
reason `PlayerHealth.Die` is authority-gated.

**Disconnect must use the identical path.** `PlayerSpawner.PlayerLeft` currently
despawns the player object immediately, so gear vanishes with them and
combat-logging becomes the mathematically optimal way to keep it — a dupe in
everything but name. Dropping on disconnect is not a nicety; it is the rule that
makes the loop honest, and it belongs to the same function as death drops so the
two cannot drift apart.

Note that a hard crash or timeout also routes through `PlayerLeft`, but only after
Fusion's timeout expires. The corpse appears late. That is correct and should not
be "fixed" by trusting a client-sent disconnect notice.

#### Open: the StateReplicator assert (SDK-internal, network thread)

```
AssertException: tick >= sc._latestTickAcknowledged
  Fusion.Simulation+StateReplicator.OnPacketDelivered(NetConnection*, Tick, ReadOnlySpan<T>)
    Simulation.StateReplicator.cs:1129
  Fusion.Simulation.INetPeerGroupCallbacks.OnNotifyDataDelivered(...)
  Fusion.Sockets.V2.Connection.UpdateNotify(...)  Connection.Recv.cs:409
  --- thrown on a socket thread, surfaced via UnityLogStream / RegisterUECatcher
```

**This was previously recorded here as a death/disconnect problem and closed as
unreproducible. Both were wrong**, and the reason is worth keeping: the diagnosis
was built from the shape of project code rather than from the assert's own message,
which nobody had captured. Two rounds of isolation harness were designed against a
subsystem the assert never touches.

What the real message says:

- It is **not in the leave path**, and not in gameplay code at all. It is in
  Fusion's delivery-notification path — the bookkeeping that tracks which sent
  packets a peer has acknowledged.
- It runs on a **socket thread**, not the simulation thread. `FixedUpdateNetwork`,
  spawn/despawn ordering and tick phase are all irrelevant to it.
- The invariant is that a delivery notification's tick must not be older than the
  latest already acknowledged. Violation means an **out-of-order or stale delivery
  notification** arrived.

Nothing in `Assets/Scripts/` can violate this directly; it is reachable only
through Fusion's own socket layer. That makes it an SDK issue to report rather than
a bug to fix here, alongside the `FusionPluginProjectSettings` assert.

**Ruled out:** the network conditioner. `NetworkConditions.Enabled` is `false`, so
synthesised loss and reordering are not the cause.

**Leading hypothesis, untested:** a recycled connection. On disconnect a
`NetConnection` slot is torn down while delivery notifications for its older ticks
may still be in flight; if the slot is reused, those can surface against a
`_latestTickAcknowledged` that has already advanced. This would explain the
original association with disconnects without any of it being the leave path's
fault. Confirming it means correlating the assert with client disconnect and
reconnect, which has not been done.

**The severity question is answered: it is cosmetic.** After the assert fires the
session continues normally — players move, shots register, enemies spawn. The
socket thread survives, so this is noise to report to Photon rather than a fault to
work around. It does *not* silently stop replication, which was the outcome worth
ruling out.

Two facts established while chasing this, both still valid and both about the leave
path rather than the assert:

**Fact 1: runner callbacks are inside the simulation.** This was assumed otherwise
and the assumption was wrong. The stack traces settle it:

```
death:  Simulation.StepSimulation:564 → InvokeTick:2020 → OnTick → InvokeFixedUpdateNetwork
leave:  Simulation.StepSimulation:564 → InvokeTick:2014 → InvokePlayerJoinedLeft → PlayerLeft
```

`OnPlayerLeft` is dispatched from within `InvokeTick`, six lines ahead of the
`FixedUpdateNetwork` dispatch. Do not reason about these two paths as "inside a
tick versus outside" — they differ only in *phase within* the tick, plus the fact
that leaving despawns the player and dying does not.

**Fact 2: the player-left phase cannot be synthesised.** It is entered only by a
player genuinely leaving, so no key press or editor tool can reproduce that context.
Any future isolation of a leave-path bug has to be a switch *on the real leave
path*, with an actual disconnect triggering each variant — not a harness firing from
`Update`, which lands outside `StepSimulation` and tests a context production never
uses. An earlier attempt did exactly that and would have produced misleading
evidence.

If it resurfaces, capture the assert message *first*. That lesson cost two rounds
of work here: an isolation harness was designed twice against the wrong subsystem
because the diagnosis came from reading project code instead of the exception.
`StateReplicator` asserts at several sites and they mean entirely different things
— a delivery-acknowledgement assert on a socket thread has nothing in common with a
write-authority one on the simulation thread, and no amount of staring at gameplay
code distinguishes them.

### Stash and persistence

### Stash and persistence

**Decision: SQL Server, central.** Per-account state cannot live on individual
game servers — a player must reach the same stash from any server. Relational and
transactional is the right shape here: extraction and production collection are
both commits, not eventual-consistency problems.

Two storage tiers, deliberately separate:

| Tier | Lives | Lifetime | Networked |
|---|---|---|---|
| **Raid inventory** | Server memory | One raid | No (see above) |
| **Stash** | SQL Server (central) | Permanent | Never |

**A database does not lighten the live inventory load, and must not be asked to.**
The server validates every move against current state at tick rate — bounds,
overlap, ownership, range — which requires that state in memory. A DB read inside
the tick loop means either blocking I/O in the simulation or an async read racing
the validation it exists to serve. Authority and remote storage are incompatible
on the hot path.

Nor is there load to relieve. An `ItemStack` is ~16 bytes aligned, so a 64-stack
container is ~1 KB; a full co-op raid is single-digit KB and even 100 BR players
is ~100 KB. The database earns its place through **durability and scale outside
the raid**, not through offloading combat.

The stash is potentially thousands of items and is only ever touched from the
menu, never during a raid. It is loaded and mutated server-side at menu time and
is never networked state in any form.

**Extraction is the only write.** On successful extraction the server persists the
surviving loadout in a single transaction: remove what was consumed or lost, add
what was brought out, in one commit. Death writes nothing — the loss is simply the
absence of a write.

That boundary must be transactional. A crash between "extraction succeeded" and
"commit completed" either loses gear the player earned or, if ordered the other
way, grants gear they did not.

**Decision: idempotent commit with retry, keyed on an extraction id.** Rather than
choosing which side to fail toward, the server assigns each extraction attempt a
unique id and makes the commit idempotent against it: applying the same extraction
id twice is a no-op that returns the same result as the first application.

- The client or session may retry after a crash without risk. A retry either
  completes the original commit or observes it already done.
- No arbitrary loser. The "fail toward loss" option robs players who earned their
  gear; "fail toward granting" duplicates, and duplication is permanent and
  spreads. Idempotency avoids picking a victim.
- The cost is real: the extraction id must be generated and persisted *before* the
  commit, the commit must be a single transaction that includes recording the id,
  and the retry path needs an owner — most likely the session, since the client
  cannot be trusted to drive it.

**The id is the audit trail's other half.** `InstanceId` makes duplication
detectable per item; the extraction id makes the *event* replayable and
attributable. Log both across the raid/stash boundary.

Note this does not remove the need to decide what happens if the retry never
arrives — a session that dies permanently mid-commit leaves the extraction
uncommitted. That is a stuck record rather than a corrupt one, which is the point,
but it still needs a reconciliation pass.

`InstanceId` is server-assigned and unique for the life of an item. It exists so
duplication is **detectable after the fact**: the same instance in two places is a
bug or an exploit, and without an audit trail neither is visible. Log every
transfer that crosses the raid/stash boundary against it.

### Weight and encumbrance — follows Tarkov

**Decision: weight follows Escape from Tarkov's model.** Grid area constrains *what
fits*; weight constrains *what you can move with*. Two independent limits, which is
what stops loadout choice being a pure packing puzzle.

The Tarkov properties being adopted:

- **Every item has a mass.** Total carried mass is the sum of everything on the
  player, including inside containers.
- **Penalties are tiered, not linear.** Crossing a threshold degrades movement speed
  and stamina; past a hard cap, sprinting is unavailable. Tiers are the right choice
  because a player can *feel* a boundary, where a smooth curve just feels vague.
- **Weight affects stamina drain and regeneration**, not only top speed.
- **Weight affects how much noise you make.** This matters more here than it looks —
  it is what gives the **Hide** approach in the hideout waves a real mechanical
  basis, and it ties into covert-movement skill.
- **Strength raises your thresholds, and carrying weight raises Strength.** This is
  the cleanest example of the use-based skill accrual decided in "Skills": the
  system that punishes you for carrying is the same one that rewards it over time.

#### The prediction problem this creates

**Weight changes movement speed, and movement is client-predicted.** That is a
direct conflict with how this game already works, and it will not announce itself —
it will surface as rubber-banding under heavy loadouts and be mistaken for a network
problem.

`NetworkPlayer.ApplyMovement` selects a speed each tick, and the client predicts that
selection locally. If the server derives speed from an inventory the client cannot
see, client and server compute different speeds for the same tick and prediction
diverges. Inventory is deliberately server-side — `PlayerInventory.CanPlayerAccess`
gates it — so the client genuinely does not have the data.

**Fix: replicate the total, not the inventory.** A single `[Networked] float`
carrying total mass on the player is enough for the client to predict correctly,
costs one float of bandwidth, and leaks nothing about *what* is being carried. The
speed calculation must then be a pure function of that replicated value, so both
sides compute it identically.

This is the same rule the rest of the netcode already follows: derive predicted
state from replicated state, never from data only one side holds.

#### Implementation note

`Mass` is the **third** field the item model needs, alongside kit/consumable
classification and weapon mod state. All three land in `ItemDef`, all three must
reach the backend catalogue, and all three should be designed in one pass rather
than three migrations.

### Stacking rules

**Decisions, as recommended:**

- **Auto-merge on pickup.** Fill existing partial stacks first, then place the
  remainder in free space. This is what players expect and it avoids inventories
  filling with fragments.
- **Partial splitting is supported.** Needed the moment a player drops or trades part
  of a stack.
- **Merge across containers on transfer**, by the same rule.

The placement logic already handles the hard case: the corpse-drop playtest repacked
a stack from `@(8,0)` into an 8-wide container that could not hold it at that
coordinate.

**The consequence that actually bites: a split creates a new instance.** Both halves
need identity, so a split cannot preserve a single `InstanceId` — and `InstanceId`
is a primary key spanning stash and custody in `schema.sql`. Splitting is therefore
an **instance-creating mutation**, not a UI convenience, and it inherits every rule
that implies: server-authoritative, transactional, and idempotent on an operation id.
A retried split that runs twice duplicates items, which is the exact dupe vector the
whole backend design exists to prevent.

Merging is the same problem inverted — it destroys an instance — and needs the same
treatment.

### Open questions

- Container capacities: how many stacks for a player, a corpse, a world crate?
- ~~Do corpse containers expire?~~ **Answered: they expire at session end**, which
  is also when insurance resolves. See "Custody as insurance".
- ~~Stacking rules?~~ **Answered above.**
- BR mode shares the code with persistence disabled; is looting otherwise
  identical, or is the grid too slow for 100-player pacing?
- ~~Which database, per-server or central?~~ **Answered: central SQL Server**, per
  "Locked decisions" — required because players carry one stash to any server.
- ~~Which way does extraction fail on a crash mid-commit?~~ **Partially answered:**
  reconnection is allowed for client-side drops; the server-side commit direction is
  still open. See "Death, downed and abduction".

## The compound (base) — DESIGN ONLY, NOT IMPLEMENTED

> A UNSC-styled safe house. Halo naming and art are permitted here under the
> non-commercial terms in "Locked decisions" — provided nothing is ever sold.

The persistent player base: where the stash lives, where raids are launched from,
and where any upgrade systems will sit. It is a **third space**, not a third game
mode — `SessionConfig` switches between co-op and BR, and the compound is neither.

**Decision: solo, no Fusion session.** No `NetworkRunner`, no replication, no
peer-to-peer anything. Nobody else is present in the compound.

**It is not, however, an offline space.** Base modules produce over time while the
player is away (see below), and production is per-account persistent state. That
makes the compound *presentation-local but state-remote*: rendering, movement and
doors run on the client; everything that yields value is held and computed by the
backend.

**Decision: separate surface and underground scenes**, joined by an explicit
transition (lift or door) with a load. Underground gets its own lighting rig and
HDRP volumes rather than fighting the surface's, and neither level pays memory for
the other.

**Decision: per-account persistent base.** Layout, module levels and production
state are stored per account, server-side, alongside the stash.

### Nothing in the compound is client-authoritative

Solo does **not** mean trusted. If the client writes to the database directly it
can grant itself anything, which is the same dupe vector as a client-authoritative
inventory wearing a different hat.

- The client never holds database credentials.
- Stash, module and production mutations go through an authenticated backend
  service that validates every request against the account's actual state.
- "The player is alone" is not a security property. It only means there is nobody
  to *observe* the cheating.

Movement, camera and doors can be fully client-side. The boundary is the stash,
the modules, and anything that converts into gear.

### Offline production

Modules continue producing while the player is in a raid or logged out. **A client
that controls its own clock controls its own output**, so none of this can be
client-side, and the client's clock is never an input.

**Do not simulate it.** A per-player server tick for base production would cost
real CPU for every account, most of which are offline, to compute something that
is a pure function of elapsed time. Use **lazy accrual** instead:

- Server stores, per module: `RecipeId`, `StartedAtUtc`, `Level`, `CollectedAtUtc`.
- On the next authenticated request touching that module, the server computes
  `elapsed = serverNow - lastCollected`, derives the yield, applies the cap, and
  commits.
- Cost is zero while nobody is looking, and it scales to any number of accounts.

Two rules that must hold:

- **Server clock only.** Client-supplied timestamps are ignored entirely, not
  merely sanity-checked.
- **Every module has an output cap.** Without one, a long absence yields unbounded
  resources — the classic idle-game exploit, and here it converts directly into
  raid gear.

Collection is a transaction, on the same footing as extraction: compute, commit,
then report. Never report then commit.

**This makes the backend a hard dependency for base features.** With the service
unreachable there is no stash, no modules and no production — so the compound
cannot double as an offline mode. **On an unreachable backend at boot the client
shows a blocking error with a retry**, rather than entering a degraded compound.
Nothing stale is ever shown, and no system needs a half-working disabled state.

### Module roadmap

Build the minimal set first, then grow toward a full Tarkov-style hideout. The
mechanical **categories** matter more than the list, because each category implies
different storage and different exploit surface:

| Category | Behaviour | Needs |
|---|---|---|
| **Passive unlock** | Raises a limit (stash size, carry weight) | A level integer. No timers. |
| **On-demand craft** | Consumes inputs, returns output after a duration | Start/finish timestamps, input escrow |
| **Continuous producer** | Yields over time, unattended | Lazy accrual + a hard output cap |
| **Fabrication** | Long, expensive, produces a large deployable | All of the above, plus a delivery rule |

First pass: storage, workbench, medical, power. Power gating the others is a
cheap way to make upgrade order a real decision.

**Vehicles are the fabrication category** — a Pelican-class dropship and a
Warthog-class 4×4. Under the non-commercial terms these can use Halo names and
designs directly; the constraint is distribution, not naming.

**Decision: every vehicle fabricated at the base is both drivable and an
extraction method.** This is the expensive answer and it should be taken with its
cost visible, because it reverses the cheapest option available in this document.

What it commits the project to:

| Requirement | Why |
|---|---|
| Server-authoritative vehicle movement | Same rule as the character controller. A client-driven vehicle is a teleport exploit carrying gear |
| Passenger state and seat authority | Who is aboard, who drives, what happens when the driver leaves or dies |
| Vehicle-versus-lag-compensation | Hitboxes on a moving platform, and shooting from one. The rewind currently assumes players move under their own KCC |
| A second movement implementation | Vehicles are not SimpleKCC. This is the third movement model after the networked player and the compound's plain controller |
| Networked vehicles in BR | 100-player sessions with vehicles is a different bandwidth and AOI problem than 100 players on foot |

**Networked vehicles therefore exist in this project.** That was the single
question the document flagged as deciding it, and this answer settles it in the
expensive direction. Plan for vehicles as their own system with their own
milestone, not as an extension of fabrication — fabrication only decides how you
*acquire* one.

Being an extraction method as well as drivable is comparatively cheap once the
vehicle exists: extraction becomes "reach the vehicle, hold it for N seconds,
leave" rather than a separate scripted dropship.

The consequence to design early is loss. A drivable, losable vehicle carrying a
squad's gear is the largest single object of value in a raid, and the rules for
what happens when it is destroyed with gear aboard need to exist before anyone
builds one.

Fabrication raises questions the other categories do not:

- ~~Is the dropship a drivable vehicle or an extraction method?~~ **Answered:
  both.** See the decision above.
- Is a fabricated vehicle **persistent gear that can be lost** in a raid? If yes
  it obeys the same rules as everything else in the stash, and losing one must
  hurt proportionally to its cost. Still open, and now sharper: a drivable vehicle
  can be destroyed with a squad's gear inside it.

### Consequences worth planning for

**The networked player does not work here.** `NetworkPlayer`, `PlayerHealth`,
`PlayerCamera` and `NetworkWeapon` are all `NetworkBehaviour`s and require a
runner. SimpleKCC is a Fusion addon and is equally unavailable. The compound needs
its own plain-MonoBehaviour controller.

That means **two movement implementations**, and they will drift in feel unless
tuning values live in shared assets rather than being typed into two inspectors.
This is the main hidden cost of the solo-local decision and it is worth accepting
knowingly.

**Shut the runner down before entering the compound.** `Bootstrap` persists the
`NetworkRunner` deliberately (see "Two scenes"), so leaving a raid must call
`Shutdown()` rather than just loading a scene, or a dead runner survives into the
base.

**Build indices are load-bearing.** `NetworkLauncher._gameplaySceneIndex` defaults
to `1`, matching `DropTest` at index 1 in `EditorBuildSettings`. **Append** the
compound scenes; inserting them ahead of `DropTest` silently repoints the raid
loader at the wrong scene. `TestSceneBuilder` also writes the build settings order,
so it must learn about the new scenes or it will drop them on the next rebuild.

### Base defence — DESIGN ONLY

Raiders attack the compound; the owner and invited squadmates defend, earning exp
and resources. This is the one part of the base that is multiplayer.

**It is a raid variant, not a multiplayer compound.** The distinction carries the
whole design:

| | Compound | Base defence |
|---|---|---|
| Fusion session | None | Yes — server-authoritative, like any raid |
| Other players | Never | Owner + invited squad |
| Stash access | Owner only | **None, for anyone** |
| Persistence | Live, every mutation | Committed once, at resolution |

Defence reuses `NetworkPlayer`, `NetworkWeapon`, lag compensation and
`SessionConfig` unchanged — it is a third `SessionConfig` mode alongside co-op and
BR, not a second netcode stack. The compound keeps its no-session guarantee
because defence is a *separate instance* that happens to use your base as its map.
Squadmates are invited into that session through the lobby; they never enter your
persistent base.

**The stash is closed during defence, to everyone including the owner.** Friends
standing in the room where your gear lives is otherwise a theft vector. No stash
UI, no container access, no exceptions; item transfer between players requires an
explicit server-validated trade.

**The map is built from the owner's real base state.** The defence server reads
module levels and layout from SQL Server and assembles the map from them, so
upgrades visibly shape the battlefield. Consequences: map assembly is
server-authoritative (a client-supplied layout would let players build an
unlosable base), and the layout must be validated on load, not trusted.

**Defeat leads to capture, not a game-over.** A lost defence drags the player to
an enemy base to escape from at an opportune moment — turning failure into a
scenario rather than a lockout. Losses on defeat: accrued producer resources, and
modules damaged or destroyed.

**Farming is the open problem.** Rewards must be computed server-side from the
session's actual outcome and committed through the same transactional path as
extraction — never reported by the client. But note that capture-and-escape is a
*consequence*, not a limiter: it costs time and adds drama without capping income,
so a player who reliably wins can farm defences indefinitely and never raid. A
real limiter is still needed — attempt cooldown, diminishing returns, or attacks
being offered rather than requested. Module loss on defeat also risks a death
spiral for weaker players; the two need balancing together.

### Boot and raid launch flow

The compound **replaces the main menu** — the game boots straight into the base.
There is no separate title screen; the map table is the menu.

```
boot → auth + fetch account state → Compound_Surface ⇄ Compound_Underground
                                         │ (map table)
                                         ▼
                                   Lobby / squad
                                         │
                                         ▼
                              Bootstrap → raid session
                                         │ (extract or die)
                                         ▼
                                   Compound_Surface
```

The three launch options are one chain, not alternatives: you boot into the
compound, a map table inside it starts the flow, and a lobby scene handles squad
assembly, loadout and matchmaking before `Bootstrap` starts the Fusion session.
The lobby is a separate scene because squad UI does not want to be rendered on top
of a fully-lit HDRP compound.

**Squads are assembled in the lobby only.** The compound stays strictly
single-player, which preserves the no-session decision above. Letting friends walk
around your base would reverse it — that needs a Fusion session, replication and
server authority over everything visible in the base, and it is the single most
expensive change anyone could request to this design.

Two consequences of booting into the base:

- **Cold start now depends on the backend.** Auth and the account-state fetch sit
  in front of the first playable frame; an unreachable service shows a blocking
  error with retry, never a hang on a black screen.
- **The compound loads before anything else**, so its load time *is* the game's
  startup time. That is a real budget for a fully-lit HDRP scene, and an argument
  for keeping the surface scene modest and pushing detail underground.

### Open questions

- ~~Is a fabricated vehicle losable in a raid?~~ **Answered: yes, and destroying
  one spills its cargo into a container** rather than deleting it. Consistent with
  corpses — value that existed in the raid stays in the raid and can be contested.
  It also makes destroying a loaded vehicle a *reward* for the attacker rather than
  pure denial, which is what stops vehicle-hunting from being griefing.
  Consequences: a destroyed vehicle needs a wreck container with a capacity at
  least as large as its hold, and the same expiry question corpses have.

- Balance: what are the output caps per continuous producer? Uncapped is an
  exploit, and the cap is what makes absence-length irrelevant.
- Does the lobby matchmake strangers, or is it squad-and-solo only? Matchmaking
  implies a queue service beyond the account backend.
- Auth: which identity provider, and does it gate at boot or at first backend
  call? It gates the first playable frame either way.
- Does the lobby support joining a friend's squad, and does that change the
  compound at all, or only the lobby?

## Death, downed and abduction — DOWNED VERIFIED, ABDUCTION DESIGN ONLY

Three player end-states, not two:

| State | Meaning | Gear | Status |
|---|---|---|---|
| **Downed** | Incapacitated, bleeding out, revivable by a squadmate | Retained | **Implemented** |
| **Dead** | Bled out or killed outright | Dropped to a corpse container | Implemented |
| **Abducted** | Captured alive, taken to an enemy base | **Confiscated**, held until escape | Design only |

### Downed state

Lives in `PlayerHealth`. Gated per mode by `SessionConfig.EnableDownedState`;
with it off, lethal damage kills outright exactly as before.

**One pool covers both bleeding out and being finished off.** On being downed,
`Health` is replaced by `_downedPool`, which drains on a timer *and* takes damage
directly with no shield. Time and bullets therefore share one number, so being shot
on the ground simply brings forward the end the timer was already heading for. Two
separate quantities would need a rule for how they interact, and that rule would be
arbitrary.

**Downed players are alive, and are still targets.** `IsAlive` stays true — a
downed player can be damaged and finished. Acting is a separate question, so
`CanAct` (`!IsDead && !IsDowned`) gates it, and `NetworkWeapon` checks that rather
than `IsAlive`. Conflating the two makes a downed player either invulnerable or
still able to fight; both are wrong.

| Behaviour while downed | |
|---|---|
| Movement | Crawl only, `_downedSpeed`. No jump, sprint or crouch |
| Weapons | Blocked via `CanAct` |
| Gear | Retained — that is what makes a revive worth attempting |
| AI attention | Still targetable, but `_downedTargetPenalty` virtual metres are added to the distance, so any standing player wins |

That AI rule is deliberate in both directions. Ignoring downed players outright
would make the floor a safe place to sit; preferring them would delete revives.

**Revive is input-driven, not an RPC.** A squadmate holds `InputButton.Interact`
within `_reviveRange`; the reviver's own `FixedUpdateNetwork` finds the nearest
downed teammate and advances their `ReviveProgress`. Input is already tick-aligned
and server-validated, so an RPC would be a second and weaker path to the same state
change. Range is checked against the server's transforms, never a client-reported
position — the same rule container access follows.

`ReviveProgress` decays when nobody is contributing. That decay is anchored to
`Runner.Tick` rather than a per-tick reset flag, because every player's
`FixedUpdateNetwork` runs in one pass and their relative order is not guaranteed; a
reset flag would work or not depending on that order.

**Going down does not notify the session.** `AlivePlayerCount` is untouched by
`EnterDowned`, because a downed player is revivable and counting them as lost would
wipe a squad that is about to stand back up. Only `Die` notifies. If everyone is
downed with nobody left to revive, they each bleed out and the wipe follows
naturally.

Revives restore `_reviveHealthFraction` of max health and no shield, with the
regen delay restarted, so standing up is a vulnerable moment rather than a reset
button.

**Self-revive is off by default** (`_allowSelfRevive`). It turns downed into a soft
speed bump for solo players and removes the squad dependency the state exists to
create. It is a field rather than a deletion because solo co-op may want it.

Disconnecting while downed drops gear through the normal leave path, since gear is
retained rather than escrowed — combat-logging while down is not a way to keep it.

### Abduction has three entry points, one flow

1. Losing a base defence.
2. Dying in a raid (branch, not every death).
3. Being downed and not revived.

They converge on a single capture routine. Three separate implementations would
drift, and the gear rules are the part that must not drift.

**Downed was therefore a prerequisite** and now exists: entry point 3 is reachable
as "`ReviveProgress` never completed and the pool hit zero" — the branch point is
`PlayerHealth.Die` when called from `TickBleedOut` rather than from damage.

**Decision: only bleeding out abducts.** Being killed outright always kills.
Chosen over a flat per-death chance, which the open questions flagged as
exploitable — if escaping is ever profitable, a flat chance can be farmed by
suiciding. Bleeding out cannot be farmed the same way, because it costs real time
and requires that no squadmate reached you.

It also gives the downed state a second job. Revive already mattered; now failing
to revive has a distinct consequence from dying, which is what makes the two
states worth separating in the first place.

The branch point is precise and already exists in code: `Die` reached from
`TickBleedOut` is the abduction path, `Die` reached from `ApplyDamage` is death.

**Decision: the AI faction holds confiscated gear, not a player.** Recoverable only
by escaping; on failure to escape it is destroyed.

Rejected: awarding it to the killing player. That creates a direct incentive to
hunt players *for* their custody gear, and a griefing surface where a squad farms
abductions against weaker players — the same "farming is the open problem" concern
raised for base defence, but with a sharper edge because the reward is another
player's actual loadout.

Because no player profits, there is no farming incentive on the captor's side at
all, and the confiscation stays a pure scenario for the victim. The cost is that
abduction is asymmetric: it is a loss for one player and a neutral event for
everyone else. That is accepted deliberately — the alternative buys symmetry with
an exploit.

### Friendly fire is on in co-op

**Decision: squadmates can damage and kill each other in co-op.** Expressed as
`SessionConfig.EnableFriendlyFire`, not inferred from team ids — the previous
team-based rule only engaged once teams were assigned, so it did nothing at all in
every scene that leaves them `Unassigned`, which is all of them.

It also makes the downed state reachable without an enemy present, which is why the
first successful downed-state playtest worked at all.

**It interacts with three systems. One is accepted, two are now decided:**

**Decision: team-killing is accepted.** Not designed against, not punished. The
reason this is defensible — and it is — is the matchmaking rule: **co-op is
squad-and-solo only, with no stranger matchmaking.** Team-killing among people who
chose to play together is a social problem with a social fix, not an exploit. The
earlier concern (a stranger kills you, loots your corpse, and insurance will not
return gear another player took) required strangers to be in your squad, and they
are not.

That protection is load-bearing and worth stating as a dependency: **if co-op ever
opens to stranger matchmaking, accepted team-killing becomes an unpriced exploit
immediately.** Those two decisions must move together.

**Decision: strangers can team-kill in base defence and battle royale.** Both are
adversarial by nature, so it is not a special case there.

**Decision: a teammate can trigger your abduction** by downing you and letting the
timer run. Consistent with accepting team-killing — the squad can do anything to
itself.

Still coupled: `DriveRevive` treats `Unassigned` as *same squad* and revives freely,
so teams govern revive while a flag governs damage. Those two should not drift.

### Gear custody is the hard part

Confiscated gear is taken, held, and recoverable by escaping. That means gear must
**survive between sessions** — captured in one, recovered in another, with the
raid session long gone in between.

**This makes the backend a hard dependency for abduction, earlier than for
extraction.** Confiscated items live in a per-account server-side container in SQL
Server, not in any session's memory. The rules:

- Confiscation is a transaction: items leave the player's raid inventory and land
  in the account's custody record, in one commit. A crash mid-commit must not
  leave gear in both places, which would be duplication, or neither, which would
  be deletion.
- Custody is authoritative. The escape session loads it from the backend; it is
  never carried in the client.
- Escaping commits the reverse transaction. Failing to escape releases custody to
  the captor or destroys it — decide which, but it must be explicit.
- An account with gear in custody is in a **persistent state across logins**. The
  compound must show it, and a player must not be able to abandon custody by
  quitting.

Reuse `LootContainer` for the enemy base's stash: it already keeps contents
server-side, so a captive cannot read what is inside before reaching it.

### Custody as insurance — modelled on Escape from Tarkov — DESIGN ONLY

Direction: custody recovery should follow **Tarkov's insurance model**. Recorded
here with the parts that map cleanly separated from the parts that conflict with
decisions already locked, because grafting it on wholesale would silently undo two
of them.

*(Tarkov's exact premiums and return windows have changed repeatedly across
versions. What follows takes the mechanics, not the numbers.)*

**The model, as Tarkov runs it:**

1. Insurance is **opt-in and pre-paid**, per item, before the raid.
2. On death, insured items are **not returned immediately**.
3. An item comes back **only if no other player took it.** Anything looted by
   someone else is gone forever.
4. Return is **delayed by real time**, then delivered to a claim inbox with an
   expiry.
5. The premium is spent either way. Surviving with your gear means you simply paid
   for nothing.

**Point 3 is the load-bearing one.** It is what stops insurance from cancelling the
loss loop: the gear only returns when nobody contested it. Take that away and
insurance becomes a free respawn of your kit.

#### What maps cleanly onto what already exists

| Tarkov mechanic | Fits here because |
|---|---|
| Return only if unlooted | Death already drops everything into a **corpse container** that other players can loot. Whether an instance was claimed is *already knowable* at raid resolution — no new systems needed |
| Real-time delayed return | The compound already computes **offline production lazily from timestamps** rather than ticking. A return timer is the same pattern: store an available-at time, resolve on read. No scheduler, no background job |
| Premium as a sink | Base modules already produce resources. That is the natural premium currency — and it gives producers a purpose beyond upgrades |
| Claim inbox with expiry | Fits the compound as a physical place you must return to, reinforcing it as the loop's hub |

#### What conflicts with locked decisions

- **Per-item insurance breaks "one custody slot per account."** That invariant was
  chosen deliberately to bound the schema and to remove every question about which
  batch a recovery applies to. Tarkov-style insurance is inherently *many
  outstanding claims at once, across several raids*. These cannot both be true.
  Either the invariant goes and custody becomes a collection, or insurance is
  restricted to one batch at a time and stops resembling Tarkov.
- **A delayed timer replaces the escape session.** The current design recovers
  custody by *playing an escape*, and destroys the gear on failure. Tarkov's model
  recovers by *waiting*. A timer is much cheaper to build, but the escape session is
  a designed feature with real identity, and a timer deletes it.
- **There is no currency.** Tarkov premiums assume traders and roubles. This project
  has neither. Base-produced resources are the only candidate.

#### Recommendation: two systems, not one

The cleanest resolution is that **insurance and custody cover different losses**
rather than one replacing the other:

- **Ordinary death → insurance.** Today this has *no* recovery path at all: you die,
  your gear goes to a corpse container, and it is gone. This is exactly the hole
  Tarkov insurance fills, and the corpse container already provides the "was it
  looted" signal for free.
- **Abduction → custody and escape, unchanged.** It stays the rarer, dramatic loss
  with an active recovery, keeps its one-slot invariant, and keeps the escape
  session that was designed for it.

This takes everything valuable from Tarkov's model, needs no invariant broken, and
leaves the abduction identity intact. If instead custody itself is to *become*
insurance, then the one-slot decision must be reopened explicitly and the escape
session retired — that is a coherent choice too, but it is a bigger one than it
looks.

#### Decisions

**Decision: one insurance kit per life.** Not per item. A single flat kit covers the
loadout, and a player may hold only one at a time.

This resolves the conflict above without breaking anything. One kit means **at most
one outstanding claim**, which is the same shape as the existing single custody
record — so `Account` keeps its one-slot invariant, "which batch does this recovery
apply to" stays permanently answered, and the schema does not grow into a
collection. It also removes the per-item premium UI entirely.

The scarcity is the mechanic. Because you hold one kit and one only, the interesting
decision moves from *what do I insure* to **when do I spend it** — which is a better
question and costs far less to build.

**Decision: insurance does not survive a wipe.** A kit in flight, and any gear
sitting in the return queue at the moment of rebirth, is destroyed with the stash.
Nothing crosses that line except the augmentation and the inscription. This closes
the smuggling hole: a player cannot park a loadout in the return queue to carry it
through a wipe that is supposed to take everything.

**Consequence: the wipe transaction must clear the insurance record too.** Alongside
stash, character and custody. If the rebirth commit misses it, a claim resolves
after the wipe and delivers a dead character's gear to a fresh one — which is
precisely the exploit the decision forbids.

#### What a kit is

**Decision: a kit is everything the player wears or uses — not consumables.**
Armour, helmet, weapons and tools are covered. Grenades, medical items and
ammunition are not, and are always lost.

This gives consumables a permanent sink, which the economy needs: insurance restores
your ability to fight but never your ability to *sustain* a fight, so every raid
still costs something no matter how it ends. It is also the right side of the line
to be generous on — losing a rifle you spent a week earning is the loss that makes
players quit; losing the grenades is an errand.

**This needs a field that does not exist yet.** `ItemDef`
(`Assets/Scripts/Core/Items/ItemDef.cs`) currently carries only id, display name,
grid footprint, max stack and world prefab. There is no category, type or slot
concept anywhere in the item model.

**And the classification has to reach the backend.** Per `ItemDef`'s own rule, only
`ItemId` crosses a network or database boundary and the server resolves everything
through its own catalogue. The backend today has no catalogue at all — `StoredItem`
is `(InstanceId, ItemId, Count)` and nothing more. If insurance is resolved
server-side, and it must be, then **the backend needs its own kit/consumable
table keyed by `ItemId`**. That is new work in `Dropshock.Backend`, not just a
Unity-side authoring field.

> **Trap:** `IsStackable` correlates with consumables today — ammo, meds and grenades
> stack; weapons and armour do not. It is tempting to use it as the proxy and skip
> the new field. Do not. The first stackable tool or non-stacking consumable turns a
> data question into a silent insurance bug, and the failure mode is items quietly
> not returning.

Two boundary cases the rule does not yet settle:

- **Ammunition loaded in a returned weapon.** The weapon is kit, the rounds inside
  are consumable. Does the rifle come back empty? Tarkov's answer is yes, and empty
  is the consistent reading here — but it must be stated, because "my gun came back
  without my ammo" is a support question either way.
- **Attachments and mods**, if they are ever separable items. They are worn
  equipment and should follow the weapon, but only if the design keeps them attached
  rather than treating them as loose inventory items.

#### Custody supersedes insurance

**Decision: abduction voids a pending insurance claim.** There is one recovery slot
per account and custody takes it. Gear already in the return queue when the player
is abducted is destroyed, not moved into custody.

This keeps the single-slot schema and gives abduction extra teeth. It also has a
consequence worth naming before it turns up in balance testing: **one abduction now
costs the player twice** — the loadout being confiscated *and* a loadout that was
already on its way back. That stacks directly onto the existing unanswered question
of whether abduction is too harsh, and onto the flagged risk of squads farming
abductions against weaker players. The mechanic is coherent; it is the severity that
is now compounding across three systems that were never balanced against each other.

#### AI counts as a looter

**Decision: if AI takes an item, it does not come back.** An NPC looting a corpse
container consumes the insurance exactly as a rival player does.

**This fixes the co-op hole.** The open concern was that PvE co-op has no looting
rivals, so everything insured would always return and the mechanic would degrade to
a pure delay with no risk. Looting AI restores that risk without a separate co-op
rule — the same system now has teeth in both modes.

Two consequences:

- **Enemies cannot loot today.** `EnemyBrain` acquires targets and fires; there is no
  looting behaviour anywhere in the AI. This decision makes corpse-looting AI a
  *prerequisite of the insurance system*, not an ambience feature. Insurance cannot
  ship before it, or every insured kit returns unconditionally.
- **AI loot chance becomes the master dial for the whole mechanic.** One number sets
  the global return rate in both modes. That is convenient — one knob instead of
  per-item tuning — but it means an AI behaviour tweak silently reprices insurance.
  The two must be tuned together, and the coupling needs documenting where the
  number lives, because nothing about editing an AI value announces that it changed
  an economy.

#### Resolved

**A returned weapon comes back empty.** The weapon is kit; the rounds in it are
consumable. No loaded magazine survives.

**"Per life" means one kit active at a time, rebuyable after each death.** Not one
per character lifetime. This is the conventional reading and the routine mechanic:
buy a kit, lose it or spend it, buy another. The single-slot schema is unaffected —
one active kit is still one outstanding record.

**Abduction loot is visible only to the abducted player or their squad.** Confiscated
gear cannot be seen or taken by anyone outside the group it was taken from. This
enforces the existing rule that no player profits from an abduction, and it means
the escape session's loot visibility is *scoped by ownership*, not by proximity —
which is a server-side filter on what the session replicates, not a UI decision.

#### Resolved

**Return delay scales with what was lost.** A bigger loss waits longer. This is the
right direction — it prices the mechanic against value rather than flat time, and it
stops insurance being strictly better the more expensive your kit was.

**A wipe forfeits gear in custody.** Consistent with taking the stash. The rebirth
transaction and `ForfeitCustody` must agree in one commit, or the record is orphaned
and the one-slot invariant breaks for the reborn character.

**Attachments and mods follow the weapon** — provided they were not removed or
tampered with. Implication: the returned item is the weapon *as configured when
lost*, so its mod state has to be stored with the insured record, not reconstructed
from a bare `ItemId`. `StoredItem` is `(InstanceId, ItemId, Count)` today and cannot
express a configured weapon. This is the second reason the item model needs to grow
(the first being kit/consumable classification) and they should be designed together.

**Corpse containers expire at the end of the session.** Clean, and it does more work
than it looks: **session end becomes the moment insurance resolves.** Whether an
insured instance was taken is decidable exactly then — nothing has to be tracked
across sessions, and there is no window where a corpse lingers and the answer is
still pending. Build the insurance resolution into session teardown.

#### Still open

- Nothing outstanding on the insurance rules themselves. The remaining dependencies
  are elsewhere: corpse-looting AI must exist (see "AI counts as a looter"), and the
  item model must carry both kit classification and mod state.

### The escape is a session

An instanced enemy base, loaded as its own Fusion session, reusing the raid
netcode. It is a fourth `SessionConfig` mode. Squadmates joining a rescue becomes
possible for free, since the session already supports multiple players — worth
designing the map for even if rescue ships later.

### Open questions

- ~~Which raid deaths abduct rather than kill?~~ **Answered: only bleeding out.**
  See the decision above.
- ~~What does the captor gain?~~ **Answered: nothing.** The AI faction holds
  confiscated gear; no player profits from an abduction.
- ~~Bleed-out duration, and can a solo player self-revive at all?~~ **Answered
  provisionally in code:** `_bleedOutSeconds` defaults to 45 and self-revive is
  off. Both are serialised fields, so they are tuning decisions rather than locked
  ones — and neither number has been playtested.
- ~~Can a player be abducted while already holding gear in custody from a previous
  abduction?~~ **Answered: no. One custody slot per account.** A player who already
  has gear in custody cannot be abducted again — they simply die, and that death
  drops gear normally. This bounds the schema to a single custody record per
  account rather than an unbounded collection, and removes a class of questions
  about which batch an escape recovers. The cost is a small oddity: holding custody
  gear makes you temporarily immune to abduction, which is a mild incentive to
  leave it unrecovered. Watch for that becoming a strategy.
- ~~Is confiscated gear destroyed on a failed escape too harsh?~~ **Answered: no.
  It stays.** Destruction on a failed escape is the intended severity, even alongside
  module loss on a failed base defence and the doubled cost of abduction voiding an
  insurance claim.
- **Reconnection is allowed, Tarkov-style.** A player who drops out can rejoin a
  session in progress. This covers the *client* crash case.

  It does **not** answer the server-side question, which is worth restating clearly
  because the two are easily conflated: if the backend crashes **partway through the
  extraction commit** — gear removed from the session, not yet written to the stash
  — reconnection cannot help, because the session is gone. The transaction either
  rolls back (player keeps the gear, and a crash becomes a dupe opportunity if the
  session state also survived) or commits forward (player loses the gear to a crash
  that was not their fault). `SqlAccountStore` already does its work in one
  transaction with the account row locked, which makes rollback the natural default —
  but the design should say so explicitly rather than inherit it.


### Skills — DESIGN ONLY, NOT IMPLEMENTED

**Decision: the skill system follows Escape from Tarkov's — the full breadth of it.**
Physical skills (strength, endurance, vitality, stress resistance, metabolism),
practical skills (search, attention, surgery, throwing, covert movement), and
per-weapon-class combat proficiency. Tarkov's defining property is that skills level
**by use**, not by spending points: you get stronger by carrying weight, quieter by
moving quietly, better with a weapon class by using it.

Use-based progression is the right fit here, because it makes the "highest skills"
half of the win condition something you arrive at by playing rather than by
allocating.

**Three consequences that need planning rather than discovering:**

- **This is the largest single system in the design.** Tarkov's skill list runs to
  dozens of entries, each with its own accrual rule, and each rule is a hook into a
  different part of gameplay — movement, inventory, healing, recoil, looting. It is
  not a subsystem that can be added at the end. Recommend picking a **small subset
  for alpha** (one physical, one practical, one weapon class) to prove the accrual
  and persistence path, then widening. The architecture matters more than the count.
- **Skill accrual must be server-authoritative, and it is a firehose.** Every point
  of progress derives from actions the server already simulates, so the data is
  there — but "client reports its own skill XP" is the same class of vulnerability
  as a client-authoritative inventory. Equally, committing every increment to SQL is
  not viable. Accrue in the session, commit once at session end, through the same
  transactional path as extraction.
- **Skills are character state, so a wipe destroys them.** Both wipes. That is
  consistent, and it is also the single largest thing a rebirth costs — far more
  than gear, because gear can be rebought and skill time cannot.

**Scope warning worth stating once:** maxing a Tarkov-breadth skill list is a
multi-thousand-hour proposition in the game it comes from, which had a full-time
studio and a large population. Making "all skills at maximum" half of the gate to
the endgame may put the endgame permanently out of reach for a non-commercial
project's player base. **"Highest skills" needs a definition that is reachable** —
a threshold rather than a cap, or a defined subset, or elite level in a chosen
specialisation. This is a tuning decision, but it decides whether anyone ever sees
the rebirth system at all.


## Auth — DECIDED FOR ALPHA ONLY, NOT IMPLEMENTED

**Decision: a shared secret for alpha.** The real identity provider is deliberately
deferred. Gating happens at boot, because the compound replaces the main menu and
the first frame already needs account state.

**A shared secret authenticates the build, not the player.** This is the limitation
to be clear-eyed about, because it is easy to mistake for a login:

- Every client carries the same secret. Anyone who extracts it from a build can talk
  to the backend as though they were the game.
- It does **not** stop one tester naming another tester's `accountId`. Account
  identity remains self-asserted; the secret only decides who may ask at all.
- It is therefore a bouncer, not a passport — adequate for a closed alpha where the
  testers are known and trusted, and **not adequate for any public build.**

If per-account isolation is wanted during alpha, the cheapest upgrade is a
per-account token issued out of band rather than one secret shared by everyone. That
is a small change now and a large one later.

**Handling rules, following the convention already set by `DROPSHOCK_SQL`:**

- The secret comes from an environment variable or untracked local config. **It is
  never committed, never logged, and never written to disk by the game.**
- The backend rejects requests without it rather than falling back to open access. A
  misconfigured server must fail closed, or the alpha's only protection silently
  disappears exactly when someone forgets to set the variable.
- The client build must not ship it in plaintext in a public artefact. For a closed
  alpha distributed directly this is acceptable; the moment builds go wider it is
  not.

**This is a tripwire, not a task.** Replacing it is a release blocker for anything
public, and it should be treated as one rather than as a nice-to-have that survives
into beta by inertia.

## Re-equipping after death — DESIGN ONLY, NOT IMPLEMENTED

Death returns the player to the hideout. From there, four routes back to a loadout:

1. **Buy from the flea market** (player-to-player).
2. **Buy from traders** (NPC vendors).
3. **Take gear from the stash.**
4. **Fight a wave of enemies at the hideout for starter loot** — the free option,
   but losing risks the base and a restart.

Routes 3 and 4 exist today in some form. **Routes 1 and 2 do not, and they are much
larger than they look.**

### The economy this assumes does not exist yet

Traders and a flea market require a currency, a pricing model and a persistent
market. The design currently has none of these — the only economic objects in it are
base-produced resources and items themselves. Adding them brings in:

- **A currency**, with sources and sinks that balance, or the market inflates until
  prices are meaningless.
- **Server-authoritative trading.** Every listing, bid and settlement is a stash
  mutation and must go through the same transactional, idempotent path as extraction
  and custody. A flea market is the single richest duping target in any extraction
  shooter, because it is the only system that lets an item change owner without
  either party dying.
- **Order matching and persistence**, which is a backend subsystem of its own —
  bigger than `StashService` as it currently stands.

**Liquidity is the risk nobody plans for.** A player-to-player market needs a
population to function. This is a non-commercial fan project; if concurrency is
measured in dozens, the flea market is an empty room and players will conclude the
economy is broken. **Traders are the safer of the two and should come first** — they
work at any population, they set reference prices the market can later anchor to,
and they are a fraction of the work. Ship traders, then decide whether the flea
market is warranted by the actual player count.

### The economy: traders now, player market maybe

**Decision: traders only, in the alpha and beta builds. A player-to-player economy
is a possible future, not a commitment.**

This is the right sequencing for the reason above — traders work at any population,
they set the reference prices a market would later anchor to, and they are a
fraction of the work. Nothing about shipping traders first forecloses a flea market;
the reverse is not true.

Two things to get right *now* so the option stays open later:

- **Price everything through a server-side catalogue from the start.** If trader
  prices live on the client or in `ItemDef`, a market added later has no authority
  to anchor to and every price becomes a client claim.
- **Make currency a first-class account field with its own transactional path**,
  the same as stash mutations. Retro-fitting server-authoritative money into a
  system that treated it as a number on a character is how duping gets in.

Deferring the flea market also defers its worst property: it is the only system in
the design that moves an item between owners without either party dying, and so the
richest duping target in the whole game. It is a much safer thing to build against
a live trader economy that already works.

### Route 4: gated stake, three approaches

**Decision: the stake is opt-in, and there are tiers.** The player chooses what is
at risk rather than being taxed for being poor. Three approaches, each with its own
trade-offs:

| Approach | Intent |
|---|---|
| **Hide** | Avoid the wave. Survive on the least, take the least. |
| **Fight** | Engage for the best loot, at the highest exposure. |
| **Run** | Break out rather than hold. Trades the position for survival. |

This is a good fit for the game — it is the ODST fantasy in miniature, and it is
three *approaches* to one encounter rather than three difficulty sliders, which
makes it a design problem rather than a numbers problem.

**Decision: a loss in Fight or Run requires creating a new character. Hide cannot
cost the character.**

Hide is the floor: it costs time and yields scraps, but it can never end a run. That
makes the tiers pure opt-in risk — the design taxes ambition, not poverty, and a
player with nothing always has a way back that cannot make things worse.

This *strengthens* Fight and Run rather than softening them. They are now chosen
**over** a safe option, and a risk taken when a safe alternative existed means
something that a risk taken under compulsion never can.

### Forced restart: back to the start, and nothing else

**Decision: a forced restart returns the player to the beginning of the game.** New
character, from the top.

**Decision: a forced restart clears the stash and the base as well.** It is total —
character, stash, base, everything. The only difference from a voluntary rebirth is
what you are given at the other end:

| | Rebirth (voluntary) | Restart (forced) |
|---|---|---|
| Trigger | Beat the game, choose the wipe | Lose a Fight or Run encounter |
| Stash and base | Cleared | Cleared |
| Result | New character, **augmented** | New character, **unaugmented** |
| Memorial inscription | **Granted** | **None** |

Making both wipes equally total is the right call, and not merely for symmetry: if a
forced restart left the stash intact it would be strictly *gentler* than the
voluntary one, and a player chasing the augmentation would be better off
deliberately losing than winning. That inverts the incentive the entire progression
system rests on.

The consequence is that the two paths now differ in **exactly one** thing — the
reward — while sharing all of their destruction. That makes the shared code path
genuinely tempting and the single distinguishing flag genuinely dangerous. Whatever
implements the wipe should take the reward as an explicit argument, so that granting
an augmentation is something a caller must actively ask for rather than something a
default can leak.

## Character progression and rebirth — DESIGN ONLY, NOT IMPLEMENTED

> Captured from a verbal design call, then **revised**: an earlier reading of this
> as a blended "one-third each" progression was wrong. It is a **prestige / rebirth
> loop**. The thirds language describes the *stages a character passes through*,
> not a final blend.

**The main game is ODST, and only ODST.** The player is an unaugmented human for
the entire ordinary lifetime of a character. Spartan status is **end-game content**,
not a track you climb during normal play.

### The loop

1. A character advances through **maturity** and **veterancy** — teen, to human, to
   veteran — as an ordinary unaugmented ODST.
2. **Beating the game** is defined by two conditions held simultaneously: the
   **highest tier of ODST gear** and the **highest skills**.
3. On meeting them, the player is offered a choice: **wipe the character**.
4. Accepting the wipe restarts them as a **teenager — selectable male or female —
   carrying one Spartan augmentation.** The augmentation is the reward for the
   wipe, and the only way to obtain one.

**Decision: the augmentation is earned by sacrifice, never granted.** It is not a
character-creation option and a first-time player does not have one. You get it by
voluntarily destroying a completed character. That inversion is the whole system:
Spartan power is priced in progress surrendered, not time served.

**Decision: character state is per-account and server-owned.** It survives death,
failed extraction and abduction. Same category as the stash and the compound —
never client-authoritative.

### Why this resolves the problem the earlier reading had

A linear teen-to-Spartan climb would have broken the extraction loop: veterans
strictly stronger than newcomers, best able to absorb a loss and least likely to
take one. **Rebirth avoids that entirely.** The route to Spartan power runs *through
giving everything up*, so the strongest players are the ones who just reset to
nothing. It converts what would have been power creep into a voluntary difficulty
and status choice, which is a far healthier fit for an extraction shooter.

It also repairs the naming. The main game stays honestly ODST — unaugmented,
outgunned, which is the archetype's entire point — and Spartan status reads as the
rare thing beyond it rather than a contradiction of the title.

### The memorial: what survives the wipe

**Decision: the wipe is real and total. The character is destroyed, not retired.**
There is no archive, no second slot, no going back to play the old one. Gear,
maturity, veterancy, skills — gone.

**Decision: three things survive — the character's name, rank, and title
("call sign") — and they are inscribed into the concrete footprint of the new
base.** Not a menu, not a statistics screen: a physical, in-world artifact in the
compound, laid into the foundation the way names are set into wet concrete.

This is the strongest idea in the system, for a reason worth stating explicitly:
**it makes the sacrifice visible and permanent at almost no art cost.** The earlier
worry was that rebirth's payoff depended entirely on character art the project does
not have. It does not. The payoff is *text set into a surface* — a decal, a
generated texture, a mesh with a name on it. That is achievable now, on primitives,
in a way character models are not. Every wipe adds a line to the foundation the
player walks across every session.

It also gives the compound a job it did not have. It was a stash room and a
production timer. It becomes the record of everyone you used to be.

### Memorial: implementation shape

- **Append-only and immutable.** A retired-character record is written once at the
  moment of the wipe and never edited or deleted. That is a clean schema shape and
  a genuinely easy thing to make idempotent — the operation id guards a single
  insert.
- **It stores only name, rank and call sign.** Not gear, not stats. Keep it that
  way; the moment the memorial stores anything mechanical it stops being a memorial
  and becomes a save file.
- **It grows without bound, and it is rendered as an extending wall.** **Decision:**
  each wipe adds an inscription to a wall that extends. The wall is therefore
  *generated from the record list*, not authored geometry with fixed slots — the
  level design must assume it lengthens forever and give it somewhere to go. Pick
  the direction and the per-entry spacing before the compound art is built, because
  retrofitting a growing wall into a finished room is a level rebuild.

### The memorial is player-authored text that strangers will read

**Decision: call signs are filtered server-side.** This is a requirement, not a
polish item, and it is not hypothetical. Per "Base defence", **raiders and invited
squadmates enter the compound as a map.** They are standing on that foundation.
Whatever a player typed is rendered in world space in front of other people.

Validation and filtering run on the **server, at the point of entry, before the
record is ever written.** Filtering at render time is too late — the string is
already persisted and replicated, and the immutability that makes the memorial
elegant also makes bad entries permanent. An append-only immutable store and
unmoderated user text are a bad combination specifically because the design offers
no way to take anything back.



### Why this exists: Halo is Spartan-centric

The framing decision behind the whole system: **Halo is a Spartan-centric universe,
and a player who sticks with this game long enough should be able to become one.**
Spartan status is the founder's reward for endurance — not a purchase, not a tier
unlock, and not something the ordinary game hands you.

That is why the price is what it is. The augmentation cannot be cheap without
cheapening the thing it points at.

### The price is total, and what that forces

**Decision: the wipe takes everything, including the stash.** Character, gear,
maturity, veterancy, skills, stash. The augmentation and the inscription are the
only things that cross the line.

This is the harshest possible version, and it is coherent — the reward is priced in
the only currency the game has. But it forces two consequences that should be
decided deliberately rather than discovered:

**1. The augmentation cannot be a minor buff.** With the stash gone there is *no*
other carry-over. A player trades hundreds of hours of accumulated material for one
augmentation and a line on a wall. If that augmentation is a small stat nudge, the
exchange rate is bad, players will do the arithmetic, and the feature goes unused —
an end-game system nobody opts into is the same as no system. A total cost demands a
transformative effect.

**2. The exchange rate is the thing to tune, not the principle.** If full Spartan
status takes N wipes and each wipe costs a complete stash, the real price is N full
playthroughs. That may be exactly right for a long-tail game — but N should be
chosen on purpose. N=3 reads very differently from N=7.

**Open edge case: what happens to gear in custody?** Per "Death, downed and
abduction", an account can hold one `CustodyRecord` of confiscated gear. If a player
wipes while custody is occupied, that gear has no owner. The wipe should almost
certainly forfeit it — consistent with taking the stash — but `ForfeitCustody` and
the rebirth transaction need to agree explicitly, or the record is orphaned and the
"one custody slot per account" invariant quietly breaks for the reborn character.

### Where this state lives

Character state is account state, so it belongs in the backend alongside the stash,
and it inherits every rule from "The account backend":

- Stored on `Account` (`Backend/Dropshock.Backend/Model.cs`), not in the Fusion
  session, and not on the client.
- Every mutation goes through `StashService`-equivalent validation and is
  **idempotent on a caller-supplied operation id**. The rebirth operation is the
  single most dangerous write in the entire design — a retried or duplicated wipe
  that double-applies could destroy a completed character and grant nothing, or
  grant two augmentations for one wipe. It must be idempotent by construction, and
  it should be the first thing the concurrency tests cover.
- The client is told its own character state so it can render it. It never asserts
  it. A client claiming "I am a Spartan" must change nothing anyone else sees.

**The current schema cannot express this.** `Account` today holds one stash and one
custody record. Rebirth needs, at minimum, a character record with maturity,
veterancy, skills and augmentation count — and if old characters are retained (see
below), a *collection* of them plus a notion of which is active. That is a
`schema.sql` change landing in the SQL and in-memory stores together, or nowhere.

### The rebirth transaction is the hard part

Everything else here is bookkeeping. The wipe is not:

- It destroys a large amount of hard-won state in one operation.
- It must be all-or-nothing. A wipe that half-applies — gear gone, augmentation not
  granted — is unrecoverable and will end that player's relationship with the game.
- It is the one operation where a player will be *certain* the server got it wrong,
  so it needs an audit trail, not just a result.

`SqlAccountStore` already takes `UPDLOCK, HOLDLOCK` on the account row and does its
work in a single transaction, which is the right shape, and that shape is now
measured rather than assumed — see "Concurrency, measured" below. Retry is handled
by `SqlStashGateway`, so the rebirth transaction inherits it for free.

The remaining gap is specific to rebirth: it is the first operation that will touch
**more than one account-scoped entity in one transaction**, which is exactly the
lock-ordering inversion that produces a deadlock. Every operation today touches one
account, so the lock order is trivially consistent. When the wipe lands, fix the
order it takes locks in and write it down, because the retry will hide an
inconsistent order as latency rather than as a bug.

### Art cost, flagged early

Male and female teenage models, plus visually distinct maturity and veterancy
stages, plus an augmented end state, is a **character art pipeline** — not a shader
or a config change. As of this writing the project has zero models, zero textures
and zero materials.

Rebirth makes this worse than it first looks: the whole point of the loop is that
other players can *see* what you gave up. If a reborn Spartan-track teenager is
visually indistinguishable from a first-day player, the reward is invisible and the
sacrifice is unpaid. **The system's payoff is delivered almost entirely through
character art**, which is the thing the project has none of.

### What is not decided yet

- ~~Does the wipe destroy the old character, or is it archived?~~ **Answered: it is
  destroyed, totally and permanently.** No second slot, no archive. Only the name,
  rank and call sign survive, as an inscription in the new base's concrete footprint
  — see "The memorial". This also settles the earlier "play as your main character"
  ambiguity: the reborn character *becomes* the main, because there is no other.
- ~~Does the memorial confer anything mechanical?~~ **Answered: no. Nothing.** It is
  purely commemorative. If an inscription granted a bonus the memorial would become
  a progression ladder, and every reason to admire it would be replaced by a reason
  to farm it.
- ~~Does the wipe take the stash?~~ **Answered: yes. The reset is total.** Character,
  gear, maturity, veterancy, skills *and stash* — all destroyed. The augmentation
  and the inscription are the only things that cross the line. See "The price is
  total, and what that forces" for the consequences.
- **What are "skills"?** The win condition is defined partly as "highest skills",
  but no skill system exists in the design or the code. Nothing else in the document
  references one. This needs its own section before the win condition is testable.
- ~~Can rebirth be repeated?~~ **Answered: yes, and it has a defined terminus.**
  Rebirth repeats, augmentations accumulating each time, **until every Halo 3: ODST
  defined achievement is completed and all the gear those unlock is obtained.** At
  that point the player begins one final run as a **fully augmented Spartan from the
  start** — and plays it to become an **ODST / veteran** again.

  The loop ends where it began, which is the right shape for this game: the reward
  for exhausting Spartan progression is the chance to be an ODST by choice rather
  than by circumstance. It also retroactively explains the original "one-third"
  phrasing — the thirds are the accumulation, and full augmentation is the cap
  reached only at the end.

  **Two things this needs before it can be built:**
  - **The achievement list must be re-defined for this game.** Halo 3: ODST's
    achievements are written against that game's campaign missions, Firefight maps
    and audio logs. This game has none of those. The names and spirit can carry over
    under the same non-commercial terms as the rest of the Halo content, but every
    completion *criterion* has to be rewritten against systems that exist here —
    raids, extractions, base defence, the hideout waves. Until that mapping exists,
    "beat the game" has no testable definition.
  - **Achievement state is account state**, permanent and surviving every wipe —
    it is the one progression axis that must *not* reset, or the terminus is
    unreachable by construction. That makes it the third thing crossing the wipe
    boundary, alongside the augmentation and the memorial inscription.

- Are "maturity" and "veterancy" two separate currencies, or one clock and one
  earned track? Maturity implies time; veterancy implies deeds. If maturity is
  literally elapsed real time, it is farmable by not playing, which needs a rule.
- Does the augmentation grant *power* or *options*? Given rebirth already prices it
  fairly, straightforward power is defensible here in a way it would not have been
  under linear progression — but it still needs a number.
- How does an augmented player interact with `PlayerHealth`? Augmentation is the
  obvious place to modify health pool, shield behaviour, `_downedPool` or
  `_bleedOutSeconds` — all currently `SerializeField` tuning values on a prefab, not
  per-player state. Making them per-player is a real refactor.
- Do reborn and first-life players share matchmaking? Rebirth solves power creep for
  the *individual* but not for the lobby.
- Does the character model shown in the compound reflect progression? If yes, the
  compound needs character art before it needs anything else.

## Gotchas — things that fail silently or misreport

Every entry here cost real time. The unifying property is that **none of them
announced themselves**: the tool reported success, or nothing happened at all, or
the error named a symptom far from its cause.

### A long timer with no feedback looks exactly like a broken one

Bleed-out was reported as broken, investigated as a defect, and turned out to be
working perfectly: 45 seconds is long enough that watching it produces no evidence
it is running. The code was read line by line, prefab values were checked, and the
input path was traced — all before the trace showed a correct `2.222/s` drain.

The generalisation is a design one rather than a debugging one: **any mechanic whose
only observable is elapsed time will be reported as a bug until it has a visible
readout.** The downed HUD needs a bleed-out bar not for polish but so the system can
be told apart from a dead one. The same applies to insurance return delays, offline
production timers and base module production — every one of them is a long timer
whose failure mode is silence.

Corollary for instrumentation: the first version of the downed trace logged input
state every tick for every player, and buried the one interesting line under
thousands of idle ones. **A trace that fires unconditionally is a trace nobody
reads.** Log the exceptional case, or throttle.

### The rule that generalises

**Never read "no error" as "it worked."** Check the exit code, not the absence of
matching text. Verify a checker can fail before trusting it pass. Three separate
times in one session a tool reported success while doing nothing:

| What reported success | Why it was lying |
|---|---|
| `typecheck.sh` | `csc` was dying on missing `libicu` before parsing a file, emitting nothing that looked like a compiler error |
| A weaving check using `strings` | `strings` is not installed; every probe returned "absent" |
| The in-memory backend suite | Its fake store accepted states SQL Server rejects |

The fix in each case was the same shape: make the check prove it can fail, then
trust it. `Tools/typecheck.sh` now checks the exit code; the backend runners count
an unexpected throw as a failure rather than dying before the summary prints.

### Unity and Fusion

- **Fusion weaves an exact-match allowlist.** An assembly holding
  `NetworkBehaviour`s but missing from `AssembliesToWeave` is not woven, and every
  `[Networked]` property in it silently stops replicating. See "The weaver
  allowlist".
- **Lag compensation ships off**, and off means every server-side shot misses with
  no error. See "Hit registration".
- **`EnemySpawner` opens with `if (_enemyPrefab == null) return;`** — an unassigned
  prefab is a permanent no-op with no log line. `Validate Setup` now checks it.
- **Removing a package can leave a dangling reference in
  `ProjectSettings/EditorBuildSettings.asset`.** Its `m_configObjects` map survives
  package removal, and Unity's `ValidateRoslynAnalyzers` then walks the reference
  into a deleted folder and throws `DirectoryNotFoundException` naming an analyzer
  DLL. Checking the manifest, the lock file and project code is *not* sufficient —
  check `ProjectSettings/` too. Hit while removing `com.unity.ai.assistant`, whose
  transitive `com.unity.dt.app-ui` was the dangling entry.
- **Truncated `.cs.meta` files** report as `CS0234 namespace does not exist`. See
  "Known environment hazard".
- **Runner callbacks are inside the simulation**, dispatched from `InvokeTick`. Do
  not reason about `OnPlayerLeft` as "outside the tick".

### Diagnosis

- **Capture the error message before diagnosing.** The `StateReplicator` assert was
  investigated twice from the shape of project code, because nobody had recorded
  what it actually said. Both rounds built isolation harnesses against a subsystem
  the assert never touches. The message named a socket thread and a delivery-
  acknowledgement invariant, neither of which is reachable from gameplay code.
- **A fixed bug does not announce itself either.** Symptoms stop silently, so a
  closed defect can sit on a list indefinitely. Re-verify open bugs before working
  them.

### Backend and tooling

- **A test double must be exactly as strict as the real store.** The in-memory
  account store lacked the uniqueness constraint that `dbo.Item`'s primary key
  enforces, so it passed a suite SQL Server then failed. A permissive fake converts
  "untested" into "falsely confident", which is worse than having no fake.
- **Duplication is unstorable, not merely detectable.** `dbo.Item.InstanceId` is the
  primary key across stash *and* custody, so one instance cannot be in two places.
  The consequence: any new code path that persists items must guard *before*
  writing, or a genuine bug surfaces as a raw `SqlException` from three layers down
  instead of a clean `WriteResult`. `ConfiscateToCustody` and `CommitExtraction`
  both carry that guard; copy it.
- **`$env:DROPSHOCK_SQL` persists for the whole PowerShell session.** Re-running
  after "changing" it can silently reuse the old value. The migration tool echoes
  `Server / Database / User` on every run for exactly this reason.
- **An OS account is not a SQL principal.** SQL Server on Linux accepts SQL
  authentication only; a login you `ssh` in as can never work as `User Id`.
- **`Microsoft.Data.SqlClient` refuses to run under
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`**, which containers without `libicu`
  otherwise need. The error names the mode, not the missing library.
- **`net8.0` targets will not launch on a machine with only a .NET 10 runtime.**
  `<RollForward>LatestMajor</RollForward>` fixes it without retargeting. The build
  succeeds either way, so this only appears at run time.
- **`SqlAccountStore` holds a transaction open between `Find` and `Save`.** One
  operation per instance; it is not safe to share across concurrent requests.

## Status / caveats

- **The netcode core is verified working.** Compiled and run as Server + two
  Clients under the network conditioner: players spawn, movement predicts, and
  **lag-compensated hit registration lands on a strafing target**. Combat is no
  longer theoretical.
- Getting there required four fixes beyond the original config flag, all recorded
  above: lag compensation was disabled in `NetworkProjectConfig`; the
  `NetworkRunner` was destroyed by the Single-mode scene load; the client raced the
  server's scene load; and `FUSION_ENABLE_MPPM` was undefined. Each failed
  silently or misreported its own cause.
- **The inventory core is written but unexercised.** `Assets/Scripts/Core/Items/`
  compiles, but nothing constructs a `GridContainer` at runtime yet — placement,
  overlap and rotation logic have not been run.
- **Assembly definitions are in place:** `Dropshock.Core` (references nothing) and
  `Dropshock.Net`. `Dropshock.Net` had to be added to `AssembliesToWeave` in
  `NetworkProjectConfig.fusion` — see "The weaver allowlist", which is the silent
  failure this split would otherwise have caused. **Verified in the editor:** both
  assemblies build, and `Dropshock.Net.dll` carries Fusion's
  `NetworkBehaviourWeaved` attribute, so `[Networked]` weaving survived the move.
- **The AI pipeline is wired.** `Assets/Prefabs/Enemy.prefab` exists with a
  populated `HitboxRoot`, and the `EnemySpawner` on the GameSession prefab points
  at it. `Validate Setup` reports all checks passing, and now covers the enemy
  prefab, its hitbox rig, the spawner reference and the weaver allowlist.
- **The downed state is verified working, end to end.** Playtested with two peers,
  with `DROPSHOCK_TRACE_DOWNED` enabled. Confirmed: entering downed from weapon
  damage (`ApplyDamage` → `EnterDowned`); damage while downed draining the reserve
  and killing; `Die` firing once; the deferred loot drop, which correctly repacked a
  stack from `@(8,0)` into an 8-wide corpse container that could not hold it at that
  coordinate; **bleed-out drain, measured at 2.222/s and matching `_downedPool /
  _bleedOutSeconds` exactly**; and **the full revive chain** — `DriveRevive` →
  `ReceiveReviveProgress` → `CompleteRevive`, with progress surviving the per-tick
  decay and reaching 1.0.

  Both systems were reported broken and neither was. See "A long timer with no
  feedback looks exactly like a broken one".

  **Still unverified in the downed path:** crawl speed (`_downedSpeed`, no logging —
  needs feel rather than a trace); the AI's 40m downed targeting penalty, which
  needs enemies actually engaging a downed player; self-revive being refused
  (`_allowSelfRevive: 0`); and the single line never yet executed — bleed-out
  reaching zero and calling `Die`, which logs `killed by player -1`. The drain is
  proven and `Die` is proven, but the join between them has not run.
- **Downed-path tracing exists**, behind `DROPSHOCK_TRACE_DOWNED` (Project Settings
  ▸ Player ▸ Scripting Define Symbols), matching the `DROPSHOCK_TRACE_SHOTS` pattern
  in `NetworkWeapon`. Both bleed-out and revive were reviewed line by line without
  finding a defect — prefab values are correct and `Interact` is collected — so the
  trace exists to tell apart the four silent failures that present identically as
  "nothing happened": input never arriving, the button not set, `CanAct` false, and
  the target scan finding nobody. Off by default; it logs every tick.
- **Friendly fire is ON in co-op, by design, and now expressed as such.** It was
  previously inferred from team ids, which meant it only engaged once teams were
  assigned and silently did nothing in every scene that leaves them `Unassigned`.
  Replaced with `SessionConfig.EnableFriendlyFire` (default true), matching the
  `EnableDownedState` pattern. Typechecked, not yet playtested.
- **The `StateReplicator` assert is an SDK issue and is cosmetic.** `tick >=
  sc._latestTickAcknowledged`, thrown on a socket thread in Fusion's delivery-
  notification path — not the leave path, and not reachable from gameplay code.
  Confirmed harmless: the session keeps running normally after it fires. Report
  upstream; do not design around it.
- **The account backend is real and verified against SQL Server.** Schema applied
  to a live 17.00.4025 instance; 23 in-memory assertions and 12 integration
  assertions pass. It has no transport and no auth, so nothing can call it from
  outside the process yet — but the persistence rules are no longer theoretical.
  This is currently the only part of the project verified against production
  infrastructure rather than typechecked.
- **`DeathTestHarness` (F9) kills a remote player on demand** on the server, in
  editor and development builds only. Death is otherwise awkward to trigger — it
  needs a shooter, a target and two windows that cannot both hold focus — which made
  the death drop the harder of the two gear paths to exercise.

## Assembly definitions

`Assets/Scripts/Core/` and `Assets/Scripts/Net/` are separate assemblies:

| Assembly | Contents | References |
|---|---|---|
| `Dropshock.Core` | Engine-agnostic shared types — damage, teams, mode ids, item model | **Nothing** |
| `Dropshock.Net` | Everything Fusion-facing — session, player, weapons, AI, networked items | `Dropshock.Core`, `Fusion.Unity`, `Photon.Realtime`, `Unity.InputSystem` |

`Core` referencing nothing is the point of the split: the item model, grid
placement and damage rules stay testable and portable, and a `Core → Net`
dependency becomes a compile error instead of a slow architectural leak.

`Assets/Editor/` deliberately has **no** asmdef and stays in
`Assembly-CSharp-Editor`. Both assemblies above are `autoReferenced`, so the
editor scripts still see them, and not adding a third asmdef avoids re-deriving
which Fusion editor assemblies they need.

### The weaver allowlist — read this before adding an assembly

Fusion's ILWeaver does **not** weave every assembly that references Fusion. It
weaves an explicit, exact-match, case-insensitive allowlist in
`NetworkProjectConfig.fusion`:

```json
"AssembliesToWeave": [
    "Assembly-CSharp",
    "Assembly-CSharp-firstpass",
    "Dropshock.Net"
],
```

There are no wildcards — `ILWeaverSettings.IsAssemblyWeavable` is a plain
`Array.FindIndex` over string equality.

**Moving a `NetworkBehaviour` into an assembly missing from that list silently
disables `[Networked]` weaving for it.** No error, no warning: properties simply
stop replicating, and the symptom is "state doesn't sync" a long way from the
cause. This is the highest-cost mistake available when adding an assembly, and it
belongs on the same shelf as the lag-compensation flag.

Any new assembly containing `NetworkBehaviour`, `INetworkStruct` or `[Networked]`
must be added there in the same commit that creates it.

### Checking the split

`Tools/typecheck.sh` compiles the two assemblies **separately, in dependency
order**, with `Core` built with no reference to `Net`. A flat single-assembly
compile would happily accept a `Core → Net` dependency that Unity later rejects,
so the two-pass build is what makes the check meaningful rather than decorative.


## The account backend — PARTIALLY IMPLEMENTED

`Backend/` is a plain .NET 8 solution outside `Assets/`, so Unity ignores it
entirely. It holds the rules that must survive between sessions: the stash,
extraction commits and gear custody.

```
Backend/
  Dropshock.Backend/        StashService, IAccountStore, model. No dependencies.
  Dropshock.Backend.Tests/  In-memory suite, no external packages       (25 assertions)
  Dropshock.Backend.Sql/    SqlAccountStore, SqlStashGateway — Microsoft.Data.SqlClient
  Dropshock.Backend.SqlTests/ The same rules against real SQL Server    (12 assertions)
  Dropshock.Backend.ConcurrencyProbe/ Two callers on one account            (6 probes)
  Dropshock.Backend.Migrate/  Applies sql/schema.sql
  sql/schema.sql            SQL Server schema
```

**Implemented and verified against a live SQL Server 2025 instance
(17.00.4025):** checkout, extraction commit, custody confiscate, recover and
forfeit — all idempotent — plus the duplication guards and the one-custody-slot
rule. The schema is applied and the integration suite passes 12/12.

```
dotnet run --project Backend/Dropshock.Backend.Tests            # 25, no database needed
dotnet run --project Backend/Dropshock.Backend.SqlTests         # 12, needs DROPSHOCK_SQL
dotnet run --project Backend/Dropshock.Backend.ConcurrencyProbe # 6,  needs DROPSHOCK_SQL
```

**Not implemented:** authentication, any transport (there is no HTTP or RPC surface
— nothing outside the process can call this yet), base modules, and offline
production.

### Concurrency, measured

Concurrency was previously listed as not implemented, on the grounds that nothing
had ever put two callers on one account. `Dropshock.Backend.ConcurrencyProbe` now
does. It is a separate project from `SqlTests` because that suite is deliberately
single-threaded and boolean, and could never have produced any of these results.

What it found, against SQL Server 2025:

| Question | Answer |
|---|---|
| Does `UPDLOCK, HOLDLOCK` actually serialise writers? | **Yes.** 8 concurrent callers, 8 items, 8 operations, no lost update |
| Does a duplicated operation id apply twice? | **No.** One `Applied`, one `AlreadyApplied` — the lock held, the PK backstop never fired |
| Did a read block writers? | **Yes, it did** — a 5s read blocked a write for 4.78s. Fixed; see below |
| What does a blocked caller get? | **Error -2, a timeout — not 1205.** Measured at exactly 30.0s |
| Is a real deadlock reachable? | **Yes, 1205 on the first attempt**, via lock escalation |

Three things follow, and all three are now in the code.

**The same-account path cannot deadlock.** Every caller's first act is `UPDLOCK,
HOLDLOCK` on its own `dbo.Account` row, so two callers on one account take the same
lock on the same resource in the same order. That is clean serialisation. The
failure mode there is a *timeout*, which is why `SqlAccountStore` now sets an
explicit `CommandTimeout` (10s) instead of silently inheriting SqlClient's 30s.

**A deadlock is nonetheless real, from lock escalation.** `Save` deletes and rewrites
every item row for an account, so a stash past roughly 5000 rows escalates to a
table lock on `dbo.Item` — and that deadlocks *across* accounts. This is not
hypothetical: the probe produced a 1205 on its first round.

**Retry cannot live in `SqlAccountStore`.** A deadlock does not fail one statement;
the server rolls back and terminates the transaction. The store holds exactly one
transaction for its lifetime, so when the error surfaces the store is already dead —
there is nothing left to retry against. Recovery means discarding it and re-running
the whole operation, `Find` included, on a new connection. That is `SqlStashGateway`:
it builds a fresh store per attempt and retries 1205, -2, 1222 and connection-level
transients with jittered exponential backoff.

**Retrying is only safe because writes are idempotent.** The operation id must be
generated by the caller *before* the first attempt and held fixed across retries. A
caller that generates a fresh id per attempt converts every retry into duplication.
This is the single most dangerous refactor available in the backend, so probe 6
asserts it and fails with `RETRY DUPLICATED THE WRITE`.

**Reads no longer take a write lock.** `Find` takes `UPDLOCK, HOLDLOCK` and holds it
until dispose, which is correct for a read-modify-write and wrong for the compound
UI reading a stash — it blocked every write to that account for the read's whole
duration. `IAccountStore.FindForRead` takes no hint and releases before returning;
`ReadStash` and `ReadCustody` use it. Enabling `READ_COMMITTED_SNAPSHOT` on the
database would make these reads fully non-blocking with no code change, and is an
open decision.

### Checkout is the other half of "death writes nothing"

The document says extraction is the only write and death writes nothing. Those are
only consistent if gear *leaves* the stash when the raid starts:

| Point | Effect on the stash |
|---|---|
| Raid start | `CheckoutLoadout` — the loadout is removed |
| Extraction | `CommitExtraction` — survivors are added back |
| Death | Nothing. The absence of a write *is* the loss |

Without the checkout, dying would cost nothing, because the gear would still be
sitting in the stash. This interpretation is recorded because it was not stated
anywhere and the alternative reading is silently wrong.

### Idempotency is enforced twice, deliberately

Every mutating call carries a caller-supplied operation id, and applying the same
id twice is a no-op returning `AlreadyApplied`. Callers may retry freely; they may
**not** invent a new id for a retry.

Underneath that, `InstanceId` is the primary key of `StashItem` across the whole
table, not per account. An extraction re-introducing an instance that already
exists throws rather than committing.

These two overlap on purpose. Removing the idempotency check in a mutation test did
not produce duplicated gear — it produced the duplication guard throwing. Belt and
braces, because duplication is permanent and spreads, and a bug here is worth more
than one line of defence.

**Validation happens before mutation.** A checkout naming any item not in the stash
removes nothing at all. A partial application would leave the stash in a state no
single operation id describes, which is exactly what idempotency is meant to
prevent.

### Applying the schema

`Backend/Dropshock.Backend.Migrate` applies `sql/schema.sql`. It reads the
connection string from `DROPSHOCK_SQL`, never logs it, refuses to run over an
existing schema, and wraps the whole file in one transaction so a failure leaves
nothing behind.

```powershell
$env:DROPSHOCK_SQL = 'Server=...;Database=Dropshock;User Id=sa;Password=...;Encrypt=True;TrustServerCertificate=True'
dotnet run --project Dropshock.Backend.Migrate            # preflight, writes nothing
dotnet run --project Dropshock.Backend.Migrate -- --apply
```

Three environment traps, all hit while building this:

- **`$env:DROPSHOCK_SQL` persists for the whole PowerShell session.** Re-running
  after "changing" it can silently use the old value. The tool now echoes
  `Server / Database / User` (never the password) on every run so the credential in
  use is visible.
- **An OS account is not a SQL principal.** SQL Server on Linux accepts SQL
  authentication only, so a login you `ssh` in as cannot be used as `User Id`. Use
  `sa` or something made with `CREATE LOGIN`.
- **`Microsoft.Data.SqlClient` refuses to run under
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.** That flag is otherwise needed in
  containers without `libicu`. Install ICU and drop the flag; the failure
  (`NotSupportedException: Globalization Invariant Mode is not supported`) names
  the mode rather than the missing library.

The projects target `net8.0` with `<RollForward>LatestMajor</RollForward>`, so they
build on an 8.0 SDK and still launch on a machine that only has a .NET 10 runtime.

### What the SQL implementation must get right
`IAccountStore.Save` must persist the stash rows, the custody record and the
applied-operation id in **one transaction**. Recording the operation id in a
separate commit from the effect it describes reintroduces the crash window that
idempotency exists to close.

One custody slot per account is enforced by the schema — `CustodyRecord.AccountId`
is the primary key — rather than by application code that could drift from the
design decision.



```
Assets/Scripts/
  Core/        Dropshock.Core  — engine-agnostic shared types (damage, teams,
               mode ids, item/grid model). References nothing.
  Net/         Dropshock.Net   — everything Fusion-facing
    Session/   Runner bootstrap, session config, session state machine
    Player/    Input, server-auth movement, health, downed state, spawning
    Weapons/   Lag-compensated server-auth firing
    Items/     Networked containers, player inventory, loot
    AI/        Enemy brain, health, spawner
Assets/Editor/ Assembly-CSharp-Editor — setup, validation and prefab builders
Tools/         Offline typecheck (see "Offline typecheck")
```
