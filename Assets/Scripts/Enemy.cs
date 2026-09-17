using System.Collections;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Health))]
public class EnemyAI : MonoBehaviour
{
    public enum State { Patrol, Idle, Investigate, Chase, Attack, Dead, TakeCover, InCover }

    [Header("Detection")]
    public float sightRange = 20f;
    [Tooltip("An ally within this range and in sight becomes the fire target instead of the player - lets enemies engage a friendly AI that gets close rather than only ever shooting the player.")]
    public float allyEngageRange = 14f;
    public float sightAngle = 90f;
    public float hearingRange = 8f; // Detect player without line of sight
    public float investigateTime = 4f; // Look for player
    public LayerMask sightBlockers;

    [Header("Combat")]
    public float attackRange = 2f;
    public float attackDamage = 10f;
    public float attackCooldown = 1.2f;
    public float shootRange = 15f;
    [Tooltip("Health fraction (0-1) at or below which the Injured locomotion overlay plays.")]
    public float InjuredHealthFraction = 0.3f;

    [Header("Cover")]
    [Tooltip("Use cover at all. Needs an EnemyCover component on the same object (auto-added if missing).")]
    public bool useCover = true;
    [Tooltip("Seconds of shooting before looking for somewhere better to shoot from. Keeps them from standing in the open trading shots forever.")]
    public float timeInOpenBeforeCover = 3.5f;
    [Tooltip("How long to stay hidden between peeks.")]
    public float coverHideTime = 1.6f;
    [Tooltip("How long to stay leaned out shooting before dropping back down.")]
    public float peekDuration = 1.8f;
    [Tooltip("Crouch (the shared crouch locomotion pose) while hidden behind cover.")]
    public bool crouchInCover = true;

    [Header("Movement")]
    public float walkSpeed = 2.5f;
    public float chaseSpeed = 5f;
    public float turnSpeed = 8f;

    [Header("Patrol")]
    public Transform[] waypoints; // Assign patrol points in Inspector
    public float waypointWaitTime = 2f;
    public bool loopPatrol = true;

    [Header("Shooting (optional)")]
    public bool canShoot = false;
    public CharacterWeaponClass weaponClass = CharacterWeaponClass.Rifle;
    public GameObject bulletPrefab;
    public Transform muzzlePoint;
    public float bulletSpeed = 40f;

    [Header("Accuracy")]
    public float baseInaccuracy = 5f;
    public float maxInaccuracy = 15f;
    public float aimDownTime = 1.5f; // Time to reach full accuracy
    public float movingInaccuracyBonus = 5f; // More inaccurate when moving
    public float alertInaccuracyBonus = 5f; // More inaccurate when freshly alerted

    private float currentAimTime = 0f; // How long enemy has been aiming
    private bool justSpottedPlayer = false; // Recently alerted
    private float alertTimer = 0f;
    private float alertDuration = 4f;

    // State
    private State currentState = State.Patrol;
    private NavMeshAgent agent;
    private Health health;
    private Transform player;
    private PlayerHealth playerHealth;

    private UpperBodyPose bodyPose; // animated body (drives Melee swing)
    private CharacterAnimationDriver characterAnimation;
    private EnemyWeapon enemyWeapon;
    private Vector3 lastKnownPlayerPos;
    private float investigateTimer;
    private float attackTimer;
    private float waypointWaitTimer;
    private bool playerInSight;
    private Transform fireTarget;   // player, or a nearby ally - recomputed each Update
    private bool fireTargetIsAlly;

    private bool isStunned = false;

    // Cover
    private EnemyCover cover;
    private Vector3 coverPosition;
    private Vector3 peekPosition;
    private float coverTimer;
    private bool peeking;
    private float timeInOpen;

    // Patrol
    private int currentWaypointIndex = 0;
    private bool patrolForward = true;
    private bool isWaiting = false;

