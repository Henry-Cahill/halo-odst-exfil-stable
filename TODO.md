# Open questions — to be answered

Parked from design sessions so building can continue. `ARCHITECTURE.md` remains the
authority; this file is a queue, not a second source of truth. **When one of these is
answered, write the decision into `ARCHITECTURE.md` and strike it here.**

Ordered by what blocks work, not by size.

---

## Tier 1 — the design cannot be built without these

### 1. What does an augmentation actually do?

Never defined, and it is the payoff of the entire rebirth loop. The player surrenders
a stash, a base and a full skill tree to get one. `ARCHITECTURE.md` argues it
therefore cannot be a minor stat nudge — a total cost demands a transformative
effect. Health? Shields? Movement? Something that changes how the game is played?

**Blocks:** the whole progression system. The reward at the end of the longest loop
in the game is currently undefined.

### 2. Does a forced restart clear achievements?

Achievements gate the rebirth terminus, and the doc records that they must survive
wipes or the endgame is unreachable by construction. But if a **forced** restart
clears them, losing a Fight or Run permanently locks that player out of ever becoming
a Spartan.

Either a deliberate and very harsh rule, or an accident waiting to happen. It should
be chosen on purpose.

### 3. A reachable definition of "highest skills"

Half the win condition. Tarkov-breadth skill maxing is a multi-thousand-hour
proposition in the game it comes from, which had a studio and a large population.
Options: a threshold rather than a cap, a defined subset, or elite level in one
chosen specialisation.

**Blocks:** any testable definition of "beat the game".

### 4. The Halo 3: ODST achievement list, re-mapped

The originals are written against that game's campaign missions, Firefight maps and
audio logs — none of which exist here. Names and spirit carry over under the same
non-commercial terms as the rest of the Halo content, but every completion
*criterion* must be rewritten against systems this game actually has: raids,
extractions, base defence, hideout waves.

**Blocks:** the other half of "beat the game".

### 5. Server-side commit direction on a crash

Reconnection covers a client dropping. It does not cover the backend dying **partway
through the extraction commit** — gear removed from the session, not yet written to
the stash.

- **Roll back:** player keeps the gear. Becomes a dupe opportunity if session state
  also survived.
- **Commit forward:** player loses gear to a crash that was not their fault.

`SqlAccountStore` does its work in one transaction with the account row locked, which
makes rollback the natural default. It should be chosen explicitly rather than
inherited.

### 6. The base-defence contradiction

"Base defence" states: **"Defeat leads to capture, not a game-over."** But losing a
hideout Fight or Run costs the character entirely.

Two losses, at the same base, with wildly different consequences. Either the hideout
wave inherits capture-not-game-over, or the document has to say why they differ.

---

## Tier 2 — numbers and definition

### 7. Weight tiers
What are the thresholds, and what does each cost — movement speed, stamina drain,
stamina regeneration, noise? Tiers were chosen over a smooth curve deliberately, so
the boundaries need to be felt.

### 8. Container capacities
Player, corpse, world crate. The first playtest already exercised the mismatch: a
10-wide inventory spilling into an 8-wide corpse container forced a repack.

### 9. Hide / Fight / Run
The actual pros and cons of each. What does each yield and risk, given **Hide cannot
cost the character**?

### 10. Insurance premium
What does a kit cost, in what currency, and how do traders price gear generally?

### 11. Producer output caps
Caps were agreed in principle. The numbers are open.

### 12. The memorial wall
Which direction does it extend, and what spacing per entry? It is generated from the
record list rather than authored geometry, so this must be settled **before compound
art is built** — retrofitting a growing wall into a finished room is a level rebuild.

---

## Tier 3 — real, but later

### 13. BR looting pacing
Is grid-based looting too slow for 100-player battle royale?

### 14. Revive vs friendly fire
Damage is now governed by `SessionConfig.EnableFriendlyFire`; revive is still
governed by team ids, with `Unassigned` treated as *same squad*. Should revive move
to the same mechanism, or is the split intentional?

---

## Not design questions — blocking work

### 15. ~~Bleed-out and revive repro~~ — RESOLVED, neither was broken

**Bleed-out:** verified. At `dt=0.0333`, `_downedPool=100`, `_bleedOutSeconds=45`,
drain is `2.222/s` (`0.07407`/tick). Health measured `73.4` after `359` ticks —
exact. The original report was a 45-second timer watched for less than 45 seconds.

**Revive:** verified. Full chain observed — `DriveRevive` → `ReceiveReviveProgress`
→ `CompleteRevive` → `[Player:2] revived.` Revive progress survived the per-tick
decay in `TickBleedOut` and reached 1.0, so the intermittent-input stall I was
watching for does not occur at these values.

**Lesson kept in ARCHITECTURE.md:** a long timer with no feedback is
indistinguishable from a broken one. The downed HUD needs a visible bleed-out bar so
the mechanic can be told apart from a dead one — and the same applies to insurance
return delays, offline production and base module timers, all of which are long
timers whose failure mode is silence.

**Remaining in the downed path, cheap to close:**

- Bleed-out actually reaching zero and calling `Die` — the one line never executed.
  Expect `killed by player -1`. Drain is proven and `Die` is proven; the join is not.
- Crawl speed (`_downedSpeed = 1.0`) — needs feel, not a trace.
- Self-revive being refused (`_allowSelfRevive: 0`).
- The AI's 40m downed targeting penalty — **unblocked, needs one session to confirm.**
  `EnemySpawner` now logs. On a server run expect `[Spawner] active on '...'` once at
  startup, then `[Spawner] spawned '<prefab>' #1 ... alive=1/4` per spawn. If neither
  line appears the component is not running at all; if the startup line appears alone,
  read the warning it prints (`targetCount=0`, prefab missing, or radii overlapping).
  Per-tick reasons are behind the `DROPSHOCK_TRACE_SPAWNER` define. Prefab and wiring
  are correct (`GameSession.prefab:88`, hitboxes populated).

### 16. Art direction not chosen
The project has zero materials, zero shaders, zero models, zero HUD. HDRP 17.5.
Two candidate starting points:

- **VISR** — highest identity-per-effort, needs no art assets, keys off `TeamId` and
  `PlayerHealth.IsDowned`.
- **Night lighting + volume profile** — turns a grey daylight box into a city at
  night with pure config.

---

## Answered — moved to ARCHITECTURE.md

Kept as a short index so nothing gets re-litigated.

| Question | Answer |
|---|---|
| Team-killing | Accepted. Depends on co-op staying squad-and-solo only |
| Skills model | Tarkov's, use-based accrual |
| Rebirth repeats? | Yes, until all ODST achievements + unlocked gear; final run starts fully augmented |
| Identity provider | Shared secret, alpha only. Fail closed, never committed |
| Extraction crash | Reconnection allowed (client side); server side still open — see #5 |
| Insurance delay | Scales with what was lost |
| Wipe vs custody | Wipe forfeits custody gear |
| Attachments | Follow the weapon unless removed or tampered with |
| Teammate-triggered abduction | Allowed |
| Failed escape destroys gear | Yes, intended severity |
| Corpse containers | Expire at session end — also when insurance resolves |
| Weight model | Tarkov's. Needs replicated total for prediction |
| Stacking | Auto-merge, partial split, cross-container. Split creates an instance |
| Matchmaking | Co-op squad-and-solo only; strangers in base defence and BR |
| Producer caps | Yes, in principle |
