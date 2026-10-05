using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A two-handed melee weapon (Sword or Axe animations): put it on a weapon prefab next to WeaponController
/// (isMeleeWeapon on), WeaponADS, WeaponSway and WeaponMovementBob. The Sledgehammer prefab is set up this way.
///
///   Attack Style   dropdown on this component: Sword (Great Sword pack - chop, sweep, thrust, heavy spin) or Axe
///                  (Pro Melee Axe pack - horizontal, backhand, downward, heavy 360). Per weapon, and it can be changed
///                  while playing (SetStyle). Only the animations change.
///   Left click     TAP = light attack. Click again as the blade lands (or a touch before - clicks are buffered) and the
///                  next swing of the chain cuts straight in. The chain is three swings, then back to the first.
///   Left click     HOLD (longer than heavyHoldTime) = heavy attack. The arm draws back and holds at the top of the
///                  wind-up while you keep the button down (charging, up to maxChargeTime); LET GO to swing. A longer
///                  charge hits harder. Let go before the wind-up is done and it swings as soon as it is.
///   Right click    shove / kick, exactly as with any other weapon (PlayerShove).
///   Shift (aim)    guard: the blade comes up in front of you (WeaponADS's aim pose).
///
/// Light attacks fire when the button is RELEASED (a tap) - that is how a tap and a hold are told apart - so there is a
/// few hundredths of a second between the click and the swing; lower heavyHoldTime to make it snappier.
///
/// HOW IT HOLDS (same machinery as the guns):
///  - The weapon root hangs off the camera and WeaponADS owns its pose: hipPosition/hipRotation are the carry (blade
///    up and across the body, diagonal in front), adsPosition/adsRotation the guard. Sway, walk bob and the holster
///    / draw animation come with it. The root's +Z is the blade (like a gun's muzzle), so the carry is pitch + yaw.
///  - WeaponHandIK puts the right hand on RightHandGrip (low on the hilt) and the left on LeftHandGrip (middle).
///    The grips are children of the "Model" node, whose own frame has the blade along +X, and THIS script places
///    them every frame: each hand's rotation is taken from the guns' own grips (so the wrist and fingers sit the
///    way the existing hand IK expects) and turned so the hilt runs through the fist, and the position is solved from
///    the hand's own finger bones so the hilt lands in the palm whatever this rig's bone axes are. Everything is a
///    plain field below - edit it in Play Mode and the hands move live. If a wrist looks twisted, dial the roll
///    (rightGripEulerOffset.x / leftGripEulerOffset.x, a turn about the blade) until it sits right; axeStyle* adds to
///    that only while the style is Axe, for when the axe clips want the wrist turned differently.
///
/// HOW IT SWINGS: the swing is a clip played on the masked UPPER body (CharacterAnimationDriver.PlayMeleeSwing), so the
/// legs keep walking underneath. While it plays both arms are handed to the clip, the weapon rides in the right hand
/// (WeaponHandFollow) and the left hand stays on the grip. Damage lands when the blade connects (timings measured from
/// the clips - MeleeSwings), in a cone in front of the camera, with a stagger and push on whatever is hit. On a hit the
/// animation freezes for a blink and the camera dips (hitStop / cameraKick) so it lands.
///
/// REQUIRES the animation system to be rebuilt (Tools/FPS Game/Build Animation System) - that is what adds the swing
/// states and parameters to the controller.
/// </summary>
[DefaultExecutionOrder(-60)]
public class MeleeWeapon : MonoBehaviour
{
    [System.Serializable]
    public class Swing
    {
        public string label = "Swing";
        [Tooltip("Multiplier on the weapon's damage.")]
        public float damageMultiplier = 1f;
        [Tooltip("How far in front of the camera the blade reaches (metres).")]
        public float range = 2.3f;
        [Tooltip("Half-angle of the hit cone around where you are looking (degrees).")]
        [Range(5f, 120f)] public float coneHalfAngle = 55f;
        [Tooltip("How far a living target is pushed back (metres).")]
        public float pushDistance = 1f;
        [Tooltip("Seconds a hit target is staggered.")]
        public float stagger = 0.5f;
        [Tooltip("Degrees the view dips when the blade connects.")]
        public float cameraKick = 2.2f;

