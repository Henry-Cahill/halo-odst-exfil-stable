using Dropshock.Core;
using Dropshock.Net.Player;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.Weapons
{
    /// <summary>
    /// Client-predicted firing with server-authoritative hit resolution.
    ///
    /// <para><b>The split matters.</b> The owning client predicts everything that
    /// affects <i>feel</i> — the fire-rate gate, the ammo counter, the muzzle flash —
    /// so pulling the trigger is instant at any ping. It predicts nothing that
    /// affects <i>truth</i>. Only the state authority raycasts and applies damage.
    /// A modified client can predict a thousand kills and none of them will exist.</para>
    ///
    /// <para><b>Lag compensation</b> is what makes that fair. The server rewinds every
    /// hitbox to where the shooter saw them at the tick the input was produced, so
    /// leading targets is unnecessary on 80ms ping without ever trusting the client's
    /// claim of a hit.</para>
    ///
    /// <para>Requires <c>HitboxRoot</c> + <c>Hitbox</c> components on damageable
    /// characters — lag compensation rewinds Fusion hitboxes, not raw PhysX colliders.</para>
    /// </summary>
    public sealed class NetworkWeapon : NetworkBehaviour
    {
        [Header("Config")]
        [SerializeField] private WeaponConfig _config;

        [Tooltip("Muzzle/eye transform the shot originates from. Use the camera root " +
                 "so what the player sees is what the server traces.")]
        [SerializeField] private Transform _muzzle;

        [Tooltip("Layers containing character hitboxes and world geometry.")]
        [SerializeField] private LayerMask _hitMask = ~0;

        [Networked] public int Ammo { get; private set; }
        [Networked] public NetworkBool IsReloading { get; private set; }
        [Networked] private TickTimer FireCooldown { get; set; }
        [Networked] private TickTimer ReloadTimer { get; set; }

        /// <summary>
        /// Incremented on every shot. Networked so remote proxies — which never run
        /// the firing code — still get exactly one FX trigger per shot.
        /// </summary>
        [Networked] private int FireCount { get; set; }

        /// <summary>
        /// Networked previous buttons for resimulation-safe edge detection.
        /// Semi-auto weapons depend on this being correct; a local field would lose
        /// the trigger press during a replayed tick and the shot would vanish.
        /// </summary>
        [Networked] private NetworkButtons PreviousButtons { get; set; }

        private ChangeDetector _changes;
        private PlayerHealth _ownerHealth;

        /// <summary>Guards the lag-compensation warning so it logs once, not every tick.</summary>
        private bool _warnedNoLagCompensation;

        /// <summary>Raised on all peers, once per shot. Hook muzzle flash/audio here.</summary>
        public event System.Action OnFired;

        public override void Spawned()
        {
            _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);
            _ownerHealth = GetComponentInParent<PlayerHealth>();

            if (HasStateAuthority && _config != null)
            {
                Ammo = _config.MagazineSize;
            }
        }

        public override void FixedUpdateNetwork()
        {
            // True on the state authority AND the input authority. The second case
            // is what gives the owning client prediction.
            if (!GetInput(out NetInput input)) return;
            if (_config == null) return;

            // A dead or downed player does not shoot. CanAct rather than IsAlive:
            // a downed player is still alive and still a target, but must not fight.
            if (_ownerHealth != null && !_ownerHealth.CanAct) return;

            ProcessReload(input);

            if (!IsReloading && ShouldFire(input))
            {
                TryFire(input.Buttons.IsSet(InputButton.Aim));
            }

            PreviousButtons = input.Buttons;
        }

        private void ProcessReload(NetInput input)
        {
            if (IsReloading)
            {
                if (ReloadTimer.Expired(Runner))
                {
                    IsReloading = false;
                    Ammo = _config.MagazineSize;
                }
                return;
            }

            if (input.Buttons.WasPressed(PreviousButtons, InputButton.Reload) &&
                Ammo < _config.MagazineSize)
            {
                BeginReload();
            }
        }

        /// <summary>
        /// Automatic weapons fire while held; semi-auto requires a fresh press,
        /// edge-detected against networked previous buttons so it survives resim.
        /// </summary>
        private bool ShouldFire(NetInput input)
        {
            return _config.IsAutomatic
                ? input.Buttons.IsSet(InputButton.Fire)
                : input.Buttons.WasPressed(PreviousButtons, InputButton.Fire);
        }

        private void BeginReload()
        {
            IsReloading = true;
            ReloadTimer = TickTimer.CreateFromSeconds(Runner, _config.ReloadSeconds);
        }

        private void TryFire(bool isAiming)
        {
            // Server-side rate limit. Predicted identically on the client, but the
            // server's copy is the one that counts — this is the gate that makes a
            // "rapid fire" cheat do nothing.
            if (!FireCooldown.ExpiredOrNotRunning(Runner)) return;

            if (Ammo <= 0)
            {
                BeginReload();
                return;
            }

            // Predicted on both peers: the cooldown and the ammo counter update
            // immediately on the owning client so the weapon feels responsive.
            FireCooldown = TickTimer.CreateFromSeconds(Runner, _config.SecondsPerShot);
            Ammo--;
            FireCount++;

            // NOT predicted: hit resolution and damage are authority-only.
            if (HasStateAuthority)
            {
                ResolveShot(isAiming);
            }
        }

        private void ResolveShot(bool isAiming)
        {
            Transform origin = _muzzle != null ? _muzzle : transform;

            Vector3 direction = ApplySpread(
                origin.forward,
                isAiming ? _config.AimSpreadDegrees : _config.HipSpreadDegrees);

            var lagCompensation = Runner != null ? Runner.LagCompensation : null;

            // Runner.LagCompensation is non-null even when the feature is switched off
            // in NetworkProjectConfig, so the null check alone is not enough: with
            // Enabled == false no hitbox snapshot history is recorded and every
            // authority-side raycast silently misses. Check both.
            bool lagCompensationEnabled =
                Runner != null && Runner.Config != null && Runner.Config.LagCompensation.Enabled;

            if (lagCompensation == null || !lagCompensationEnabled)
            {
                // Fail loudly once, not every tick the trigger is held.
                if (!_warnedNoLagCompensation)
                {
                    _warnedNoLagCompensation = true;
                    Debug.LogError(
                        $"[{nameof(NetworkWeapon)}] Lag compensation is unavailable — " +
                        "hit registration is disabled and every shot will miss. Enable " +
                        "LagCompensation in NetworkProjectConfig and verify hitboxes are " +
                        "registered on the player prefab.", this);
                }
                return;
            }

            // The critical call: rewind hitboxes to the shooter's view of the world
            // at the tick this input was generated.
            bool hit = lagCompensation.Raycast(
                origin.position,
                direction,
                _config.Range,
                Object.InputAuthority,
                out LagCompensatedHit result,
                _hitMask,
                HitOptions.IncludePhysX | HitOptions.SubtickAccuracy);

            if (!hit)
            {
                // Lag comp is on and the ray still found nothing. Usual causes, in
                // order of likelihood: _hitMask excludes the Hitbox layer, the muzzle
                // transform points the wrong way, or the target's hitboxes are not
                // registered with its HitboxRoot.
                TraceShot("MISS — raycast found nothing", origin.position, direction);
                return;
            }

            // Hitbox hits resolve through the hitbox root; PhysX hits resolve through
            // the collider's GameObject. Either can be null depending on what was hit.
            IDamageable target = null;

            if (result.Hitbox != null && result.Hitbox.Root != null)
            {
                target = result.Hitbox.Root.GetComponentInParent<IDamageable>();
            }
            else if (result.GameObject != null)
            {
                target = result.GameObject.GetComponentInParent<IDamageable>();
            }

            if (target == null)
            {
                // Something was hit, but nothing on it implements IDamageable. Usually
                // geometry, or a hitbox whose IDamageable sits outside its parent chain.
                TraceShot(
                    $"HIT '{(result.GameObject != null ? result.GameObject.name : "?")}' " +
                    "but found no IDamageable on it",
                    origin.position, direction);
                return;
            }

            if (!target.IsAlive)
            {
                TraceShot("HIT a target that is already dead", origin.position, direction);
                return;
            }

            var damage = new DamageInfo(
                amount: _config.Damage,
                point: result.Point,
                direction: direction,
                instigatorTeam: _ownerHealth != null ? _ownerHealth.Team : TeamId.Unassigned,
                instigatorPlayerId: Object.InputAuthority.PlayerId);

            target.ApplyDamage(damage);
            TraceShot($"HIT '{target}' for {_config.Damage}", origin.position, direction);
        }

        /// <summary>
        /// Per-shot authority-side tracing, compiled out unless DROPSHOCK_TRACE_SHOTS
        /// is defined (Project Settings ▸ Player ▸ Scripting Define Symbols).
        ///
        /// <para>Hit registration fails silently by nature — every failure path is just
        /// "no damage happened", and they are indistinguishable in a playtest. This
        /// names which one fired. Off by default because it logs on every shot.</para>
        /// </summary>
        [System.Diagnostics.Conditional("DROPSHOCK_TRACE_SHOTS")]
        private void TraceShot(string outcome, Vector3 origin, Vector3 direction)
        {
            Debug.Log($"[Shot] tick={Runner.Tick} player={Object.InputAuthority.PlayerId} {outcome}", this);
            Debug.DrawRay(origin, direction * _config.Range, Color.red, 2f, false);
        }

        /// <summary>
        /// Deterministic spread derived from the network tick, so the server and the
        /// predicting client compute an identical cone for the same shot.
        /// <see cref="Random"/> here would desync prediction from the server and
        /// produce shots that visibly disagree with their own tracers.
        /// </summary>
        private Vector3 ApplySpread(Vector3 forward, float spreadDegrees)
        {
            if (spreadDegrees <= 0f) return forward;

            int tick = Runner.Tick;
            float a = Hash01(tick * 73856093) * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Hash01(tick * 19349663)) * spreadDegrees;

            return Quaternion.AngleAxis(r, Quaternion.AngleAxis(a * Mathf.Rad2Deg, forward) * Vector3.up)
                   * forward;
        }

        private static float Hash01(int seed)
        {
            unchecked
            {
                uint x = (uint)seed;
                x ^= x >> 16; x *= 0x7feb352dU;
                x ^= x >> 15; x *= 0x846ca68bU;
                x ^= x >> 16;
                return (x & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        public override void Render()
        {
            // Render runs once per frame after resimulation settles, so a predicted
            // shot fires its FX once rather than once per replayed tick.
            foreach (var change in _changes.DetectChanges(this))
            {
                if (change == nameof(FireCount))
                {
                    OnFired?.Invoke();
                }
            }
        }
    }
}
