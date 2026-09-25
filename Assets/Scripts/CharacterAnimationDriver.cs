using System.Collections;
using UnityEngine;

public enum CharacterWeaponClass
{
    Unarmed = 0,
    Pistol = 1,
    Rifle = 2
}

/// <summary>What Ragdoll asks the driver for when a character dies.</summary>
public struct DeathRequest
{
    /// <summary>Horizontal, character-local (x right, z forward): the way the body should fall.</summary>
    public Vector3 fallDirLocal;
    public bool headshot;
    public bool crouching;
    public bool allowMirrored;
    /// <summary>The "death from right" clip falls towards the character's LEFT (measured). Flip only if your clip differs.</summary>
    public bool rightDeathFallsLeft;
    /// <summary>Horizontal speed at the moment of death, m/s.</summary>
    public float planarSpeed;
    /// <summary>A knockback-sized hit (shotgun blast at close range): only the fast "as if shotgunned" clips qualify.</summary>
    public bool heavy;
}

public struct DeathChoice
{
    public string stateName;
    public int stateHash;
    public Vector3 fallDirLocal;
    /// <summary>How far through THIS clip the ragdoll should take over (fraction, at the default 0.33 setting). Measured
    /// per clip so the handoff happens as the body commits to falling but before it reaches the floor.</summary>
    public float handoffPoint;
}

/// <summary>
/// Talks to the Animator built by AnimationSystemBuilder (Assets/Animations/CharacterAnimator.controller).
/// Base layer picks the right full-body directional blend tree for the current weapon;
/// UpperBody layer (masked to arms/spine/head) handles Aim/Fire/Reload/Melee without
/// ever interrupting the legs. Put this next to the Animator, same as before.
///
/// Public API is unchanged (SetWeaponFrom, PlayShoot, PlayReload...). New: the death
/// variant picker (TryChooseDeath / PlayDeath, used by Ragdoll), directional hit notification,
/// and look-target pass-through to CharacterMotionPolish, which this adds to the humanoid
/// Animator automatically.
/// </summary>
[RequireComponent(typeof(Animator))]
public class CharacterAnimationDriver : MonoBehaviour
{
    public CharacterWeaponClass defaultWeaponClass = CharacterWeaponClass.Unarmed;

    Animator animator;
    CharacterWeaponClass currentWeaponClass;
    CharacterMotionPolish polish;
    Ragdoll ragdollOwner;
    int upperBodyLayer = -1;

    static readonly int WeaponClassHash = Animator.StringToHash("WeaponClass");
    static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
    static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");
    static readonly int AimingHash = Animator.StringToHash("Aiming");
    static readonly int FireHash = Animator.StringToHash("Fire");
    static readonly int ReloadHash = Animator.StringToHash("Reload");
    static readonly int MeleeHash = Animator.StringToHash("Melee");
    static readonly int DeadHash = Animator.StringToHash("Dead");
    static readonly int InjuredHash = Animator.StringToHash("Injured");
    static readonly int DeathFromBackHash = Animator.StringToHash("DeathFromBack");
    static readonly int GrenadeHash = Animator.StringToHash("Grenade");
    static readonly int TouchHash = Animator.StringToHash("TouchingGround");
    bool hasTouchParam;
    static readonly int MeleeIndexHash = Animator.StringToHash("MeleeIndex");
    static readonly int ShoveHash = Animator.StringToHash("Shove");
    bool hasMeleeIndexParam, hasShoveParam;
    int meleeVariants = 1;

    /// <summary>Seconds from the shove trigger to the moment the kick connects (measured from the
    /// clip: peak foot speed at 0.60s into the raw 1.40s clip, played at 1.3x - see
    /// AnimationSystemBuilder's Shove state).</summary>
    public const float ShoveStrikeDelay = 0.46f;

