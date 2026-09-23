using UnityEngine;

/// <summary>
/// Procedural torso for the player's body, applied on top of whatever the Animator plays so
/// the torso stops looking stiff from first (and third) person.
///
///  - Look pitch: looking down bends the spine forward, looking up leans it back (a fraction
///    of the camera pitch, spread over spine/chest/upper chest). The camera follows the head's
///    actual displacement from this, so it sits where the head really is.
///  - Reload: torso twists slightly LEFT while reloading (reaching for the mag).
///  - Weapon switch (3), holster and draw: torso twists slightly RIGHT for a moment (reaching
///    for the weapon).
///  - Aiming: shoulders rise and the upper body moves slightly forward.
///
/// Runs in LateUpdate at order 250: after the Animator and TorsoMotionDampener, BEFORE
/// WeaponHandIK (300) which locks the hands onto the gun last - so the hands stay on the
/// weapon and only the shoulders/torso move around them.
///
/// Added automatically by PlayerSetup. Every amount is a public field: tune by eye.
/// </summary>
[DefaultExecutionOrder(250)]
public class TorsoPoseDriver : MonoBehaviour
{
    public static TorsoPoseDriver Instance { get; private set; }

    [Header("Look Pitch")]
    [Tooltip("Fraction of the camera pitch the torso follows. 0.4 = looking 60 degrees down bends the torso 24.")]
    [Range(0f, 0.7f)] public float pitchFollow = 0.4f;
    public float maxBendDown = 35f;
    public float maxLeanBack = 25f;
    [Tooltip("How much of the head's resulting movement the camera copies.")]
    [Range(0f, 1.5f)] public float cameraFollow = 1.2f;
    [Tooltip("Extra camera push FORWARD (metres) at full look-down, so the camera travels forward with the bent torso instead of the chest coming up into it. The torso bend itself is unchanged.")]
    public float lookDownCameraForward = 0.14f;
    [Tooltip("Slight camera drop (metres) at full look-down.")]
    public float lookDownCameraDrop = 0.02f;
    [Tooltip("Camera pitch (degrees) that counts as 'full' look-down for the two values above.")]
    public float lookDownFullPitch = 75f;
    [Tooltip("Extra camera push forward (metres) while walking/running forward, so the chest doesn't catch up to a camera that hasn't moved. Separate from the look-down push above - this one is about MOVING forward, not looking down.")]
    public float walkForwardCameraPush = 0.05f;

    [Header("Twist (degrees)")]
    [Tooltip("Left twist while reloading.")]
    public float reloadTwist = 12f;
    [Tooltip("Right twist pulse when switching weapon / holstering / drawing.")]
    public float switchTwist = 12f;
    public float switchDuration = 0.5f;
    public float twistSpeed = 7f;

    [Header("Aiming")]
    [Tooltip("Metres the shoulders rise while aiming.")]
    public float aimShoulderRaise = 0.03f;
    [Tooltip("Metres the upper body moves forward while aiming.")]
    public float aimForward = 0.03f;
    public float aimBlendSpeed = 8f;