    void Start()
    {
        agent  = GetComponent<NavMeshAgent>();
        health = GetComponent<Health>();

        GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
        if (playerObj != null)
        {
            player       = playerObj.transform;
            playerHealth = playerObj.GetComponent<PlayerHealth>();
        }

        // Connect to health events
        health.onDeath.AddListener(OnDeath);
        health.onDamaged.AddListener((healthFraction) => characterAnimation?.SetInjured(healthFraction <= InjuredHealthFraction));
        agent.speed = walkSpeed;
        bodyPose = GetComponentInChildren<UpperBodyPose>();
        characterAnimation = GetComponentInChildren<CharacterAnimationDriver>();
        enemyWeapon = GetComponent<EnemyWeapon>();
        if (useCover)
        {
            cover = GetComponent<EnemyCover>();
            if (cover == null) cover = gameObject.AddComponent<EnemyCover>();
            if (cover.sightBlockers == 0) cover.sightBlockers = sightBlockers;
        }
        if (!canShoot)
            characterAnimation?.SetWeaponClass(CharacterWeaponClass.Unarmed);
        else
        {
            CharacterWeaponClass selectedClass = weaponClass;
            if (name.IndexOf("Club", System.StringComparison.OrdinalIgnoreCase) >= 0)
                selectedClass = CharacterWeaponClass.Pistol;
            characterAnimation?.SetWeaponClass(selectedClass);
        }

        // Start patrolling if waypoints are set, otherwise idle
        if (waypoints != null && waypoints.Length > 0)
            EnterPatrol();
        else
            EnterIdle();
    }

    void Update()
    {
        if (currentState == State.Dead || isStunned) return;

        playerInSight = CanSeePlayer();
        ChooseFireTarget();

        // Always update last known position when player is visible
        if (playerInSight)
            lastKnownPlayerPos = player.position;

        switch (currentState)
        {
            case State.Patrol:      HandlePatrol();      break;
            case State.Idle:        HandleIdle();        break;
            case State.Investigate: HandleInvestigate(); break;
            case State.Chase:       HandleChase();       break;
            case State.Attack:      HandleAttack();      break;
            case State.TakeCover:   HandleTakeCover();   break;
            case State.InCover:     HandleInCover();     break;
        }

        // Gun down while there's nothing to shoot at, up the moment there is. Same idea
        // as the player's lower-weapon key, just driven by the state machine.
        bool inCombat = currentState == State.Chase || currentState == State.Attack
            || currentState == State.TakeCover || currentState == State.InCover
            || (currentState == State.Investigate && playerInSight);
        enemyWeapon?.SetCombatReady(inCombat);
    }

    // State handlers
    void HandlePatrol()
    {
        agent.isStopped = false;
        agent.speed = walkSpeed;

        // Immediately chase if player spotted
        if (playerInSight) { EnterChase(); return; }

        // Hearing check
        float distToPlayer = Vector3.Distance(transform.position, player.position);
        if (distToPlayer <= hearingRange)
        {
            lastKnownPlayerPos = player.position;
            EnterInvestigate();
            return;
        }

        if (waypoints.Length == 0) { EnterIdle(); return; }

        if (isWaiting)
        {
            // Wait at waypoint before moving to next
            waypointWaitTimer -= Time.deltaTime;
            if (waypointWaitTimer <= 0f)
            {
                isWaiting = false;
                MoveToNextWaypoint();
            }
            return;
        }

        // Check if reached current waypoint
        if (ReachedDestination())
        {
            isWaiting = true;
            waypointWaitTimer = waypointWaitTime;
            return;
        }
    }

    void HandleIdle()
    {
        agent.isStopped = true;

        if (playerInSight) { EnterChase(); return; }

        // Hearing check (detect nearby player)
        float distToPlayer = Vector3.Distance(transform.position, player.position);
        if (distToPlayer <= hearingRange)
        {
            lastKnownPlayerPos = player.position;
            EnterInvestigate();
        }
    }