    /// <summary>True when the controller was built with the velocity-space (m/s) blend trees. False for a controller
    /// that hasn't been rebuilt yet, which still expects the old 0-3 tier values.</summary>
    public bool UsesVelocityBlend { get; private set; }

    /// <summary>Seconds from the melee trigger to the moment the swing actually connects (measured from the clips,
    /// after the builder's speed-up and start offset). Anything that deals melee damage should wait this long.</summary>
    public const float MeleeStrikeDelay = 0.36f;
    static readonly int HitHash = Animator.StringToHash("Hit");
    static readonly int MoveXHash = Animator.StringToHash("MoveX");
    static readonly int MoveYHash = Animator.StringToHash("MoveY");

    // ---- death variants ---------------------------------------------------
    // State names as built by AnimationSystemBuilder. Everything numeric here was MEASURED from the FBX
    // animation data (forward kinematics of the skeleton, not guessed from the file names):
    //   fall  - which way the body ends up lying, character-local (x right, z forward), from head-vs-feet
    //           at the end of the clip. NB several names are misleading: "death from the front" actually
    //           falls FORWARD, and "death from the back" falls forward-and-left.
    //   handoff - fraction of the clip at which to hand over to the ragdoll: about a third, brought earlier
    //           for the fast clips that are already on the floor by then (measured time to hips < 40 cm).
    // Variants whose state is missing from the controller are skipped, so an old controller still works
    // until the builder is re-run (then only Death, DeathBack and DeathCrouch exist).
    struct DeathDef
    {
        public string state; public float fallX, fallZ, handoff; public bool head, crouch, mirrored, side, moving, heavy;
        public DeathDef(string s, float x, float z, float handoff, bool head = false, bool crouch = false, bool mirrored = false,
                        bool side = false, bool moving = false, bool heavy = false)
        { state = s; fallX = x; fallZ = z; this.handoff = handoff; this.head = head; this.crouch = crouch; this.mirrored = mirrored; this.side = side; this.moving = moving; this.heavy = heavy; }
    }

    static readonly DeathDef[] DeathTable =
    {
        // ---- falls forward ----
        new DeathDef("Death",            0.04f,  1.00f, 0.33f),
        new DeathDef("Death_FrontM",    -0.04f,  1.00f, 0.33f, mirrored: true),
        new DeathDef("DeathBack",       -0.57f,  0.82f, 0.30f),
        new DeathDef("Death_BackM",      0.57f,  0.82f, 0.30f, mirrored: true),
        new DeathDef("Death_HeadBack",  -0.05f,  1.00f, 0.33f, head: true),
        new DeathDef("Death_HeadBackM",  0.05f,  1.00f, 0.33f, head: true, mirrored: true),
        new DeathDef("Death_Moving",    -0.12f,  0.99f, 0.33f, moving: true),
        new DeathDef("Death_MovingM",    0.12f,  0.99f, 0.33f, moving: true, mirrored: true),
        // ---- falls backward ----
        new DeathDef("Death_HeadFront",  0.38f, -0.92f, 0.30f, head: true),
        new DeathDef("Death_HeadFrontM",-0.38f, -0.92f, 0.30f, head: true, mirrored: true),
        new DeathDef("Death_Stumble",    0.16f, -0.99f, 0.18f),
        new DeathDef("Death_StumbleM",  -0.16f, -0.99f, 0.18f, mirrored: true),
        new DeathDef("Death_FallOver",   0.22f, -0.97f, 0.24f),
        new DeathDef("Death_FallOverM", -0.22f, -0.97f, 0.24f, mirrored: true),
        // ---- falls to the side (the clip falls to the character's left) ----
        new DeathDef("Death_Right",     -0.96f, -0.27f, 0.28f, side: true),
        new DeathDef("Death_Left",       0.96f, -0.27f, 0.28f, side: true, mirrored: true),
        // ---- crouching ----
        new DeathDef("DeathCrouch",      0.02f,  1.00f, 0.33f, head: true, crouch: true),
        // ---- heavy hits (shotgun blast): fast, already airborne / down by a third, so an early handoff ----
        new DeathDef("Death_ShotFront",  0.02f, -1.00f, 0.25f, heavy: true),
        new DeathDef("Death_ShotFrontM",-0.02f, -1.00f, 0.25f, mirrored: true, heavy: true),
        new DeathDef("Death_ShotBack",  -0.01f,  1.00f, 0.16f, heavy: true),
        new DeathDef("Death_ShotBackM",  0.01f,  1.00f, 0.16f, mirrored: true, heavy: true),
    };

