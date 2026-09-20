using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Toggles a humanoid ragdoll. The bone Rigidbodies/Colliders (built by the
/// "Build Ragdoll" menu) stay kinematic + disabled while alive so the Animator
/// drives the pose; on death they go dynamic and the character collapses.
/// Auto-hooks Health (enemies) and PlayerHealth (player) death events.
/// </summary>
public class Ragdoll : MonoBehaviour
{
    public Animator animator;

    [Header("Death Animation Before Ragdoll")]
    [Tooltip("Chance (0-1) that a death animation plays before the ragdoll takes over. The rest of the time the body goes straight to physics, so deaths don't all look identical.")]
    [Range(0f, 1f)] public float deathAnimationChance = 0.5f;
    [Tooltip("How far through the death clip the ragdoll takes over (0.5 = halfway). Earlier hands more of the fall to physics; later keeps more of the authored motion.")]
    [Range(0.1f, 1f)] public float ragdollTakeoverPoint = 0.45f;
    [Tooltip("Safety cap in seconds - the ragdoll always takes over by now even if the clip is long or the animator never reaches the takeover point.")]
    public float maxDeathAnimationTime = 1.2f;
    [Tooltip("How much of the death animation's own bone velocity is handed to the ragdoll when it takes over. 1 = the corpse keeps falling exactly the way the clip was moving it, which is what makes the handover invisible; 0 = it drops from rest.")]
    [Range(0f, 1.5f)] public float velocityInheritance = 1f;

    [Header("Ground Safety")]
    [Tooltip("Backstop for bodies that get through the floor anyway: for a few seconds after death the hips are raycast against the ground and the whole corpse is lifted back up if it ends up below it. Collision fixes come first, but the level's floor colliders are thin enough that this is worth keeping on.")]
    public bool groundClamp = true;
    [Tooltip("Layers treated as ground by the clamp above.")]
    public LayerMask groundLayers = ~0;
    [Tooltip("How long the clamp stays active after death.")]
    public float groundClampDuration = 4f;
    [Tooltip("How far below the ground the hips have to be before the body is lifted. Must stay comfortably above the depth a normal collapse reaches, or a corpse lying on a slope gets nudged every step.")]
    public float groundClampTolerance = 0.35f;

    [Header("Settling")]
    [Tooltip("Once the corpse has stopped moving its bones are frozen (kinematic), so later gunfire, grenades or pushes can't drive it through the floor. Costs nothing per frame afterwards.")]
    public bool freezeWhenSettled = true;
    [Tooltip("Bones slower than this (m/s) count as settled.")]
    public float settleSpeed = 0.2f;
    [Tooltip("How long everything must stay slower than settleSpeed before freezing.")]
    public float settleTime = 0.6f;
    [Tooltip("Freeze regardless after this long.")]
    public float maxSettleTime = 8f;
    [Tooltip("Death-animation handover: false = the corpse drops in place (only the limbs' motion relative to the body is kept). true = also keeps the clip's overall travel, which sent bodies flying backwards.")]
    public bool inheritBodyTravel = false;

    [Header("Explosions")]
    [Tooltip("Extra impulse applied to each bone when killed by an explosion, on top of whatever the blast itself pushed.")]
    public float explosionRagdollForce = 9f;

    [Header("Gunfire Knockback")]
    [Tooltip("Total accumulated hit force (see AccumulateHitForce) needed on the killing blow to skip the death animation entirely and go straight to a shoved ragdoll. A single rifle round should stay well under this; several shotgun pellets landing the same frame add up past it, which is what makes a shotgun kill fling the body backward while a rifle kill still gets its death animation.")]
    public float knockbackSkipAnimationThreshold = 16f;
    [Tooltip("Multiplies the accumulated hit force into an actual physics impulse spread across every bone when the threshold above is exceeded.")]
    public float knockbackImpulseScale = 5f;
    Vector3 pendingHitDirectionSum;  // running sum of direction*force for the current death
    float pendingHitForceSum;
    bool knockedBack;                // this death's force cleared the threshold above

    Rigidbody[] bones;
    Transform[] allBones;      // every skeleton transform, for pose repair on death
    Vector3[] restLocalPos;    // their sane local positions captured before any pose writer runs
    bool dead;
    bool liveHitboxes;
    Transform hipsBone;
    Vector3[] lastBonePositions; // for handing the death clip's motion to the ragdoll // enemies keep bone colliders on while alive so bullets can hit limbs

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        // Only the skeleton under the animator is the ragdoll — never touch
        // rigidbodies/colliders on the character root (pickup trigger, CharacterController...)
        var skeleton = animator != null ? animator.transform : transform;
        bones = skeleton.GetComponentsInChildren<Rigidbody>();
        // The humanoid stretch DoF on these hugely-scaled rig bones lets the
        // muscle-pose writer translate bones tens of meters while alive (looks
        // fine in first person, but a corpse inheriting it gets yanked through
        // the floor). Snapshot every bone's local position now, while the rig
        // is untouched, so Collapse() can heal translations before physics on.
        allBones = skeleton.GetComponentsInChildren<Transform>();
        restLocalPos = new Vector3[allBones.Length];
        for (int i = 0; i < allBones.Length; i++)
            restLocalPos[i] = allBones[i].localPosition;
        if (animator != null && animator.isHuman) hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
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
                // thin bone capsules tunnel straight through floor meshes with
                // discrete collision — continuous CD stops that. Depenetration
                // stays at the PhysX default so a bone that dies overlapping a
                // wall pops free in a few frames instead of slow-crawling
                // through geometry (a low cap never finishes the job).
                // ContinuousSpeculative rather than ContinuousDynamic: speculative
                // contacts are generated from the body's swept bounds every step, so
                // they still catch a bone that was TELEPORTED into place (which is
                // exactly what the pose-heal below does) and they work for rotation as
                // well as translation. ContinuousDynamic's sweep starts from PhysX's
                // last known pose, which after a teleport is the wrong place - that's
                // the remaining intermittent fall-through case, and matches "it looks
                // like collision is skipped entirely".
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                // the pose-heal teleports bones just before this switch — without
                // an explicit reset PhysX inherits that jump as launch velocity
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                // A death pose can leave bones heavily overlapping each other or the
                // floor. Left uncapped, PhysX's depenetration resolves that overlap
                // in a single step, which can fling a bone fast enough to tunnel
                // straight through thin floor geometry even with continuous CD -
                // this is almost certainly why it's intermittent (some death poses
                // overlap far worse than others). Capping it forces a gentle
                // multi-frame push-out instead of one explosive pop.
                rb.maxDepenetrationVelocity = 2f;
                rb.solverIterations = 12;
                rb.solverVelocityIterations = 4;
            }
            var col = rb.GetComponent<Collider>();
            if (col != null && !(col is CharacterController))
            {
                col.enabled = on || liveHitboxes;
                // alive: hitboxes are TRIGGERS so they exert zero physical force on the
                // player/props (solid kinematic colliders catapult everything they sweep
                // through). dead: solid again so the ragdoll collides with the world.
                col.isTrigger = !on && liveHitboxes;
            }
        }
    }

    // A settling ragdoll never legitimately needs to move faster than this - if a
    // bone is moving faster, it's almost certainly a depenetration or joint-
    // projection spike (both addressed elsewhere), not intended motion. This is a
    // backstop on top of those actual fixes, for whatever still slips past them -
    // it doesn't depend on collision detection working, unlike a raycast-based
    // ground correction would (if a ragdoll isn't colliding with anything, there's
    // no reliable surface to snap it back to either).
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

    // Last line of defence for the bodies that still get through - and the shape of the
    // failure (sinking straight down, feet first, still upright) is the giveaway that
    // they're not being caught by collision at all rather than being flung: a body that
    // never registers a floor contact just accelerates downward keeping its pose. Rather
    // than trying to make PhysX catch it, this watches the hips against a downward
    // raycast and lifts the whole corpse back to the surface if it ends up below it.
    IEnumerator GroundClamp()
    {
        if (hipsBone == null) yield break;

        float elapsed = 0f;
        while (elapsed < groundClampDuration)
        {
            elapsed += Time.deltaTime;

            // Cast from well above the hips so the ray starts outside any floor the body
            // may already be inside - and ignore the corpse's OWN colliders on the way
            // down. Without that filter the ray hits the body's head/shoulder first, reads
            // "the ground is above the hips", lifts the whole thing, and does it again the
            // next frame: that's what was launching bodies (and the player) into the air.
            Vector3 origin = hipsBone.position + Vector3.up * 3f;
            if (RaycastIgnoringSelf(origin, Vector3.down, 12f, out RaycastHit hit))
            {
                float sunk = hit.point.y - hipsBone.position.y;
                if (sunk > groundClampTolerance)
                {
                    // Move every bone up together, so the pose is preserved and nothing
                    // gets torn apart by its joints, then kill the downward velocity that
                    // put it there. Only the rigidbodies are moved - everything else in
                    // the skeleton is parented under one of them and comes along.
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

    // A downward cast that skips every collider belonging to this character.
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

    /// <summary>Called by Explosive just before the killing damage lands. An explosive
    /// death skips the death animation entirely - a body being thrown by a blast has no
    /// business playing a scripted collapse - and if the blast was close enough, the body
    /// comes apart.</summary>
    public void NotifyExplosion(Vector3 blastCentre, float force, bool dismember)
    {
        killedByExplosion = true;
        explosionCentre = blastCentre;
        explosionForceReceived = force;
        explosionDismembers = dismember;
    }

    bool killedByExplosion;
    Vector3 explosionCentre;
    float explosionForceReceived;
    bool explosionDismembers;

    /// <summary>Called by Health/PlayerHealth on every hit that carries a knockback
    /// amount - not just the killing one. Force ACCUMULATES rather than being judged
    /// per-hit, so several shotgun pellets landing in the same frame add up to a real
    /// shove even though each pellet's own force is modest; a single rifle round stays
    /// small. Whatever total is on the books when Die() fires is what decides the death.</summary>
    public void AccumulateHitForce(Vector3 direction, float force)
    {
        if (force <= 0f || direction.sqrMagnitude < 0.0001f) return;
        pendingHitDirectionSum += direction.normalized * force;
        pendingHitForceSum += force;
    }

    public void Die()
    {
        if (dead) return;
        dead = true;
        StartCoroutine(DeathSequence());
    }

    /// <summary>Optional: tell the ragdoll which way the killing shot came from before it
    /// dies, so the right death clip plays. If nothing calls this, the direction is worked
    /// out from the player's position instead.</summary>
    public void NotifyHitDirection(Vector3 worldDirectionOfShot)
    {
        Vector3 flat = Vector3.ProjectOnPlane(worldDirectionOfShot, Vector3.up);
        if (flat.sqrMagnitude < 0.0001f) return;
        hitFromBack = Vector3.Dot(transform.forward, flat.normalized) > 0f; // travelling the same way we face = hit in the back
        hasHitDirection = true;
    }

    bool hitFromBack;
    bool hasHitDirection;

    // Plays a death animation part-way through (some of the time) and then hands the
    // fall over to physics mid-motion, seeding the bones with the velocity the clip was
    // already moving them at, so the swap reads as one continuous fall instead of the
    // body freezing and then dropping.
    IEnumerator DeathSequence()
    {
        // A big enough gunfire shove (several shotgun pellets in one frame, typically)
        // skips the death animation the same way an explosion does - a body being
        // punched backward has no business playing a scripted collapse first, and this
        // is also what fixes it standing still through the whole animation and only
        // THEN rocketing backward: if the animation plays, no extra shove is added on
        // top of it (below), so it just settles where it fell.
        knockedBack = !killedByExplosion && pendingHitForceSum >= knockbackSkipAnimationThreshold;

        bool playAnimation = animator != null && animator.enabled
            && !killedByExplosion                       // blown up: straight to physics
            && !knockedBack                              // shoved hard: straight to physics
            && Random.value < deathAnimationChance;

        if (playAnimation)
        {
            if (!hasHitDirection) hitFromBack = WorkOutHitFromBack();

            var driver = GetComponentInChildren<CharacterAnimationDriver>();
            if (driver != null) driver.SetDead(true, hitFromBack);
            else { animator.SetBool("DeathFromBack", hitFromBack); animator.SetBool("Dead", true); }

            // Let the transition actually start before measuring progress.
            yield return null;
            yield return null;

            float elapsed = 0f;
            while (elapsed < maxDeathAnimationTime)
            {
                elapsed += Time.deltaTime;
                var info = animator.GetCurrentAnimatorStateInfo(0);
                if (info.normalizedTime >= ragdollTakeoverPoint) break;
                CacheBonePositions();
                yield return null;
            }
            CacheBonePositions();
        }

        yield return Collapse(seedVelocities: playAnimation);
    }

    // No hit direction was supplied, so infer it: the player is what killed this thing
    // in practice, and standing behind the enemy means it was shot in the back.
    bool WorkOutHitFromBack()
    {
        var playerObj = GameObject.FindGameObjectWithTag("Player");
        if (playerObj == null) return false;
        Vector3 toPlayer = Vector3.ProjectOnPlane(playerObj.transform.position - transform.position, Vector3.up);
        return Vector3.Dot(transform.forward, toPlayer) < 0f;
    }

    void CacheBonePositions()
    {
        if (bones == null) return;
        if (lastBonePositions == null || lastBonePositions.Length != bones.Length)
            lastBonePositions = new Vector3[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            if (bones[i] != null) lastBonePositions[i] = bones[i].position;
    }

    IEnumerator Collapse(bool seedVelocities = false)
    {
        // Wait a frame so other onDeath listeners (e.g. EnemyAI disabling colliders) run first.
        yield return null;
        if (animator != null) animator.enabled = false;
        // the player's CharacterController is a solid capsule wrapped around the
        // body — bones going dynamic inside it get shoved out (often through the
        // floor). It's dead weight after death, so switch it off before physics on.
        var cc = GetComponentInParent<CharacterController>(); if (cc != null) cc.enabled = false;
        var agent = GetComponentInParent<NavMeshAgent>(); if (agent != null) agent.enabled = false;
        var loco = GetComponentInChildren<CharacterLocomotion>(); if (loco != null) loco.enabled = false;
        var pose = GetComponentInChildren<UpperBodyPose>(); if (pose != null) pose.enabled = false;
        // restore the first-person-hidden head — its zero-scale bone breaks ragdoll physics
        var hider = GetComponentInChildren<HeadHider>(); if (hider != null) hider.enabled = false;

        // heal the skeleton: keep the death pose's rotations but restore every
        // bone's local translation — the live muscle pose leaves limbs flung
        // meters away, and a corpse spawning like that tears through the level
        if (allBones != null)
            for (int i = 0; i < allBones.Length; i++)
                if (allBones[i] != null && i < restLocalPos.Length)
                    allBones[i].localPosition = restLocalPos[i];

        // bones spawn interpenetrating each other (gun-hold pose folds the arms
        // into the chest box) and non-adjacent pairs aren't joint-exempted — the
        // depenetration explosion launches/wedges the corpse. The corpse only
        // needs to collide with the world, so ignore every intra-ragdoll pair.
        var skeleton = animator != null ? animator.transform : transform;
        var cols = skeleton.GetComponentsInChildren<Collider>();
        for (int i = 0; i < cols.Length; i++)
            for (int j = i + 1; j < cols.Length; j++)
                Physics.IgnoreCollision(cols[i], cols[j], true);

        // loosen the joints so the corpse flops instead of planking
        foreach (var j in GetComponentsInChildren<CharacterJoint>())
        {
            j.swing1Limit = new SoftJointLimit { limit = 70f };
            j.swing2Limit = new SoftJointLimit { limit = 70f };
            j.lowTwistLimit = new SoftJointLimit { limit = -45f };
            j.highTwistLimit = new SoftJointLimit { limit = 45f };
        }
        // Push every transform write above (the pose heal, and the collider enable/
        // trigger flips) into PhysX before anything goes dynamic. Without this the
        // bodies wake up at their PRE-heal poses for one step and then get corrected,
        // and that correction is a teleport with no collision detection behind it -
        // a bone sitting under the floor for one step comes out the other side.
        Physics.SyncTransforms();
        // Measure the clip's last frame of motion right before physics takes over.
        Vector3[] preSwitch = null;
        if (seedVelocities && lastBonePositions != null)
        {
            preSwitch = new Vector3[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null) preSwitch[i] = bones[i].position;
        }

        SetPhysics(true);
        Physics.SyncTransforms();

        if (preSwitch != null && Time.deltaTime > 0f)
        {
            // Hand the animation's own motion to the bodies so the corpse carries on
            // falling the way the clip was throwing it, rather than stopping dead and
            // then dropping - that pause is what makes an animation-to-ragdoll swap
            // look like two separate events.
            // The death clips travel backwards as they play; inheriting that whole-body
            // motion is what made corpses fly back. Remove the average sideways travel and
            // keep only each bone's motion relative to it (plus vertical fall).
            Vector3 meanH = Vector3.zero; int n = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || i >= lastBonePositions.Length) continue;
                Vector3 v = (preSwitch[i] - lastBonePositions[i]) / Time.deltaTime;
                meanH += new Vector3(v.x, 0f, v.z); n++;
            }
            if (n > 0) meanH /= n;
            if (inheritBodyTravel) meanH = Vector3.zero;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || i >= lastBonePositions.Length) continue;
                Vector3 v = (preSwitch[i] - lastBonePositions[i]) / Time.deltaTime - meanH;
                bones[i].linearVelocity = Vector3.ClampMagnitude(v * velocityInheritance, inheritBodyTravel ? MaxSafeSpeed : 3f);
            }
        }

        if (killedByExplosion)
        {
            // Throw the bones outward from the blast, then (if it was a close one) take
            // the body apart. Order matters: the limbs have to still be attached when the
            // impulse lands so the whole corpse launches together, then separates.
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
            // Same idea as the explosion branch but a directional shove rather than a
            // radial one - every bone gets pushed the same way, roughly like the whole
            // body being punched backward, using the direction the accumulated hits
            // actually came from rather than always "straight back" so it still looks
            // right if the shots came from an angle.
            Vector3 direction = pendingHitDirectionSum.sqrMagnitude > 0.0001f
                ? pendingHitDirectionSum.normalized : transform.forward;
            float impulse = pendingHitForceSum * knockbackImpulseScale;

            foreach (var rb in bones)
            {
                if (rb == null) continue;
                rb.AddForce(direction * impulse, ForceMode.Impulse);
                rb.linearVelocity = Vector3.ClampMagnitude(rb.linearVelocity, MaxSafeSpeed);
            }
        }

        pendingHitDirectionSum = Vector3.zero;
        pendingHitForceSum = 0f;

        StartCoroutine(GroundSafetyNet());
        if (groundClamp) StartCoroutine(GroundClamp());
    }
}