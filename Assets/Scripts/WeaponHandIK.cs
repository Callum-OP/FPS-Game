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
/// Tune elbowHint if the elbows pop to a weird angle - it's an optional world-space
/// point the forearm bends towards (roughly out to the character's side and slightly
/// forward works for most rigs).
/// </summary>
[RequireComponent(typeof(Animator))]
public class WeaponHandIK : MonoBehaviour
{
    [Tooltip("How fast the IK weight blends in/out when weapons change or grips are cleared.")]
    public float blendSpeed = 8f;

    [Tooltip("Optional world-space elbow hint transforms (leave empty if not needed).")]
    public Transform rightElbowHint;
    public Transform leftElbowHint;

    [Header("Finger Grip")]
    [Tooltip("Curl applied to the right hand's fingers while it's gripping (weight > 0). Fades in/out with the same weight as the hand IK itself, so an empty hand relaxes back to the animated pose.")]
    public FingerGripPose rightGripPose = new FingerGripPose();
    [Tooltip("Curl applied to the left hand's fingers while it's gripping (weight > 0).")]
    public FingerGripPose leftGripPose = new FingerGripPose();

    Animator anim;
    Transform rightGrip;
    Transform leftGrip;
    Transform reloadRightOverride;
    Transform reloadLeftOverride;
    WeaponHandIKAnimatorBridge animatorBridge;
    float rightWeight;
    float leftWeight;

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

    /// <summary>Called by PlayerSetup whenever the active weapon changes. Pass null for
    /// either hand to release it (weight fades out smoothly, no snapping).</summary>
    public void SetGripTargets(Transform right, Transform left)
    {
        rightGrip = right;
        leftGrip = left;
        // A new weapon means any in-progress reload pose from the old one is meaningless.
        reloadRightOverride = null;
        reloadLeftOverride = null;
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

        ApplyHand(AvatarIKGoal.RightHand, effectiveRight, rightWeight, rightElbowHint, AvatarIKHint.RightElbow);
        ApplyHand(AvatarIKGoal.LeftHand, effectiveLeft, leftWeight, leftElbowHint, AvatarIKHint.LeftElbow);

        // Finger curl runs after the arm IK above so it's shaping this frame's already-posed
        // fingers, not fighting the arm placement.
        ApplyFingerGrip(rightFingerBones, rightGripPose, rightWeight);
        ApplyFingerGrip(leftFingerBones, leftGripPose, leftWeight);
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

    void ApplyHand(AvatarIKGoal goal, Transform grip, float weight, Transform elbowHint, AvatarIKHint hint)
    {
        anim.SetIKPositionWeight(goal, weight);
        anim.SetIKRotationWeight(goal, weight);
        if (weight <= 0f) return; // grip may already be null while weight fades out

        if (grip != null)
        {
            anim.SetIKPosition(goal, grip.position);
            anim.SetIKRotation(goal, grip.rotation);
        }

        if (elbowHint != null)
        {
            anim.SetIKHintPositionWeight(hint, weight);
            anim.SetIKHintPosition(hint, elbowHint.position);
        }
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