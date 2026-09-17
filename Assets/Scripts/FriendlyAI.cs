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
    [Tooltip("How close to the player the ally tries to stay.")]
    public float followDistance = 4f;
    [Tooltip("Distance at which it gives up on whatever it's doing and catches up.")]
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

        switch (currentState)
        {
            case State.Follow: HandleFollow(); break;
            case State.Engage: HandleEngage(); break;
            case State.Cover:  HandleCover();  break;
        }

        // Gun down when there's nothing to shoot at, same as the enemies.
        allyWeapon?.SetCombatReady(currentState != State.Follow || target != null);
    }

    void HandleFollow()
    {
        agent.isStopped = false;
        agent.speed = Vector3.Distance(transform.position, player.position) > followDistance * 2f
            ? chaseSpeed : walkSpeed;

        // Stand off rather than treading on the player's heels.
        Vector3 spot = player.position - (player.forward * followDistance);
        if (NavMesh.SamplePosition(spot, out NavMeshHit hit, 3f, NavMesh.AllAreas))
            agent.SetDestination(hit.position);

        if (Vector3.Distance(transform.position, agent.destination) <= agent.stoppingDistance + 0.3f)
            agent.isStopped = true;

        characterAnimation?.SetAiming(false);
        allyWeapon?.SetAiming(false);

        if (target != null && canShoot) { currentState = State.Engage; timeInOpen = 0f; }
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