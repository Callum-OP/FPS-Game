using UnityEngine;

/// <summary>
/// Procedural "life" layer on top of whatever the Animator plays. It adds the small
/// secondary motions that make an animated character read as a body with weight instead
/// of a set of clips being swapped:
///
///  - Start / stop weight shift: leans into a start, rocks back on a stop, banks into a
///    sideways change of direction. Works off the change in the body's velocity, so an
///    instant stop (CharacterController) and a gradual one (NavMeshAgent) both work.
///  - Turn lag: the upper body trails the root when it turns and catches up (overlapping
///    action), instead of the whole torso being welded to the root's yaw.
///  - Hit flinch: a directional spring impulse through spine, chest and head. Sits on top
///    of the masked UB_Hit clip, so a hit from the left reads differently to one from behind.
///  - Fire kick / melee lunge: a small recoil through the torso when the character fires,
///    a forward lunge when it swings.
///  - Aim pitch + reload twist for AI (the player already gets these from TorsoPoseDriver,
///    so this steps aside whenever a TorsoPoseDriver shares the Animator).
///  - Head look: the head tracks whoever the AI is looking at (its target, or the last place
///    it saw the player), and glances around while idle.
///
/// Added automatically by CharacterAnimationDriver, so nothing has to be set up by hand.
///
/// Player: the first-person body is deliberately left alone (strength 0). The gun and the
/// camera hang off the body root, and TorsoPoseDriver already spends a lot of effort
/// keeping the torso steady around them - adding motion there would bring the wobble back.
/// In third person the player gets a reduced amount.
///
/// Runs in LateUpdate at order 260: after the Animator and TorsoPoseDriver (250), BEFORE
/// WeaponHandIK (300), so the hands are locked onto the gun last and only the shoulders
/// move around them. It only ever adds to the pose the Animator produced this frame and
/// never reads back its own output, so it can't feed back into itself.
///
/// Costs one small LateUpdate per character; nothing is allocated per frame. Dead
/// characters fade out and stop (see EnterDeathMode).
/// </summary>
[DefaultExecutionOrder(260)]
public class CharacterMotionPolish : MonoBehaviour
{
    [Header("Strength (0 = off)")]
    [Tooltip("Enemies and allies.")]
    [Range(0f, 1f)] public float aiStrength = 1f;
    [Tooltip("The player while the third-person camera is on.")]
    [Range(0f, 1f)] public float playerThirdPersonStrength = 0.5f;
    [Tooltip("The player in first person. Leave at 0: the torso is stabilised around the gun and camera on purpose.")]
    [Range(0f, 1f)] public float playerFirstPersonStrength = 0f;

    [Header("Start / Stop Weight Shift")]
    public bool weightShift = true;
    [Tooltip("Degrees of forward/back lean per m/s of sudden speed change. 5 m/s to a stop at 1.3 = about 6.5 degrees rocking back.")]
    public float leanPerMetrePerSecond = 1.3f;
    [Tooltip("Degrees of sideways bank per m/s of sudden sideways speed change.")]
    public float sideLeanPerMetrePerSecond = 1.0f;
    [Tooltip("Seconds the smoothed velocity takes to catch up with the real one. Longer = a longer, softer lean.")]
    public float velocityFollowTime = 0.16f;
    public float maxForwardLean = 9f;
    public float maxBackLean = 8f;
    public float maxSideLean = 6f;
    [Tooltip("Seconds to smooth the lean itself.")]
    public float leanSmoothTime = 0.07f;

    [Header("Turn Lag (overlapping action)")]
    public bool turnLag = true;
    [Tooltip("Seconds the upper body takes to catch up with the root's facing.")]
    public float yawFollowTime = 0.14f;
    [Tooltip("Fraction of the lag that is shown.")]
    [Range(0f, 1f)] public float turnLagAmount = 0.4f;
    public float maxTurnLag = 18f;

    [Header("Impacts")]
    public bool hitFlinch = true;
    [Tooltip("Degrees/second of spring velocity per unit of hit force (a rifle round is 5 by default).")]
    public float flinchImpulse = 26f;
    public float maxFlinchVelocity = 320f;
    public float maxFlinchAngle = 22f;
    [Tooltip("The head moves this many times as much as the torso.")]
    public float headFlinchScale = 1.6f;
    public float springStiffness = 190f;
    public float springDamping = 15f;
    public bool fireKick = true;
    [Tooltip("Backwards spring velocity (deg/s) added by each shot.")]
    public float fireKickImpulse = 70f;
    [Tooltip("Forward spring velocity (deg/s) added by a melee swing.")]
    public float lungeImpulse = 240f;