    void HandleInvestigate()
    {
        agent.isStopped = false;
        agent.speed = walkSpeed;
        agent.SetDestination(lastKnownPlayerPos);

        if (playerInSight) { EnterChase(); return; }

        // Give up searching after set time
        investigateTimer -= Time.deltaTime;
        if (investigateTimer <= 0f || ReachedDestination())
        {
            // Return to patrol if waypoints exist, otherwise idle
            if (waypoints != null && waypoints.Length > 0)
                EnterPatrol();
            else
                EnterIdle();
        }
    }

    void HandleChase()
    {
        agent.isStopped = false;
        agent.speed = chaseSpeed;
        agent.SetDestination(player.position);

        float dist = Vector3.Distance(transform.position, player.position);

        // Switch to attack if close enough
        if (dist <= attackRange) { EnterAttack(); return; }

        // Shoot if in range and has sight of the player, OR there's a closer ally to
        // engage instead (that's what makes an enemy break off toward a nearby ally
        // rather than only ever caring about the player).
        bool engagingAlly = fireTargetIsAlly && fireTarget != null
            && Vector3.Distance(transform.position, fireTarget.position) <= shootRange;
        if (canShoot && ((dist <= shootRange && playerInSight) || engagingAlly))
        {
            // Out of ammo, or been standing in the open too long - go find something to
            // stand behind rather than reloading in the middle of a firefight.
            if (useCover && cover != null && (NeedsReload() || timeInOpen >= timeInOpenBeforeCover)
                && cover.FindCover(player.position, out coverPosition, out peekPosition))
            {
                EnterTakeCover();
                return;
            }

            if (NeedsReload()) { enemyWeapon?.Reload(); return; }

            agent.isStopped = true;
            timeInOpen += Time.deltaTime;
            FacePlayer();
            TryShoot();
            return;
        }

        timeInOpen = 0f;

        characterAnimation?.SetAiming(false);
        enemyWeapon?.SetAiming(false);

        // Lost sight
        if (!playerInSight)
            EnterInvestigate();
    }

    void HandleTakeCover()
    {
        agent.isStopped = false;
        agent.speed = chaseSpeed;
        agent.SetDestination(coverPosition);
        characterAnimation?.SetAiming(false);
        enemyWeapon?.SetAiming(false);

        // Reload on the way - the whole point of breaking off was to get the magazine
        // changed somewhere the player isn't shooting at.
        if (NeedsReload()) enemyWeapon?.Reload();

        if (ReachedDestination() || Vector3.Distance(transform.position, coverPosition) < 0.6f)
            EnterInCover();
    }

    void HandleInCover()
    {
        coverTimer -= Time.deltaTime;

        if (peeking)
        {
            // Leaned out: stand, face the player, shoot until the timer runs out or the
            // magazine does.
            agent.isStopped = false;
            agent.SetDestination(peekPosition);
            characterAnimation?.SetCrouching(false);
            FacePlayer();

            if (playerInSight && canShoot && !NeedsReload())
                TryShoot();

            if (coverTimer <= 0f || NeedsReload())
            {
                peeking = false;
                coverTimer = coverHideTime;
            }
            return;
        }

        // Hidden: drop down, stop shooting, reload if needed.
        agent.SetDestination(coverPosition);
        bool atCover = Vector3.Distance(transform.position, coverPosition) < 0.6f;
        agent.isStopped = atCover;
        if (crouchInCover) characterAnimation?.SetCrouching(atCover);
        characterAnimation?.SetAiming(false);
        enemyWeapon?.SetAiming(false);

        if (NeedsReload()) { enemyWeapon?.Reload(); return; }

        // Player got close, or wandered off - cover isn't the answer any more.
        float dist = Vector3.Distance(transform.position, player.position);
        if (dist <= attackRange) { LeaveCover(); EnterAttack(); return; }

        if (coverTimer <= 0f)
        {
            // Cover that can no longer see the player is useless - find new cover or
            // just push forward.
            if (cover != null && !cover.HasLineOfSight(peekPosition + Vector3.up * 1.5f, player.position))
            {
                if (cover.FindCover(player.position, out coverPosition, out peekPosition))
                {
                    EnterTakeCover();
                    return;
                }
                LeaveCover();
                EnterChase();
                return;
            }

            peeking = true;
            coverTimer = peekDuration;
        }
    }

