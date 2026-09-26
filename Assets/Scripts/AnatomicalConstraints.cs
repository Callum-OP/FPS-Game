using UnityEngine;

/// <summary>
/// Runtime corrective layer enforcing simple anatomical joint limits on humanoid leg/foot/
/// spine/head bones AFTER the Animator and TorsoPoseDriver have posed the frame, so no clip
/// (or a bad blend between clips) can leave a foot pointing sideways, a knee bending
/// backwards/sideways, etc. Same idea as TorsoPoseDriver's own foot/torso correction, just
/// generalised into a standing per-bone limit table.
///
/// Deliberately does NOT touch the arms/hands: WeaponHandIK (order 300, runs right after
/// this) is the sole owner of arm posing for gameplay accuracy (grip snapping, reach
/// clamping, pull-back) - clamping the elbow/wrist here too would be a second system
/// fighting over the same transform, which is the single most common bug class in this
/// project's history. The elbow/wrist DO get real anatomical limits in the ragdoll
/// (RagdollBuilder), since that only ever runs after death, once the Animator/WeaponHandIK
/// have both stopped touching the rig.
///
/// Attach to the same object as the Animator (works on Player and on enemies/allies -
/// generic, only needs a Humanoid Animator, same pattern as WeaponHandIK/TorsoPoseDriver/
/// TorsoMotionDampener - put it right next to those, not on an outer parent object).
///
/// HOW THE CLAMP WORKS: each bone's rotation is measured relative to its own REST local
/// rotation (captured once in Awake, i.e. the bind pose). That relative rotation is
/// decomposed into a twist (spin around the bone's own chain axis, local +X - this
/// project's Mixamo-retargeted rigs consistently use local X as the bone-to-child axis,
/// the same convention RagdollBuilder already assumes for CharacterJoint.axis) and a swing,
/// itself split into "bend" (local Z - the main hinge direction: knee/ankle flex, leg
/// forward/back swing, torso forward/back bend, head nod) and "lateral" (local Y -
/// sideways: hip ab/adduction, foot inversion, torso side-bend, head tilt). Each of the
/// three is clamped independently and the rotation rebuilt from the clamped pieces, so a
/// clip gets compressed at the edge of a limit rather than ever visibly breaking it.
///
/// Defaults below are a reasonable starting point, not measured against this project's
/// actual rig - tune them by eye in Play Mode like every other placement/feel value here
/// (WeaponHandIK's armReachSafety, TorsoPoseDriver's lean amounts, etc).
/// </summary>
[DefaultExecutionOrder(260)] // after TorsoPoseDriver (250), before WeaponHandIK (300)
public class AnatomicalConstraints : MonoBehaviour
{
    [System.Serializable]
    public struct JointLimit
    {
        public HumanBodyBones bone;
        [Tooltip("Twist = rotation around the bone's own chain axis (local +X). Separate min/max because some joints should only ever twist one way (most won't use much of this).")]
        public float twistMin, twistMax;
        [Tooltip("Bend = the main hinge swing (local Z) - e.g. knee/ankle flex, forward leg swing, torso forward/back bend, head nod.")]
        public float bendMin, bendMax;
        [Tooltip("Lateral = the sideways swing (local Y) - e.g. leg ab/adduction, foot inversion, torso side-bend, head tilt. Symmetric; keep tight for hinge-like joints.")]
        public float lateralLimit;

        public JointLimit(HumanBodyBones bone, float twistMin, float twistMax, float bendMin, float bendMax, float lateralLimit)
        {
            this.bone = bone; this.twistMin = twistMin; this.twistMax = twistMax;
            this.bendMin = bendMin; this.bendMax = bendMax; this.lateralLimit = lateralLimit;
        }
    }

    [Tooltip("Per-bone limits in degrees. Arms/hands deliberately excluded - see class comment.")]
    public JointLimit[] limits = Defaults();

    [Tooltip("Manual override: if this project's rig has more than one Animator component and auto-detection isn't working, optional.")]
    public Animator animatorOverride;