    Animator anim;
    PlayerSetup setup;
    PlayerMovement pm;
    EnemyWeapon aiWeapon;      // enemies and allies: pitch/reload/aim come from here instead
    static readonly int GroundedHash = Animator.StringToHash("IsGrounded");
    float footW;
    Transform hips, lUpperArm, rUpperArm;
    Vector3 headLocalFwd; bool hasHeadFwd; float headW, walkFwdW;
    // Calibrated per-instance so we never depend on a bone axis guess or on toe bones being
    // mapped (both silently broke the previous attempt on some rigs).
    Vector3 lFootLocalFwd = Vector3.forward, rFootLocalFwd = Vector3.forward; bool hasFootCal;
    Vector3 lKneeLocalFwd = Vector3.forward, rKneeLocalFwd = Vector3.forward;
    Vector3 modelBaseLocal; bool hasModelBase; Vector2 hipRest;
    float dbgTime; Vector2 dbgMin = new Vector2(99, 99), dbgMax = new Vector2(-99, -99);
    static readonly int MoveXH = Animator.StringToHash("MoveX"), MoveYH = Animator.StringToHash("MoveY");
    Vector2 shRest; float shRestRoll; bool hasShRest;
    Vector3 restLocal; float restYaw; bool hasRest;
    Vector3 bodyRest; float bodyRestYaw; bool hasBodyRest; float groundW; int ikFrame = -1;
    Vector3 hipShiftCached; float hipYawCached;
    [Header("Body Sway Stabiliser")]
    [Tooltip("Cancels the walk/run animation's left/right hip sway and twist in everything ABOVE the hips, so the torso/arms/head stay steady against the gun and camera (which are fixed to the body root) while the legs still swing. This is the 'body wobbles side to side like it's on a spring attached to the gun' fix.")]
    public bool stabilizeBody = true; // residual upper-body cancel, runs after the hip stabiliser
    [Tooltip("How much sideways hip sway to remove (1 = all).")]
    [Range(0f, 1f)] public float stabilizeSway = 1f;
    [Tooltip("How much forward/back hip sway to remove.")]
    [Range(0f, 1f)] public float stabilizeForward = 0.3f; // was 0.5 - the residual forward correction here was occasionally over-correcting INTO the fixed camera
    [Tooltip("How much of the hips' yaw wobble to remove.")]
    [Range(0f, 1f)] public float stabilizeYaw = 0.75f;
    [Tooltip("How fast the resting reference follows the hips (per second). Must be slow next to the step cycle - a full stride takes about a second.")]
    public float restingRate = 1.5f;
    [Tooltip("Hard cap on how far the upper body is shifted (metres).")]
    public float maxShift = 0.12f;
    public float maxYawCorrection = 20f;

    [Header("Hip Lock (keeps the whole body in one spot)")]
    [Tooltip("The Mixamo locomotion clips are NOT in-place: the hips travel 1-2 metres forward (or sideways for strafes) every loop and snap back. With the importer's XZ bake option that travel stays in the pose, so the body swims in and out / side to side around the gun and camera, which hang off the body root. This pins the hips' ground position (XZ) over the model origin every frame by moving the whole model rigidly; vertical bob is untouched. The legs then step as a true in-place walk cycle.")]
    public bool lockHipsInPlace = true;
    [Range(0f, 1f)] public float hipLockStrength = 1f;
    [Tooltip("Safety cap (metres).")]
    public float maxHipLockOffset = 3f;
    [Tooltip("Logs the hips' travel from the model origin (min/max per 2 s) so you can see what the clips are really doing. Turn on, walk around, send me the lines.")]
    public bool debugHipTravel = false;

    [Header("Shoulder-Line Stabiliser (the real zig-zag fix)")]
    [Tooltip("The walk/run clips swing the hips AND counter-swing the spine; the shoulders still end up yawing, rolling and shifting a few degrees/cm every step. The gun and camera hang off the body root and don't, so in first person the arms/torso wobble left-right around the gun. This measures the actual shoulder line (left/right upper-arm bones) and cancels that motion.")]
    public bool stabilizeShoulders = true;
    [Range(0f, 1f)] public float shoulderYawRemove = 0.95f;
    [Range(0f, 1f)] public float shoulderRollRemove = 0.9f;
    [Range(0f, 1f)] public float shoulderSlideRemove = 1f;
    [Tooltip("Also removes the clip's CONSTANT off-facing (walking 'diagonally / slanted'), not just the oscillation. 1 = shoulders always square to the body/gun.")]
    [Range(0f, 1f)] public float shoulderSquareUp = 0.9f;
    public float maxShoulderYaw = 25f;
    public float maxShoulderRoll = 12f;