        public Swing() { }
        public Swing(string label, float damageMultiplier, float range, float cone, float push, float stagger, float kick)
        { this.label = label; this.damageMultiplier = damageMultiplier; this.range = range; coneHalfAngle = cone; pushDistance = push; this.stagger = stagger; cameraKick = kick; }
    }

    [Header("Damage")]
    public float damage = 55f;
    [Header("Attack Style")]
    [Tooltip("Which animation set this weapon swings with: Sword (Great Sword pack) or Axe (Pro Melee Axe pack).")]
    public MeleeStyle style = MeleeStyle.Sword;

    [Header("Swings - damage / reach per attack")]
    [Tooltip("Sword light combo, in order: overhead chop, cross sweep, thrust.")]
    public Swing[] swordSwings =
    {
        new Swing("Overhead chop", 1.2f, 2.2f, 40f, 1.0f, 0.6f, 2.6f),
        new Swing("Cross sweep",   1.0f, 2.4f, 80f, 1.4f, 0.5f, 1.8f),
        new Swing("Thrust",        1.4f, 2.8f, 28f, 2.2f, 0.9f, 3.2f),
    };
    [Tooltip("Sword heavy: the high spin attack.")]
    public Swing swordHeavy = new Swing("Spin attack", 2.0f, 2.5f, 100f, 2.4f, 1.0f, 4.0f);
    [Tooltip("Axe light combo, in order: horizontal, backhand, downward.")]
    public Swing[] axeSwings =
    {
        new Swing("Horizontal", 1.1f, 2.3f, 85f, 1.2f, 0.6f, 2.4f),
        new Swing("Backhand",   1.1f, 2.3f, 85f, 1.2f, 0.6f, 2.4f),
        new Swing("Downward",   1.5f, 2.4f, 35f, 0.8f, 0.9f, 3.4f),
    };
    [Tooltip("Axe heavy: the big 360 swing.")]
    public Swing axeHeavy = new Swing("360 swing", 2.4f, 2.6f, 75f, 2.8f, 1.2f, 5.0f);

    [Tooltip("Shown to the world when this weapon is dropped (the pickup that gives it back).")]
    public GameObject pickupPrefab;

    [Header("Combo")]
    [Tooltip("A click this long before the next swing is allowed is remembered and fires the moment it is.")]
    public float inputBuffer = 0.3f;
    [Tooltip("Seconds after a swing finishes that the chain still continues to the next swing; later and it restarts at the chop.")]
    public float comboWindow = 0.55f;
    [Tooltip("Seconds after the blade lands before the next swing may cut in. Lower = faster chains.")]
    public float chainDelayAfterStrike = 0.10f;
    [Tooltip("Extra pause after the last swing of the light chain.")]
    public float finisherRecovery = 0.25f;

    [Header("Heavy attack (hold the attack button)")]
    [Tooltip("Seconds the button must be held before it counts as a heavy attack instead of a tap.")]
    public float heavyHoldTime = 0.25f;
    [Tooltip("Longest the arm holds at the top of the wind-up before the swing is let go on its own.")]
    public float maxChargeTime = 1.2f;
    [Tooltip("Extra damage at full charge: 0.35 = +35%. Scales with how long it was held.")]
    public float chargeDamageBonus = 0.35f;
    [Tooltip("Seconds after the blade lands before another attack may cut in.")]
    public float heavyChainDelay = 0.2f;
    [Tooltip("Extra pause once a heavy attack is over.")]
    public float heavyRecovery = 0.3f;

    [Header("Feel")]
    [Tooltip("Seconds the animation freezes (almost) when the blade hits something living. 0 = off.")]
    public float hitStop = 0.06f;
    [Range(0f, 1f)] public float hitStopSpeed = 0.08f;
    [Tooltip("Scale on every swing's camera kick when it hits something living. A swing that only hits a wall/floor uses wallKickScale.")]
    public float livingKickScale = 1f;
    public float wallKickScale = 0.6f;
    public float kickDownTime = 0.05f;
    public float kickRecoverTime = 0.28f;

