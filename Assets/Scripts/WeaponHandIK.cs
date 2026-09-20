using UnityEngine;

/// <summary>
/// Per-hand finger curl amounts applied on top of whatever the animation clip is doing
/// with the fingers, so the hand actually closes around the grip instead of hanging
/// open/flat against it. Degrees are the curl added at each knuckle; tune by eye in Play
/// Mode with the game paused on a firing/idle frame - a light grip is usually only
/// 15-25 degrees at the base knuckle, a tight fist-like grip is 60-80.
/// </summary>
[System.Serializable]
public class FingerGripPose
{
    [Tooltip("No longer used - the curl axis is now worked out per finger joint from the rig's bind pose (see WeaponHandIK finger override). Kept only so existing serialized values don't break.")]
    public Vector3 curlAxis = new Vector3(1f, 0f, 0f);
    [Range(0f, 90f)] public float thumbCurl = 25f;
    [Range(0f, 90f)] public float indexCurl = 45f;
    [Range(0f, 90f)] public float middleCurl = 50f;
    [Range(0f, 90f)] public float ringCurl = 50f;
    [Range(0f, 90f)] public float pinkyCurl = 50f;
}

/// <summary>
/// Fixes the "shooting animation doesn't match the weapon" problem WITHOUT rebuilding
/// the camera/body/weapon hierarchy.
///
/// The weapon stays a child of the camera (so aim/recoil/sway/lean all keep working
/// exactly as before). This script runs after the Animator has posed the arms from the
/// retargeted Mixamo clips, and uses Unity's built-in Animator IK pass to bend the hand
/// bones the rest of the way onto two small empty transforms you place on the weapon
/// itself - RightHandGrip (and optionally LeftHandGrip for two-handed weapons). Because
/// it runs after animation and reads the weapon's live position, it stays correct no
/// matter how the camera pitches or how far the baked clip's hand position is from the
/// weapon's actual grip - unlike trying to re-align the whole body/camera to fix it.
///
/// SETUP (per weapon prefab):
///  1. Add an empty child GameObject where the right hand should sit on the grip,
///     rotated so its local +Z (forward) points the same way the hand's fingers curl
///     around the grip. Name it e.g. "RightHandGrip".
///  2. For two-handed weapons (rifles), add a second empty at the forward hand/foregrip,
///     named e.g. "LeftHandGrip".
///  3. Assign these to WeaponController.rightHandGrip / leftHandGrip in the Inspector.
///  4. Put this script on the SAME GameObject as the player's Animator (the body), next
///     to CharacterAnimationDriver. PlayerSetup wires it automatically on weapon switch.
///  5. Re-run Tools/FPS Game/Build Animation System once after pulling this change - it
///     now also enables the IK pass on the base Animator layer, which this script needs.
///
/// Tune elbowHint if the elbows still pop to a weird angle after the automatic reach
/// clamp/fallback hint below - it's an optional world-space point the forearm bends
/// towards (roughly out to the character's side and slightly forward works for most
/// rigs). armReachSafety controls how far a hand is allowed to reach before being
/// clamped back to the arm's own measured length, which is the main fix for arms
/// bending backwards/mangled when a grip point is placed somewhere hard to reach.
/// </summary>
[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(300)] // LateUpdate after everything that poses bones (UpperBodyPose, TorsoMotionDampener...)
public class WeaponHandIK : MonoBehaviour
{
    [Tooltip("How fast the IK weight blends in/out when weapons change or grips are cleared.")]
    public float blendSpeed = 8f;

    [Tooltip("Optional world-space elbow hint transforms (leave empty if not needed) - if left empty, a reasonable forward/outward hint is computed automatically each frame so the elbow has a sane direction to bend towards instead of the IK solver picking an arbitrary (sometimes backward-looking) one.")]
    public Transform rightElbowHint;
    public Transform leftElbowHint;

    [Tooltip("Safety margin (fraction of the arm's actual measured upper-arm+forearm length) grip targets are clamped within. Prevents a grip point placed too far away - or a moving weapon transform that briefly overshoots - from forcing the elbow past full extension, which is what causes the arm to snap/bend the wrong way instead of just stopping at a straight arm.")]
    [Range(0.5f, 1f)] public float armReachSafety = 0.95f;