    [Header("Aim (AI only)")]
    [Tooltip("Bend the torso towards where the AI is aiming, the same way TorsoPoseDriver does for the player.")]
    public bool aiAimPitch = true;
    [Range(0f, 0.7f)] public float aimPitchFollow = 0.35f;
    public float maxAimBendDown = 25f;
    public float maxAimLeanBack = 18f;
    [Tooltip("Degrees the torso twists left while the AI reloads (reaching for the magazine).")]
    public float reloadTwist = 10f;
    public float twistSpeed = 6f;

    [Header("Head Look")]
    public bool headLook = true;
    public float maxHeadYaw = 65f;
    public float maxHeadPitchUp = 30f;
    public float maxHeadPitchDown = 35f;
    [Tooltip("Seconds the head takes to swing round to its target.")]
    public float lookSmoothTime = 0.12f;
    [Tooltip("How fast the look blends in and out (per second).")]
    public float lookWeightSpeed = 5f;
    [Tooltip("Share of the head turn taken by the neck (the rest is the head itself).")]
    [Range(0f, 1f)] public float neckShare = 0.4f;
    [Tooltip("While nothing is being looked at and the AI is standing still, glance around now and then.")]
    public bool idleGlances = true;
    public Vector2 glanceInterval = new Vector2(2.5f, 6f);
    public float glanceYaw = 35f;

    // ---- references ----
    Animator anim;
    Transform root, spine, chest, upperChest, neck, head;
    PlayerMovement pm;
    EnemyWeapon aiWeapon;
    bool hasTorsoDriver;
    bool initialised;

    // ---- weight shift ----
    Vector3 lastRootPos, velFollow;
    bool hasLastPos;
    float leanPitch, leanPitchVel, leanRoll, leanRollVel;

    // ---- turn lag ----
    float yawFollow, lagYaw, lagYawVel;
    bool hasYaw;

    // ---- springs: [0] pitch (+ tips forward), [1] roll (+ leans right), [2] yaw (+ turns right) ----
    readonly float[] sx = new float[3];
    readonly float[] sv = new float[3];

    // ---- aim ----
    float aimPitch, aimPitchVel, aimTwist;

    // ---- look ----
    Transform lookTransform;
    float lookHeight = 1.4f;
    bool hasLookPoint;
    Vector3 lookPoint;
    Vector3 headLocalFwd; bool hasHeadFwd;
    float lookW, appliedYaw, appliedYawVel, appliedPitch, appliedPitchVel;
    float glanceTimer, glanceHold, glanceTarget;

    // ---- death ----
    bool dead;
    float deadBlend;

    /// <summary>Which way the character is currently being asked to look, if anywhere.</summary>
    public bool HasLookTarget => lookTransform != null || hasLookPoint;