    bool[] deathStateExists;

    public bool IsCrouching { get; private set; }
    public Animator BodyAnimator => animator;
    public CharacterMotionPolish Polish => polish;

    void Awake()
    {
        // Prefer a HUMANOID animator with a controller assigned. Enemy/ally prefabs have two
        // Animator components: a non-humanoid one on the root (left over from an earlier setup,
        // avatar unset) and the real one on the nested model, which is the one every bone lookup,
        // IK pass and TorsoPoseDriver/WeaponHandIK actually needs. Falling back to "last one with a
        // controller" (previous behaviour) only if nothing humanoid turns up, so this never regresses
        // a prefab that has just the one Animator.
        var animators = GetComponentsInChildren<Animator>(true);
        for (int i = animators.Length - 1; i >= 0; i--)
        {
            if (animators[i].runtimeAnimatorController != null && animators[i].avatar != null && animators[i].avatar.isHuman)
            {
                animator = animators[i];
                break;
            }
        }
        if (animator == null)
        {
            for (int i = animators.Length - 1; i >= 0; i--)
            {
                if (animators[i].runtimeAnimatorController != null) { animator = animators[i]; break; }
            }
        }
        currentWeaponClass = defaultWeaponClass;
        ragdollOwner = GetComponentInParent<Ragdoll>();
        if (ragdollOwner == null) ragdollOwner = GetComponentInChildren<Ragdoll>();

        if (animator != null)
        {
            foreach (var p in animator.parameters)
            {
                if (p.nameHash == HitHash) hasHitParam = true;
                if (p.nameHash == TouchHash) hasTouchParam = true;
                if (p.nameHash == MeleeIndexHash) hasMeleeIndexParam = true;
                if (p.nameHash == ShoveHash) hasShoveParam = true;
                if (p.name == "MoveInMetres") UsesVelocityBlend = true;
            }
            animator.SetInteger(WeaponClassHash, (int)currentWeaponClass);
            upperBodyLayer = animator.GetLayerIndex("UpperBody");
            if (hasMeleeIndexParam && upperBodyLayer >= 0)
            {
                meleeVariants = 1;
                if (animator.HasState(upperBodyLayer, Animator.StringToHash("UB_Melee2"))) meleeVariants = 2;
                if (meleeVariants == 2 && animator.HasState(upperBodyLayer, Animator.StringToHash("UB_Melee3"))) meleeVariants = 3;
            }

            // Procedural secondary motion lives on the humanoid body, added here so there is
            // nothing to set up on the prefabs.
            if (animator.avatar != null && animator.avatar.isHuman)
            {
                polish = animator.GetComponent<CharacterMotionPolish>();
                if (polish == null) polish = animator.gameObject.AddComponent<CharacterMotionPolish>();
            }
        }
    }

    /// <summary>Straight from CharacterLocomotion each frame - local-space move direction.</summary>
    public void SetMove(float x, float y)
    {
        if (!CanAnimate()) return;
        animator.SetFloat(MoveXHash, x);
        animator.SetFloat(MoveYHash, y);
    }

    public void SetGrounded(bool grounded)
    {
        if (CanAnimate()) animator.SetBool(IsGroundedHash, grounded);
    }

    /// <summary>The REAL ground contact (IsGrounded may be switched on early to start the landing).</summary>
    public void SetTouchingGround(bool touching)
    {
        if (hasTouchParam && CanAnimate()) animator.SetBool(TouchHash, touching);
    }