    void EnterTakeCover()
    {
        currentState = State.TakeCover;
        peeking = false;
        timeInOpen = 0f;
        agent.isStopped = false;
        Debug.Log($"{name} → Taking cover");
    }

    void EnterInCover()
    {
        currentState = State.InCover;
        peeking = false;
        coverTimer = coverHideTime;
        Debug.Log($"{name} → In cover");
    }

    void LeaveCover()
    {
        peeking = false;
        characterAnimation?.SetCrouching(false);
    }

    // Empty magazine, or mid-reload - either way there's nothing to fire right now.
    bool NeedsReload() => enemyWeapon != null && (!enemyWeapon.HasAmmo || enemyWeapon.IsReloading);

    void HandleAttack()
    {
        if (isStunned) return; // Cannot attack when stunned

        characterAnimation?.SetAiming(false);
        enemyWeapon?.SetAiming(false);
        FacePlayer();
        agent.isStopped = true;

        float dist = Vector3.Distance(transform.position, player.position);

        if (dist > attackRange * 1.5f) { EnterChase(); return; }

        attackTimer -= Time.deltaTime;
        if (attackTimer <= 0f)
        {
            attackTimer = attackCooldown;
            if (bodyPose != null) bodyPose.TriggerMelee();
            playerHealth.TakeDamage(attackDamage);
            Debug.Log($"{name} attacked player for {attackDamage} damage");
        }
    }

    // States
    void EnterPatrol()
    {
        currentState = State.Patrol;
        agent.isStopped = false;
        agent.speed = walkSpeed;
        agent.SetDestination(waypoints[currentWaypointIndex].position);
        Debug.Log($"{name} → Patrolling");
    }

    void EnterIdle()
    {
        currentState = State.Idle;
        agent.isStopped = true;
        Debug.Log($"{name} → Idle");
    }

    void EnterInvestigate()
    {
        currentState = State.Investigate;
        investigateTimer = investigateTime;
        agent.SetDestination(lastKnownPlayerPos);
        Debug.Log($"{name} → Investigating");
    }

    void EnterChase()
    {
        currentState = State.Chase;
        agent.speed = chaseSpeed;

        // Just spotted player
        justSpottedPlayer = true;
        alertTimer = alertDuration;
        currentAimTime = 0f;

        Debug.Log($"{name} → Chasing");
    }

    void EnterAttack()
    {
        currentState = State.Attack;
        attackTimer = 0f; // Attack immediately
        agent.isStopped = true;
        Debug.Log($"{name} → Attacking");
    }

    // Patrol
    void MoveToNextWaypoint()
    {
        if (loopPatrol)
        {
            // Loop: 0 → 1 → 2 → 3 → 0 → 1...
            currentWaypointIndex = (currentWaypointIndex + 1) % waypoints.Length;
        }
        else
        {
            // Back and forward: 0 → 1 → 2 → 3 → 2 → 1 → 0...
            if (patrolForward)
            {
                currentWaypointIndex++;
                if (currentWaypointIndex >= waypoints.Length - 1)
                    patrolForward = false;
            }
            else
            {
                currentWaypointIndex--;
                if (currentWaypointIndex <= 0)
                    patrolForward = true;
            }
        }

        agent.SetDestination(waypoints[currentWaypointIndex].position);
    }