    [Header("Whole-Body Stabiliser (hips + planted feet)")]
    [Tooltip("Moves the whole body (hips) back towards a steady spot while the FEET stay exactly where the animation put them, so the legs bend to absorb the sway instead of the pelvis wandering left/right around the gun. Applied through the Animator's IK pass, so knees/legs follow correctly.")]
    public bool stabilizeHips = false; // off: removing hip yaw made the spine's own counter-rotation over-swing the shoulders
    [Tooltip("Fraction of the hips' sideways sway removed (1 = none left).")]
    [Range(0f, 1f)] public float hipSwayReduce = 1f;
    [Range(0f, 1f)] public float hipForwardReduce = 0.6f;
    [Range(0f, 1f)] public float hipYawReduce = 0.7f;
    [Tooltip("Cap on how far the hips are moved (metres). Higher = steadier body but legs stretch more.")]
    public float maxHipShift = 0.15f;
    public float maxHipYaw = 15f;
    [Tooltip("Fixes clips whose body is turned off the travel direction on average (the 'walking diagonally / slanted right' look): the whole animated pose is rotated about the body root so the body faces the same way as the gun/camera. 1 = fully, 0 = leave the clip's heading alone.")]
    [Range(0f, 1f)] public float headingCorrect = 0.85f;
    public float maxHeadingCorrection = 35f;
    [Tooltip("Turns planted/swinging feet towards the body's facing by this fraction of their error, so feet stop landing sideways.")]
    [Range(0f, 1f)] public float footYawAlign = 0.6f;
    public float maxFootYawCorrection = 25f;
    [Tooltip("How fast the feet/knee \"neutral facing\" calibration follows while standing still (per second). This is what the correction below is measured against, so it self-calibrates to THIS rig instead of assuming a bone axis or relying on toe bones (which some Mixamo humanoid rigs never map, silently breaking any fix based on them).")]
    public float footCalibrationRate = 2f;
    [Tooltip("Twists the knee back towards forward-facing when it points sideways/inward - the leg equivalent of the elbow fix, and probably the actual fix for \"lower leg looks inward\" (that's very likely calf TWIST, which rotating the foot alone can't touch).")]
    public bool kneeNeverSideways = true;
    [Range(0f, 1f)] public float kneeTwistCorrect = 0.85f;
    public float maxKneeTwistCorrection = 30f;

    [Header("Face Forward While Walking Forward")]
    [Tooltip("The head turns left/right with the stride while walking forward (looks like bobbing side to side). While moving forward the head yaw is held to the body's facing.")]
    public bool headFaceForward = true;
    [Range(0f, 1f)] public float headForwardStrength = 1f;
    [Tooltip("While moving forward, feet are turned this far towards straight ahead (1 = fully). Fixes the left foot landing turned in.")]
    [Range(0f, 1f)] public float footForwardWhenWalking = 1f;
    public float maxFootYawWalking = 50f;
    [Tooltip("Points the knees straight ahead while walking forward (lower legs drifting inwards, 'limp' look). 0 = off.")]
    [Range(0f, 1f)] public float kneeForwardStrength = 0.7f;

    [Header("Feet While Airborne")]
    [Tooltip("Points the feet along the body's facing (yaw) while jumping/falling/landing, and levels them out this much (0-1).")]
    [Range(0f, 1f)] public float airFootLevel = 0.6f;
    Transform root, spine, chest, upperChest, head, lShoulder, rShoulder;
    float pitch, pitchVel, twist, aimW, switchT = 1f;
    Vector3 camOffset, camOffsetVel;

    void Awake() { if (GetComponentInParent<PlayerMovement>() != null) Instance = this; }

    void Start()
    {
        anim = GetComponent<Animator>();
        setup = GetComponentInParent<PlayerSetup>();
        pm = GetComponentInParent<PlayerMovement>();
        aiWeapon = GetComponentInParent<EnemyWeapon>();
        if (anim == null || !anim.isHuman || (pm == null && aiWeapon == null)) { enabled = false; return; }
        root = pm != null ? pm.transform : (aiWeapon != null ? aiWeapon.transform : transform);
        spine = anim.GetBoneTransform(HumanBodyBones.Spine);
        chest = anim.GetBoneTransform(HumanBodyBones.Chest);
        upperChest = anim.GetBoneTransform(HumanBodyBones.UpperChest);
        head = anim.GetBoneTransform(HumanBodyBones.Head);
        hips = anim.GetBoneTransform(HumanBodyBones.Hips);
        lUpperArm = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        rUpperArm = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
        CalibrateLegs(true);
        // Head-local direction that points forward in the bind pose (rig-independent way to read head yaw).
        if (head != null) { headLocalFwd = Quaternion.Inverse(head.rotation) * root.forward; hasHeadFwd = true; }
        lShoulder = anim.GetBoneTransform(HumanBodyBones.LeftShoulder);
        rShoulder = anim.GetBoneTransform(HumanBodyBones.RightShoulder);
        if (spine == null && chest == null) enabled = false;
    }

    /// <summary>Adds the driver to a humanoid Animator object if it doesn't have one (used for enemies/allies).</summary>
    public static void EnsureOn(Animator a)
    {
        if (a != null && a.avatar != null && a.avatar.isHuman && a.GetComponent<TorsoPoseDriver>() == null)
            a.gameObject.AddComponent<TorsoPoseDriver>();
    }