    [Header("Audio (optional)")]
    public AudioClip[] swingClips;
    public AudioClip hitClip;
    public AudioClip wallHitClip;

    [Header("Grips (in the model's frame: blade along +X)")]
    [Tooltip("Where the hilt passes through the right fist - low on the handle.")]
    public Vector3 rightHilt = new Vector3(-0.22f, 0f, 0f);
    [Tooltip("Where the hilt passes through the left fist - the middle.")]
    public Vector3 leftHilt = Vector3.zero;
    [Tooltip("Turn the right hand about the weapon's own axes after the default (degrees). X is a roll about the blade.")]
    public Vector3 rightGripEulerOffset = Vector3.zero;
    public Vector3 leftGripEulerOffset = Vector3.zero;
    [Tooltip("Added to the right grip rotation only while the style is Axe (the axe clips may want the wrist turned differently).")]
    public Vector3 axeStyleRightGripEuler = Vector3.zero;
    [Tooltip("Added to the left grip rotation only while the style is Axe.")]
    public Vector3 axeStyleLeftGripEuler = Vector3.zero;
    [Tooltip("Nudge a hand (model-frame metres) after the palm is placed on the hilt.")]
    public Vector3 rightGripPositionOffset = Vector3.zero;
    public Vector3 leftGripPositionOffset = Vector3.zero;
    [Tooltip("How far along wrist -> knuckles the hilt sits in the fist (1 = at the knuckles).")]
    [Range(0.3f, 1.2f)] public float palmForward = 0.8f;
    [Tooltip("How far the hilt axis sits out of the palm, metres (about the hilt's radius plus a little skin).")]
    public float palmLift = 0.022f;
    [Header("Fingers (degrees of curl round the hilt)")]
    public bool curlFingers = true;
    public FingerGripPose fingerCurl = new FingerGripPose { thumbCurl = 40f, indexCurl = 70f, middleCurl = 75f, ringCurl = 75f, pinkyCurl = 75f };

    [Header("References (all found automatically)")]
    public WeaponController weapon;
    public Transform rightGrip;
    public Transform leftGrip;

    // ---- runtime ------------------------------------------------------------------------------
    PlayerSetup playerSetup;
    WeaponInventory inventory;
    CharacterAnimationDriver driver;
    WeaponHandIK hands;
    Camera cam;
    Transform rightHandBone, leftHandBone;
    Vector3 rightPalmFwd, leftPalmFwd;      // wrist -> knuckles, in each hand bone's own axes, metres
    bool havePalm, setupDone, warnedStates, warnedBones;

    InputAction slashAction;
    float bufferedUntil = -1f;
    bool swinging;
    int swingIndex = -1, lastIndex = -1, swingId;
    bool pendingPress;                    // the button is down and not yet decided: tap (light) or hold (heavy)
    float pressTime;
    enum HeavyPhase { None, WindUp, Hold }
    HeavyPhase heavyPhase = HeavyPhase.None;
    int heavyIndex;
    float heavyApexAt, heavyHoldStart;
    float swingStart, swingEnd, chainOpenAt, nextAllowedAt;

    Coroutine kickRoutine, stopRoutine;
    FingerGripPose savedRight, savedLeft;
    bool savedRightOverride, savedLeftOverride, fingersSaved;
    HashSet<Health> hitThisSwing = new HashSet<Health>();

    // The guns' own hand grips (the AR's), which the hand IK already treats as "a hand holding something": the hand's
    // rotation in a grip's frame is what makes the wrist/fingers read correctly. The sword's grips are these, turned so
    // the hilt runs along the knuckles (right: gun-up becomes blade; left: gun-forward becomes blade).
    static readonly Quaternion ArRightGrip = new Quaternion(0.4432338f, -0.5835341f, -0.44387126f, 0.5157617f);
    static readonly Quaternion ArLeftGrip = new Quaternion(0.52335405f, 0.46327052f, 0.60436046f, -0.3823996f);