    [Header("Finger Grip")]
    [Tooltip("Curl applied to the right hand's fingers while it's gripping (weight > 0). Fades in/out with the same weight as the hand IK itself, so an empty hand relaxes back to the animated pose.")]
    public FingerGripPose rightGripPose = new FingerGripPose();
    [Tooltip("Curl applied to the left hand's fingers while it's gripping (weight > 0).")]
    public FingerGripPose leftGripPose = new FingerGripPose();
    [Header("Strict Hand Lock")]
    [Tooltip("After the Animator, the IK pass and every other script that touches the body (UpperBodyPose, TorsoMotionDampener...) have finished, snap each hand exactly onto its weapon grip with a final two-bone solve. The gun is the master; the hands can no longer lag behind it or be pulled off by a later script. The shoulders, upper arms and elbows keep their animation (walk bounce etc.) - only the hand placement is guaranteed.")]
    public bool strictHandLock = true;
    [Tooltip("How far (metres) the hand may stretch past the arm's full length to stay on the grip. Beyond that the hand falls short and the existing weapon pull-back closes the gap.")]
    public float lockMaxStretch = 0.05f;

    [Header("Finger Curl Override")]
    [Tooltip("Finger rotations added inside OnAnimatorIK are overwritten by the Animator straight afterwards (which is why tweaking curl values changed nothing). The grip is now applied in LateUpdate, AFTER the Animator, and it REPLACES the animated finger pose (blended in by the grip weight) instead of adding to it - so a hand whose clips curl backwards is corrected as well.")]
    public bool overrideLeftFingers = true;
    public bool overrideRightFingers = false;
    [Tooltip("Tick if that hand's fingers curl AWAY from the palm. The curl direction is worked out from the bind pose assuming the palms face down in it (a T-pose).")]
    public bool invertLeftCurl = false;
    public bool invertRightCurl = false;

    [Header("Weapon Pull-Back (keeps the gun in reach)")]
    [Tooltip("When aiming makes the weapon (parented to the camera) move out beyond the arms' actual reach, the left hand on two-handed weapons usually runs out first, since it travels further from the body. Rather than let the hand visibly stop short of the grip (which the arm reach clamp above would otherwise do), the weapon is pulled back toward whichever shoulder is short by however much - in whatever direction actually closes the gap, not just straight back, so this helps for looking down as well as up. Set to 0 to disable and go back to the hand just clamping short.")]
    public float maxPullBack = 0.25f;
    [Tooltip("How fast the pull-back eases in/out as the required amount/direction changes.")]
    public float pullBackSpeed = 10f;
    [Tooltip("Shortfall (metres) ignored before any pull-back happens at all. Without a deadzone the weapon reacts to the last millimetre of every walk-cycle wobble, which is what makes it twitch side to side while moving.")]
    public float pullBackDeadzone = 0.04f;
    [Tooltip("Seconds of smoothing on the pull-back. Higher = the weapon drifts to its new spot instead of snapping there each step.")]
    public float pullBackSmoothTime = 0.25f;
    [Tooltip("How much of the pull-back is allowed SIDEWAYS (in the weapon holder's local X). 0 means the weapon is only ever pulled back/up/down, never left or right - this is the fix for the gun and arm flicking to the right and back while walking, which is the animated shoulder swinging in and out of reach once per step and dragging the weapon with it.")]
    [Range(0f, 1f)] public float lateralPullBackFraction = 0f;
    [Tooltip("Measure reach from the shoulder's RESTING position (its bind spot on the body) rather than its live animated position. The live shoulder swings several centimetres every step, so the 'how far short is the hand' measurement - and therefore the pull-back - oscillates with the walk cycle even when nothing about the aim has changed. Leave this on unless you specifically want the weapon to breathe with the animation.")]
    public bool measureReachFromRestingShoulder = true;

    [Header("Torso Lean (helps the off-hand reach on two-handed weapons)")]
    [Tooltip("Degrees the chest twists to bring the off-hand's shoulder closer to its grip on two-handed weapons (e.g. a rifle's foregrip), rather than relying on arm stretch and pull-back alone. Set to 0 to disable. Flip the sign if it twists the wrong way for your rig.")]
    public float maxTorsoLean = 8f;
    [Tooltip("How fast the torso lean eases in/out.")]
    public float torsoLeanSpeed = 6f;

