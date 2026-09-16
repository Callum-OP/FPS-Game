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
        float elapsed = 0f;
        while (elapsed < 3f)
        {
            elapsed += Time.deltaTime;

            foreach (var rb in bones)
            {
                if (rb == null) continue;
                if (rb.linearVelocity.sqrMagnitude > MaxSafeSpeed * MaxSafeSpeed)
                    rb.linearVelocity = rb.linearVelocity.normalized * MaxSafeSpeed;
            }

            yield return null;
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
            // may already be inside.
            Vector3 origin = hipsBone.position + Vector3.up * 3f;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 12f, groundLayers, QueryTriggerInteraction.Ignore))
            {
                float sunk = hit.point.y - hipsBone.position.y;
                if (sunk > 0.25f) // hips are a quarter of a metre below the floor surface
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
        bool playAnimation = animator != null && animator.enabled
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
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || i >= lastBonePositions.Length) continue;
                Vector3 v = (preSwitch[i] - lastBonePositions[i]) / Time.deltaTime;
                bones[i].linearVelocity = Vector3.ClampMagnitude(v * velocityInheritance, MaxSafeSpeed);
            }
        }

        StartCoroutine(GroundSafetyNet());
        if (groundClamp) StartCoroutine(GroundClamp());
    }
}