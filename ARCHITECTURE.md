# Project Dropshock — Architecture & Setup

> `Dropshock` is a placeholder codename/namespace. The repo folder still says
> `HALO-ODST-EXFIL`, but the shipping product is **original IP** — no Halo names,
> lore, art, audio, or trademarks in any committed asset.

## Locked decisions

| Decision | Choice | Why |
|---|---|---|
| IP | Original IP, ODST-inspired tone | Halo is Microsoft IP; not commercially releasable |
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

## Building the test setup

After importing Fusion 2 + SimpleKCC:

1. **Dropshock ▸ Setup ▸ Build Test Scene** — creates both scenes, the player
   prefab, the config assets, the `Hitbox` layer, and the build settings order.
2. **Dropshock ▸ Setup ▸ Validate Setup** — checks the failure modes that are
   silent at runtime (missing `HitboxRoot`, `NetworkTransform` fighting SimpleKCC,
   unassigned weapon config, wrong scene build order).
3. Confirm the player prefab is registered in `NetworkProjectConfig`.
4. Open `Assets/Scenes/Bootstrap.unity` and press Play.

The setup is generated by editor scripts rather than committed as binary `.prefab`
and `.unity` assets. That keeps the wiring reviewable in a diff, re-runnable after
an SDK upgrade, and free of hand-authored GUIDs.

**Two scenes, deliberately.** `Bootstrap` owns the `NetworkRunner` and persists;
`DropTest` is the map Fusion loads over it. Putting the runner in the gameplay
scene works fine in the editor and breaks the moment you need to change maps
between matches.

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

## Status / caveats

- This environment has no Unity or .NET toolchain, so **none of this code has been
  compiled.** It is written against the Fusion 2 API from documentation knowledge.
  Expect to fix API mismatches on first import — treat the first compile as part
  of the task, not as a failure.
- **No assembly definitions yet.** Everything sits in `Assembly-CSharp` so it
  compiles immediately against Fusion without asmdef reference guesswork. Splitting
  into `Dropshock.Core` / `Dropshock.Net` is a follow-up once Fusion's actual
  asmdef names can be read off disk — worth doing before the codebase grows.

## Layout

```
Assets/Scripts/
  Core/        Engine-agnostic shared types (damage, teams, mode ids)
  Net/
    Session/   Runner bootstrap, session config, session state machine
    Player/    Input, server-auth movement, health, spawning
    Weapons/   Lag-compensated server-auth firing
```
