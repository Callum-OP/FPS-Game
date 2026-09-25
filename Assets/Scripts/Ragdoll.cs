using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Death: an authored death animation for the first part of the fall, then a physics ragdoll
/// that takes over from the exact pose and motion the animation had.
///
/// The bone Rigidbodies/Colliders (built by the "Build Ragdoll" menu) stay kinematic + disabled
/// while alive so the Animator drives the pose. Auto-hooks Health (enemies) and PlayerHealth
/// (player) death events.
///
/// How a death plays out:
///  1. Which clip: from where the killing shot came from (the projectile registers its collider
///     and direction, see RegisterHit), whether it hit the head, whether the body was crouching,
///     and how fast it was moving - a runner falls the way it was running. The variants come from
///     CharacterAnimationDriver.TryChooseDeath (front/back/side/headshot/crouch, plus mirrored
///     copies of each).
///  2. The clip plays for ragdollHandoffPoint of its length (about a third), with the upper-body
///     layer, hand IK and every procedural pose writer faded out so the whole body falls. If the
///     body was moving it keeps sliding forward with its momentum (momentumCarry).
///  3. Handoff, at the END of a rendered frame so the pose on screen is the pose physics starts
///     from: the bones are seeded with the linear AND angular velocity the animation was moving
///     them with (measured over the last few frames, at each bone's centre of mass), the joints are
///     opened just wide enough to contain the animated pose and then tightened over
///     jointRelaxTime, and the killing shot's impulse is applied at the bone it hit.
///  Explosions, big shotgun shoves and mid-air deaths skip the clip and go straight to physics,
///  still with the body's own momentum.
/// </summary>
public class Ragdoll : MonoBehaviour
{
    public Animator animator;

    [Header("Death Animation")]
    [Tooltip("Play an authored death clip before the ragdoll takes over. Explosions, hard knockbacks and mid-air deaths always go straight to physics.")]
    public bool useDeathAnimations = true;
    [Tooltip("How far through the death clip the ragdoll takes over (0.33 = a third). Earlier hands more of the fall to physics; later keeps more of the authored motion.")]
    [Range(0.1f, 0.9f)] public float ragdollHandoffPoint = 0.33f;
    [Tooltip("Random +/- added to the handoff point per death, so a group of deaths doesn't all switch at the same instant.")]
    [Range(0f, 0.15f)] public float handoffJitter = 0.04f;
    [Tooltip("The clip always plays at least this long (seconds) before the handoff.")]
    public float minAnimationTime = 0.25f;
    [Tooltip("Safety cap in seconds - the ragdoll always takes over by now.")]
    public float deathAnimationTimeCap = 2f;
    [Tooltip("Seconds to crossfade from whatever the body was doing into the death clip.")]
    public float deathCrossfade = 0.1f;
    [Tooltip("Random playback speed per death.")]
    public Vector2 deathSpeedRange = new Vector2(0.95f, 1.1f);
    [Tooltip("Also use mirrored copies of the clips, which doubles the number of distinct deaths.")]
    public bool mirrorVariants = true;
    [Tooltip("The 'death from right' clip is assumed to fall towards the character's left. Untick if the side deaths fall into the shot.")]
    public bool rightDeathFallsLeft = true;

    [Header("Momentum")]
    [Tooltip("Fraction of the body's speed at the moment of death that it keeps sliding with while the clip plays. A runner shot dead skids and tumbles instead of stopping dead.")]
    [Range(0f, 1f)] public float momentumCarry = 0.6f;
    [Tooltip("How quickly that slide dies away (per second).")]
    public float slideDamping = 3.5f;
    [Tooltip("How strongly the body's speed steers which clip is chosen, per m/s. A runner moving forward at 5 m/s outweighs the direction of the shot.")]
    public float momentumSteering = 0.35f;

