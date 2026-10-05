using System.Collections;
using UnityEngine;

/// <summary>How a MeleeWeapon swings: which animation pack its attacks come from. Pick it per weapon with the Style dropdown.</summary>
public enum MeleeStyle
{
    Sword = 0,   // Great Sword pack: chop -> sweep -> thrust, heavy spin
    Axe = 1      // Pro Melee Axe pack: horizontal -> backhand -> downward, heavy 360
}

/// <summary>
/// Timing for every melee swing, measured from the FBX hand motion (Python forward-kinematics over the Mixamo clips:
/// right-hand speed relative to the hips). All the clip times below are RAW clip seconds; a swing plays at `speed`, and
/// starts `offset` seconds into the clip (the dead time at the start, where the arm is only leaving its rest pose, is
/// skipped - the same trick the unarmed melee uses). Index = (int)style * 4 + n, with n 0-2 the light combo and 3 the heavy.
///   strike  the moment the blade connects (peak hand speed)
///   apex    heavies only: the top of the wind-up, where the arm is drawn back - the swing freezes here while the
///           attack button is held, and releases when it's let go
///   exit    when the upper body starts blending back to idle (the arm has mostly returned)
/// </summary>
public static class MeleeSwings
{
    public class Def
    {
        public string state, clipKey;
        public float length, speed, offset, fade, strike, apex, exit;
        public bool heavy;
        public Def(string state, string clipKey, float length, float speed, float offset, float fade, float strike, float apex, float exit, bool heavy)
        { this.state = state; this.clipKey = clipKey; this.length = length; this.speed = speed; this.offset = offset; this.fade = fade; this.strike = strike; this.apex = apex; this.exit = exit; this.heavy = heavy; }
    }

    public const int PerStyle = 4;        // three lights + one heavy
    public const int LightCount = 3;

    public static readonly Def[] All =
    {
        // Sword (Great Sword pack, in-place swings that start from the ready pose)
        new Def("Melee_Sword1",     "sw_slash1", 1.27f, 1.45f, 0.00f, 0.06f, 0.60f, 0f,    1.02f, false),   // overhead chop
        new Def("Melee_Sword2",     "sw_slash2", 1.77f, 1.60f, 0.00f, 0.06f, 0.80f, 0f,    1.42f, false),   // cross sweep
        new Def("Melee_Sword3",     "sw_slash3", 1.20f, 1.40f, 0.00f, 0.06f, 0.47f, 0f,    0.96f, false),   // thrust
        new Def("Melee_SwordHeavy", "sw_heavy",  1.87f, 1.15f, 0.00f, 0.12f, 1.10f, 0.83f, 1.60f, true),    // high spin attack
        // Axe (Pro Melee Axe pack: horizontal, backhand, downward, 360 high)
        new Def("Melee_Axe1",       "act_melee",   2.13f, 1.90f, 0.30f, 0.06f, 0.93f, 0f,   1.48f, false),
        new Def("Melee_Axe2",       "act_melee_b", 3.03f, 1.90f, 0.35f, 0.06f, 1.07f, 0f,   1.62f, false),
        new Def("Melee_Axe3",       "act_melee_d", 2.27f, 1.90f, 0.20f, 0.06f, 0.83f, 0f,   1.38f, false),
        new Def("Melee_AxeHeavy",   "ax_heavy",    2.90f, 1.25f, 0.30f, 0.12f, 1.10f, 0.67f, 1.75f, true),
    };

    public static int Light(MeleeStyle style, int n) => (int)style * PerStyle + n;
    public static int Heavy(MeleeStyle style) => (int)style * PerStyle + (PerStyle - 1);