    Animator anim;
    Transform rightGrip;
    Transform leftGrip;
    Transform reloadRightOverride;
    Transform reloadLeftOverride;
    System.Action reloadAnchorRefresh; // re-places the reload hand target against the FINAL weapon pose (see LateUpdate)
    WeaponHandIKAnimatorBridge animatorBridge;
    float rightWeight;
    Transform lockRightGrip, lockLeftGrip;      // set by ApplyIK each pass; null during reload overrides
    Transform rightLowerArm, leftLowerArm;
    float leftWeight;

    // Cached once in Awake for the reach clamp and fallback elbow hint below.
    Transform rightShoulder, rightHandBone;
    Transform leftShoulder, leftHandBone;
    Vector3 rightShoulderRestLocal, leftShoulderRestLocal; // in animator-root space, captured once
    Vector3 pullBackVelocity; // SmoothDamp state
    float rightArmReach = Mathf.Infinity;
    float leftArmReach = Mathf.Infinity;
    Transform chestBone;
    float currentTorsoLean;

    // Weapon pull-back state. The pull-back is no longer written onto the weapon
    // transform here - it's handed to WeaponADS (the single owner of the weapon root),
    // which composes it in Update BEFORE the Animator's IK pass runs. Two reasons:
    //  - This script used to move the weapon in LateUpdate, i.e. after it had already
    //    solved the hands against the weapon's earlier position. That one-frame
    //    mismatch is the hand/gun jitter when looking up while walking.
    //  - Measuring "how far short is the hand" against a weapon this script had itself
    //    already pulled back is a feedback loop: pull back -> gap closes -> pull-back
    //    eases off -> gap opens -> pull back again, which oscillates. appliedPullBack
    //    below is subtracted back out before measuring, so the required pull-back is a
    //    stable function of the camera/body pose alone.
    WeaponADS offsetOwner;
    Vector3 pullBackOffset;       // world-space pull-back being requested, eased each frame
    Vector3 appliedPullBack;      // what the owner is currently applying, for the measurement above
    int lastIKFrame = -1;         // the IK pass runs once per IK-enabled layer (base + UpperBody);
                                  // per-frame state must only advance on the first of them

    // [proximal, intermediate, distal] per finger, thumb through pinky.
    Transform[][] rightFingerBones;
    Transform[][] leftFingerBones;

    // Per-joint bind-pose local rotation + local curl axis, built once in Awake.
    class FingerRig { public Quaternion[][] bind; public Vector3[][] axis; }
    FingerRig rightRig, leftRig;
    static readonly float[] jointTaper = { 1f, 1f, 0.7f }; // knuckle curls fully, fingertip curls a bit less

    void Awake()
    {
        foreach (var candidate in GetComponentsInChildren<Animator>(true))
        {
            if (candidate.avatar != null && candidate.avatar.isHuman)
            {
                anim = candidate;
                break;
            }
        }

        if (anim == null)
        {
            Debug.LogError($"{name} could not find a humanoid Animator in its hierarchy. Weapon hand IK is disabled.", this);
            return;
        }

        if (anim.gameObject != gameObject)
        {
            animatorBridge = anim.gameObject.GetComponent<WeaponHandIKAnimatorBridge>();
            if (animatorBridge == null)
                animatorBridge = anim.gameObject.AddComponent<WeaponHandIKAnimatorBridge>();
            animatorBridge.owner = this;
        }

        rightFingerBones = CacheFingerBones(isRight: true);
        leftFingerBones = CacheFingerBones(isRight: false);
        rightRig = BuildFingerRig(rightFingerBones, anim.GetBoneTransform(HumanBodyBones.RightHand), invertRightCurl);
        leftRig = BuildFingerRig(leftFingerBones, anim.GetBoneTransform(HumanBodyBones.LeftHand), invertLeftCurl);

        rightLowerArm = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
        leftLowerArm = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        rightShoulder = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
        rightHandBone = anim.GetBoneTransform(HumanBodyBones.RightHand);
        leftShoulder = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        leftHandBone = anim.GetBoneTransform(HumanBodyBones.LeftHand);

        if (rightShoulder != null) rightShoulderRestLocal = anim.transform.InverseTransformPoint(rightShoulder.position);
        if (leftShoulder != null) leftShoulderRestLocal = anim.transform.InverseTransformPoint(leftShoulder.position);

        rightArmReach = MeasureArmReach(rightShoulder, anim.GetBoneTransform(HumanBodyBones.RightLowerArm), rightHandBone);
        leftArmReach = MeasureArmReach(leftShoulder, anim.GetBoneTransform(HumanBodyBones.LeftLowerArm), leftHandBone);

        chestBone = anim.GetBoneTransform(HumanBodyBones.Chest);
        if (chestBone == null) chestBone = anim.GetBoneTransform(HumanBodyBones.UpperChest);
        if (chestBone == null) chestBone = anim.GetBoneTransform(HumanBodyBones.Spine);
    }

