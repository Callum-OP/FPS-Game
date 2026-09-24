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
    /// <summary>The "death from right" clip is assumed to fall towards the character's LEFT. Flip if it looks backwards.</summary>
    public bool rightDeathFallsLeft;
}

public struct DeathChoice
{
    public string stateName;
    public int stateHash;
    public Vector3 fallDirLocal;
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
    static readonly int HitHash = Animator.StringToHash("Hit");
    static readonly int MoveXHash = Animator.StringToHash("MoveX");
    static readonly int MoveYHash = Animator.StringToHash("MoveY");

    // ---- death variants ---------------------------------------------------
    // State names as built by AnimationSystemBuilder. Fall direction is character-local
    // (x right, z forward): a death "from the front" falls backwards (-z). Variants whose
    // state is missing from the controller are skipped, so an old controller (Death,
    // DeathBack, DeathCrouch only) still works until the builder is re-run.
    struct DeathDef
    {
        public string state; public float fallX, fallZ; public bool head, crouch, mirrored, side;
        public DeathDef(string s, float x, float z, bool head, bool crouch, bool mirrored, bool side = false)
        { state = s; fallX = x; fallZ = z; this.head = head; this.crouch = crouch; this.mirrored = mirrored; this.side = side; }
    }

    static readonly DeathDef[] DeathTable =
    {
        new DeathDef("Death",             0f, -1f, false, false, false),
        new DeathDef("Death_FrontM",      0f, -1f, false, false, true),
        new DeathDef("DeathBack",         0f,  1f, false, false, false),
        new DeathDef("Death_BackM",       0f,  1f, false, false, true),
        new DeathDef("Death_HeadFront",   0f, -1f, true,  false, false),
        new DeathDef("Death_HeadFrontM",  0f, -1f, true,  false, true),
        new DeathDef("Death_HeadBack",    0f,  1f, true,  false, false),
        new DeathDef("Death_HeadBackM",   0f,  1f, true,  false, true),
        new DeathDef("Death_Right",      -1f,  0f, false, false, false, true),
        new DeathDef("Death_Left",        1f,  0f, false, false, true,  true),
        new DeathDef("DeathCrouch",       0f, -1f, true,  true,  false),
        new DeathDef("Death_CrouchBack",  0f,  1f, true,  true,  false),
    };

    bool[] deathStateExists;

    public bool IsCrouching { get; private set; }
    public Animator BodyAnimator => animator;
    public CharacterMotionPolish Polish => polish;

    void Awake()
    {
        var animators = GetComponentsInChildren<Animator>(true);
        for (int i = animators.Length - 1; i >= 0; i--)
        {
            if (animators[i].runtimeAnimatorController != null)
            {
                animator = animators[i];
                break;
            }
        }
        currentWeaponClass = defaultWeaponClass;
        ragdollOwner = GetComponentInParent<Ragdoll>();
        if (ragdollOwner == null) ragdollOwner = GetComponentInChildren<Ragdoll>();

        if (animator != null)
        {
            foreach (var p in animator.parameters) { if (p.nameHash == HitHash) hasHitParam = true; if (p.nameHash == TouchHash) hasTouchParam = true; }
            animator.SetInteger(WeaponClassHash, (int)currentWeaponClass);
            upperBodyLayer = animator.GetLayerIndex("UpperBody");

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
        if (weapon == null) { SetWeaponClass(CharacterWeaponClass.Unarmed); return; }
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
        animator.SetTrigger(MeleeHash);
        polish?.Lunge();
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
            if (d.crouch != useCrouch) continue;
            if (d.mirrored && !req.allowMirrored) continue;

            float fx = d.fallX;
            if (d.side && !req.rightDeathFallsLeft) fx = -fx;

            float dir = fx * fall.x + d.fallZ * fall.z;
            float headBonus = d.head == req.headshot ? 1f : -0.6f;
            if (useCrouch) headBonus = 0f;
            float score = dir * 2f + headBonus + Random.value * 0.35f;
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
            fallDirLocal = new Vector3(outX, 0f, def.fallZ)
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

    IEnumerator FadeLayer(int layer, float duration)
    {
        float start = animator.GetLayerWeight(layer);
        float t = 0f;
        while (t < duration && animator != null && animator.enabled)
        {
            t += Time.deltaTime;
            animator.SetLayerWeight(layer, Mathf.Lerp(start, 0f, Mathf.Clamp01(t / Mathf.Max(0.01f, duration))));
            yield return null;
        }
        if (animator != null) animator.SetLayerWeight(layer, 0f);
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