    /// <summary>Seconds from starting the swing to the blade connecting (a light swing).</summary>
    public static float StrikeDelay(int i) => All[i].fade + (All[i].strike - All[i].offset) / All[i].speed;
    /// <summary>Seconds from starting a swing until the upper body is handed back to idle (a light swing).</summary>
    public static float Duration(int i) => All[i].fade + (All[i].exit - All[i].offset) / All[i].speed;
    /// <summary>Heavy: seconds from starting the swing until the wind-up reaches its apex and can hold.</summary>
    public static float ApexDelay(int i) => All[i].fade + (All[i].apex - All[i].offset) / All[i].speed;
    /// <summary>Heavy: seconds from letting go (at the apex) to the blade connecting.</summary>
    public static float ReleaseToStrike(int i) => (All[i].strike - All[i].apex) / All[i].speed;
    /// <summary>Heavy: seconds from letting go (at the apex) to the upper body being handed back.</summary>
    public static float ReleaseToExit(int i) => (All[i].exit - All[i].apex) / All[i].speed;
}

/// <summary>Which animation set a character uses for the poses/animations that have variants (the unarmed idle, walk, run,
/// strafes and turn-in-place). Only the animations change, never the model.</summary>
public enum AnimationVariant
{
    Default = 0,
    Female = 1
}

public enum CharacterWeaponClass
{
    Unarmed = 0,
    Pistol = 1,
    Rifle = 2,
    Sword = 3   // two-handed melee weapon (MeleeWeapon): the Great Sword pack's stance and locomotion
}

/// <summary>What Ragdoll asks the driver for when a character dies.</summary>
public struct DeathRequest
{
    /// <summary>Horizontal, character-local (x right, z forward): the way the body should fall.</summary>
    public Vector3 fallDirLocal;
    /// <summary>Horizontal, character-local, unit length: the direction the killing shot was TRAVELLING (no momentum mixed in).
    /// Front hits travel towards -z, hits from the character's left travel towards +x.</summary>
    public Vector3 hitDirLocal;
    public bool headshot;
    public bool crouching;
    public bool allowMirrored;
    /// <summary>The "death from right" clip falls towards the character's LEFT (measured). Flip only if your clip differs.</summary>
    public bool rightDeathFallsLeft;
    /// <summary>Horizontal speed at the moment of death, m/s.</summary>
    public float planarSpeed;
    /// <summary>True if the character had been moving continuously above Ragdoll.movingSpeedThreshold
    /// for at least Ragdoll.requiredMovingDuration - what the "moving"-flagged variants below actually
    /// gate on now, instead of the old instant planarSpeed check (a single fast frame could trigger a
    /// full stumble before).</summary>
    public bool sustainedMovement;
    /// <summary>Chance (0-1) that this death plays one of the stumble-to-the-floor clips instead of a normal variant.
    /// Ragdoll works it out from speed and how hard the killing blast hit; 0 = never.</summary>
    public float stumbleChance;
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
    /// <summary>The clip already carries the body's own forward travel, so Ragdoll's extra momentum slide is skipped.</summary>
    public bool skipSlide;
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
    int femaleLegsLayer = -1;

    static readonly int WeaponClassHash = Animator.StringToHash("WeaponClass");
    static readonly int AnimVariantHash = Animator.StringToHash("AnimVariant");
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
    static readonly int ShoveHeavyHash = Animator.StringToHash("ShoveHeavy");
    bool hasMeleeIndexParam, hasShoveParam, hasShoveHeavyParam;
    int meleeVariants = 1;

    // Playback speeds of the shove clips (set on their states by AnimationSystemBuilder).
    public const float ShoveLightSpeed = 1.25f;
    public const float ShoveHeavySpeed = 1.15f;
    // Playback speed of the shoved reactions, and the crossfade (seconds) from a vault or gesture clip back to the
    // weapon pose - both read by AnimationSystemBuilder; VaultClimb assumes the fade length.
    public const float ShovedSpeed = 1.3f;
    public const float OneShotExitFade = 0.25f;

    // Seconds from the Shove trigger to the moment each shove connects. Measured from the clips (peak limb speed:
    // punch arm ~0.30s into the raw clip, kick leg ~0.34s) and divided by the playback speeds above.
    public const float ShoveLightStrikeDelay = 0.30f / ShoveLightSpeed;
    public const float ShoveHeavyStrikeDelay = 0.34f / ShoveHeavySpeed;
    public static float ShoveStrikeDelayFor(bool heavy) => heavy ? ShoveHeavyStrikeDelay : ShoveLightStrikeDelay;
    // How long each shove takes to play out (raw clip length / speed) - hand follow is released around then.
    const float ShoveLightDuration = 1.00f / ShoveLightSpeed;
    const float ShoveHeavyDuration = 1.43f / ShoveHeavySpeed;

