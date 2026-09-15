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
    Rigidbody[] bones;
    Transform[] allBones;      // every skeleton transform, for pose repair on death
    Vector3[] restLocalPos;    // their sane local positions captured before any pose writer runs
    bool dead;
    bool liveHitboxes; // enemies keep bone colliders on while alive so bullets can hit limbs

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
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
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

    public void Die()
    {
        if (dead) return;
        dead = true;
        StartCoroutine(Collapse());
    }

    IEnumerator Collapse()
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
        SetPhysics(true);
        StartCoroutine(GroundSafetyNet());
    }
}