    // Measures the arm's actual upper-arm + forearm length from its bind pose, once, so
    // the reach clamp always matches this specific rig regardless of its proportions or
    // scale - no numbers to tune by hand. Returns Infinity (i.e. don't clamp) if any bone
    // is missing, so a rig without a full arm chain just behaves as it did before.
    float MeasureArmReach(Transform shoulder, Transform elbow, Transform hand)
    {
        if (shoulder == null || elbow == null || hand == null) return Mathf.Infinity;
        float upperArmLen = Vector3.Distance(shoulder.position, elbow.position);
        float forearmLen = Vector3.Distance(elbow.position, hand.position);
        return (upperArmLen + forearmLen) * armReachSafety;
    }

    // The live shoulder bone travels several centimetres per step; its resting spot on
    // the body doesn't. Using the resting spot for the reach measurement keeps the
    // pull-back a function of where the weapon is relative to the BODY, not of where
    // the walk animation happens to be in its cycle.
    Vector3 StableShoulderPosition(bool isRight, Transform live)
    {
        if (!measureReachFromRestingShoulder || anim == null) return live.position;
        Vector3 rest = isRight ? rightShoulderRestLocal : leftShoulderRestLocal;
        return rest == Vector3.zero ? live.position : anim.transform.TransformPoint(rest);
    }

    // Works out, once, the local axis each finger joint must rotate about to close into
    // the palm. In the bind pose (a T-pose: palms face down) the palm normal is
    // perpendicular to both the finger direction and the knuckle line; fingers curl
    // TOWARDS the way the palm faces, so the hinge axis is fingerDirection x palmNormal
    // (rotating d about a moves the tip along a x d = palmNormal). This is derived per
    // joint from the actual bone positions, so it's correct for either hand whatever
    // orientation the rig's finger bones were authored with.
    FingerRig BuildFingerRig(Transform[][] bones, Transform hand, bool invert)
    {
        if (bones == null || hand == null) return null;
        Transform index = bones[1][0], middle = bones[2][0], little = bones[4][0];
        if (index == null || middle == null || little == null) return null;

        Vector3 knuckleLine = little.position - index.position;
        Vector3 fingerDir = middle.position - hand.position;
        Vector3 palm = Vector3.Cross(fingerDir, knuckleLine).normalized;
        if (palm.y > 0f) palm = -palm; // palms face down in the bind pose
        if (Mathf.Abs(palm.y) < 0.2f)
            Debug.LogWarning($"{name}: bind pose doesn't look like palms-down - finger curl direction may be wrong; use the invert curl toggles.", this);

        var rig = new FingerRig { bind = new Quaternion[5][], axis = new Vector3[5][] };
        for (int f = 0; f < 5; f++)
        {
            rig.bind[f] = new Quaternion[3];
            rig.axis[f] = new Vector3[3];
            for (int j = 0; j < 3; j++)
            {
                Transform bone = bones[f][j];
                if (bone == null) continue;
                rig.bind[f][j] = bone.localRotation;

                Vector3 d;
                if (j < 2 && bones[f][j + 1] != null) d = bones[f][j + 1].position - bone.position;
                else
                {
                    Transform prev = j > 0 ? bones[f][j - 1] : null;
                    d = bone.position - (prev != null ? prev.position : hand.position);
                }

                Vector3 a = Vector3.Cross(d, palm);
                if (a.sqrMagnitude < 1e-8f) a = Vector3.right; // degenerate - can't tell
                a.Normalize();
                if (invert) a = -a;
                rig.axis[f][j] = bone.InverseTransformDirection(a);
            }
        }
        return rig;
    }