    // Jump / fall / land: the clips leave the feet bent at odd angles, so point them along
    // the body's facing (which is where the camera looks, yaw-wise) and level them out.
    void OnAnimatorIK(int layer)
    {
        if (layer != 0 || anim == null || root == null) return;
        var st = anim.GetCurrentAnimatorStateInfo(0);
        bool air = st.IsName("JumpUp") || st.IsName("Airborne") || st.IsName("Land");
        float dt = Time.deltaTime;
        footW = Mathf.MoveTowards(footW, air ? 1f : 0f, dt * (air ? 10f : 6f));
        groundW = Mathf.MoveTowards(groundW, (!air && stabilizeHips) ? 1f : 0f, dt * 8f);
        alignW = Mathf.MoveTowards(alignW, (!air && !stabilizeHips) ? 1f : 0f, dt * 8f);

        if (groundW > 0.001f) StabilizeHipsIK(dt);
        else if (alignW > 0.001f) AlignFeet();
        if (footW > 0.001f)
        {
            FootFix(AvatarIKGoal.LeftFoot);
            FootFix(AvatarIKGoal.RightFoot);
        }
    }

    // Pull the pelvis back towards its slow average (removing the step-by-step sway) while
    // pinning both feet at the positions the animation gave them - "feet as the anchor,
    // hips steady". Also turns the feet towards the body's facing.
    void StabilizeHipsIK(float dt)
    {
        Vector3 bp = anim.bodyPosition;
        Quaternion br = anim.bodyRotation;

        // Animated foot goals, read before the body is moved (they are world-space, so the
        // feet stay put while the legs re-bend to the moved hips).
        Vector3 lp = anim.GetIKPosition(AvatarIKGoal.LeftFoot), rp = anim.GetIKPosition(AvatarIKGoal.RightFoot);
        Quaternion lr = anim.GetIKRotation(AvatarIKGoal.LeftFoot), rr = anim.GetIKRotation(AvatarIKGoal.RightFoot);

        if (ikFrame != Time.frameCount) // the IK pass can run more than once a frame
        {
            ikFrame = Time.frameCount;
            Vector3 local = root.InverseTransformPoint(bp);
            Vector3 f = Vector3.ProjectOnPlane(br * Vector3.forward, root.up);
            float yaw = f.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(root.forward, f, root.up) : 0f;
            if (!hasBodyRest) { bodyRest = local; bodyRestYaw = yaw; hasBodyRest = true; }
            float k = 1f - Mathf.Exp(-restingRate * dt);
            bodyRest = Vector3.Lerp(bodyRest, local, k);
            bodyRestYaw = Mathf.LerpAngle(bodyRestYaw, yaw, k);

            Vector3 dev = local - bodyRest;
            Vector3 shift = new Vector3(-dev.x * hipSwayReduce, 0f, -dev.z * hipForwardReduce);
            hipShiftCached = Vector3.ClampMagnitude(shift, maxHipShift);
            float wobble = Mathf.Clamp(-Mathf.DeltaAngle(bodyRestYaw, yaw) * hipYawReduce, -maxHipYaw, maxHipYaw);
            float constant = Mathf.Clamp(-bodyRestYaw * headingCorrect, -maxHeadingCorrection, maxHeadingCorrection);
            hipYawCached = wobble + constant;
        }

        // Rotate the WHOLE animated pose (hips and both foot goals) about the body root, so
        // strides stay lined up with the hips - no feet skating diagonally.
        Quaternion R = Quaternion.AngleAxis(hipYawCached * groundW, root.up);
        Vector3 pivot = root.position;
        Vector3 shifted = bp + root.TransformVector(hipShiftCached * groundW);
        anim.bodyPosition = pivot + R * (shifted - pivot);
        anim.bodyRotation = R * br;

        PinFoot(AvatarIKGoal.LeftFoot, pivot + R * (lp - pivot), R * lr);
        PinFoot(AvatarIKGoal.RightFoot, pivot + R * (rp - pivot), R * rr);
    }