    static JointLimit[] Defaults() => new JointLimit[]
    {
        // Torso: kept fairly free since TorsoPoseDriver already relies on some give here for
        // lean/twist - this just stops it (or a bad clip) going fully unnatural.
        new JointLimit(HumanBodyBones.Spine, -20f, 20f, -25f, 35f, 20f),
        new JointLimit(HumanBodyBones.Chest, -15f, 15f, -20f, 25f, 15f),

        // Head: generous yaw (needs to turn to look around/at things), tight everything else -
        // "limited sideways movement" means tight lateral tilt, not tight look-around yaw.
        new JointLimit(HumanBodyBones.Head, -70f, 70f, -45f, 40f, 20f),

        // Legs: thighs get the most freedom (walking/running/crouching), knees are a near-
        // pure forward-only hinge, feet stay pointing mostly forward with only a little give.
        new JointLimit(HumanBodyBones.LeftUpperLeg,  -20f, 20f, -25f, 110f, 35f),
        new JointLimit(HumanBodyBones.RightUpperLeg, -20f, 20f, -25f, 110f, 35f),
        new JointLimit(HumanBodyBones.LeftLowerLeg,   -6f,  6f,   0f, 140f,  8f),
        new JointLimit(HumanBodyBones.RightLowerLeg,  -6f,  6f,   0f, 140f,  8f),
        new JointLimit(HumanBodyBones.LeftFoot,      -15f, 15f, -30f,  45f, 15f),
        new JointLimit(HumanBodyBones.RightFoot,     -15f, 15f, -30f,  45f, 15f),
    };

    Animator anim;
    Transform[] bones;
    Quaternion[] restLocal;
    bool[] valid;

    void Start() => TryInit();

    void TryInit()
    {
        // Ask CharacterAnimationDriver for the Animator it already correctly resolved, rather
        // than re-deriving this here.
        anim = animatorOverride; // manual override wins outright - see its tooltip
        if (anim == null)
        {
            var driver = GetComponentInChildren<CharacterAnimationDriver>();
            if (driver != null) anim = driver.BodyAnimator;
        }
        if (anim == null)
        {
            foreach (var candidate in GetComponentsInChildren<Animator>(true))
            {
                if (candidate.runtimeAnimatorController != null && candidate.avatar != null && candidate.avatar.isHuman) { anim = candidate; break; }
            }
        }
        if (anim == null)
        {
            enabled = false;
            return;
        }

        bones = new Transform[limits.Length];
        restLocal = new Quaternion[limits.Length];
        valid = new bool[limits.Length];

        for (int i = 0; i < limits.Length; i++)
        {
            Transform t = anim.GetBoneTransform(limits[i].bone);
            bones[i] = t;
            valid[i] = t != null;
            if (valid[i]) restLocal[i] = t.localRotation;
        }
    }

    void LateUpdate()
    {
        if (bones == null) return; // not initialized - see TryInit
        for (int i = 0; i < limits.Length; i++)
        {
            if (valid[i]) Clamp(bones[i], restLocal[i], limits[i]);
        }
    }

    static void Clamp(Transform bone, Quaternion rest, JointLimit lim)
    {
        // Rotation this frame's pose has ADDED on top of the bind pose, in local space.
        Quaternion delta = Quaternion.Inverse(rest) * bone.localRotation;

        // --- Twist: component of 'delta' around local +X ---
        Vector3 xyz = new Vector3(delta.x, delta.y, delta.z);
        Vector3 twistVec = Vector3.Project(xyz, Vector3.right);
        Quaternion twist = Normalize(new Quaternion(twistVec.x, twistVec.y, twistVec.z, delta.w));
        Quaternion swing = delta * Quaternion.Inverse(twist);

        float twistAngle = SignedAngleAroundAxis(twist, Vector3.right);
        Quaternion clampedTwist = Quaternion.AngleAxis(Mathf.Clamp(twistAngle, lim.twistMin, lim.twistMax), Vector3.right);

        // --- Swing: split into bend (local Z) and lateral (local Y) ---
        // swing's own axis already lies in the Y/Z plane (perpendicular to the twist axis
        // by construction), so its Y/Z components ARE the lateral/bend amounts directly.
        swing.ToAngleAxis(out float swingAngle, out Vector3 swingAxis);
        if (swingAngle > 180f) swingAngle -= 360f;
        Vector3 bendVec = swingAxis * swingAngle; // degrees, split across Y (lateral) / Z (bend)

        float bend = Mathf.Clamp(bendVec.z, lim.bendMin, lim.bendMax);
        float lateral = Mathf.Clamp(bendVec.y, -lim.lateralLimit, lim.lateralLimit);
        Quaternion clampedSwing = Quaternion.AngleAxis(lateral, Vector3.up) * Quaternion.AngleAxis(bend, Vector3.forward);

        bone.localRotation = rest * clampedSwing * clampedTwist;
    }

    static Quaternion Normalize(Quaternion q)
    {
        float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return mag < 0.0001f ? Quaternion.identity : new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
    }

    static float SignedAngleAroundAxis(Quaternion q, Vector3 axis)
    {
        q.ToAngleAxis(out float angle, out Vector3 qAxis);
        if (angle > 180f) angle -= 360f;
        if (Vector3.Dot(qAxis, axis) < 0f) angle = -angle; // ToAngleAxis's axis can flip; realign to our reference
        return angle;
    }
}