    public void SetCrouching(bool crouching)
    {
        IsCrouching = crouching;
        if (CanAnimate()) animator.SetBool(IsCrouchingHash, crouching);
    }

    public void SetAiming(bool aiming)
    {
        if (CanAnimate()) animator.SetBool(AimingHash, aiming);
    }

    public void SetWeaponClass(CharacterWeaponClass weaponClass)
    {
        currentWeaponClass = weaponClass;
        if (CanAnimate()) animator.SetInteger(WeaponClassHash, (int)weaponClass);
    }

    /// <summary>Convenience used by PlayerSetup/WeaponController - infers weapon class from the gun.</summary>
    public void SetWeaponFrom(WeaponController weapon)
    {
        if (weapon == null || weapon.isMeleeWeapon) { SetWeaponClass(CharacterWeaponClass.Unarmed); return; }
        SetWeaponClass(weapon.isAutomatic || weapon.isShotgun ? CharacterWeaponClass.Rifle : CharacterWeaponClass.Pistol);
    }

    public void PlayShoot()
    {
        if (!CanAnimate()) return;
        animator.SetTrigger(FireHash);
        polish?.Kick(1f);
    }
    public void PlayReload() { if (CanAnimate()) animator.SetTrigger(ReloadHash); }
    public void PlayMelee()
    {
        if (!CanAnimate()) return;
        if (hasMeleeIndexParam) animator.SetInteger(MeleeIndexHash, meleeVariants > 1 ? Random.Range(0, meleeVariants) : 0);
        animator.SetTrigger(MeleeHash);
        // The lunge goes in with the swing, not at the key press.
        if (polish != null) StartCoroutine(LungeAfter(MeleeStrikeDelay * 0.5f));
    }