    float alignW;
    // Hip IK is off: only turn the feet towards the body's facing (rotation only, position left alone).
    // Learns, per foot/knee bone, which of ITS OWN local directions currently points along
    // root.forward - the same trick used for the head. This sidesteps two things that broke
    // the earlier attempt: some Mixamo humanoid avatars never map toe bones at all (silently
    // falling back to a guessed axis), and a bone's "forward" axis isn't guaranteed to match
    // any particular local axis across different rigs/packs.
    void CalibrateLegs(bool force)
    {
        var lFoot = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
        var rFoot = anim.GetBoneTransform(HumanBodyBones.RightFoot);
        var lKnee = anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        var rKnee = anim.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        if (lFoot != null) lFootLocalFwd = Quaternion.Inverse(lFoot.rotation) * root.forward;
        if (rFoot != null) rFootLocalFwd = Quaternion.Inverse(rFoot.rotation) * root.forward;
        if (lKnee != null) lKneeLocalFwd = Quaternion.Inverse(lKnee.rotation) * root.forward;
        if (rKnee != null) rKneeLocalFwd = Quaternion.Inverse(rKnee.rotation) * root.forward;
        hasFootCal = true;
    }

    void AlignFeet()
    {
        float fwdW = Mathf.Clamp01(anim.GetFloat(MoveYH) / 0.5f);
        float align = Mathf.Lerp(footYawAlign, footForwardWhenWalking, fwdW);
        float maxCorr = Mathf.Lerp(maxFootYawCorrection, maxFootYawWalking, fwdW);
        float kneeW = kneeTwistCorrect * fwdW;

        AlignOneFoot(true, align, maxCorr, kneeW);
        AlignOneFoot(false, align, maxCorr, kneeW);
    }

    void AlignOneFoot(bool left, float align, float maxCorr, float kneeW)
    {
        var g = left ? AvatarIKGoal.LeftFoot : AvatarIKGoal.RightFoot;
        var footBone = anim.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
        var kneeBone = anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
        var hipBone = anim.GetBoneTransform(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);

        // --- Foot yaw: using the CALIBRATED axis, not an assumed one and not the toe bones
        // (some rigs never map LeftToes/RightToes, which silently broke the previous version).
        Quaternion rot = anim.GetIKRotation(g);
        if (footBone != null)
        {
            Vector3 fwd = footBone.rotation * (left ? lFootLocalFwd : rFootLocalFwd);
            Vector3 flat = Vector3.ProjectOnPlane(fwd, Vector3.up);
            if (flat.sqrMagnitude > 1e-6f)
            {
                float err = Vector3.SignedAngle(flat, root.forward, Vector3.up);
                float corr = Mathf.Clamp(err * align, -maxCorr, maxCorr);
                rot = Quaternion.AngleAxis(corr, Vector3.up) * rot;
            }
        }
        anim.SetIKRotationWeight(g, alignW);
        anim.SetIKRotation(g, rot);

        // --- Knee twist: the "lower leg looks inward" symptom is very likely the CALF bone
        // twisting around the hip->ankle axis, which rotating only the foot/ankle can never
        // fix (the calf is animated independently of it). This is the exact same fix as the
        // elbow-never-inward correction, applied to the leg: measure which way the knee
        // currently points (calibrated axis again), and if it's twisted off root.forward,
        // nudge it back via the Animator's knee hint while holding the foot exactly where the
        // clip put it.
        if (kneeNeverSideways && kneeW > 0.001f && kneeBone != null && hipBone != null)
        {
            Vector3 axis = footBone != null ? (footBone.position - hipBone.position) : Vector3.up;
            if (axis.sqrMagnitude > 1e-6f)
            {
                Vector3 kneeFwd = kneeBone.rotation * (left ? lKneeLocalFwd : rKneeLocalFwd);
                Vector3 pole = Vector3.ProjectOnPlane(kneeFwd, axis);
                Vector3 preferred = Vector3.ProjectOnPlane(root.forward, axis);
                if (pole.sqrMagnitude > 1e-6f && preferred.sqrMagnitude > 1e-6f)
                {
                    float twistErr = Vector3.SignedAngle(pole, preferred, axis);
                    float twistCorr = Mathf.Clamp(twistErr * kneeW, -maxKneeTwistCorrection, maxKneeTwistCorrection);
                    if (Mathf.Abs(twistCorr) > 0.05f)
                    {
                        Vector3 kneePos = kneeBone.position;
                        Vector3 newKnee = hipBone.position + Quaternion.AngleAxis(twistCorr, axis.normalized) * (kneePos - hipBone.position);
                        anim.SetIKPositionWeight(g, alignW);
                        anim.SetIKPosition(g, anim.GetIKPosition(g)); // hold the foot exactly where the clip put it
                        var hint = left ? AvatarIKHint.LeftKnee : AvatarIKHint.RightKnee;
                        anim.SetIKHintPositionWeight(hint, alignW);
                        anim.SetIKHintPosition(hint, newKnee);
                    }
                }
            }
        }
    }