    Transform[][] CacheFingerBones(bool isRight)
    {
        // HumanBodyBones calls it "Little", not "Pinky" - same finger.
        HumanBodyBones[,] bones = isRight
            ? new HumanBodyBones[,]
            {
                { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal },
                { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
                { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
                { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
                { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal },
            }
            : new HumanBodyBones[,]
            {
                { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal },
                { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
                { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
                { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
                { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
            };

        var result = new Transform[5][];
        for (int f = 0; f < 5; f++)
        {
            result[f] = new Transform[3];
            for (int j = 0; j < 3; j++)
                result[f][j] = anim.GetBoneTransform(bones[f, j]); // null if this rig has no finger bones - handled at apply time
        }
        return result;
    }

    /// <summary>The live hand bone (not the weapon grip) - use this to attach props that
    /// should move rigidly with the hand mesh itself, e.g. a magazine held during reload,
    /// rather than the WeaponHandIK target which is a point in weapon space.</summary>
    public Transform GetHandBone(bool isRight) =>
        anim != null ? anim.GetBoneTransform(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand) : null;

    Transform reloadGrabAnchor;

    /// <summary>Auto-creates (once) and repositions a child of the character's own Hips
    /// bone for weapons to reach toward during reload - e.g. a hip mag pouch. This lives
    /// on the PLAYER, not the weapon, since it's a body-relative spot that's the same
    /// regardless of which gun is held, and it needs no manual scene placement at all:
    /// pass whatever local offset/rotation you want and it just gets applied every call,
    /// so tuning the numbers on the calling WeaponReloadHandler updates it live, including
    /// while in Play Mode.</summary>
    public Transform GetOrCreateReloadGrabPoint(Vector3 localOffset, Vector3 localRotationEuler)
    {
        if (anim == null) return null;

        if (reloadGrabAnchor == null)
        {
            Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) return null;
            GameObject go = new GameObject("ReloadGrabPoint (auto)");
            go.transform.SetParent(hips, false);
            reloadGrabAnchor = go.transform;
        }

        reloadGrabAnchor.localPosition = localOffset;
        reloadGrabAnchor.localRotation = Quaternion.Euler(localRotationEuler);
        return reloadGrabAnchor;
    }

    /// <summary>Called by PlayerSetup whenever the active weapon changes. Pass null for
    /// either hand to release it (weight fades out smoothly, no snapping).</summary>
    public void SetGripTargets(Transform right, Transform left)
    {
        rightGrip = right;
        leftGrip = left;
        // A new weapon means any in-progress reload pose from the old one is meaningless.
        reloadRightOverride = null;
        reloadLeftOverride = null;
        reloadAnchorRefresh = null;

        // Re-resolved per weapon so the pull-back always acts on whatever's actually
        // equipped now, and reset so a leftover amount from the previous weapon doesn't
        // carry over onto the new one.
        if (offsetOwner != null) offsetOwner.SetPullBackWorldOffset(Vector3.zero);
        offsetOwner = right != null ? right.GetComponentInParent<WeaponADS>() : null;
        pullBackOffset = Vector3.zero;
        appliedPullBack = Vector3.zero;
        offsetOwner?.SetPullBackWorldOffset(Vector3.zero);
    }

    /// <summary>Called by WeaponReloadHandler to temporarily steer a hand away from its
    /// normal weapon grip (e.g. left hand reaching for a spare mag) without touching the
    /// underlying grip targets set by SetGripTargets. Pass null for either hand to hand
    /// control of that hand back to its normal grip - the blend weight (rightWeight/
    /// leftWeight) fades smoothly either way since ApplyIK never snaps.</summary>
    /// <param name="refresh">Optional. Called in LateUpdate, after everything else has moved
    /// the weapon this frame, so the reload hand target can be re-placed against the
    /// weapon's FINAL pose (a reload that shifts the gun after the IK pass would
    /// otherwise leave the hand aimed at where the gun WAS).</param>
    public void SetReloadOverride(Transform right, Transform left, System.Action refresh = null)
    {
        reloadRightOverride = right;
        reloadLeftOverride = left;
        reloadAnchorRefresh = (right != null || left != null) ? refresh : null;
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (anim == null) return;

        ApplyIK();
    }

    public void ApplyIK()
    {
        if (anim == null) return;

        // OnAnimatorIK fires once per layer with an IK pass enabled - both the base and
        // the UpperBody layer have one - so this runs TWICE per frame. The IK goals have
        // to be set on every pass, but anything that advances state or rotates bones
        // directly must only happen once, or weights blend at double speed and the
        // finger curl is applied twice (on top of the double-rate pull-back easing, a
        // further source of the shaking).
        bool firstPassThisFrame = lastIKFrame != Time.frameCount;
        lastIKFrame = Time.frameCount;

        // Reload overrides win when present; otherwise fall back to the weapon's normal grips.
        Transform effectiveRight = reloadRightOverride != null ? reloadRightOverride : rightGrip;
        Transform effectiveLeft = reloadLeftOverride != null ? reloadLeftOverride : leftGrip;

        // Don't fight the melee swing / ragdoll / death poses - only apply while a
        // weapon is actually meant to be held.
        if (firstPassThisFrame)
        {
            float rightTarget = effectiveRight != null ? 1f : 0f;
            float leftTarget = effectiveLeft != null ? 1f : 0f;
            rightWeight = Mathf.MoveTowards(rightWeight, rightTarget, blendSpeed * Time.deltaTime);
            leftWeight = Mathf.MoveTowards(leftWeight, leftTarget, blendSpeed * Time.deltaTime);
        }

        // The strict lock also covers reload targets: the hand has to land EXACTLY on the
        // mag pouch / reload point, not wherever later scripts and the moving gun leave it.
        lockRightGrip = effectiveRight;
        lockLeftGrip = effectiveLeft;

        Vector3 rightExcessVec = ApplyHand(AvatarIKGoal.RightHand, effectiveRight, rightWeight, rightElbowHint, AvatarIKHint.RightElbow, rightShoulder, rightArmReach);
        Vector3 leftExcessVec = ApplyHand(AvatarIKGoal.LeftHand, effectiveLeft, leftWeight, leftElbowHint, AvatarIKHint.LeftElbow, leftShoulder, leftArmReach);

        // A reload override sends a hand somewhere on the BODY (the hip mag pouch, then
        // back) - deliberately away from its weapon grip. That's not the weapon being out
        // of reach, so it must never feed the pull-back below, or the gun gets yanked
        // toward the hand every time the hip-mounted anchor happens to swing far enough
        // from the shoulder. Standing still the anchor barely moves and rarely crosses
        // the reach threshold, so this was invisible - but walking, the hips (and the
        // anchor riding on them) swing through the gait cycle every stride, so the
        // "excess" trips intermittently in time with footfalls and yanks the weapon by up
        // to maxPullBack. That's the reload-while-walking forward jump.
        if (reloadRightOverride != null) rightExcessVec = Vector3.zero;
        if (reloadLeftOverride != null) leftExcessVec = Vector3.zero;

        if (!firstPassThisFrame) return;

        // Whichever hand is short of its grip by more drives the pull-back - the left
        // hand on a two-handed weapon usually needs more, since it travels further from
        // the body. Pull along the ACTUAL direction that hand fell short by (not just
        // straight back), so this helps regardless of whether the camera's pitched up,
        // down, or is just turned to an awkward angle.
        Vector3 dominantExcess = leftExcessVec.sqrMagnitude >= rightExcessVec.sqrMagnitude ? leftExcessVec : rightExcessVec;
        Vector3 targetPullBack = -dominantExcess;

        // Strip out most (by default all) of the sideways component. A pull-back that's
        // free to point anywhere follows the shoulder's own left/right swing, which is
        // exactly the "gun and arm jumps right then returns left while walking" - and it
        // never helps reach anyway, since the problem it exists to solve is the weapon
        // being too far FORWARD/UP, not too far to one side.
        Transform lateralRef = offsetOwner != null ? offsetOwner.transform : anim.transform;
        Vector3 side = lateralRef.right;
        float lateral = Vector3.Dot(targetPullBack, side);
        targetPullBack -= side * lateral * (1f - lateralPullBackFraction);

        if (targetPullBack.magnitude > maxPullBack)
            targetPullBack = targetPullBack.normalized * maxPullBack;

        // SmoothDamp rather than MoveTowards: a constant-rate move reaches its target
        // within a frame or two at typical speeds, so per-step changes in the target
        // pass straight through as visible steps. This spreads them over ~pullBackSmoothTime.
        pullBackOffset = Vector3.SmoothDamp(pullBackOffset, targetPullBack, ref pullBackVelocity,
            Mathf.Max(0.01f, pullBackSmoothTime), Mathf.Infinity, Time.deltaTime);

        // Handed to the weapon root's owner rather than written here - see the field
        // comments above for why that removes both the one-frame lag and the feedback.
        appliedPullBack = pullBackOffset;
        offsetOwner?.SetPullBackWorldOffset(pullBackOffset);

        // Twist the torso towards the off-hand's grip so it doesn't have to rely on arm
        // stretch and pull-back alone to reach a two-handed weapon's foregrip - a real
        // shoulder would lead into the reach rather than staying square to the target.
        float targetLean = leftWeight > 0f ? maxTorsoLean * leftWeight : 0f;
        currentTorsoLean = Mathf.MoveTowards(currentTorsoLean, targetLean, torsoLeanSpeed * Time.deltaTime);
        if (chestBone != null && Mathf.Abs(currentTorsoLean) > 0.01f)
            chestBone.Rotate(Vector3.up, currentTorsoLean, Space.Self);

    }

    // Fingers are posed here, not in OnAnimatorIK: the Animator finalises bone
    // transforms after the IK pass, so anything written there is thrown away. LateUpdate
    // runs after the Animator has posed the rig, so this is the last word on the fingers.
    void LateUpdate()
    {
        if (anim == null) return;
        if (strictHandLock)
        {
            // The reload hand path was placed before the gun's reload movement was applied
            // this frame - re-place it now so the hand meets the reload point on the gun
            // as it actually is, instead of overshooting it.
            if (reloadAnchorRefresh != null && (reloadRightOverride != null || reloadLeftOverride != null))
                reloadAnchorRefresh();

            if (lockRightGrip != null) LockHand(rightShoulder, rightLowerArm, rightHandBone, lockRightGrip, rightWeight);
            if (lockLeftGrip != null) LockHand(leftShoulder, leftLowerArm, leftHandBone, lockLeftGrip, leftWeight);
        }
        if (overrideRightFingers) ApplyFingerOverride(rightRig, rightFingerBones, rightGripPose, rightWeight);
        if (overrideLeftFingers) ApplyFingerOverride(leftRig, leftFingerBones, leftGripPose, leftWeight);
    }

    // Final two-bone solve: puts `hand` exactly on `grip` (position AND rotation),
    // keeping the elbow on whichever side of the shoulder->grip line the animation/IK
    // already had it, so the arm's animated character survives. `weight` fades the lock
    // in and out with the grip blend.
    void LockHand(Transform upper, Transform lower, Transform hand, Transform grip, float weight)
    {
        if (upper == null || lower == null || hand == null || grip == null || weight <= 0f) return;

        Vector3 s = upper.position, e = lower.position, h = hand.position;
        float a = (e - s).magnitude, b = (h - e).magnitude;
        if (a < 1e-4f || b < 1e-4f) return;

        Vector3 toTarget = grip.position - s;
        float dist = toTarget.magnitude;
        if (dist < 1e-4f) return;
        Vector3 dir = toTarget / dist;

        // Solve the bend for a reachable distance; anything beyond full extension is
        // handled by the small stretch at the end.
        float d = Mathf.Clamp(dist, Mathf.Abs(a - b) + 0.001f, a + b - 0.0005f);

        Vector3 elbowOffset = e - s;
        Vector3 pole = elbowOffset - dir * Vector3.Dot(elbowOffset, dir);
        if (pole.sqrMagnitude < 1e-8f)
            pole = Vector3.down - dir * Vector3.Dot(Vector3.down, dir);
        pole.Normalize();

        float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
        Vector3 newElbow = s + dir * (a * cosA) + pole * (a * Mathf.Sqrt(1f - cosA * cosA));

        upper.rotation = Quaternion.Slerp(upper.rotation,
            Quaternion.FromToRotation(e - s, newElbow - s) * upper.rotation, weight);

        // The forearm swings from wherever the elbow actually ended up (weight < 1 means
        // it isn't exactly at newElbow), towards the grip.
        Vector3 e2 = lower.position, h2 = hand.position;
        Vector3 wantHand = s + dir * Mathf.Min(dist, a + b);
        lower.rotation = Quaternion.Slerp(lower.rotation,
            Quaternion.FromToRotation(h2 - e2, wantHand - e2) * lower.rotation, weight);

        // Small allowed stretch so a hand that's a few centimetres short still sits on the
        // grip (any larger shortfall is left to the weapon pull-back).
        Vector3 finalPos = s + dir * Mathf.Min(dist, a + b + Mathf.Max(0f, lockMaxStretch));
        hand.position = Vector3.Lerp(hand.position, finalPos, weight);
        hand.rotation = Quaternion.Slerp(hand.rotation, grip.rotation, weight);
    }

    void ApplyFingerOverride(FingerRig rig, Transform[][] bones, FingerGripPose pose, float weight)
    {
        if (rig == null || bones == null || weight <= 0f) return;

        float[] curls = { pose.thumbCurl, pose.indexCurl, pose.middleCurl, pose.ringCurl, pose.pinkyCurl };
        for (int f = 0; f < 5; f++)
        {
            for (int j = 0; j < 3; j++)
            {
                Transform bone = bones[f][j];
                if (bone == null) continue;
                // Absolute pose (bind pose + curl), blended over whatever the clip did.
                Quaternion target = rig.bind[f][j] * Quaternion.AngleAxis(curls[f] * jointTaper[j], rig.axis[f][j]);
                bone.localRotation = Quaternion.Slerp(bone.localRotation, target, weight);
            }
        }
    }

    // Returns the world-space vector by which the raw (pre-clamp) target exceeded
    // maxReach - i.e. pointing away from the shoulder, with a magnitude equal to how far
    // over the limit it was - or Vector3.zero if it was already in reach / there's
    // nothing to measure against. ApplyIK uses this from both hands to drive the weapon
    // pull-back above, pulling the weapon back along whichever direction actually closes
    // the gap rather than a fixed axis.
    Vector3 ApplyHand(AvatarIKGoal goal, Transform grip, float weight, Transform elbowHint, AvatarIKHint hint, Transform shoulder, float maxReach)
    {
        anim.SetIKPositionWeight(goal, weight);
        anim.SetIKRotationWeight(goal, weight);
        if (weight <= 0f) return Vector3.zero; // grip may already be null while weight fades out

        Vector3 excessVector = Vector3.zero;

        if (grip != null)
        {
            Vector3 targetPos = grip.position;

            // Clamp to the arm's actual measured reach - see armReachSafety above for
            // why. Without this, a grip point placed too far away (or a moving weapon
            // transform that briefly overshoots during a lean/lift) forces the elbow
            // past full extension, and Unity's two-bone IK solver has no anatomical
            // limits of its own to stop it snapping into a mangled, sometimes
            // backward-looking bend to reach the impossible target anyway.
            if (shoulder != null && maxReach < Mathf.Infinity)
            {
                // Measured against where the grip would be WITHOUT the pull-back this
                // script is already causing, so the requested amount doesn't chase its
                // own effect (see appliedPullBack above), and from a shoulder position
                // that doesn't swing with the walk cycle (see the field tooltip).
                Vector3 measureShoulder = StableShoulderPosition(goal == AvatarIKGoal.RightHand, shoulder);
                Vector3 rawFromShoulder = (targetPos - appliedPullBack) - measureShoulder;
                float rawDist = rawFromShoulder.magnitude;
                if (rawDist > maxReach + pullBackDeadzone)
                    excessVector = (rawFromShoulder / rawDist) * (rawDist - maxReach - pullBackDeadzone);

                // The hand itself still clamps against the grip's real current position.
                Vector3 fromShoulder = targetPos - shoulder.position;
                float dist = fromShoulder.magnitude;
                if (dist > maxReach)
                    targetPos = shoulder.position + (fromShoulder / dist) * maxReach;
            }

            anim.SetIKPosition(goal, targetPos);
            anim.SetIKRotation(goal, grip.rotation);
        }

        if (elbowHint != null)
        {
            anim.SetIKHintPositionWeight(hint, weight);
            anim.SetIKHintPosition(hint, elbowHint.position);
        }
        else if (shoulder != null)
        {
            // No authored hint - give the solver a sane forward-and-outward direction to
            // bend the elbow towards instead of letting it pick an arbitrary one, which
            // is the other common cause of an elbow popping backwards/inwards.
            float side = goal == AvatarIKGoal.RightHand ? 1f : -1f;
            Vector3 fallbackHint = shoulder.position + anim.transform.forward * 0.3f
                + anim.transform.right * side * 0.25f - anim.transform.up * 0.15f;
            anim.SetIKHintPositionWeight(hint, weight);
            anim.SetIKHintPosition(hint, fallbackHint);
        }

        return excessVector;
    }

}

public class WeaponHandIKAnimatorBridge : MonoBehaviour
{
    public WeaponHandIK owner;

    void OnAnimatorIK(int layerIndex)
    {
        owner?.ApplyIK();
    }
}