    // ---- melee swings (Sword / Axe style, light combo + heavy; upper-body states built by AnimationSystemBuilder) ----
    // Timing and speeds live in MeleeSwings. They play on the masked UPPER body, so the legs keep doing whatever the
    // locomotion is doing. The swing is picked with the Swing trigger + SwingIndex int; the graph transitions carry the
    // start offset, and every swing state can cut straight into the next. A heavy has SwingSpeed wired in as its state
    // speed multiplier, so setting it to 0 holds the arm at the top of the wind-up and 1 lets it go.
    static readonly int SwingHash = Animator.StringToHash("Swing");
    static readonly int SwingIndexHash = Animator.StringToHash("SwingIndex");
    static readonly int SwingSpeedHash = Animator.StringToHash("SwingSpeed");
    Coroutine swingRelease;

    /// <summary>Starts melee swing `swingIndex` (see MeleeSwings). Both arms are handed to the clip while it plays, the weapon
    /// rides in the right hand (WeaponHandFollow) and the left hand stays on its grip. A light swing gives the arms back by
    /// itself when it's done; a heavy waits for FinishMeleeSwing. False if the controller has no swing states yet (run
    /// Tools/FPS Game/Build Animation System).</summary>
    public bool PlayMeleeSwing(int swingIndex)
    {
        if (!CanAnimate() || upperBodyLayer < 0 || swingIndex < 0 || swingIndex >= MeleeSwings.All.Length) return false;
        var def = MeleeSwings.All[swingIndex];
        if (!HasParam(SwingHash) || !animator.HasState(upperBodyLayer, Animator.StringToHash(def.state))) return false;

        if (upperFade != null) { StopCoroutine(upperFade); upperFade = null; }
        if (animator.GetLayerWeight(upperBodyLayer) < 0.99f) animator.SetLayerWeight(upperBodyLayer, 1f);
        animator.SetFloat(SwingSpeedHash, 1f);
        animator.SetInteger(SwingIndexHash, swingIndex);
        animator.SetTrigger(SwingHash);
        StartCoroutine(ResetSwingTrigger());

        Hands?.SetAnimationFollow(1f, 0f);
        SetWeaponFollowsHand(true);
        if (swingRelease != null) { StopCoroutine(swingRelease); swingRelease = null; }
        if (!def.heavy) swingRelease = StartCoroutine(SwingReleaseRoutine(MeleeSwings.Duration(swingIndex)));
        if (polish != null && !def.heavy) StartCoroutine(LungeAfter(MeleeSwings.StrikeDelay(swingIndex) * 0.5f));
        return true;
    }

    bool HasParam(int hash)
    {
        foreach (var p in animator.parameters) if (p.nameHash == hash) return true;
        return false;
    }

    // The trigger is only consumed if a transition takes it; if the layer was busy it must not linger and fire a swing later.
    IEnumerator ResetSwingTrigger()
    {
        yield return null; yield return null; yield return null;
        if (animator != null) animator.ResetTrigger(SwingHash);
    }

    /// <summary>Heavy swing: true freezes the arm where it is (the apex of the wind-up), false lets it swing.</summary>
    public void SetSwingHold(bool hold)
    {
        if (CanAnimate()) animator.SetFloat(SwingSpeedHash, hold ? 0f : 1f);
    }

    /// <summary>Heavy swing: hands the arms back to the weapon grips after `seconds` (call when the swing is let go).</summary>
    public void FinishMeleeSwing(float seconds)
    {
        if (swingRelease != null) StopCoroutine(swingRelease);
        swingRelease = StartCoroutine(SwingReleaseRoutine(seconds));
    }

    IEnumerator SwingReleaseRoutine(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        swingRelease = null;
        ReleaseSwingHands();
    }

    // Hands back to the weapon grips - unless a full-body action (a shove) has meanwhile taken the arms for itself.
    void ReleaseSwingHands()
    {
        if (fullBodyAction) return;
        Hands?.ClearAnimationFollow();
        SetWeaponFollowsHand(false);
    }