    [Header("Handoff To Physics")]
    [Tooltip("How much of the clip's own bone velocity is handed to the ragdoll. 1 = it carries on falling exactly the way the clip was moving it, which is what makes the handover invisible.")]
    [Range(0f, 1.5f)] public float velocityInheritance = 1f;
    [Tooltip("Same for spin.")]
    [Range(0f, 1.5f)] public float angularInheritance = 1f;
    [Tooltip("Cap on the speed any bone starts with (m/s).")]
    public float maxHandoffSpeed = 7f;
    [Tooltip("Cap on the spin any bone starts with (rad/s).")]
    public float maxHandoffAngularSpeed = 18f;
    [Tooltip("Impulse (N s) the killing shot gives the bone it hit, along the direction it travelled. Headshots get headImpulseScale times this.")]
    public float hitImpulse = 3f;
    public float headImpulseScale = 1.5f;
    [Tooltip("Joint limits the ragdoll settles to. They start wide enough to contain the animated pose (so nothing snaps) and tighten to these.")]
    public float looseSwingLimit = 70f;
    public float looseTwistLimit = 45f;
    [Tooltip("Seconds the joint limits take to tighten from the animated pose to the values above.")]
    public float jointRelaxTime = 0.6f;

    [Header("Ground Safety")]
    [Tooltip("Backstop for bodies that get through the floor anyway: for a few seconds after death the hips are raycast against the ground and the whole corpse is lifted back up if it ends up below it.")]
    public bool groundClamp = true;
    [Tooltip("Layers treated as ground by the clamp above.")]
    public LayerMask groundLayers = ~0;
    [Tooltip("How long the clamp stays active after death.")]
    public float groundClampDuration = 4f;
    [Tooltip("How far below the ground the hips have to be before the body is lifted.")]
    public float groundClampTolerance = 0.35f;

    [Header("Settling")]
    [Tooltip("Once the corpse has stopped moving its bones are frozen (kinematic), so later gunfire, grenades or pushes can't drive it through the floor.")]
    public bool freezeWhenSettled = true;
    [Tooltip("Bones slower than this (m/s) count as settled.")]
    public float settleSpeed = 0.2f;
    [Tooltip("How long everything must stay slower than settleSpeed before freezing.")]
    public float settleTime = 0.6f;
    [Tooltip("Freeze regardless after this long.")]
    public float maxSettleTime = 8f;

    [Header("Explosions")]
    [Tooltip("Extra impulse applied to each bone when killed by an explosion, on top of whatever the blast itself pushed.")]
    public float explosionRagdollForce = 9f;

    [Header("Gunfire Knockback")]
    [Tooltip("Total accumulated hit force needed on the killing blow to skip the death animation entirely and go straight to a shoved ragdoll. A single rifle round stays well under this; several shotgun pellets landing the same frame add up past it.")]
    public float knockbackSkipAnimationThreshold = 16f;
    [Tooltip("Multiplies the accumulated hit force into an actual physics impulse spread across every bone when the threshold above is exceeded.")]
    public float knockbackImpulseScale = 5f;

    Vector3 pendingHitDirectionSum;
    float pendingHitForceSum;
    bool knockedBack;

    // last registered hit (RegisterHit) - the killing blow is the last one before Die()
    Vector3 lastHitDirection, lastHitPoint;
    bool hasHitInfo, lastHitWasHead;
    Rigidbody lastHitBody;

    Rigidbody[] bones;
    Vector3[] comLocal;         // each bone's centre of mass, bone-local (capsule centre)
    Transform[] allBones;
    Vector3[] restLocalPos;
    CharacterJoint[] joints;
    Quaternion[] jointRest;     // relative rotation of each joint's bodies at rest (where the limits are centred)
    bool dead, handedOff;
    bool liveHitboxes;
    Transform hipsBone;
    Rigidbody headBody;
    CharacterAnimationDriver driver;
    NavMeshAgent agent;
    CharacterController cc;
    Vector3 rootVelocity;       // planar + vertical, captured the instant of death

    // motion history recorded (only) while dying, newest last
    const int HistoryLen = 6;
    float[] histTime;
    Vector3[][] histPos;
    Quaternion[][] histRot;
    int histCount;

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        var skeleton = animator != null ? animator.transform : transform;
        bones = skeleton.GetComponentsInChildren<Rigidbody>();