    void PinFoot(AvatarIKGoal goal, Vector3 pos, Quaternion rot)
    {
        Vector3 flat = Vector3.ProjectOnPlane(rot * Vector3.forward, Vector3.up);
        if (flat.sqrMagnitude > 1e-4f)
        {
            float err = Vector3.SignedAngle(flat, root.forward, Vector3.up);
            float corr = Mathf.Clamp(err * footYawAlign, -maxFootYawCorrection, maxFootYawCorrection);
            rot = Quaternion.AngleAxis(corr, Vector3.up) * rot;
        }
        anim.SetIKPositionWeight(goal, groundW);
        anim.SetIKPosition(goal, pos);
        anim.SetIKRotationWeight(goal, groundW);
        anim.SetIKRotation(goal, rot);
    }

    void FootFix(AvatarIKGoal goal)
    {
        Quaternion cur = anim.GetIKRotation(goal);
        Vector3 fwd = cur * Vector3.forward;
        Vector3 flat = Vector3.ProjectOnPlane(fwd, Vector3.up);
        if (flat.sqrMagnitude < 1e-4f) return;
        float yaw = Vector3.SignedAngle(flat, root.forward, Vector3.up);
        float pitchAng = Mathf.Asin(Mathf.Clamp(fwd.normalized.y, -1f, 1f)) * Mathf.Rad2Deg; // + = toes up
        Quaternion q = Quaternion.AngleAxis(yaw, Vector3.up) * cur;
        // Level: rotate about the foot's own sideways axis to cancel part of the toe pitch.
        q = Quaternion.AngleAxis(pitchAng * airFootLevel, q * Vector3.right) * q;
        anim.SetIKPositionWeight(goal, footW);
        anim.SetIKPosition(goal, anim.GetIKPosition(goal));
        anim.SetIKRotationWeight(goal, footW);
        anim.SetIKRotation(goal, q);
    }

    /// <summary>Right-hand reach twist (weapon switch, holster, draw).</summary>
    public void PlayWeaponSwitch() => switchT = 0f;

    void LateUpdate()
    {
        if (anim == null || !anim.enabled) return;
        float dt = Time.deltaTime;

        // --- targets ---
        float rawPitch = pm != null ? pm.CameraPitch : (aiWeapon != null ? aiWeapon.EyePitch : 0f);
        float targetPitch = Mathf.Clamp(rawPitch * pitchFollow, -maxLeanBack, maxBendDown);
        pitch = Mathf.SmoothDamp(pitch, targetPitch, ref pitchVel, 0.08f);

        bool reloading = pm != null ? (setup != null && setup.activeWeapon != null && setup.activeWeapon.GetIsReloading())
                                    : (aiWeapon != null && aiWeapon.IsReloading);
        float targetTwist = reloading ? -reloadTwist : 0f;
        if (switchT < 1f)
        {
            switchT += dt / Mathf.Max(0.05f, switchDuration);
            targetTwist += switchTwist * Mathf.Sin(Mathf.Clamp01(switchT) * Mathf.PI);
        }
        twist = Mathf.Lerp(twist, targetTwist, 1f - Mathf.Exp(-twistSpeed * dt));

        bool aiming = pm != null ? (setup != null && setup.weaponADS != null && setup.weaponADS.IsAiming())
                                 : (aiWeapon != null && aiWeapon.IsAimingNow);
        aimW = Mathf.MoveTowards(aimW, aiming ? 1f : 0f, aimBlendSpeed * dt);
        float aimK = Mathf.SmoothStep(0f, 1f, aimW);

        // --- apply (world-space about the body's axes, each bone about its own joint) ---
        if (lockHipsInPlace && hips != null) LockHips(dt);

        if (stabilizeShoulders && lUpperArm != null && rUpperArm != null) StabilizeShoulders(dt);
        else if (stabilizeBody && hips != null) StabilizeSway(dt);

        Vector3 headBefore = head != null ? root.InverseTransformPoint(head.position) : Vector3.zero;

        Bend(spine, 0.3f); Bend(chest, 0.4f); Bend(upperChest, chest != null ? 0.3f : 0f);
        if (chest == null && upperChest == null) Bend(spine, 0.7f);

        Vector3 up = root.up, fwd = root.forward;
        Transform upperBody = upperChest != null ? upperChest : (chest != null ? chest : spine);
        if (aimK > 0f && upperBody != null) upperBody.position += fwd * (aimForward * aimK);
        if (aimK > 0f)
        {
            if (lShoulder != null) lShoulder.position += up * (aimShoulderRaise * aimK);
            if (rShoulder != null) rShoulder.position += up * (aimShoulderRaise * aimK);
        }

        // Head: hold its yaw to the body's facing while walking forward.
        if (Mathf.Abs(anim.GetFloat(MoveXH)) < 0.05f && Mathf.Abs(anim.GetFloat(MoveYH)) < 0.05f)
            CalibrateLegs(false);

        walkFwdW = Mathf.MoveTowards(walkFwdW, Mathf.Clamp01(anim.GetFloat(MoveYH) / 0.5f), 6f * dt);
        if (headFaceForward && hasHeadFwd && head != null && walkFwdW > 0.001f)
        {
            Vector3 hf = Vector3.ProjectOnPlane(head.rotation * headLocalFwd, root.up);
            if (hf.sqrMagnitude > 1e-4f)
            {
                float herr = Vector3.SignedAngle(hf, root.forward, root.up);
                head.rotation = Quaternion.AngleAxis(herr * headForwardStrength * walkFwdW, root.up) * head.rotation;
            }
        }

        // Camera follows how far the head moved because of all of the above.
        if (head != null && pm != null)
        {
            Vector3 delta = root.InverseTransformPoint(head.position) - headBefore;
            float lk = Mathf.Clamp01(Mathf.Max(0f, rawPitch) / Mathf.Max(1f, lookDownFullPitch));
            lk = lk * lk * (3f - 2f * lk);
            Vector3 want = delta * cameraFollow + new Vector3(0f, -lookDownCameraDrop * lk, lookDownCameraForward * lk)
                + Vector3.forward * (walkForwardCameraPush * walkFwdW);
            camOffset = Vector3.SmoothDamp(camOffset, want, ref camOffsetVel, 0.06f);
            pm.SetCameraTorsoOffset(camOffset);
        }
    }