    void Start()
    {
        anim = GetComponent<Animator>();
        if (anim == null || !anim.isHuman) { enabled = false; return; }

        pm = GetComponentInParent<PlayerMovement>();
        aiWeapon = GetComponentInParent<EnemyWeapon>();
        var ragdoll = GetComponentInParent<Ragdoll>();
        root = pm != null ? pm.transform : (ragdoll != null ? ragdoll.transform : transform.root);
        hasTorsoDriver = GetComponent<TorsoPoseDriver>() != null;

        spine = anim.GetBoneTransform(HumanBodyBones.Spine);
        chest = anim.GetBoneTransform(HumanBodyBones.Chest);
        upperChest = anim.GetBoneTransform(HumanBodyBones.UpperChest);
        neck = anim.GetBoneTransform(HumanBodyBones.Neck);
        head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (spine == null && chest == null) { enabled = false; return; }

        // Head-local direction that points forward in the bind pose - the same rig-independent
        // way TorsoPoseDriver reads head yaw, so it doesn't rely on a guessed bone axis.
        if (head != null) { headLocalFwd = Quaternion.Inverse(head.rotation) * root.forward; hasHeadFwd = true; }

        glanceTimer = Random.Range(glanceInterval.x, glanceInterval.y);
        initialised = true;
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>Look at a transform (its position plus heightOffset metres up).</summary>
    public void SetLookTarget(Transform target, float heightOffset = 1.4f)
    {
        lookTransform = target;
        lookHeight = heightOffset;
        hasLookPoint = false;
    }

    /// <summary>Look at a fixed world point (e.g. the last place the player was seen).</summary>
    public void SetLookPoint(Vector3 worldPoint)
    {
        lookTransform = null;
        hasLookPoint = true;
        lookPoint = worldPoint;
    }

    public void ClearLook()
    {
        lookTransform = null;
        hasLookPoint = false;
    }

    /// <summary>Directional flinch. worldTravelDirection is the way the bullet was travelling.</summary>
    public void Flinch(Vector3 worldTravelDirection, float force)
    {
        if (!hitFlinch || dead || !initialised || force <= 0f) return;
        if (StrengthNow() <= 0.001f) return;

        Vector3 d = worldTravelDirection; d.y = 0f;
        if (d.sqrMagnitude < 1e-4f) return;
        Vector3 local = root.InverseTransformDirection(d.normalized);
        float mag = Mathf.Min(force, 20f) * flinchImpulse;

        // Shot from behind shoves the torso forward, shot from the left shoves it right.
        AddImpulse(0, local.z * mag);
        AddImpulse(1, local.x * mag);
        AddImpulse(2, Random.Range(-1f, 1f) * mag * 0.3f);
    }

    /// <summary>Small recoil through the torso per shot.</summary>
    public void Kick(float scale = 1f)
    {
        if (!fireKick || dead || !initialised || StrengthNow() <= 0.001f) return;
        AddImpulse(0, -fireKickImpulse * scale);
        AddImpulse(2, Random.Range(-1f, 1f) * fireKickImpulse * 0.15f * scale);
    }

    /// <summary>Forward lunge for a melee swing.</summary>
    public void Lunge()
    {
        if (dead || !initialised || StrengthNow() <= 0.001f) return;
        AddImpulse(0, lungeImpulse);
        AddImpulse(2, Random.Range(-1f, 1f) * lungeImpulse * 0.25f);
    }

    /// <summary>The character is dying: fade everything out so the authored death animation
    /// (and then the ragdoll) has the skeleton to itself.</summary>
    public void EnterDeathMode() { dead = true; }

    void AddImpulse(int axis, float velocity)
    {
        sv[axis] = Mathf.Clamp(sv[axis] + velocity, -maxFlinchVelocity, maxFlinchVelocity);
    }

    float StrengthNow()
    {
        if (pm != null) return ThirdPersonMode.Active ? playerThirdPersonStrength : playerFirstPersonStrength;
        return aiStrength;
    }

    // ------------------------------------------------------------------
    // Frame update
    // ------------------------------------------------------------------

    void LateUpdate()
    {
        if (!initialised || anim == null || !anim.enabled) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        deadBlend = Mathf.MoveTowards(deadBlend, dead ? 1f : 0f, dt / 0.1f);
        float live = 1f - deadBlend;
        float strength = StrengthNow() * live;

        if (strength <= 0.001f)
        {
            // Nothing to add. Forget history so the first frame back doesn't see a huge jump.
            hasLastPos = false; hasYaw = false;
            sx[0] = sx[1] = sx[2] = 0f; sv[0] = sv[1] = sv[2] = 0f;
            leanPitch = leanRoll = 0f;
            return;
        }

        Vector3 up = root.up, right = root.right, fwd = root.forward;

        float pitch = 0f, roll = 0f, yaw = 0f;

        // ---- start / stop weight shift ----
        if (weightShift)
        {
            Vector3 pos = root.position;
            if (!hasLastPos) { lastRootPos = pos; velFollow = Vector3.zero; hasLastPos = true; }
            Vector3 vel = (pos - lastRootPos) / dt;
            vel.y = 0f;
            lastRootPos = pos;

            // How far the real velocity is ahead of a slowly-following copy of itself: a step
            // change in speed shows up at full size at once and then decays as the copy catches
            // up, which is exactly the rock-back on a stop or push-off on a start.
            Vector3 dv = vel - velFollow;
            velFollow += dv * (1f - Mathf.Exp(-dt / Mathf.Max(0.02f, velocityFollowTime)));
            Vector3 dvLocal = root.InverseTransformDirection(dv);

            float wantPitch = dvLocal.z * leanPerMetrePerSecond;
            wantPitch = Mathf.Clamp(wantPitch, -maxBackLean, maxForwardLean);
            float wantRoll = Mathf.Clamp(dvLocal.x * sideLeanPerMetrePerSecond, -maxSideLean, maxSideLean);

            leanPitch = Mathf.SmoothDamp(leanPitch, wantPitch, ref leanPitchVel, leanSmoothTime);
            leanRoll = Mathf.SmoothDamp(leanRoll, wantRoll, ref leanRollVel, leanSmoothTime);
            pitch += leanPitch;
            roll += leanRoll;
        }

        // ---- turn lag ----
        if (turnLag)
        {
            float rootYaw = root.eulerAngles.y;
            if (!hasYaw) { yawFollow = rootYaw; hasYaw = true; }
            // yawFollow trails the root; the gap is how far the root has turned "ahead" of the torso.
            float gap = Mathf.DeltaAngle(yawFollow, rootYaw);
            yawFollow += gap * (1f - Mathf.Exp(-dt / Mathf.Max(0.02f, yawFollowTime)));
            float wantLag = Mathf.Clamp(-gap * turnLagAmount, -maxTurnLag, maxTurnLag);
            lagYaw = Mathf.SmoothDamp(lagYaw, wantLag, ref lagYawVel, 0.05f);
            yaw += lagYaw;
        }

        // ---- springs (flinch, fire kick, lunge) ----
        if (hitFlinch || fireKick)
        {
            int steps = dt > 0.02f ? 2 : 1;
            float h = Mathf.Min(dt, 0.05f) / steps;
            for (int s = 0; s < steps; s++)
                for (int i = 0; i < 3; i++)
                {
                    float a = -springStiffness * sx[i] - springDamping * sv[i];
                    sv[i] += a * h;
                    sx[i] += sv[i] * h;
                    sx[i] = Mathf.Clamp(sx[i], -maxFlinchAngle, maxFlinchAngle);
                }
            pitch += sx[0];
            roll += sx[1];
            yaw += sx[2];
        }

        // ---- AI aim pitch and reload twist ----
        if (aiWeapon != null && !hasTorsoDriver && aiAimPitch)
        {
            float targetPitch = Mathf.Clamp(aiWeapon.EyePitch * aimPitchFollow, -maxAimLeanBack, maxAimBendDown);
            aimPitch = Mathf.SmoothDamp(aimPitch, targetPitch, ref aimPitchVel, 0.08f);
            aimTwist = Mathf.Lerp(aimTwist, aiWeapon.IsReloading ? -reloadTwist : 0f, 1f - Mathf.Exp(-twistSpeed * dt));
            pitch += aimPitch;
            yaw += aimTwist;
        }

        // ---- apply to the spine chain ----
        if (Mathf.Abs(pitch) > 0.01f || Mathf.Abs(roll) > 0.01f || Mathf.Abs(yaw) > 0.01f)
            ApplyTorso(pitch * strength, roll * strength, yaw * strength, up, right, fwd);

        // Head flinch on top of the torso's.
        if (head != null && (hitFlinch || fireKick))
        {
            float extra = headFlinchScale - 1f;
            if (extra > 0.01f && (Mathf.Abs(sx[0]) > 0.01f || Mathf.Abs(sx[1]) > 0.01f))
            {
                Quaternion q = Quaternion.AngleAxis(-sx[1] * extra * strength, fwd)
                             * Quaternion.AngleAxis(sx[0] * extra * strength, right);
                head.rotation = q * head.rotation;
            }
        }

        if (headLook && head != null && hasHeadFwd)
            ApplyHeadLook(dt, strength, up);
    }

    // Spreads a rotation over spine, chest and upper chest so the bend is smooth rather than
    // a single hinge, using the same shares as TorsoPoseDriver.
    void ApplyTorso(float pitch, float roll, float yaw, Vector3 up, Vector3 right, Vector3 fwd)
    {
        float sS = 0.3f, sC = 0.4f, sU = 0.3f;
        if (chest == null && upperChest == null) { sS = 1f; sC = 0f; sU = 0f; }
        else if (upperChest == null) { sC = 0.7f; sU = 0f; }
        else if (chest == null) { sU = 0.7f; sC = 0f; }

        Bend(spine, sS, pitch, roll, yaw, up, right, fwd);
        Bend(chest, sC, pitch, roll, yaw, up, right, fwd);
        Bend(upperChest, sU, pitch, roll, yaw, up, right, fwd);
    }

    static void Bend(Transform bone, float share, float pitch, float roll, float yaw, Vector3 up, Vector3 right, Vector3 fwd)
    {
        if (bone == null || share <= 0f) return;
        // + pitch about the body's right axis tips forward; + roll leans right (hence the minus
        // about forward); + yaw turns right.
        Quaternion q = Quaternion.AngleAxis(yaw * share, up)
                     * Quaternion.AngleAxis(-roll * share, fwd)
                     * Quaternion.AngleAxis(pitch * share, right);
        bone.rotation = q * bone.rotation;
    }

    // ------------------------------------------------------------------
    // Head look
    // ------------------------------------------------------------------

    void ApplyHeadLook(float dt, float strength, Vector3 up)
    {
        bool hasTarget = HasLookTarget;
        Vector3 headPos = head.position;
        Vector3 headFwd = head.rotation * headLocalFwd;
        Vector3 flatFwd = Vector3.ProjectOnPlane(headFwd, up);
        if (flatFwd.sqrMagnitude < 1e-5f) return;
        flatFwd.Normalize();

        float yawErr = 0f, pitchErr = 0f;
        float wantW = 0f;

        if (hasTarget)
        {
            Vector3 point = lookTransform != null ? lookTransform.position + up * lookHeight : lookPoint;
            Vector3 to = point - headPos;
            Vector3 toFlat = Vector3.ProjectOnPlane(to, up);
            if (toFlat.sqrMagnitude > 0.01f)
            {
                // Target yaw relative to the BODY, clamped, then turned back into an error
                // against where the animation currently has the head pointing.
                float relToBody = Mathf.Clamp(Vector3.SignedAngle(root.forward, toFlat, up), -maxHeadYaw, maxHeadYaw);
                float headRel = Vector3.SignedAngle(root.forward, flatFwd, up);
                yawErr = Mathf.DeltaAngle(headRel, relToBody);

                float wantElev = Mathf.Atan2(to.y, toFlat.magnitude) * Mathf.Rad2Deg;
                wantElev = Mathf.Clamp(wantElev, -maxHeadPitchDown, maxHeadPitchUp);
                float curElev = Mathf.Asin(Mathf.Clamp(headFwd.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
                pitchErr = wantElev - curElev;
                wantW = 1f;
            }
        }
        else if (idleGlances && aiWeapon != null && LocalSpeedLow())
        {
            glanceTimer -= dt;
            if (glanceHold > 0f)
            {
                glanceHold -= dt;
                if (glanceHold <= 0f) { glanceTarget = 0f; glanceTimer = Random.Range(glanceInterval.x, glanceInterval.y); }
            }
            else if (glanceTimer <= 0f)
            {
                glanceTarget = Random.Range(-glanceYaw, glanceYaw);
                glanceHold = Random.Range(0.8f, 1.8f);
            }

            if (Mathf.Abs(glanceTarget) > 0.5f)
            {
                float headRel = Vector3.SignedAngle(root.forward, flatFwd, up);
                yawErr = Mathf.DeltaAngle(headRel, glanceTarget);
                wantW = 0.8f;
            }
        }

        lookW = Mathf.MoveTowards(lookW, wantW, lookWeightSpeed * dt);
        if (lookW <= 0.001f && !hasTarget)
        {
            // Glide the applied angles back to zero so a released look doesn't snap.
            appliedYaw = Mathf.SmoothDamp(appliedYaw, 0f, ref appliedYawVel, lookSmoothTime);
            appliedPitch = Mathf.SmoothDamp(appliedPitch, 0f, ref appliedPitchVel, lookSmoothTime);
        }
        else
        {
            appliedYaw = Mathf.SmoothDamp(appliedYaw, yawErr, ref appliedYawVel, lookSmoothTime);
            appliedPitch = Mathf.SmoothDamp(appliedPitch, pitchErr, ref appliedPitchVel, lookSmoothTime);
        }

        float w = Mathf.Max(lookW, 0f) * strength;
        float yawApply = appliedYaw * w;
        float pitchApply = appliedPitch * w;
        if (Mathf.Abs(yawApply) < 0.02f && Mathf.Abs(pitchApply) < 0.02f) return;

        Vector3 rightFlat = Vector3.Cross(up, flatFwd).normalized;
        // Positive rotation about the body's right axis tips the face DOWN, so looking up is negative.
        Quaternion q = Quaternion.AngleAxis(yawApply, up) * Quaternion.AngleAxis(-pitchApply, rightFlat);

        if (neck != null && neckShare > 0f)
        {
            Quaternion qn = Quaternion.Slerp(Quaternion.identity, q, neckShare);
            neck.rotation = qn * neck.rotation;
            Quaternion qh = Quaternion.Slerp(Quaternion.identity, q, 1f - neckShare);
            head.rotation = qh * head.rotation;
        }
        else
        {
            head.rotation = q * head.rotation;
        }
    }

    bool LocalSpeedLow() => velFollow.sqrMagnitude < 0.09f;

    void OnDisable()
    {
        hasLastPos = false; hasYaw = false;
    }
}
