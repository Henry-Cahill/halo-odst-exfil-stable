# Project Dropshock — Architecture & Setup

> `Dropshock` is a placeholder codename/namespace. The repo folder still says
> `HALO-ODST-EXFIL`, but the shipping product is **original IP** — no Halo names,
> lore, art, audio, or trademarks in any committed asset.

## Locked decisions

| Decision | Choice | Why |
|---|---|---|
| IP | Original IP, ODST-inspired tone | Halo is Microsoft IP; not commercially releasable |
| Engine | Unity 6 LTS (`6000.0.x` LTS line) | `6000.7.0a2` alpha is unsafe for a funded multi-year build |
| Render pipeline | HDRP | Already set up; **needs a perf budget review before BR scale** |
| Netcode | **Photon Fusion 2**, Server mode | Built-in area-of-interest + tick-aligned prediction; proven at 100+ CCU |
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

1. **Recreate on Unity 6 LTS.** The project is currently an empty HDRP template on
   `6000.7.0a2`. Do *not* try to downgrade in place — HDRP 17.7 is pinned to the
   alpha and package resolution will fight you. Create a fresh HDRP project on the
   latest Unity 6 LTS and copy `Assets/Scripts/` + `ARCHITECTURE.md` across.
2. **Import Fusion 2.** Download the SDK from the Photon dashboard
   (`dashboard.photonengine.com`) — it is a `.unitypackage`, not available on the
   public UPM registry.
3. **Create a Fusion App ID** in the Photon dashboard and paste it into
   `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset`.
4. **Set the simulation tick rate** in `NetworkProjectConfig`. Start at 30Hz for
   co-op. BR at 100 players will likely need it lower — measure, don't guess.
5. **Layers.** Create a `Hitbox` layer and assign it to character hitboxes; the
   weapon code raycasts against it.

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