    /// <summary>Cuts a swing short (weapon put away, dropped, player died): the upper body goes back to idle and the hands to the grips.</summary>
    public void CancelMeleeSwing()
    {
        if (!CanAnimate()) return;
        bool wasActive = swingRelease != null;
        if (swingRelease != null) { StopCoroutine(swingRelease); swingRelease = null; }
        animator.SetFloat(SwingSpeedHash, 1f);
        animator.ResetTrigger(SwingHash);
        if (upperBodyLayer >= 0 && IsInSwingState()) animator.CrossFadeInFixedTime(Animator.StringToHash("UB_Idle"), 0.1f, upperBodyLayer);
        if (wasActive || IsInSwingState()) ReleaseSwingHands();
    }

    bool IsInSwingState()
    {
        if (upperBodyLayer < 0) return false;
        var st = animator.GetCurrentAnimatorStateInfo(upperBodyLayer);
        foreach (var d in MeleeSwings.All) if (st.shortNameHash == Animator.StringToHash(d.state)) return true;
        return false;
    }

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
        new DeathDef("Death_Stumble",    0.16f, -0.99f, 0.18f, moving: true),
        new DeathDef("Death_StumbleM",  -0.16f, -0.99f, 0.18f, mirrored: true, moving: true),
        new DeathDef("Death_FallOver",   0.22f, -0.97f, 0.24f, moving: true),
        new DeathDef("Death_FallOverM", -0.22f, -0.97f, 0.24f, mirrored: true, moving: true),
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

    [Header("Animation Set")]
    [Tooltip("Which set of animations this character uses where there are variants (unarmed idle/walk/run/strafes/turns). Pick it per prefab or per instance - it can also be flipped at runtime. Only the animations change, never the model. Pistol and rifle carriers get the female gait on the legs only (there is no female armed stance); the arms and weapon pose stay as they are.")]
    public AnimationVariant animationVariant = AnimationVariant.Default;
    [Tooltip("Seconds to ease between the sets when the variant changes while playing (0 = instant).")]
    public float variantBlendTime = 0.2f;
    bool hasVariantParam;

    /// <summary>Switches this character's animation set (a toggle/dropdown/UI can call this).</summary>
    public void SetAnimationVariant(AnimationVariant variant) { animationVariant = variant; }
    public AnimationVariant CurrentAnimationVariant => animationVariant;

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
        // Several clips (vaults, shoved reactions) are imported with their horizontal travel extracted so code can
        // move the character; the Animator must never apply that itself.
        if (animator != null) animator.applyRootMotion = false;
        currentWeaponClass = defaultWeaponClass;
        ragdollOwner = GetComponentInParent<Ragdoll>();
        if (ragdollOwner == null) ragdollOwner = GetComponentInChildren<Ragdoll>();