    // Picks who to actually shoot at this frame: the nearest visible ally within
    // allyEngageRange if there is one, otherwise the player. This runs independently of
    // playerInSight/state - it only changes AIM, not the chase/investigate logic, which
    // still tracks the player as before.
    void ChooseFireTarget()
    {
        fireTarget = playerInSight ? player : null;
        fireTargetIsAlly = false;

        float best = allyEngageRange;
        foreach (var ally in FindObjectsByType<FriendlyAI>(FindObjectsSortMode.None))
        {
            if (ally == null || !ally.enabled) continue; // FriendlyAI disables itself on death
            float d = Vector3.Distance(transform.position, ally.transform.position);
            if (d > best) continue;
            if (!HasLineOfSightTo(ally.transform.position + Vector3.up * 1.2f)) continue;
            fireTarget = ally.transform;
            fireTargetIsAlly = true;
            best = d;
        }
    }

    // Same raycast CanSeePlayer uses, generalised to any point - characters standing
    // between the shooter and the target don't block this (only real geometry should).
    bool HasLineOfSightTo(Vector3 targetPoint)
    {
        Vector3 from = transform.position + Vector3.up * 1.5f;
        Vector3 dir = targetPoint - from;
        float dist = dir.magnitude;
        if (dist < 0.05f) return true;

        foreach (var hit in Physics.RaycastAll(from, dir / dist, dist, sightBlockers, QueryTriggerInteraction.Ignore))
        {
            if (hit.transform.GetComponentInParent<EnemyAI>() != null) continue;
            if (hit.transform.GetComponentInParent<FriendlyAI>() != null) continue;
            if (hit.transform.CompareTag("Player")) continue;
            return false;
        }
        return true;
    }

    // Detection
    bool CanSeePlayer()
    {
        if (player == null) return false;

        Vector3 dirToPlayer = (player.position - transform.position);
        float dist = dirToPlayer.magnitude;

        // Distance check
        if (dist > sightRange) return false;

        // Angle check
        float angle = Vector3.Angle(transform.forward, dirToPlayer);
        if (angle > sightAngle) return false;

        // Line of sight check
        if (Physics.Raycast(transform.position + Vector3.up * 1.5f,
            dirToPlayer.normalized, out RaycastHit hit, dist, sightBlockers,
            QueryTriggerInteraction.Ignore)) // don't let own trigger hitboxes block sight
        {
            // Hit something before reaching player
            if (!hit.transform.CompareTag("Player"))
            {
                Debug.DrawRay(transform.position + Vector3.up * 1.5f,
                    dirToPlayer.normalized * dist, Color.red);
                return false;
            }
        }

        // Nothing blocking line of sight to player
        Debug.DrawRay(transform.position + Vector3.up * 1.5f,
            dirToPlayer.normalized * dist, Color.green);
        return true;
    }

    // Combat
    void FacePlayer()
    {
        Transform facing = fireTarget != null ? fireTarget : player;
        Vector3 dir = (facing.position - transform.position).normalized;
        dir.y = 0f;
        if (dir == Vector3.zero) return;

        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.Slerp(
            transform.rotation, targetRot, turnSpeed * Time.deltaTime);
    }