    IEnumerator LungeAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (polish != null) polish.Lunge();
    }

    /// <summary>Right-click shove/kick: a full-body base-layer state (see AnimationSystemBuilder),
    /// so - unlike the melee swings - the masked UpperBody layer is faded to 0 for the duration
    /// instead of just left running: it would otherwise sit on top of the kick and lock the arms
    /// into whatever aim/idle pose they were in, which looks wrong for a full-body move.</summary>
    public void PlayShove()
    {
        if (!hasShoveParam || !CanAnimate()) return;
        animator.SetTrigger(ShoveHash);
        BlendOutUpperBody(0.08f);
        StartCoroutine(RestoreUpperBodyAfter(ShoveStrikeDelay + 0.35f));
        if (polish != null) StartCoroutine(LungeAfter(ShoveStrikeDelay * 0.6f));
    }

    IEnumerator RestoreUpperBodyAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        RestoreUpperBody(0.15f);
    }

    /// <summary>Fades the masked UpperBody layer back up to full weight - the counterpart to
    /// BlendOutUpperBody, used once a Shove's full-body clip has finished.</summary>
    public void RestoreUpperBody(float duration)
    {
        if (upperBodyLayer < 0 || !CanAnimate()) return;
        StartCoroutine(FadeLayerTo(upperBodyLayer, 1f, duration));
    }

    /// <summary>Torso lunge only, no arm clip - for armed characters whose hands are locked to the gun.</summary>
    public void PlayLunge() { polish?.Lunge(); }

    /// <summary>Plays the grenade throw on the masked upper body - legs keep walking.</summary>
    bool hasHitParam;
    float lastHitTime = -10f, lastHealthFraction = 1f;
    [Header("Hit Reaction")]
    [Tooltip("Minimum seconds between flinches so sustained fire doesn't lock the upper body into one.")]
    public float hitReactionCooldown = 0.8f;

    void Start()
    {
        // Flinch whenever health goes DOWN (player or any Health-based character).
        var ph = GetComponentInParent<PlayerHealth>();
        if (ph != null) ph.onHealthChanged += OnHealthFraction;
        var h = GetComponentInParent<Health>();
        if (h != null) h.onDamaged.AddListener(OnHealthFraction);
    }

    void OnHealthFraction(float fraction)
    {
        if (fraction < lastHealthFraction - 0.0001f && fraction > 0f) PlayHit();
        lastHealthFraction = fraction;
    }

    /// <summary>Short masked upper-body flinch (UB_Hit). Rate limited.</summary>
    public void PlayHit()
    {
        if (!hasHitParam || !CanAnimate() || Time.time - lastHitTime < hitReactionCooldown) return;
        lastHitTime = Time.time;
        animator.SetTrigger(HitHash);
    }

    /// <summary>Directional flinch, called by Ragdoll for every hit that carries a direction. Not rate limited:
    /// it's a spring, so stacked hits just add up.</summary>
    public void NotifyHit(Vector3 worldTravelDirection, float force)
    {
        polish?.Flinch(worldTravelDirection, force);
    }

    public void PlayGrenade() { if (CanAnimate()) animator.SetTrigger(GrenadeHash); }

    // ---- look ----
    public void SetLookTarget(Transform target, float heightOffset = 1.4f) { polish?.SetLookTarget(target, heightOffset); }
    public void SetLookPoint(Vector3 worldPoint) { polish?.SetLookPoint(worldPoint); }
    public void ClearLook() { polish?.ClearLook(); }

    // ---- death -------------------------------------------------------------

    /// <summary>Legacy entry point. When a Ragdoll is present it owns the death (it picks the variant
    /// and hands over to physics), so this does nothing - PlayerSetup still calls it on player death.</summary>
    public void SetDead(bool dead)
    {
        if (ragdollOwner != null) return;
        if (CanAnimate()) animator.SetBool(DeadHash, dead);
    }

    /// <summary>Legacy directional entry point; same rule as SetDead(bool).</summary>
    public void SetDead(bool dead, bool fromBack)
    {
        if (ragdollOwner != null || !CanAnimate()) return;
        animator.SetBool(DeathFromBackHash, fromBack);
        animator.SetBool(DeadHash, dead);
    }

    bool HasState(string name)
    {
        return animator.HasState(0, Animator.StringToHash(name))
            || animator.HasState(0, Animator.StringToHash("Base Layer." + name));
    }

    /// <summary>Picks the death clip that best matches the request from the variants the controller
    /// actually contains. False if it has none at all (then the body goes straight to physics).</summary>
    public bool TryChooseDeath(DeathRequest req, out DeathChoice choice)
    {
        choice = default;
        if (!CanAnimate()) return false;

        if (deathStateExists == null)
        {
            deathStateExists = new bool[DeathTable.Length];
            for (int i = 0; i < DeathTable.Length; i++) deathStateExists[i] = HasState(DeathTable[i].state);
        }

        Vector3 fall = new Vector3(req.fallDirLocal.x, 0f, req.fallDirLocal.z);
        fall = fall.sqrMagnitude > 1e-4f ? fall.normalized : new Vector3(0f, 0f, -1f);

        // Crouch variants only exist for a crouching body; if the controller has none, stand up
        // and use the normal ones rather than skipping the animation.
        bool anyCrouch = false;
        for (int i = 0; i < DeathTable.Length; i++)
            if (deathStateExists[i] && DeathTable[i].crouch) anyCrouch = true;
        bool useCrouch = req.crouching && anyCrouch;

        int best = -1; float bestScore = float.NegativeInfinity;
        for (int i = 0; i < DeathTable.Length; i++)
        {
            if (!deathStateExists[i]) continue;
            var d = DeathTable[i];
            if (d.heavy != req.heavy) continue;          // blasts use the shotgun clips, everything else never does
            if (d.crouch != useCrouch) continue;
            if (d.mirrored && !req.allowMirrored) continue;
            if (d.moving && req.planarSpeed < 1f) continue; // "walking to dying" only for a body that was moving

            float fx = d.fallX;
            if (d.side && !req.rightDeathFallsLeft) fx = -fx;

            float dir = fx * fall.x + d.fallZ * fall.z;
            float headBonus = req.heavy || useCrouch ? 0f : (d.head == req.headshot ? 1f : -0.6f);
            float movingBonus = d.moving && dir > 0.5f ? 1.5f : 0f;
            float score = dir * 2f + headBonus + movingBonus + Random.value * 0.35f;
            if (score > bestScore) { bestScore = score; best = i; }
        }

        if (best < 0) return false;

        var def = DeathTable[best];
        float outX = def.fallX;
        if (def.side && !req.rightDeathFallsLeft) outX = -outX;
        choice = new DeathChoice
        {
            stateName = def.state,
            stateHash = Animator.StringToHash(def.state),
            fallDirLocal = new Vector3(outX, 0f, def.fallZ),
            handoffPoint = def.handoff
        };
        return true;
    }

    /// <summary>Crossfades the base layer into the chosen death state and clears the upper-body layer
    /// so the arms fall with the rest of the body instead of holding the aim pose.</summary>
    public void PlayDeath(DeathChoice choice, float crossfade, float speed)
    {
        if (!CanAnimate()) return;
        polish?.EnterDeathMode();
        BlendOutUpperBody(0.1f);
        animator.speed = Mathf.Max(0.1f, speed);
        animator.CrossFadeInFixedTime(choice.stateHash, Mathf.Max(0.01f, crossfade), 0);
    }

    /// <summary>Fades the masked UpperBody layer to zero over the given time.</summary>
    public void BlendOutUpperBody(float duration)
    {
        if (upperBodyLayer < 0 || !CanAnimate()) return;
        StartCoroutine(FadeLayer(upperBodyLayer, duration));
    }

    IEnumerator FadeLayer(int layer, float duration) => FadeLayerTo(layer, 0f, duration);

    IEnumerator FadeLayerTo(int layer, float target, float duration)
    {
        float start = animator.GetLayerWeight(layer);
        float t = 0f;
        while (t < duration && animator != null && animator.enabled)
        {
            t += Time.deltaTime;
            animator.SetLayerWeight(layer, Mathf.Lerp(start, target, Mathf.Clamp01(t / Mathf.Max(0.01f, duration))));
            yield return null;
        }
        if (animator != null) animator.SetLayerWeight(layer, target);
    }

    /// <summary>Length in seconds of the state that is (or is about to be) playing on the base layer with
    /// the given hash, or 0 if it isn't there yet. Looks at both the current and the incoming state, since
    /// during a crossfade the destination is the "next" one.</summary>
    public float GetStateLength(int stateHash)
    {
        if (!CanAnimate()) return 0f;
        var next = animator.GetNextAnimatorStateInfo(0);
        if (animator.IsInTransition(0) && next.shortNameHash == stateHash) return next.length;
        var cur = animator.GetCurrentAnimatorStateInfo(0);
        if (cur.shortNameHash == stateHash) return cur.length;
        return 0f;
    }

    /// <summary>Length of the death clip currently playing on the base layer.</summary>
    public float GetCurrentBaseStateLength()
    {
        if (!CanAnimate()) return 0f;
        var info = animator.GetCurrentAnimatorStateInfo(0);
        return info.length;
    }

    /// <summary>Normalised progress through the current base-layer state (0-1+).</summary>
    public float GetCurrentBaseStateProgress()
    {
        if (!CanAnimate()) return 0f;
        return animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
    }
    public void SetInjured(bool injured) { if (CanAnimate()) animator.SetBool(InjuredHash, injured); }

    bool CanAnimate() => animator != null && animator.runtimeAnimatorController != null;

    public CharacterWeaponClass CurrentWeaponClass => currentWeaponClass;
}