        if (animator != null)
        {
            foreach (var p in animator.parameters)
            {
                if (p.nameHash == HitHash) hasHitParam = true;
                if (p.nameHash == AnimVariantHash) hasVariantParam = true;
                if (p.nameHash == TouchHash) hasTouchParam = true;
                if (p.nameHash == MeleeIndexHash) hasMeleeIndexParam = true;
                if (p.nameHash == ShoveHash) hasShoveParam = true;
                if (p.nameHash == ShoveHeavyHash) hasShoveHeavyParam = true;
                if (p.name == "MoveInMetres") UsesVelocityBlend = true;
            }
            animator.SetInteger(WeaponClassHash, (int)currentWeaponClass);
            if (hasVariantParam) animator.SetFloat(AnimVariantHash, (float)animationVariant);   // no blend on spawn
            upperBodyLayer = animator.GetLayerIndex("UpperBody");
            femaleLegsLayer = animator.GetLayerIndex("FemaleLegs");   // -1 if the female pack wasn't imported when the controller was built
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

    float groundedHoldUntil;

    /// <summary>Report "grounded" to the Animator for this many seconds whatever the ground check says. Used while a
    /// script carries the character (vault/climb): its CharacterController/agent is switched off, which reads as
    /// airborne and would drop the Animator into the jump states the moment the clip ends.</summary>
    public void HoldGrounded(float seconds) { groundedHoldUntil = Mathf.Max(groundedHoldUntil, Time.time + seconds); }

    public void SetGrounded(bool grounded)
    {
        if (Time.time < groundedHoldUntil) grounded = true;
        if (CanAnimate()) animator.SetBool(IsGroundedHash, grounded);
    }

    /// <summary>The REAL ground contact (IsGrounded may be switched on early to start the landing).</summary>
    public void SetTouchingGround(bool touching)
    {
        if (Time.time < groundedHoldUntil) touching = true;
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
        // A melee weapon with a MeleeWeapon component is a proper two-handed sword (own stance and slashes); any
        // other melee prop stays a held prop with the unarmed locomotion.
        if (weapon.isMeleeWeapon)
        {
            SetWeaponClass(weapon.GetComponent<MeleeWeapon>() != null ? CharacterWeaponClass.Sword : CharacterWeaponClass.Unarmed);
            return;
        }
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

    // ---- full-body one-shot actions (shove, shoved, vault, gestures) -------------------------------------
    // These play on the base layer so the legs and torso are animated too. While one plays the masked UpperBody
    // layer is faded out (it would otherwise pin the arms/spine to the aim or idle pose) and WeaponHandIK is told,
    // per hand, how much of that arm to hand to the clip: 1 = the clip owns the hand, 0 = it stays on the weapon
    // grip. Everything blends both ways, so hands ease into and out of the animation instead of snapping.
    WeaponHandIK handIK;
    Coroutine upperFade;
    Coroutine actionRelease;
    bool fullBodyAction;

    public WeaponHandIK Hands
    {
        get
        {
            // Keep looking until it exists: EnemyWeapon adds the WeaponHandIK when it equips its gun, which can be
            // after the first time anything asks for it. Caching that early "null" meant a full-body action
            // never handed an arm to its clip (the hand stayed glued to the gun while the body moved).
            if (handIK == null)
            {
                handIK = GetComponentInParent<WeaponHandIK>();
                if (handIK == null) handIK = GetComponentInChildren<WeaponHandIK>();
            }
            return handIK;
        }
    }

    public bool InFullBodyAction => fullBodyAction;
    public bool HasBaseState(string name) => CanAnimate() && HasState(name);

    // ---- weapon rides in the animated right hand ---------------------------------------------------------
    WeaponHandFollow weaponFollow;

    /// <summary>While true (and the right hand has been handed to a clip) the held weapon moves with that hand and
    /// the left hand stays on its foregrip - see WeaponHandFollow. Works for the player and for AI. Released
    /// automatically by EndFullBodyAction.</summary>
    public void SetWeaponFollowsHand(bool value)
    {
        if (weaponFollow == null)
        {
            if (!value) return;
            var hands = Hands;
            if (hands == null) return;
            weaponFollow = hands.GetComponent<WeaponHandFollow>();
            if (weaponFollow == null) weaponFollow = hands.gameObject.AddComponent<WeaponHandFollow>();
            weaponFollow.handIK = hands;
        }
        weaponFollow.SetActive(value);
    }

    // ---- clip-driven pose (idle gestures) ----------------------------------------------------------------
    // The procedural layers that sit on top of the Animator (TorsoPoseDriver's stabilisers and hip lock,
    // CharacterMotionPolish's head look / weight shift, AnatomicalConstraints' joint limits) are all built to
    // keep a WALKING body steady around the gun. That is exactly wrong while a full-body gesture is playing:
    // they flatten the torso lean, pin the hips over the feet (so a planted foot slides), fight the head nod and
    // clamp a raised foot. While this is above 0 they all back off by that fraction and let the clip through.
    // Eased over ~0.25s both ways so nothing snaps. Read by those scripts; set by AIRelaxedIdle.
    [Header("Clip-Driven Pose (idle gestures)")]
    [Tooltip("How fast the procedural torso/head/limit layers fade out and back in around a gesture (per second, 4 = about 0.25s).")]
    public float clipDrivenBlendSpeed = 4f;
    float clipDrivenTarget, clipDrivenValue;
    int clipDrivenFrame = -1;

    /// <summary>0 = normal procedural body, 1 = the playing clip owns the torso, head and legs. Eased, cached per frame.</summary>
    public float ClipDriven
    {
        get
        {
            if (clipDrivenFrame != Time.frameCount)
            {
                clipDrivenFrame = Time.frameCount;
                clipDrivenValue = Mathf.MoveTowards(clipDrivenValue, clipDrivenTarget, clipDrivenBlendSpeed * Time.deltaTime);
            }
            return clipDrivenValue;
        }
    }

    public void SetClipDriven(float target) { clipDrivenTarget = Mathf.Clamp01(target); }

    /// <summary>Crossfades the base layer into a one-shot state and hands the given fraction of each arm to the clip.
    /// False (and nothing changes) if the controller doesn't contain that state yet.</summary>
    public bool CrossFadeBase(string stateName, float fade, float rightHandFollow, float leftHandFollow)
    {
        if (!HasBaseState(stateName)) return false;
        animator.CrossFadeInFixedTime(Animator.StringToHash(stateName), Mathf.Max(0.01f, fade), 0);
        BlendOutUpperBody(0.1f);
        Hands?.SetAnimationFollow(rightHandFollow, leftHandFollow);
        fullBodyAction = true;
        return true;
    }

    /// <summary>Cuts a full-body clip short: crossfades the base layer straight back to the pose for the current
    /// weapon (Unarmed / Pistol / Rifle). A gesture that just plays out needs none of this (its state exits by
    /// itself on the last frame); this is for being interrupted half-way - e.g. combat starting while the
    /// character has a foot raised. False if the controller has no such state.</summary>
    public bool ReturnToWeaponPose(float fade = 0.25f)
    {
        if (!CanAnimate()) return false;
        string state = currentWeaponClass == CharacterWeaponClass.Rifle ? "Rifle"
                     : currentWeaponClass == CharacterWeaponClass.Pistol ? "Pistol"
                     : currentWeaponClass == CharacterWeaponClass.Sword ? "Sword" : "Unarmed";
        if (!HasState(state)) return false;
        animator.CrossFadeInFixedTime(Animator.StringToHash(state), Mathf.Max(0.01f, fade), 0);
        return true;
    }

    /// <summary>Gives both arms back to the weapon grip and fades the UpperBody layer back in.</summary>
    public void EndFullBodyAction(float upperBodyFade = 0.2f)
    {
        if (actionRelease != null) { StopCoroutine(actionRelease); actionRelease = null; }
        Hands?.ClearAnimationFollow();
        SetWeaponFollowsHand(false);
        RestoreUpperBody(upperBodyFade);
        fullBodyAction = false;
    }

    /// <summary>Ends the current full-body action after `seconds`.</summary>
    public void EndFullBodyActionAfter(float seconds)
    {
        if (actionRelease != null) StopCoroutine(actionRelease);
        actionRelease = StartCoroutine(EndActionRoutine(seconds));
    }

    IEnumerator EndActionRoutine(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        actionRelease = null;
        EndFullBodyAction();
    }

    /// <summary>Right-click shove. Light (tap) is the punch - authored left-handed and mirrored, so the RIGHT hand
    /// follows the clip while the left stays on the weapon grip. Heavy (hold) is the kick - hands stay on the grip.
    /// The UpperBody layer is faded out for the duration (see above).</summary>
    public void PlayShove(bool heavy = false)
    {
        if (!hasShoveParam || !CanAnimate()) return;
        if (hasShoveHeavyParam) animator.SetBool(ShoveHeavyHash, heavy);
        animator.SetTrigger(ShoveHash);
        BlendOutUpperBody(0.08f);
        Hands?.SetAnimationFollow(heavy ? 0f : 1f, 0f);
        // The punch: the gun goes with the punching (right) hand and the left hand stays on its foregrip.
        // The kick keeps both hands on the gun, so the gun stays put.
        SetWeaponFollowsHand(!heavy);
        if (heavy) StartKickCamera();
        fullBodyAction = true;
        EndFullBodyActionAfter((heavy ? ShoveHeavyDuration : ShoveLightDuration) * 0.78f);
        if (polish != null) StartCoroutine(LungeAfter(ShoveStrikeDelayFor(heavy) * 0.6f));
    }

    [Header("Kick Shove Camera (player)")]
    [Tooltip("Degrees the view tips UP during the kick shove, then comes back down - so the camera moves with the body and head. 0 = off.")]
    public float kickCameraPitch = 12f;
    [Tooltip("Seconds to tip up (the leg coming up).")]
    public float kickCameraUpTime = 0.22f;
    [Tooltip("Seconds held at the top (the kick landing).")]
    public float kickCameraHoldTime = 0.1f;
    [Tooltip("Seconds to settle back down.")]
    public float kickCameraDownTime = 0.45f;
    Coroutine kickCamera;
    PlayerMovement kickCameraOwner;

    void StartKickCamera()
    {
        if (kickCameraPitch <= 0.01f) return;
        if (kickCameraOwner == null) kickCameraOwner = GetComponentInParent<PlayerMovement>();
        if (kickCameraOwner == null) return; // AI have no camera
        if (kickCamera != null) StopCoroutine(kickCamera);
        kickCamera = StartCoroutine(KickCameraRoutine());
    }

    IEnumerator KickCameraRoutine()
    {
        float t = 0f;
        float total = kickCameraUpTime + kickCameraHoldTime + kickCameraDownTime;
        while (t < total)
        {
            t += Time.deltaTime;
            float k;
            if (t < kickCameraUpTime) k = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.01f, kickCameraUpTime));
            else if (t < kickCameraUpTime + kickCameraHoldTime) k = 1f;
            else k = 1f - Mathf.SmoothStep(0f, 1f, (t - kickCameraUpTime - kickCameraHoldTime) / Mathf.Max(0.01f, kickCameraDownTime));
            kickCameraOwner.SetCameraActionPitch(kickCameraPitch * k);
            yield return null;
        }
        kickCameraOwner.SetCameraActionPitch(0f);
        kickCamera = null;
    }