    void TryShoot()
    {
        attackTimer -= Time.deltaTime;
        if (attackTimer > 0f || bulletPrefab == null || muzzlePoint == null) return;

        // One round per shot. An enemy with an EnemyWeapon that's empty or reloading
        // can't fire at all - EnemyAI's cover logic is what gets it reloaded.
        if (enemyWeapon != null && !enemyWeapon.TryConsumeAmmo())
        {
            enemyWeapon.Reload();
            return;
        }

        attackTimer = attackCooldown;
        characterAnimation?.SetAiming(true);
        enemyWeapon?.SetAiming(true);
        characterAnimation?.PlayShoot();

        // Build up aim time while stationary and in sight
        if (agent.isStopped)
            currentAimTime += attackCooldown;
        else
            currentAimTime = Mathf.Max(0f, currentAimTime - Time.deltaTime);

        // Tick down alert penalty
        if (justSpottedPlayer)
        {
            alertTimer -= attackCooldown;
            if (alertTimer <= 0f)
                justSpottedPlayer = false;
        }

        // Calculate final inaccuracy
        // Better accuracy the longer they aim
        float aimAccuracyBonus = Mathf.Lerp(0f, maxInaccuracy - baseInaccuracy,
            Mathf.Clamp01(currentAimTime / aimDownTime));

        float inaccuracy = baseInaccuracy
            + (agent.velocity.magnitude > 0.1f ? movingInaccuracyBonus : 0f)
            + (justSpottedPlayer ? alertInaccuracyBonus : 0f)
            - aimAccuracyBonus;

        inaccuracy = Mathf.Max(0f, inaccuracy);

        if (fireTarget == null) return;

        // Apply random spread
        Vector3 direction = (fireTarget.position - muzzlePoint.position).normalized;
        direction = Quaternion.Euler(
            Random.Range(-inaccuracy, inaccuracy),
            Random.Range(-inaccuracy, inaccuracy),
            0f) * direction;

        // Spawn bullet
        GameObject bullet = Instantiate(bulletPrefab,
            muzzlePoint.position + muzzlePoint.forward * 0.3f,
            muzzlePoint.rotation);

        bullet.transform.forward = direction;

        // firedByEnemy covers both cases: Projectile.CanDamage lets an enemy bullet hurt
        // the player OR anything carrying a FriendlyAI, never another EnemyAI.
        Projectile proj = bullet.GetComponent<Projectile>();
        if (proj != null) proj.firedByEnemy = true;

        Rigidbody rb = bullet.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearVelocity = direction * bulletSpeed;
        }

        // Stop them hitting themself
        Collider bulletCol = bullet.GetComponent<Collider>();
        if (bulletCol != null)
            foreach (Collider col in GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(bulletCol, col);
    }

    bool ReachedDestination()
    {
        return !agent.pathPending
            && agent.remainingDistance <= agent.stoppingDistance;
    }

    void OnDeath()
    {
        currentState = State.Dead;
        agent.isStopped = true;

        characterAnimation?.SetCrouching(false);

        // The Dead bool is NOT set here any more. Ragdoll.cs now decides whether this
        // death plays an animation first (and which direction it was shot from) before
        // handing over to physics - setting the bool here would force the front-death
        // clip regardless and start it a frame early.

        // Drop the held weapon as a world pickup, same as the player does.
        enemyWeapon?.Drop();

        // Disable colliders so the corpse stops blocking shots/paths — the Ragdoll
        // component re-enables the bone colliders a frame later and the body
        // collapses where it died (no despawn).
        foreach (Collider col in GetComponentsInChildren<Collider>())
            col.enabled = false;

        enabled = false;
    }

    // View sight range in editor
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, sightRange);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);
        Gizmos.color = Color.blue;
        Gizmos.DrawWireSphere(transform.position, hearingRange);

        // Draw patrol route in editor
        if (waypoints == null || waypoints.Length < 2) return;
        Gizmos.color = Color.white;
        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null) continue;
            Gizmos.DrawSphere(waypoints[i].position, 0.2f);
            int next = loopPatrol
                ? (i + 1) % waypoints.Length
                : Mathf.Min(i + 1, waypoints.Length - 1);
            if (waypoints[next] != null)
                Gizmos.DrawLine(waypoints[i].position, waypoints[next].position);
        }
    }

    public void Stun(float duration)
    {
        if (isStunned) return;
        StartCoroutine(StunRoutine(duration));
    }

    IEnumerator StunRoutine(float duration)
    {
        isStunned = true;
        agent.isStopped = true;
        Debug.Log($"{name} stunned for {duration}s");

        yield return new WaitForSeconds(duration);

        isStunned = false;
        agent.isStopped = false;
    }
}