    public bool IsSwinging => swinging;

    // ---- lifecycle ----------------------------------------------------------------------------
    void Awake()
    {
        slashAction = PlayerInputMap.Make("SwordSlash", PlayerInputMap.Fire);
        if (weapon == null) weapon = GetComponent<WeaponController>();
        if (rightGrip == null && weapon != null) rightGrip = weapon.rightHandGrip;
        if (leftGrip == null && weapon != null) leftGrip = weapon.leftHandGrip;
    }

    void OnEnable()
    {
        setupDone = false;       // (re)resolve the player and the hands - this weapon may have moved between bodies
        bufferedUntil = -1f;
    }

    bool Setup()
    {
        playerSetup = GetComponentInParent<PlayerSetup>();
        if (playerSetup == null) return false;
        inventory = playerSetup.GetComponent<WeaponInventory>();
        driver = playerSetup.GetComponentInChildren<CharacterAnimationDriver>();
        hands = playerSetup.GetComponentInChildren<WeaponHandIK>(true);
        cam = playerSetup.fpCamera != null ? playerSetup.fpCamera : Camera.main;
        if (driver == null || driver.BodyAnimator == null || !driver.BodyAnimator.isHuman) return false;
        if (rightGrip == null) rightGrip = FindChild("RightHandGrip");
        if (leftGrip == null) leftGrip = FindChild("LeftHandGrip");

        Animator a = driver.BodyAnimator;
        rightHandBone = a.GetBoneTransform(HumanBodyBones.RightHand);
        leftHandBone = a.GetBoneTransform(HumanBodyBones.LeftHand);
        Transform rk = a.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        Transform lk = a.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
        havePalm = rightHandBone != null && leftHandBone != null && rk != null && lk != null;
        if (havePalm)
        {
            rightPalmFwd = rightHandBone.InverseTransformDirection(rk.position - rightHandBone.position);
            leftPalmFwd = leftHandBone.InverseTransformDirection(lk.position - leftHandBone.position);
        }
        else if (!warnedBones)
        {
            warnedBones = true;
            Debug.LogWarning("[MeleeWeapon] Couldn't find the hand/finger bones - the hands will sit at the grip points as authored.", this);
        }

        if (curlFingers && hands != null && !fingersSaved)
        {
            savedRight = Copy(hands.rightGripPose); savedLeft = Copy(hands.leftGripPose);
            savedRightOverride = hands.overrideRightFingers; savedLeftOverride = hands.overrideLeftFingers;
            fingersSaved = true;
            hands.overrideRightFingers = true; hands.overrideLeftFingers = true;
            Paste(hands.rightGripPose, fingerCurl); Paste(hands.leftGripPose, fingerCurl);
        }

        setupDone = true;
        return true;
    }