    void OnDisable()
    {
        if (kickCamera != null) { StopCoroutine(kickCamera); kickCamera = null; }
        if (kickCameraOwner != null) kickCameraOwner.SetCameraActionPitch(0f);
    }

    [Header("Shoved Reaction")]
    [Tooltip("How much of each arm follows the stagger clip while shoved (the rest stays on the weapon grip, so a held gun isn't dropped).")]
    [Range(0f, 1f)] public float shovedHandFollow = 0.6f;
    [Tooltip("Seconds the shoved stagger holds the arms/upper body before handing them back (raw clip length / playback speed is about 1.05-1.3s).")]
    public float shovedDuration = 1.0f;

    /// <summary>Plays the stagger for a body pushed along `pushDirectionWorld` (the way it is being shoved - away from
    /// whoever shoved it). The clip is picked by which side of the body the shove came FROM. False if the controller
    /// has no shoved states (rebuild the animation system) so the caller can fall back to a plain flinch.</summary>
    public bool PlayShoved(Vector3 pushDirectionWorld)
    {
        if (!CanAnimate() || pushDirectionWorld.sqrMagnitude < 1e-4f) return false;
        Vector3 from = animator.transform.InverseTransformDirection(-pushDirectionWorld);
        from.y = 0f;
        string state;
        if (Mathf.Abs(from.x) > Mathf.Abs(from.z)) state = from.x > 0f ? "Shoved_Right" : "Shoved_Left";
        else state = from.z > 0f ? "Shoved_Front" : "Shoved_Back";
        if (!CrossFadeBase(state, 0.1f, shovedHandFollow, shovedHandFollow)) return false;
        EndFullBodyActionAfter(shovedDuration);
        return true;
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

    // Eases the animator's AnimVariant parameter to the chosen set. Polled, so changing the dropdown in the Inspector
    // during Play, or calling SetAnimationVariant, both take effect straight away.
    void Update()
    {
        if (animator == null) return;

        if (hasVariantParam)
        {
            float target = (float)animationVariant;
            float current = animator.GetFloat(AnimVariantHash);
            if (!Mathf.Approximately(current, target))
            {
                if (variantBlendTime <= 0f || Mathf.Abs(current - target) < 0.002f) animator.SetFloat(AnimVariantHash, target);
                else animator.SetFloat(AnimVariantHash, target, variantBlendTime, Time.deltaTime);
            }
        }

        // Pistol / rifle carriers have no female stance (the pack is unarmed only), so the female gait goes on the legs alone:
        // the FemaleLegs layer fades in under the armed pose. It is off whenever the base layer owns the legs for a reason of
        // its own - crouching, in the air, injured, or a full-body action (shove, vault, death).
        if (femaleLegsLayer >= 0 && CanAnimate())
        {
            bool armed = currentWeaponClass == CharacterWeaponClass.Pistol || currentWeaponClass == CharacterWeaponClass.Rifle;
            bool on = animationVariant == AnimationVariant.Female && armed && !IsCrouching && !fullBodyAction
                      && animator.GetBool(IsGroundedHash) && !animator.GetBool(InjuredHash);
            float step = variantBlendTime > 0.01f ? Time.deltaTime / variantBlendTime : 1f;
            float w = Mathf.MoveTowards(animator.GetLayerWeight(femaleLegsLayer), on ? 1f : 0f, step);
            animator.SetLayerWeight(femaleLegsLayer, w);
        }
    }

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

        // Chance-based: a body that was moving when it was shot may stumble to the floor instead of dropping.
        if (req.sustainedMovement && req.stumbleChance > 0f && Random.value < req.stumbleChance && TryChooseStumble(req, out choice))
            return true;

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
            if (d.moving && !req.sustainedMovement) continue; // "walking to dying" only after real sustained movement, not a single fast frame

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

    // Stumble-to-the-floor clips, one per side the shot came from. handoff = fraction of the clip at which the
    // ragdoll takes over, measured directly from each clip (shortly before the hips drop below 40cm - much
    // earlier than the normal deaths' ~1/3 because these clips carry the character most of the way to the floor
    // themselves before the ragdoll needs to take over).
    struct StumbleDef { public string state; public float handoff; }
    static readonly StumbleDef StumbleFront = new StumbleDef { state = "Death_StumbleFront", handoff = 0.35f };
    static readonly StumbleDef StumbleBack  = new StumbleDef { state = "Death_StumbleBack",  handoff = 0.30f };
    static readonly StumbleDef StumbleLeft  = new StumbleDef { state = "Death_StumbleLeft",  handoff = 0.33f };
    static readonly StumbleDef StumbleRight = new StumbleDef { state = "Death_StumbleRight", handoff = 0.50f };

    /// <summary>Picks the stumble clip matching the side the killing shot came from (hitDirLocal: the direction the
    /// shot was TRAVELLING, character-local - a shot travelling towards -z came from the front). False if the
    /// controller has none of the four states yet.</summary>
    bool TryChooseStumble(DeathRequest req, out DeathChoice choice)
    {
        choice = default;
        Vector3 hit = new Vector3(req.hitDirLocal.x, 0f, req.hitDirLocal.z);
        if (hit.sqrMagnitude < 1e-4f) hit = new Vector3(req.fallDirLocal.x, 0f, req.fallDirLocal.z);
        if (hit.sqrMagnitude < 1e-4f) return false;

        StumbleDef def;
        if (Mathf.Abs(hit.x) > Mathf.Abs(hit.z)) def = hit.x > 0f ? StumbleLeft : StumbleRight;   // travelling +x = came from the left
        else def = hit.z < 0f ? StumbleFront : StumbleBack;                                        // travelling -z = came from the front
        if (!HasState(def.state)) return false;

        choice = new DeathChoice
        {
            stateName = def.state,
            stateHash = Animator.StringToHash(def.state),
            fallDirLocal = hit,
            handoffPoint = def.handoff,
            skipSlide = true      // the clip already carries the body's own travel to the floor
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
        if (upperFade != null) StopCoroutine(upperFade);
        upperFade = StartCoroutine(FadeLayer(upperBodyLayer, duration));
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