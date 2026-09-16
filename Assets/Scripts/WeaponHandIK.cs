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
    [Tooltip("Local axis each finger joint curls around. Mixamo/standard humanoid rigs curl fingers about local X for the base and middle knuckles - flip the sign or try Y/Z if fingers splay sideways instead of curling.")]
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

    [Header("Weapon Pull-Back (keeps the gun in reach)")]
    [Tooltip("When aiming makes the weapon (parented to the camera) move out beyond the arms' actual reach, the left hand on two-handed weapons usually runs out first, since it travels further from the body. Rather than let the hand visibly stop short of the grip (which the arm reach clamp above would otherwise do), the weapon is pulled back toward whichever shoulder is short by however much - in whatever direction actually closes the gap, not just straight back, so this helps for looking down as well as up. Set to 0 to disable and go back to the hand just clamping short.")]
    public float maxPullBack = 0.25f;
    [Tooltip("How fast the pull-back eases in/out as the required amount/direction changes.")]
    public float pullBackSpeed = 10f;

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
    WeaponHandIKAnimatorBridge animatorBridge;
    float rightWeight;
    float leftWeight;

    // Cached once in Awake for the reach clamp and fallback elbow hint below.
    Transform rightShoulder, rightHandBone;
    Transform leftShoulder, leftHandBone;
    float rightArmReach = Mathf.Infinity;
    float leftArmReach = Mathf.Infinity;
    Transform chestBone;
    float currentTorsoLean;

    // Weapon pull-back state. movedWeaponPart is the same "weapon's own top-level pivot"
    // concept WeaponReloadHandler uses (rightGrip's parent) - resolved fresh whenever the
    // grips change so it always points at whichever weapon is actually equipped. The
    // applied offset is tracked in WORLD space so this can be purely additive (undo last
    // frame's, apply this frame's) in LateUpdate, which lets it safely compose with
    // WeaponReloadHandler's own additive (local-space) move on the same pivot regardless
    // of which component's LateUpdate happens to run first - the two only ever touch the
    // transform through their own tracked delta, never by reading and overwriting the
    // other's contribution.
    Transform movedWeaponPart;
    Vector3 pullBackOffset;      // current world-space pull-back vector, eased towards its target each frame
    Vector3 appliedPullBackOffset;

    // [proximal, intermediate, distal] per finger, thumb through pinky.
    Transform[][] rightFingerBones;
    Transform[][] leftFingerBones;
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

        rightShoulder = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
        rightHandBone = anim.GetBoneTransform(HumanBodyBones.RightHand);
        leftShoulder = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        leftHandBone = anim.GetBoneTransform(HumanBodyBones.LeftHand);

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

        // Same "weapon's own top-level pivot" WeaponReloadHandler resolves - re-resolved
        // per weapon so pull-back always acts on whatever's actually equipped now, and
        // reset so a leftover pull-back amount from the previous weapon doesn't carry
        // over onto the new one.
        movedWeaponPart = right != null ? right.parent : null;
        pullBackOffset = Vector3.zero;
        appliedPullBackOffset = Vector3.zero;
    }

    /// <summary>Called by WeaponReloadHandler to temporarily steer a hand away from its
    /// normal weapon grip (e.g. left hand reaching for a spare mag) without touching the
    /// underlying grip targets set by SetGripTargets. Pass null for either hand to hand
    /// control of that hand back to its normal grip - the blend weight (rightWeight/
    /// leftWeight) fades smoothly either way since ApplyIK never snaps.</summary>
    public void SetReloadOverride(Transform right, Transform left)
    {
        reloadRightOverride = right;
        reloadLeftOverride = left;
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (anim == null) return;

        ApplyIK();
    }

    public void ApplyIK()
    {
        if (anim == null) return;

        // Reload overrides win when present; otherwise fall back to the weapon's normal grips.
        Transform effectiveRight = reloadRightOverride != null ? reloadRightOverride : rightGrip;
        Transform effectiveLeft = reloadLeftOverride != null ? reloadLeftOverride : leftGrip;

        // Don't fight the melee swing / ragdoll / death poses - only apply while a
        // weapon is actually meant to be held.
        float rightTarget = effectiveRight != null ? 1f : 0f;
        float leftTarget = effectiveLeft != null ? 1f : 0f;
        rightWeight = Mathf.MoveTowards(rightWeight, rightTarget, blendSpeed * Time.deltaTime);
        leftWeight = Mathf.MoveTowards(leftWeight, leftTarget, blendSpeed * Time.deltaTime);

        Vector3 rightExcessVec = ApplyHand(AvatarIKGoal.RightHand, effectiveRight, rightWeight, rightElbowHint, AvatarIKHint.RightElbow, rightShoulder, rightArmReach);
        Vector3 leftExcessVec = ApplyHand(AvatarIKGoal.LeftHand, effectiveLeft, leftWeight, leftElbowHint, AvatarIKHint.LeftElbow, leftShoulder, leftArmReach);

        // Whichever hand is short of its grip by more drives the pull-back - the left
        // hand on a two-handed weapon usually needs more, since it travels further from
        // the body. Pull along the ACTUAL direction that hand fell short by (not just
        // straight back), so this helps regardless of whether the camera's pitched up,
        // down, or is just turned to an awkward angle.
        Vector3 dominantExcess = leftExcessVec.sqrMagnitude >= rightExcessVec.sqrMagnitude ? leftExcessVec : rightExcessVec;
        Vector3 targetPullBack = -dominantExcess;
        if (targetPullBack.magnitude > maxPullBack)
            targetPullBack = targetPullBack.normalized * maxPullBack;
        pullBackOffset = Vector3.MoveTowards(pullBackOffset, targetPullBack, pullBackSpeed * Time.deltaTime);

        // Twist the torso towards the off-hand's grip so it doesn't have to rely on arm
        // stretch and pull-back alone to reach a two-handed weapon's foregrip - a real
        // shoulder would lead into the reach rather than staying square to the target.
        float targetLean = leftWeight > 0f ? maxTorsoLean * leftWeight : 0f;
        currentTorsoLean = Mathf.MoveTowards(currentTorsoLean, targetLean, torsoLeanSpeed * Time.deltaTime);
        if (chestBone != null && Mathf.Abs(currentTorsoLean) > 0.01f)
            chestBone.Rotate(Vector3.up, currentTorsoLean, Space.Self);

        // Finger curl runs after the arm IK above so it's shaping this frame's already-posed
        // fingers, not fighting the arm placement.
        ApplyFingerGrip(rightFingerBones, rightGripPose, rightWeight);
        ApplyFingerGrip(leftFingerBones, leftGripPose, leftWeight);
    }

    void LateUpdate()
    {
        if (movedWeaponPart == null) return;

        // Purely additive in WORLD space: undo whatever was added last frame, then add
        // this frame's amount. World space means no local/parent conversion needed for
        // a pure directional pull, and undo-then-redo of the same world vector is safe
        // regardless of what else touched the transform in between (its parent doesn't
        // move again until next frame) - this is what lets it compose safely with
        // WeaponReloadHandler's own additive (local-space) move on the same pivot,
        // regardless of which component's LateUpdate happens to run first.
        movedWeaponPart.position -= appliedPullBackOffset;
        appliedPullBackOffset = pullBackOffset;
        movedWeaponPart.position += appliedPullBackOffset;
    }

    void ApplyFingerGrip(Transform[][] fingerBones, FingerGripPose pose, float weight)
    {
        if (fingerBones == null || weight <= 0f) return;

        float[] curls = { pose.thumbCurl, pose.indexCurl, pose.middleCurl, pose.ringCurl, pose.pinkyCurl };
        for (int f = 0; f < 5; f++)
        {
            float baseCurl = curls[f] * weight;
            if (baseCurl <= 0f) continue;

            for (int j = 0; j < 3; j++)
            {
                Transform bone = fingerBones[f][j];
                if (bone == null) continue; // rig doesn't have this joint - skip it
                bone.Rotate(pose.curlAxis, baseCurl * jointTaper[j], Space.Self);
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
                Vector3 fromShoulder = targetPos - shoulder.position;
                float dist = fromShoulder.magnitude;
                if (dist > maxReach)
                {
                    Vector3 dir = fromShoulder / dist;
                    excessVector = dir * (dist - maxReach);
                    targetPos = shoulder.position + dir * maxReach;
                }
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