    Transform FindChild(string childName)
    {
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t.name == childName) return t;
        return null;
    }

    static FingerGripPose Copy(FingerGripPose p)
    {
        return new FingerGripPose { thumbCurl = p.thumbCurl, indexCurl = p.indexCurl, middleCurl = p.middleCurl, ringCurl = p.ringCurl, pinkyCurl = p.pinkyCurl };
    }

    static void Paste(FingerGripPose into, FingerGripPose from)
    {
        into.thumbCurl = from.thumbCurl; into.indexCurl = from.indexCurl; into.middleCurl = from.middleCurl;
        into.ringCurl = from.ringCurl; into.pinkyCurl = from.pinkyCurl;
    }

    void OnDisable()
    {
        // Holstered, dropped or swapped away: put everything this script touched back.
        if (driver != null) driver.CancelMeleeSwing();
        heavyPhase = HeavyPhase.None; pendingPress = false;
        swinging = false;
        swingId++;
        StopAllCoroutines();
        kickRoutine = null; stopRoutine = null;
        if (playerSetup != null && playerSetup.playerMovement != null) playerSetup.playerMovement.SetCameraActionPitch(0f);
        if (driver != null && driver.BodyAnimator != null) driver.BodyAnimator.speed = 1f;

        if (hands != null && fingersSaved)
        {
            Paste(hands.rightGripPose, savedRight); Paste(hands.leftGripPose, savedLeft);
            hands.overrideRightFingers = savedRightOverride; hands.overrideLeftFingers = savedLeftOverride;
        }
        fingersSaved = false;
    }

    void OnDestroy()
    {
        if (slashAction != null) slashAction.Disable();
    }

    // ---- per frame ----------------------------------------------------------------------------
    public void SetStyle(MeleeStyle newStyle) { style = newStyle; }

    void Update()
    {
        if (!setupDone && !Setup()) return;

        ApplyGrips();

        bool pressed = slashAction.WasPressedThisFrame();
        bool held = slashAction.ReadValue<float>() > 0.5f;

        if (pressed) { pendingPress = true; pressTime = Time.time; }

        if (pendingPress)
        {
            if (!held)
            {
                // Let go before heavyHoldTime: a tap - a light attack.
                pendingPress = false;
                bufferedUntil = Time.time + inputBuffer;
            }
            else if (Time.time - pressTime >= heavyHoldTime && CanStartSwing())
            {
                // Held long enough (and the weapon is free): a heavy attack. If the weapon is busy mid-chain this waits
                // until the blade has landed; letting go first turns it into a light attack instead.
                pendingPress = false;
                StartHeavy();
            }
        }

        if (heavyPhase != HeavyPhase.None) UpdateHeavy(held);

        if (swinging && heavyPhase == HeavyPhase.None && Time.time >= swingEnd) swinging = false;

        if (heavyPhase == HeavyPhase.None && bufferedUntil >= Time.time && CanStartSwing())
        {
            bufferedUntil = -1f;
            StartLight();
        }
    }

    bool CanStartSwing()
    {
        if (heavyPhase != HeavyPhase.None) return false;
        if (inventory != null && (inventory.IsHolsterBusy || inventory.IsHolstered || inventory.GrenadeOut)) return false;
        if (playerSetup.playerHealth != null && playerSetup.playerHealth.GetHealthFraction() <= 0f) return false;
        if (swinging) return Time.time >= chainOpenAt;          // cut the next swing in as the blade lands
        if (Time.time < nextAllowedAt) return false;
        if (driver != null && driver.InFullBodyAction) return false;   // a shove / vault / stagger is playing
        return true;
    }

    // ---- grips --------------------------------------------------------------------------------
    void ApplyGrips()
    {
        if (rightGrip != null)
        {
            Quaternion g = Quaternion.Euler(rightGripEulerOffset) * Quaternion.Euler(style == MeleeStyle.Axe ? axeStyleRightGripEuler : Vector3.zero)
                         * Quaternion.AngleAxis(-120f, new Vector3(1f, 1f, 1f)) * ArRightGrip;
            rightGrip.localRotation = g;
            rightGrip.localPosition = GripPosition(g, rightHilt, rightPalmFwd) + rightGripPositionOffset;
        }
        if (leftGrip != null)
        {
            Quaternion g = Quaternion.Euler(leftGripEulerOffset) * Quaternion.Euler(style == MeleeStyle.Axe ? axeStyleLeftGripEuler : Vector3.zero)
                         * Quaternion.AngleAxis(90f, Vector3.up) * ArLeftGrip;
            leftGrip.localRotation = g;
            leftGrip.localPosition = GripPosition(g, leftHilt, leftPalmFwd) + leftGripPositionOffset;
        }
    }

    // The hand bone's origin is the wrist. Put it where it has to be for the palm point (a fraction of the way from the
    // wrist to the knuckles) plus the hilt's radius, towards the side the palm faces (the model's +Y), to land on the hilt.
    Vector3 GripPosition(Quaternion grip, Vector3 hilt, Vector3 wristToKnuckles)
    {
        if (!havePalm) return hilt;
        return hilt - grip * (wristToKnuckles * palmForward) - Vector3.up * palmLift;
    }

    // ---- swinging -----------------------------------------------------------------------------
    bool Play(int index)
    {
        if (driver != null && driver.PlayMeleeSwing(index)) return true;
        if (!warnedStates)
        {
            warnedStates = true;
            Debug.LogWarning("[MeleeWeapon] The animation controller has no melee swing states yet (or this style's clips aren't imported) - run Tools > FPS Game > Build Animation System.", this);
        }
        return false;
    }

    void StartLight()
    {
        int count = MeleeSwings.LightCount;
        int next;
        if (swinging && lastIndex >= 0) next = (lastIndex + 1) % count;                                   // cutting in mid-chain
        else if (lastIndex >= 0 && lastIndex < count - 1 && Time.time <= swingEnd + comboWindow) next = lastIndex + 1;  // just finished one
        else next = 0;                                                                                      // fresh chain

        int index = MeleeSwings.Light(style, next);
        if (!Play(index)) return;

        swinging = true;
        swingIndex = index;
        lastIndex = next;
        swingId++;
        swingStart = Time.time;
        float strike = MeleeSwings.StrikeDelay(index);
        swingEnd = swingStart + MeleeSwings.Duration(index);
        chainOpenAt = swingStart + strike + chainDelayAfterStrike;
        nextAllowedAt = swingEnd + (next == count - 1 ? finisherRecovery : 0f);
        hitThisSwing.Clear();

        StartCoroutine(SwingRoutine(index, swingId, strike, 1f));
    }

    void StartHeavy()
    {
        int index = MeleeSwings.Heavy(style);
        if (!Play(index)) return;

        swinging = true;
        swingIndex = index;
        lastIndex = -1;                       // the light chain starts over afterwards
        swingId++;
        hitThisSwing.Clear();
        heavyIndex = index;
        heavyPhase = HeavyPhase.WindUp;
        heavyApexAt = Time.time + MeleeSwings.ApexDelay(index);
        // Nothing else may start until it has been let go and has played out.
        swingEnd = chainOpenAt = nextAllowedAt = float.MaxValue;
    }

    void UpdateHeavy(bool held)
    {
        if (heavyPhase == HeavyPhase.WindUp)
        {
            if (Time.time < heavyApexAt) return;
            if (held)
            {
                // At the top of the wind-up: freeze the arm there until the button is let go.
                driver.SetSwingHold(true);
                heavyPhase = HeavyPhase.Hold;
                heavyHoldStart = Time.time;
            }
            else ReleaseHeavy(0f);          // let go before the wind-up finished: swing as soon as it has
        }
        else if (heavyPhase == HeavyPhase.Hold)
        {
            float charge = Time.time - heavyHoldStart;
            if (!held || charge >= maxChargeTime) ReleaseHeavy(charge);
        }
    }

    void ReleaseHeavy(float charge)
    {
        float charge01 = maxChargeTime > 0.01f ? Mathf.Clamp01(charge / maxChargeTime) : 0f;
        int index = heavyIndex;
        heavyPhase = HeavyPhase.None;
        driver.SetSwingHold(false);

        float strikeIn = MeleeSwings.ReleaseToStrike(index);
        float exitIn = MeleeSwings.ReleaseToExit(index);
        driver.FinishMeleeSwing(exitIn);

        swingStart = Time.time;
        swingEnd = swingStart + exitIn;
        chainOpenAt = swingStart + strikeIn + heavyChainDelay;
        nextAllowedAt = swingEnd + heavyRecovery;
        swingId++;
        StartCoroutine(SwingRoutine(index, swingId, strikeIn, 1f + chargeDamageBonus * charge01));
    }

    IEnumerator SwingRoutine(int index, int id, float strikeDelay, float scale)
    {
        // Whoosh just before the blade is at full speed.
        float whoosh = Mathf.Max(0f, strikeDelay - 0.12f);
        yield return new WaitForSeconds(whoosh);
        if (id != swingId) yield break;
        PlayClip(swingClips != null && swingClips.Length > 0 ? swingClips[Random.Range(0, swingClips.Length)] : null);

        yield return new WaitForSeconds(strikeDelay - whoosh);
        if (id != swingId) yield break;
        Strike(index, scale);
    }

    Swing GetSwing(int index)
    {
        bool axe = index / MeleeSwings.PerStyle == (int)MeleeStyle.Axe;
        int n = index % MeleeSwings.PerStyle;
        if (n == MeleeSwings.PerStyle - 1) return axe ? axeHeavy : swordHeavy;
        Swing[] list = axe ? axeSwings : swordSwings;
        return list != null && n < list.Length ? list[n] : new Swing();
    }

    void Strike(int index, float scale)
    {
        Swing s = GetSwing(index);

        Transform eye = ThirdPersonMode.Active && ThirdPersonMode.Eye != null ? ThirdPersonMode.Eye : (cam != null ? cam.transform : transform);
        Vector3 origin = eye.position;
        Vector3 forward = eye.forward;
        Transform self = playerSetup.transform;

        bool hitLiving = false;
        foreach (Collider col in Physics.OverlapSphere(origin + forward * s.range * 0.5f, s.range * 0.75f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (col.transform.IsChildOf(self)) continue;
            Health h = col.GetComponentInParent<Health>();
            if (h == null || hitThisSwing.Contains(h)) continue;

            Vector3 point = col.ClosestPoint(origin);
            Vector3 to = point - origin;
            float dist = to.magnitude;
            if (dist > s.range) continue;
            if (dist > 0.05f && Vector3.Angle(forward, to) > s.coneHalfAngle) continue;
            // Not through a wall.
            if (Physics.Linecast(origin, point, out RaycastHit block, ~0, QueryTriggerInteraction.Ignore)
                && !block.collider.transform.IsChildOf(self) && block.collider.GetComponentInParent<Health>() != h) continue;

            hitThisSwing.Add(h);
            hitLiving = true;

            Vector3 away = h.transform.position - self.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.ProjectOnPlane(forward, Vector3.up);
            away.Normalize();

            // Push first, then damage - a killing blow should still read as a blow (the ragdoll takes over).
            h.PushBack(away, s.pushDistance * scale, s.stagger);
            h.TakeDamage(damage * s.damageMultiplier * scale, forward, 6f * s.damageMultiplier * scale);
        }

        if (hitLiving)
        {
            PlayClip(hitClip);
            Kick(s.cameraKick * livingKickScale * scale);
            if (hitStop > 0f) { if (stopRoutine != null) StopCoroutine(stopRoutine); stopRoutine = StartCoroutine(HitStop()); }
        }
        else if (Physics.Raycast(origin, forward, out RaycastHit wall, s.range, ~0, QueryTriggerInteraction.Ignore) && !wall.collider.transform.IsChildOf(self))
        {
            PlayClip(wallHitClip);
            Kick(s.cameraKick * wallKickScale);
        }
    }

    static void PlayClip(AudioClip clip)
    {
        if (clip != null && AudioManager.Instance != null) AudioManager.Instance.Play(clip);
    }

    // ---- feel ---------------------------------------------------------------------------------
    void Kick(float degrees)
    {
        if (degrees <= 0.01f || playerSetup == null || playerSetup.playerMovement == null) return;
        if (kickRoutine != null) StopCoroutine(kickRoutine);
        kickRoutine = StartCoroutine(KickRoutine(degrees));
    }

    IEnumerator KickRoutine(float degrees)
    {
        PlayerMovement pm = playerSetup.playerMovement;
        float t = 0f;
        while (t < kickDownTime)
        {
            t += Time.unscaledDeltaTime;
            pm.SetCameraActionPitch(-degrees * Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.01f, kickDownTime)));
            yield return null;
        }
        t = 0f;
        while (t < kickRecoverTime)
        {
            t += Time.unscaledDeltaTime;
            pm.SetCameraActionPitch(-degrees * (1f - Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.01f, kickRecoverTime))));
            yield return null;
        }
        pm.SetCameraActionPitch(0f);
        kickRoutine = null;
    }

    IEnumerator HitStop()
    {
        Animator a = driver != null ? driver.BodyAnimator : null;
        if (a == null) yield break;
        a.speed = hitStopSpeed;
        yield return new WaitForSecondsRealtime(hitStop);
        a.speed = 1f;
        stopRoutine = null;
    }
}