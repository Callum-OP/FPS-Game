using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// An ally: sticks near the player, shoots at enemies, takes cover, dies like everything
/// else.
///
/// Deliberately a separate script rather than a flag on EnemyAI - the two want opposite
/// things in almost every state (an enemy hunts the player, an ally follows them) and
/// tangling both into one state machine makes both harder to change. What they do share -
/// the animation driver, the weapon, cover finding, ragdolls - is already in components
/// they both just use.
///
/// Allies are identified by this component: Projectile checks for it to work out who a
/// bullet is allowed to hurt, so an ally needs nothing else to be on the player's side.
///
/// SETUP: take the Enemy prefab, remove EnemyAI, add this (plus EnemyWeapon and
/// EnemyCover as usual - EnemyWeapon requires an EnemyAI, so see the note on
/// weaponPrefab below if you want an ally with a visible gun).
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Health))]
public class FriendlyAI : MonoBehaviour
{
    public enum State { Follow, Engage, Cover, Dead }

    [Header("Following")]
    [Tooltip("Stand-off distance used only while actively catching up to the player (see activeFollowRange) - not used while patrolling nearby.")]
    public float followDistance = 4f;
    [Tooltip("While within this range of the player, the ally patrols/wanders nearby instead of tailing them directly. Beyond it, they drop whatever they're doing and beeline back.")]
    public float activeFollowRange = 10f;
    [Tooltip("How far from the player's current position the ally wanders while patrolling.")]
    public float patrolRadius = 6f;
    [Tooltip("How long the ally pauses at each patrol point before picking a new one.")]
    public float patrolMinPause = 1.5f;
    public float patrolMaxPause = 4f;
    [Tooltip("Distance at which it gives up on combat/cover and catches up to the player instead.")]
    public float leashDistance = 18f;
    public float walkSpeed = 3.5f;
    public float chaseSpeed = 5.5f;
    public float turnSpeed = 8f;

    [Header("Combat")]
    public bool canShoot = true;
    public float sightRange = 22f;
    public float shootRange = 18f;
    public float attackCooldown = 0.9f;
    public float inaccuracy = 4f;
    public GameObject bulletPrefab;
    public Transform muzzlePoint;
    public float bulletSpeed = 40f;
    public LayerMask sightBlockers;
    [Tooltip("Health fraction at or below which the injured locomotion overlay plays.")]
    public float InjuredHealthFraction = 0.3f;

    [Header("Cover")]
    public bool useCover = true;
    [Tooltip("Take cover once a fight has been going this long.")]
    public float timeInOpenBeforeCover = 4f;
    public float coverHideTime = 1.4f;
    public float peekDuration = 2f;

    [Header("Testing")]
    [Tooltip("Let the player's own bullets damage this ally. Off by default so you can't accidentally gun down your own squad - flip per-ally to experiment with friendly fire.")]
    public bool allowFriendlyFireFromPlayer = false;

    [Header("Auto Weapon Upgrade")]
    [Tooltip("Automatically switch to a strictly better dropped weapon lying nearby.")]
    public bool autoUpgradeWeapon = true;
    [HideInInspector] public float weaponDetectRadius = 3f; // replaced by scavengeRadius
    [HideInInspector] public float autoPickupDelay = 4f;    // replaced by scavengeDelay
    [Tooltip("How far away a dropped weapon is noticed. The ally walks over to it.")]
    public float scavengeRadius = 8f;
    [Tooltip("Seconds a better weapon has to stay the chosen target before it's taken - a short window for the player to grab it first.")]
    public float scavengeDelay = 0.8f;
    [Tooltip("How close (metres) the ally has to get to grab it.")]
    public float scavengeReach = 1.4f;
    [Tooltip("Ignore weapons that appeared less than this many seconds ago, so an ally doesn't snatch what you've just dropped or swapped away.")]
    public float scavengeMinPickupAge = 1f;
    [Tooltip("Only look for an upgrade while just following/patrolling, never mid-fight.")]
    public bool onlyUpgradeOutOfCombat = true;

    State currentState = State.Follow;
    NavMeshAgent agent;
    Health health;
    Transform player;
    CharacterAnimationDriver characterAnimation;
    EnemyWeapon allyWeapon;
    EnemyCover cover;

    Transform target;          // current enemy
    float attackTimer;
    float timeInOpen;
    float coverTimer;
    bool peeking;
    Vector3 coverPosition, peekPosition;

    // Patrol
    Vector3 patrolTarget;
    bool hasPatrolTarget;
    bool patrolWaiting;
    float patrolPauseTimer;


    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        health = GetComponent<Health>();
        characterAnimation = GetComponentInChildren<CharacterAnimationDriver>();
        allyWeapon = GetComponent<EnemyWeapon>();

        var playerObj = GameObject.FindGameObjectWithTag("Player");
        if (playerObj != null) player = playerObj.transform;

        health.onDeath.AddListener(OnDeath);
        health.onDamaged.AddListener(f => characterAnimation?.SetInjured(f <= InjuredHealthFraction));