    // The gun and camera hang off the body ROOT, but the locomotion clips sway the hips (and
    // twist them) left/right with every step - so the body slides around the gun. Track the
    // hips' slow average in root space and subtract only the oscillation from the upper body.
    void StabilizeSway(float dt)
    {
        Transform shiftBone = spine != null ? spine : chest;
        if (shiftBone == null) return;

        Vector3 local = root.InverseTransformPoint(hips.position);
        Vector3 fwdFlat = Vector3.ProjectOnPlane(anim.bodyRotation * Vector3.forward, root.up);
        float yaw = fwdFlat.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(root.forward, fwdFlat, root.up) : 0f;

        if (!hasRest) { restLocal = local; restYaw = yaw; hasRest = true; }
        float k = 1f - Mathf.Exp(-restingRate * dt);
        restLocal = Vector3.Lerp(restLocal, local, k);
        restYaw = Mathf.LerpAngle(restYaw, yaw, k);

        Vector3 dev = local - restLocal;
        Vector3 shiftLocal = new Vector3(-dev.x * stabilizeSway, 0f, -dev.z * stabilizeForward);
        shiftLocal = Vector3.ClampMagnitude(shiftLocal, maxShift);
        shiftBone.position += root.TransformVector(shiftLocal);

        float yawFix = Mathf.Clamp(-Mathf.DeltaAngle(restYaw, yaw) * stabilizeYaw, -maxYawCorrection, maxYawCorrection);
        if (Mathf.Abs(yawFix) > 0.01f)
        {
            // About the hips' position so the spine pivots where it joins the pelvis.
            Quaternion q = Quaternion.AngleAxis(yawFix, root.up);
            shiftBone.rotation = q * shiftBone.rotation;
            Vector3 p = hips.position;
            shiftBone.position = p + q * (shiftBone.position - p);
        }
    }

