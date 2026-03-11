using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Health))]
public class EnemyAI : MonoBehaviour
{
    public enum State { Idle, Investigate, Chase, Attack, Dead }

    [Header("Detection")]
    public float sightRange = 20f;
    public float sightAngle = 90f;
    public float hearingRange = 8f; // Detect player without line of sight
    public float investigateTime = 4f; // Look for player
    public LayerMask sightBlockers;

    [Header("Combat")]
    public float attackRange = 2f;
    public float attackDamage = 10f;
    public float attackCooldown = 1.2f;
    public float shootRange = 15f;

    [Header("Movement")]
    public float walkSpeed = 2.5f;
    public float chaseSpeed = 5f;
    public float turnSpeed = 8f;

    [Header("Shooting (optional)")]
    public bool canShoot = false;
    public GameObject bulletPrefab;
    public Transform muzzlePoint;
    public float bulletSpeed = 40f;

    // State
    private State currentState = State.Idle;
    private NavMeshAgent agent;
    private Health health;
    private Transform player;
    private Health playerHealth;

    private Vector3 lastKnownPlayerPos;
    private float investigateTimer;
    private float attackTimer;
    private bool playerInSight;

    void Start()
    {
        agent  = GetComponent<NavMeshAgent>();
        health = GetComponent<Health>();

        GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
        if (playerObj != null)
        {
            player       = playerObj.transform;
            playerHealth = playerObj.GetComponent<Health>();
        }

        // Hook into health events
        health.onDeath.AddListener(OnDeath);

        agent.speed = walkSpeed;
    }

    void Update()
    {
        if (currentState == State.Dead) return;

        playerInSight = CanSeePlayer();

        // Always update last known position when player is visible
        if (playerInSight)
            lastKnownPlayerPos = player.position;

        switch (currentState)
        {
            case State.Idle:        HandleIdle();        break;
            case State.Investigate: HandleInvestigate(); break;
            case State.Chase:       HandleChase();       break;
            case State.Attack:      HandleAttack();      break;
        }
    }

    // State Hnadlers
    void HandleIdle()
    {
        agent.isStopped = true;

        if (playerInSight)
        {
            EnterChase();
            return;
        }

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

        if (playerInSight)
        {
            EnterChase();
            return;
        }

        // Give up after set time
        investigateTimer -= Time.deltaTime;
        if (investigateTimer <= 0f || ReachedDestination())
            EnterIdle();
    }

    void HandleChase()
    {
        agent.isStopped = false;
        agent.speed = chaseSpeed;
        agent.SetDestination(player.position);

        float dist = Vector3.Distance(transform.position, player.position);

        // Switch to attack if close enough
        if (dist <= attackRange)
        {
            EnterAttack();
            return;
        }

        // Shoot if in range and has sight
        if (canShoot && dist <= shootRange && playerInSight)
        {
            agent.isStopped = true;
            FacePlayer();
            TryShoot();
            return;
        }

        // Lost sight
        if (!playerInSight)
        {
            EnterInvestigate();
        }
    }

    void HandleAttack()
    {
        FacePlayer();
        agent.isStopped = true;

        float dist = Vector3.Distance(transform.position, player.position);

        // Chase player
        if (dist > attackRange * 1.5f)
        {
            EnterChase();
            return;
        }

        // Attack cooldown
        attackTimer -= Time.deltaTime;
        if (attackTimer <= 0f)
        {
            attackTimer = attackCooldown;
            playerHealth?.TakeDamage(attackDamage);
            Debug.Log($"{name} attacked player for {attackDamage} damage");
        }
    }

    // States
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
        Debug.Log($"{name} → Chasing");
    }

    void EnterAttack()
    {
        currentState = State.Attack;
        attackTimer = 0f;  // Attack immediately
        agent.isStopped = true;
        Debug.Log($"{name} → Attacking");
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
            dirToPlayer.normalized, out RaycastHit hit, dist, sightBlockers))
        {
            // Hit something before reaching player
            if (!hit.transform.CompareTag("Player"))
            {
                Debug.DrawRay(transform.position + Vector3.up * 1.5f,
                    dirToPlayer.normalized * dist, Color.red);
                return false;
            }
        }

        Debug.DrawRay(transform.position + Vector3.up * 1.5f,
            dirToPlayer.normalized * dist, Color.green);
        return true;
    }

    // Combat
    void FacePlayer()
    {
        Vector3 dir = (player.position - transform.position).normalized;
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

        attackTimer = attackCooldown;

        GameObject bullet = Instantiate(bulletPrefab,
            muzzlePoint.position + muzzlePoint.forward * 0.3f,
            muzzlePoint.rotation);

        bullet.transform.forward = (player.position - muzzlePoint.position).normalized;

        Rigidbody rb = bullet.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearVelocity = bullet.transform.forward * bulletSpeed;
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

        // Disable collider
        foreach (Collider col in GetComponentsInChildren<Collider>())
            col.enabled = false;

        Debug.Log($"{name} died");
        Destroy(gameObject, 2f);
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
    }
}