        if (useCover)
        {
            cover = GetComponent<EnemyCover>();
            if (cover == null) cover = gameObject.AddComponent<EnemyCover>();
            if (cover.sightBlockers == 0) cover.sightBlockers = sightBlockers;
        }

        characterAnimation?.SetWeaponClass(canShoot ? CharacterWeaponClass.Rifle : CharacterWeaponClass.Unarmed);
        agent.speed = walkSpeed;
    }

    void Update()
    {
        if (currentState == State.Dead || player == null) return;

        target = FindNearestVisibleEnemy();
        allyWeapon?.SetAimPoint(target != null, target != null ? target.position + Vector3.up * 1.1f : Vector3.zero);

        // Look at the enemy being engaged, or glance at the player when close; otherwise let go.
        if (characterAnimation != null)
        {
            if (target != null) characterAnimation.SetLookTarget(target, 1.4f);
            else if (Vector3.Distance(transform.position, player.position) < 8f) characterAnimation.SetLookTarget(player, 1.5f);
            else characterAnimation.ClearLook();
        }

        switch (currentState)
        {
            case State.Follow: HandleFollow(); break;
            case State.Engage: HandleEngage(); break;
            case State.Cover:  HandleCover();  break;
        }

        // Gun down when there's nothing to shoot at, same as the enemies.
        allyWeapon?.SetCombatReady(currentState != State.Follow || target != null);

        HandleWeaponUpgrade();
    }

    void HandleFollow()
    {
        float distToPlayer = Vector3.Distance(transform.position, player.position);

        if (distToPlayer > activeFollowRange)
        {
            // Too far to bother patrolling - drop it and head straight back.
            hasPatrolTarget = false;
            patrolWaiting = false;
            agent.isStopped = false;
            agent.speed = chaseSpeed;

            Vector3 spot = player.position - (player.forward * followDistance);
            if (NavMesh.SamplePosition(spot, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                agent.SetDestination(hit.position);

            if (Vector3.Distance(transform.position, agent.destination) <= agent.stoppingDistance + 0.3f)
                agent.isStopped = true;
        }
        else
        {
            // Close enough - wander near the player instead of hovering right behind
            // them like a shadow.
            agent.speed = walkSpeed;

            if (patrolWaiting)
            {
                agent.isStopped = true;
                patrolPauseTimer -= Time.deltaTime;
                if (patrolPauseTimer <= 0f) { patrolWaiting = false; hasPatrolTarget = false; }
            }
            else if (!hasPatrolTarget
                || Vector3.Distance(transform.position, patrolTarget) <= agent.stoppingDistance + 0.3f)
            {
                if (hasPatrolTarget)
                {
                    // Arrived - pause before wandering off to somewhere new.
                    patrolWaiting = true;
                    patrolPauseTimer = Random.Range(patrolMinPause, patrolMaxPause);
                }
                else
                {
                    // Re-centred on the player's CURRENT position each time, so the
                    // patrol area drifts along with them rather than anchoring to
                    // wherever they happened to be when this ally last picked a spot.
                    Vector2 circle = Random.insideUnitCircle * patrolRadius;
                    Vector3 probe = player.position + new Vector3(circle.x, 0f, circle.y);
                    if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    {
                        patrolTarget = hit.position;
                        hasPatrolTarget = true;
                        agent.isStopped = false;
                        agent.SetDestination(patrolTarget);
                    }
                }
            }
        }

        characterAnimation?.SetAiming(false);
        allyWeapon?.SetAiming(false);

        if (target != null && canShoot) { currentState = State.Engage; timeInOpen = 0f; }
    }

    // Looks for a strictly better dropped weapon nearby and, after it's sat in range
    // uninterrupted for autoPickupDelay seconds, takes it. Runs every Update but bails
    // out immediately unless actually idle/patrolling, so it never interrupts a fight.
    void HandleWeaponUpgrade()
    {
        if (allyWeapon == null) return;
        bool allowed = autoUpgradeWeapon && (!onlyUpgradeOutOfCombat || currentState == State.Follow);
        allyWeapon.UpdateScavenge(allowed, scavengeRadius, scavengeDelay, scavengeReach,
                                  scavengeMinPickupAge, agent, chaseSpeed);
    }

    // Rough DPS estimate - damage per pellet times pellets, over the time between shots.
    // Good enough to rank "this gun is clearly better", which is all this needs to do.
    static float ScoreOf(WeaponController wc)
    {
        if (wc == null || wc.bulletPrefab == null) return 0f;
        var proj = wc.bulletPrefab.GetComponent<Projectile>();
        float dmg = proj != null ? proj.damage : 0f;
        int pellets = Mathf.Max(1, wc.pelletCount);
        float rate = Mathf.Max(0.01f, wc.fireRate);
        return dmg * pellets / rate;
    }

    void HandleEngage()
    {
        // Don't chase a fight so far that the player is left alone.
        if (target == null || Vector3.Distance(transform.position, player.position) > leashDistance)
        {
            currentState = State.Follow;
            return;
        }

        float dist = Vector3.Distance(transform.position, target.position);

        if (NeedsReload())
        {
            allyWeapon?.Reload();
            if (useCover && cover != null && cover.FindCover(target.position, out coverPosition, out peekPosition))
            { EnterCover(); return; }
        }

        if (dist > shootRange)
        {
            agent.isStopped = false;
            agent.speed = chaseSpeed;
            agent.SetDestination(target.position);
            return;
        }

        timeInOpen += Time.deltaTime;
        if (useCover && cover != null && timeInOpen >= timeInOpenBeforeCover
            && cover.FindCover(target.position, out coverPosition, out peekPosition))
        { EnterCover(); return; }

        agent.isStopped = true;
        FaceTarget(target.position);
        TryShoot();
    }

    void HandleCover()
    {
        if (target == null) { LeaveCover(); currentState = State.Follow; return; }

        coverTimer -= Time.deltaTime;

        if (peeking)
        {
            agent.isStopped = false;
            agent.SetDestination(peekPosition);
            characterAnimation?.SetCrouching(false);
            FaceTarget(target.position);
            if (!NeedsReload()) TryShoot();

            if (coverTimer <= 0f || NeedsReload()) { peeking = false; coverTimer = coverHideTime; }
            return;
        }

        agent.SetDestination(coverPosition);
        bool atCover = Vector3.Distance(transform.position, coverPosition) < 0.6f;
        agent.isStopped = atCover;
        characterAnimation?.SetCrouching(atCover);
        characterAnimation?.SetAiming(false);
        allyWeapon?.SetAiming(false);

        if (NeedsReload()) { allyWeapon?.Reload(); return; }

        if (coverTimer <= 0f) { peeking = true; coverTimer = peekDuration; }
    }

    void EnterCover()
    {
        currentState = State.Cover;
        peeking = false;
        coverTimer = coverHideTime;
        timeInOpen = 0f;
    }

    void LeaveCover()
    {
        peeking = false;
        characterAnimation?.SetCrouching(false);
    }

    bool NeedsReload() => allyWeapon != null && (!allyWeapon.HasAmmo || allyWeapon.IsReloading);

    Transform FindNearestVisibleEnemy()
    {
        Transform best = null;
        float bestDist = sightRange;

        foreach (var enemy in FindObjectsByType<EnemyAI>(FindObjectsSortMode.None))
        {
            if (enemy == null || !enemy.enabled) continue; // EnemyAI disables itself on death
            float d = Vector3.Distance(transform.position, enemy.transform.position);
            if (d > bestDist) continue;
            if (!HasLineOfSight(enemy.transform)) continue;
            best = enemy.transform;
            bestDist = d;
        }
        return best;
    }

    bool HasLineOfSight(Transform other)
    {
        Vector3 from = transform.position + Vector3.up * 1.5f;
        Vector3 dir = (other.position + Vector3.up * 1.2f) - from;
        float dist = dir.magnitude;
        if (Physics.Raycast(from, dir / dist, out RaycastHit hit, dist, sightBlockers, QueryTriggerInteraction.Ignore))
            return hit.transform.IsChildOf(other.root);
        return true;
    }

    void FaceTarget(Vector3 position)
    {
        Vector3 dir = position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(dir), turnSpeed * Time.deltaTime);
    }

    void TryShoot()
    {
        attackTimer -= Time.deltaTime;
        if (attackTimer > 0f || bulletPrefab == null || muzzlePoint == null || target == null) return;

        if (allyWeapon != null && !allyWeapon.TryConsumeAmmo()) { allyWeapon.Reload(); return; }

        attackTimer = attackCooldown;
        characterAnimation?.SetAiming(true);
        allyWeapon?.SetAiming(true);
        characterAnimation?.PlayShoot();

        Vector3 direction = ((target.position + Vector3.up * 1.1f) - muzzlePoint.position).normalized;
        direction = Quaternion.Euler(
            Random.Range(-inaccuracy, inaccuracy),
            Random.Range(-inaccuracy, inaccuracy), 0f) * direction;

        GameObject bullet = Instantiate(bulletPrefab,
            muzzlePoint.position + direction * 0.3f, Quaternion.LookRotation(direction));
        bullet.transform.forward = direction;

        // Marks the bullet as ours: hurts enemies, never the player or another ally.
        var proj = bullet.GetComponent<Projectile>();
        if (proj != null) proj.firedByFriendly = true;

        Rigidbody rb = bullet.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearVelocity = direction * bulletSpeed;
        }

        Collider bulletCol = bullet.GetComponent<Collider>();
        if (bulletCol != null)
            foreach (Collider col in GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(bulletCol, col);
    }

    void OnDeath()
    {
        currentState = State.Dead;
        agent.isStopped = true;
        characterAnimation?.SetCrouching(false);
        allyWeapon?.Drop();

        foreach (Collider col in GetComponentsInChildren<Collider>())
            col.enabled = false;

        enabled = false;
    }
}