        // Snapshot every bone's local position while the rig is untouched, so a pathological pose
        // (humanoid stretch flinging limbs metres away) can be healed before physics starts.
        allBones = skeleton.GetComponentsInChildren<Transform>();
        restLocalPos = new Vector3[allBones.Length];
        for (int i = 0; i < allBones.Length; i++)
            restLocalPos[i] = allBones[i].localPosition;
        if (animator != null && animator.isHuman)
        {
            hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null) headBody = head.GetComponent<Rigidbody>();
        }

        comLocal = new Vector3[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            var cap = bones[i] != null ? bones[i].GetComponent<CapsuleCollider>() : null;
            comLocal[i] = cap != null ? cap.center : Vector3.zero;
        }

        joints = skeleton.GetComponentsInChildren<CharacterJoint>();
        jointRest = new Quaternion[joints.Length];
        for (int i = 0; i < joints.Length; i++)
        {
            var parent = joints[i].connectedBody;
            jointRest[i] = parent != null
                ? Quaternion.Inverse(parent.transform.rotation) * joints[i].transform.rotation
                : Quaternion.identity;
        }

        driver = GetComponentInChildren<CharacterAnimationDriver>();
        agent = GetComponentInParent<NavMeshAgent>();
        cc = GetComponentInParent<CharacterController>();

        // the player must NOT have live bone colliders (own bullets would hit their arms)
        liveHitboxes = GetComponentInParent<PlayerHealth>() == null;
        SetPhysics(false);
    }

    void Start()
    {
        var h = GetComponentInParent<Health>();
        if (h != null) h.onDeath.AddListener(Die);
        var ph = GetComponentInParent<PlayerHealth>();
        if (ph != null) ph.onDeath += Die;
    }

    void SetPhysics(bool on)
    {
        if (bones == null) return;
        foreach (var rb in bones)
        {
            if (rb == null) continue;
            rb.isKinematic = !on;
            if (on)
            {
                // Thin capsules tunnel through floor meshes with discrete collision. Speculative
                // continuous detection catches bones that were teleported into place.
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                // Gentle multi-frame push-out of any overlap instead of one explosive pop.
                rb.maxDepenetrationVelocity = 2f;
                rb.solverIterations = 12;
                rb.solverVelocityIterations = 4;
            }
            var col = rb.GetComponent<Collider>();
            if (col != null && !(col is CharacterController))
            {
                col.enabled = on || liveHitboxes;
                // alive: hitboxes are TRIGGERS so they exert no physical force on the player/props.
                // dead: solid again so the ragdoll collides with the world.
                col.isTrigger = !on && liveHitboxes;
            }
        }
    }

    // ------------------------------------------------------------------
    // Inputs from the rest of the game
    // ------------------------------------------------------------------

    /// <summary>Called by Explosive just before the killing damage lands. An explosive death skips the
    /// death animation entirely, and if the blast was close enough, the body comes apart.</summary>
    public void NotifyExplosion(Vector3 blastCentre, float force, bool dismember)
    {
        killedByExplosion = true;
        explosionCentre = blastCentre;
        explosionForceReceived = force;
        explosionDismembers = dismember;
    }

    bool usedClip;
    bool killedByExplosion;
    Vector3 explosionCentre;
    float explosionForceReceived;
    bool explosionDismembers;

    /// <summary>Called by Health/PlayerHealth on every hit that carries a knockback amount. Force
    /// ACCUMULATES, so several shotgun pellets landing in the same frame add up to a real shove.
    /// Also makes a living body flinch in the direction it was hit.</summary>
    public void AccumulateHitForce(Vector3 direction, float force)
    {
        if (force <= 0f || direction.sqrMagnitude < 0.0001f) return;
        pendingHitDirectionSum += direction.normalized * force;
        pendingHitForceSum += force;
        if (!dead) driver?.NotifyHit(direction, force);
    }

    /// <summary>Called by Projectile just before it damages this body: which collider it hit, where, and the
    /// way it was travelling. The last one before death decides the death clip (headshot or not, which
    /// side) and where the killing impulse is applied.</summary>
    public void RegisterHit(Collider col, Vector3 point, Vector3 travelDirection)
    {
        if (dead) return;
        if (travelDirection.sqrMagnitude < 0.0001f) return;
        lastHitDirection = travelDirection.normalized;
        lastHitPoint = point;
        hasHitInfo = true;
        lastHitBody = col != null ? col.attachedRigidbody : null;
        lastHitWasHead = lastHitBody != null && lastHitBody == headBody;
    }

    /// <summary>Optional: tell the ragdoll which way the killing shot was travelling.</summary>
    public void NotifyHitDirection(Vector3 worldDirectionOfShot)
    {
        if (worldDirectionOfShot.sqrMagnitude < 0.0001f) return;
        lastHitDirection = worldDirectionOfShot.normalized;
        hasHitInfo = true;
    }

    public void Die()
    {
        if (dead) return;
        dead = true;

        // Read now, in the same call as the killing shot: the agent is about to be stopped and its
        // velocity would be gone by the time the coroutine runs.
        rootVelocity = Vector3.zero;
        if (agent != null && agent.enabled) rootVelocity = agent.velocity;
        else if (cc != null && cc.enabled) rootVelocity = cc.velocity;

        StartCoroutine(DeathSequence());
    }

    // ------------------------------------------------------------------
    // Death sequence
    // ------------------------------------------------------------------

    IEnumerator DeathSequence()
    {
        knockedBack = !killedByExplosion && pendingHitForceSum >= knockbackSkipAnimationThreshold;

        bool airborne = IsAirborne();
        // A knockback-sized hit used to skip the animation. It now plays the fast "as if shotgunned" clip
        // (if the controller has one) and the shove is applied on top at the handoff.
        bool animate = useDeathAnimations && animator != null && animator.enabled
            && animator.runtimeAnimatorController != null && driver != null
            && !killedByExplosion && !airborne;

        DeathChoice choice = default;
        if (animate) animate = driver.TryChooseDeath(BuildDeathRequest(), out choice);

        BeginDeathPose();

        float speed = Random.Range(Mathf.Min(deathSpeedRange.x, deathSpeedRange.y), Mathf.Max(deathSpeedRange.x, deathSpeedRange.y));
        // Per-clip handoff (measured), scaled by the Inspector value relative to its 0.33 default.
        float clipPoint = animate && choice.handoffPoint > 0f ? choice.handoffPoint : 0.33f;
        float point = Mathf.Clamp(clipPoint * (ragdollHandoffPoint / 0.33f) + Random.Range(-handoffJitter, handoffJitter), 0.08f, 0.95f);
        usedClip = animate;
        if (animate) driver.PlayDeath(choice, deathCrossfade, speed);
        else if (driver != null && driver.Polish != null) driver.Polish.EnterDeathMode();

        Vector3 planar = new Vector3(rootVelocity.x, 0f, rootVelocity.z);
        if (momentumCarry > 0f && planar.sqrMagnitude > 0.25f)
            StartCoroutine(SlideRoutine(planar * momentumCarry));

        histCount = 0;
        float start = Time.time;
        float length = 0f;
        int frames = 0;

        while (true)
        {
            // End of the frame: every bone is exactly where it is drawn this frame.
            yield return new WaitForEndOfFrame();
            RecordSample();
            frames++;
            float elapsed = Time.time - start;

            bool ready;
            if (animate)
            {
                if (length <= 0f && frames >= 2) length = driver.GetStateLength(choice.stateHash);
                float clip = length > 0f ? length : 2f;
                float wanted = Mathf.Clamp(clip * point / Mathf.Max(0.1f, speed), minAnimationTime, deathAnimationTimeCap);
                ready = elapsed >= wanted || elapsed >= deathAnimationTimeCap;
            }
            else
            {
                ready = elapsed >= 0.05f;
            }

            if (ready && frames >= 3) break;
        }

        Handoff();
    }

    // Everything that would write to the bones (or fight the death pose) stops or fades here.
    void BeginDeathPose()
    {
        var loco = GetComponentInChildren<CharacterLocomotion>(); if (loco != null) loco.enabled = false;
        foreach (var d in GetComponentsInChildren<TorsoMotionDampener>(true)) d.enabled = false;
        foreach (var t in GetComponentsInChildren<TorsoPoseDriver>(true)) t.EnterDeathMode();
        foreach (var w in GetComponentsInChildren<WeaponHandIK>(true)) w.SetGripTargets(null, null);
    }

    Vector3 FallbackHitDirection()
    {
        if (pendingHitDirectionSum.sqrMagnitude > 0.0001f) return pendingHitDirectionSum.normalized;
        var playerObj = GameObject.FindGameObjectWithTag("Player");
        if (playerObj != null)
        {
            Vector3 away = Vector3.ProjectOnPlane(transform.position - playerObj.transform.position, Vector3.up);
            if (away.sqrMagnitude > 0.0001f) return away.normalized;
        }
        return -transform.forward;
    }

    DeathRequest BuildDeathRequest()
    {
        Vector3 hit = hasHitInfo ? lastHitDirection : FallbackHitDirection();
        hit = Vector3.ProjectOnPlane(hit, Vector3.up);
        hit = hit.sqrMagnitude > 0.0001f ? hit.normalized : -transform.forward;

        Vector3 vel = new Vector3(rootVelocity.x, 0f, rootVelocity.z);
        Vector3 fallWorld = hit + vel * momentumSteering;
        Vector3 local = transform.InverseTransformDirection(fallWorld);
        local.y = 0f;

        return new DeathRequest
        {
            fallDirLocal = local,
            headshot = hasHitInfo && lastHitWasHead,
            crouching = driver != null && driver.IsCrouching,
            allowMirrored = mirrorVariants,
            rightDeathFallsLeft = rightDeathFallsLeft,
            planarSpeed = vel.magnitude,
            heavy = knockedBack
        };
    }

    bool IsAirborne()
    {
        var pm = GetComponentInParent<PlayerMovement>();
        bool grounded = true;
        if (pm != null) grounded = pm.IsGrounded;
        else if (cc != null && cc.enabled) grounded = cc.isGrounded;
        if (grounded) return false;
        // Controller ground flags flicker; only call it airborne if there's really nothing below.
        return !Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down, 0.9f, groundLayers, QueryTriggerInteraction.Ignore);
    }

    // Keeps the body moving with the momentum it died with while the clip plays.
    IEnumerator SlideRoutine(Vector3 velocity)
    {
        Vector3 v = velocity;
        while (!handedOff && v.sqrMagnitude > 0.01f)
        {
            yield return null;
            float dt = Time.deltaTime;
            v *= Mathf.Exp(-slideDamping * dt);
            MoveRoot(v * dt);
        }
    }

    void MoveRoot(Vector3 delta)
    {
        if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Move(delta);
        else if (cc != null && cc.enabled) cc.Move(delta + Vector3.down * 0.02f);
        else transform.position += delta;
    }

    // ------------------------------------------------------------------
    // Motion history
    // ------------------------------------------------------------------

    void RecordSample()
    {
        if (bones == null || bones.Length == 0) return;
        if (histTime == null)
        {
            histTime = new float[HistoryLen];
            histPos = new Vector3[HistoryLen][];
            histRot = new Quaternion[HistoryLen][];
            for (int i = 0; i < HistoryLen; i++)
            {
                histPos[i] = new Vector3[bones.Length];
                histRot[i] = new Quaternion[bones.Length];
            }
        }

        // shift down, newest at the end
        if (histCount == HistoryLen)
        {
            var t0 = histTime[0]; var p0 = histPos[0]; var r0 = histRot[0];
            for (int i = 0; i < HistoryLen - 1; i++) { histTime[i] = histTime[i + 1]; histPos[i] = histPos[i + 1]; histRot[i] = histRot[i + 1]; }
            histTime[HistoryLen - 1] = t0; histPos[HistoryLen - 1] = p0; histRot[HistoryLen - 1] = r0;
            histCount--;
        }

        int n = histCount;
        histTime[n] = Time.time;
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            Transform t = bones[i].transform;
            histPos[n][i] = t.TransformPoint(comLocal[i]);
            histRot[n][i] = t.rotation;
        }
        histCount++;
    }

    // Velocity of each bone's centre of mass and its spin, from the newest sample against one at
    // least ~0.05 s older (a single frame of motion is too noisy to hand to physics).
    bool MeasureMotion(out Vector3[] linear, out Vector3[] angular)
    {
        linear = null; angular = null;
        if (histCount < 2) return false;

        int newest = histCount - 1;
        int older = newest - 1;
        while (older > 0 && histTime[newest] - histTime[older] < 0.05f) older--;
        float dt = histTime[newest] - histTime[older];
        if (dt < 1e-4f) return false;

        linear = new Vector3[bones.Length];
        angular = new Vector3[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            linear[i] = (histPos[newest][i] - histPos[older][i]) / dt;

            Quaternion dq = histRot[newest][i] * Quaternion.Inverse(histRot[older][i]);
            dq.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
                angular[i] = axis.normalized * (angle * Mathf.Deg2Rad / dt);
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Handoff to physics
    // ------------------------------------------------------------------

    void Handoff()
    {
        handedOff = true;

        // Measure before anything moves.
        bool haveMotion = MeasureMotion(out Vector3[] lin, out Vector3[] ang);

        // Stop every writer of bone poses. The pose on screen this frame is the pose physics starts from.
        if (animator != null) animator.enabled = false;
        // The player's CharacterController is a solid capsule wrapped around the body - bones going
        // dynamic inside it get shoved out (often through the floor). Dead weight now.
        if (cc != null) cc.enabled = false;
        if (agent != null) agent.enabled = false;
        foreach (var l in GetComponentsInChildren<CharacterLocomotion>(true)) l.enabled = false;
        foreach (var u in GetComponentsInChildren<UpperBodyPose>(true)) u.enabled = false;
        foreach (var t in GetComponentsInChildren<TorsoPoseDriver>(true)) t.enabled = false;
        foreach (var d in GetComponentsInChildren<TorsoMotionDampener>(true)) d.enabled = false;
        foreach (var p in GetComponentsInChildren<CharacterMotionPolish>(true)) p.enabled = false;
        foreach (var w in GetComponentsInChildren<WeaponHandIK>(true)) w.enabled = false;
        // restore the first-person-hidden head - its zero-scale bone breaks ragdoll physics
        foreach (var h in GetComponentsInChildren<HeadHider>(true)) h.enabled = false;

        HealPose();

        // The corpse only needs to collide with the world, so ignore every intra-ragdoll pair -
        // a death pose can leave bones overlapping and the depenetration would launch the body.
        var skeleton = animator != null ? animator.transform : transform;
        var cols = skeleton.GetComponentsInChildren<Collider>();
        for (int i = 0; i < cols.Length; i++)
            for (int j = i + 1; j < cols.Length; j++)
                Physics.IgnoreCollision(cols[i], cols[j], true);

        // Open the joints just wide enough to contain the animated pose, so nothing snaps on the first step.
        float[] startSwing = new float[joints.Length];
        float[] startTwist = new float[joints.Length];
        OpenJointsAroundPose(startSwing, startTwist);

        // Push every transform write above into PhysX before anything goes dynamic.
        Physics.SyncTransforms();
        SetPhysics(true);
        Physics.SyncTransforms();

        // Seed with the motion the animation had.
        if (haveMotion)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                bones[i].maxAngularVelocity = Mathf.Max(bones[i].maxAngularVelocity, maxHandoffAngularSpeed + 2f);
                bones[i].linearVelocity = Vector3.ClampMagnitude(lin[i] * velocityInheritance, maxHandoffSpeed);
                bones[i].angularVelocity = Vector3.ClampMagnitude(ang[i] * angularInheritance, maxHandoffAngularSpeed);
            }
        }

        if (killedByExplosion)
        {
            // Throw the bones outward from the blast, then (if close) take the body apart. The limbs have
            // to still be attached when the impulse lands so the whole corpse launches together.
            foreach (var rb in bones)
            {
                if (rb == null) continue;
                rb.AddExplosionForce(explosionForceReceived + explosionRagdollForce * 100f,
                    explosionCentre, 8f, 0.8f, ForceMode.Impulse);
                rb.linearVelocity = Vector3.ClampMagnitude(rb.linearVelocity, MaxSafeSpeed);
            }

            if (explosionDismembers)
                GetComponentInChildren<Dismemberment>()?.SeverRandomLimbs(explosionCentre);
        }
        else if (knockedBack)
        {
            // A directional shove on every bone, like the whole body being punched backward.
            Vector3 direction = pendingHitDirectionSum.sqrMagnitude > 0.0001f
                ? pendingHitDirectionSum.normalized : transform.forward;
            // The shotgun clip already carries most of the motion; the shove adds to it rather than replaces it.
            float impulse = pendingHitForceSum * knockbackImpulseScale * (usedClip ? 0.5f : 1f);

            foreach (var rb in bones)
            {
                if (rb == null) continue;
                rb.AddForce(direction * impulse, ForceMode.Impulse);
                rb.linearVelocity = Vector3.ClampMagnitude(rb.linearVelocity, MaxSafeSpeed);
            }
        }
        else if (hasHitInfo && lastHitBody != null && hitImpulse > 0f)
        {
            // The killing bullet's own push, at the bone it hit - a headshot snaps the head back.
            float scale = lastHitWasHead ? headImpulseScale : 1f;
            lastHitBody.AddForceAtPosition(lastHitDirection * (hitImpulse * scale), lastHitPoint, ForceMode.Impulse);
        }

        pendingHitDirectionSum = Vector3.zero;
        pendingHitForceSum = 0f;

        StartCoroutine(RelaxJoints(startSwing, startTwist));
        StartCoroutine(GroundSafetyNet());
        if (groundClamp) StartCoroutine(GroundClamp());
    }

    // Only heals bones whose local position is pathologically off (the humanoid stretch DoF can fling
    // limbs metres away and a corpse spawning like that tears through the level). Deliberately does NOT
    // touch the hips in the normal case: the death clip moves them, and snapping them back to their
    // standing position is what made the old handover visibly pop.
    void HealPose()
    {
        if (allBones == null) return;
        for (int i = 0; i < allBones.Length; i++)
        {
            Transform t = allBones[i];
            if (t == null || i >= restLocalPos.Length) continue;
            if (t == hipsBone) continue;

            Vector3 rest = restLocalPos[i];
            Vector3 cur = t.localPosition;
            bool bad = float.IsNaN(cur.x) || float.IsNaN(cur.y) || float.IsNaN(cur.z)
                || (cur - rest).sqrMagnitude > 0.0625f * rest.sqrMagnitude + 1e-8f;
            if (bad) t.localPosition = rest;
        }

        if (hipsBone != null)
        {
            Vector3 hp = hipsBone.position;
            bool bad = float.IsNaN(hp.x) || (hp - transform.position).sqrMagnitude > 9f;
            if (bad)
            {
                int idx = System.Array.IndexOf(allBones, hipsBone);
                if (idx >= 0) hipsBone.localPosition = restLocalPos[idx];
            }
        }
    }

    // A swing or twist angle can never exceed the total rotation between the two bodies (swing-twist
    // decomposition: cos(total/2) = cos(swing/2)cos(twist/2)), so opening every limit to the total angle
    // plus a margin guarantees the animated pose is inside them.
    void OpenJointsAroundPose(float[] startSwing, float[] startTwist)
    {
        for (int i = 0; i < joints.Length; i++)
        {
            var j = joints[i];
            if (j == null) continue;
            var parent = j.connectedBody;
            float dev = 0f;
            if (parent != null)
            {
                Quaternion cur = Quaternion.Inverse(parent.transform.rotation) * j.transform.rotation;
                dev = Quaternion.Angle(cur, jointRest[i]);
            }
            float swing = Mathf.Clamp(Mathf.Max(looseSwingLimit, dev + 10f), 1f, 175f);
            float twist = Mathf.Clamp(Mathf.Max(looseTwistLimit, dev + 10f), 1f, 175f);
            startSwing[i] = swing; startTwist[i] = twist;
            SetJointLimits(j, swing, twist);
        }
    }

    static void SetJointLimits(CharacterJoint j, float swing, float twist)
    {
        j.swing1Limit = new SoftJointLimit { limit = swing };
        j.swing2Limit = new SoftJointLimit { limit = swing };
        j.lowTwistLimit = new SoftJointLimit { limit = -twist };
        j.highTwistLimit = new SoftJointLimit { limit = twist };
    }

    IEnumerator RelaxJoints(float[] startSwing, float[] startTwist)
    {
        float t = 0f;
        float total = Mathf.Max(0.01f, jointRelaxTime);
        while (t < total)
        {
            yield return null;
            t += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / total));
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i] == null) continue; // severed by Dismemberment
                SetJointLimits(joints[i],
                    Mathf.Lerp(startSwing[i], looseSwingLimit, k),
                    Mathf.Lerp(startTwist[i], looseTwistLimit, k));
            }
        }
    }

    // ------------------------------------------------------------------
    // Safety nets (unchanged behaviour)
    // ------------------------------------------------------------------

    // A settling ragdoll never legitimately needs to move faster than this - if a bone is, it's almost
    // certainly a depenetration or joint spike, not intended motion.
    const float MaxSafeSpeed = 8f;

    IEnumerator GroundSafetyNet()
    {
        float elapsed = 0f, calm = 0f;
        while (elapsed < maxSettleTime)
        {
            elapsed += Time.deltaTime;
            float fastest = 0f;
            foreach (var rb in bones)
            {
                if (rb == null || rb.isKinematic) continue;
                float sp = rb.linearVelocity.magnitude;
                if (sp > MaxSafeSpeed) rb.linearVelocity = rb.linearVelocity.normalized * MaxSafeSpeed;
                fastest = Mathf.Max(fastest, sp);
            }
            calm = fastest < settleSpeed ? calm + Time.deltaTime : 0f;
            if (freezeWhenSettled && elapsed > 1.5f && calm >= settleTime) break;
            yield return null;
        }
        if (!freezeWhenSettled) yield break;
        foreach (var rb in bones)
        {
            if (rb == null) continue;
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }
    }

    // Last line of defence for bodies that still get through the floor: watches the hips against a
    // downward raycast and lifts the whole corpse back to the surface if it ends up below it.
    IEnumerator GroundClamp()
    {
        if (hipsBone == null) yield break;

        float elapsed = 0f;
        while (elapsed < groundClampDuration)
        {
            elapsed += Time.deltaTime;

            // Cast from well above the hips, ignoring the corpse's own colliders on the way down.
            Vector3 origin = hipsBone.position + Vector3.up * 3f;
            if (RaycastIgnoringSelf(origin, Vector3.down, 12f, out RaycastHit hit))
            {
                float sunk = hit.point.y - hipsBone.position.y;
                if (sunk > groundClampTolerance)
                {
                    Vector3 lift = Vector3.up * (sunk + 0.1f);
                    foreach (var rb in bones)
                    {
                        if (rb == null) continue;
                        rb.position += lift;
                        Vector3 v = rb.linearVelocity;
                        if (v.y < 0f) v.y = 0f;
                        rb.linearVelocity = v;
                    }
                    Physics.SyncTransforms();
                }
            }

            yield return new WaitForFixedUpdate();
        }
    }

    bool RaycastIgnoringSelf(Vector3 origin, Vector3 direction, float distance, out RaycastHit best)
    {
        best = default;
        var hits = Physics.RaycastAll(origin, direction, distance, groundLayers, QueryTriggerInteraction.Ignore);
        float nearest = float.PositiveInfinity;
        bool found = false;
        Transform self = transform.root;

        foreach (var h in hits)
        {
            if (h.collider.transform.root == self) continue;
            if (h.distance < nearest) { nearest = h.distance; best = h; found = true; }
        }
        return found;
    }
}