    void LockHips(float dt)
    {
        Transform par = transform.parent;
        if (par == null) return;
        if (!hasModelBase) { modelBaseLocal = transform.localPosition; hasModelBase = true; }

        Vector3 hp = par.InverseTransformPoint(hips.position);                       // includes last frame's offset
        Vector3 animHips = hp - (transform.localPosition - modelBaseLocal);          // the animation's own hips position
        Vector2 rel = new Vector2(animHips.x - modelBaseLocal.x, animHips.z - modelBaseLocal.z);

        // Learn where the hips sit over the feet when standing still (so the body doesn't get dragged off-centre).
        if (Mathf.Abs(anim.GetFloat(MoveXH)) < 0.05f && Mathf.Abs(anim.GetFloat(MoveYH)) < 0.05f)
            hipRest = Vector2.Lerp(hipRest, rel, 1f - Mathf.Exp(-4f * dt));

        Vector2 off = -(rel - hipRest) * hipLockStrength;
        if (off.magnitude > maxHipLockOffset) off = off.normalized * maxHipLockOffset;
        Vector3 lp = modelBaseLocal;
        lp.x += off.x; lp.z += off.y;
        transform.localPosition = lp;

        if (debugHipTravel)
        {
            dbgMin = Vector2.Min(dbgMin, rel - hipRest); dbgMax = Vector2.Max(dbgMax, rel - hipRest);
            dbgTime += dt;
            if (dbgTime > 2f)
            {
                Debug.Log($"[HipTravel] {name}: hips X {dbgMin.x:F2}..{dbgMax.x:F2}  Z {dbgMin.y:F2}..{dbgMax.y:F2} (metres from rest, before lock)", this);
                dbgTime = 0f; dbgMin = new Vector2(99, 99); dbgMax = new Vector2(-99, -99);
            }
        }
    }

    // Measures the REAL shoulder line (left/right upper-arm bones) - independent of bone axes
    // - and rotates/shifts the spine so it stays square to the body root and steady.
    void StabilizeShoulders(float dt)
    {
        Transform bone = spine != null ? spine : chest;
        if (bone == null) return;

        Vector3 line = rUpperArm.position - lUpperArm.position;
        float len = line.magnitude; if (len < 1e-3f) return;
        Vector3 flat = Vector3.ProjectOnPlane(line, root.up);
        if (flat.sqrMagnitude < 1e-6f) return;

        float yaw = Vector3.SignedAngle(root.right, flat, root.up);                       // + = shoulders turned right
        float roll = Mathf.Asin(Mathf.Clamp(Vector3.Dot(line / len, root.up), -1f, 1f)) * Mathf.Rad2Deg; // + = right shoulder up
        Vector3 c = (rUpperArm.position + lUpperArm.position) * 0.5f;
        Vector3 cl = root.InverseTransformPoint(c);
        Vector2 slide = new Vector2(cl.x, cl.z);

        if (!hasShRest) { shRest = slide; shRestRoll = roll; hasShRest = true; }
        float k = 1f - Mathf.Exp(-restingRate * dt);
        shRest = Vector2.Lerp(shRest, slide, k);
        shRestRoll = Mathf.Lerp(shRestRoll, roll, k);

        // Yaw: wobble AND constant off-facing. Roll: wobble only (keeps a deliberate lean).
        float yawFix = Mathf.Clamp(-yaw * Mathf.Max(shoulderYawRemove, shoulderSquareUp), -maxShoulderYaw, maxShoulderYaw);
        float rollFix = Mathf.Clamp(-(roll - shRestRoll) * shoulderRollRemove, -maxShoulderRoll, maxShoulderRoll);

        Quaternion q = Quaternion.AngleAxis(yawFix, root.up) * Quaternion.AngleAxis(rollFix, root.forward);
        Vector3 pivot = bone.position;
        bone.rotation = q * bone.rotation;

        // Where the shoulder centre ends up after that rotation, then take out its sideways/forward slide.
        Vector3 c2 = pivot + q * (c - pivot);
        Vector3 cl2 = root.InverseTransformPoint(c2);
        Vector3 shift = new Vector3(-(cl2.x - shRest.x) * shoulderSlideRemove, 0f, -(cl2.z - shRest.y) * stabilizeForward);
        bone.position += root.TransformVector(Vector3.ClampMagnitude(shift, maxShift));
    }

    void Bend(Transform bone, float share)
    {
        if (bone == null || share <= 0f) return;
        // + pitch about the body's right axis tips the torso forward; + twist about up turns right.
        Quaternion q = Quaternion.AngleAxis(twist * share, root.up) * Quaternion.AngleAxis(pitch * share, root.right);
        bone.rotation = q * bone.rotation;
    }

    void OnDisable()
    {
        if (pm != null) pm.SetCameraTorsoOffset(Vector3.zero);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }
}