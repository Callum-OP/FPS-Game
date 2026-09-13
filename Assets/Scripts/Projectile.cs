using UnityEngine;

public class Projectile : MonoBehaviour
{
    public float speed = 80f;
    public float damage = 25f;
    public float lifetime = 5f;
    
    [Range(0, 1)]
    public float minDamagePercent = 0.1f; // 10%

    public static bool debugLog = false; // set by test tools to trace impacts

    [Tooltip("Set by EnemyAI on its own bullets: damages only the player, never other enemies.")]
    public bool firedByEnemy = false;

    private Rigidbody rb;
    private float spawnTime;
    private bool hasHit = false;

    void Start()
    {
        spawnTime = Time.time;
        // Set bullet velocity
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.linearVelocity = transform.forward * speed;
        Destroy(gameObject, lifetime);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (hasHit) return;
        hasHit = true;

        if (debugLog)
            Debug.Log($"BULLET '{name}' hit '{collision.collider.name}' root='{collision.collider.transform.root.name}' at {collision.contacts[0].point}");

        float currentDamage = CalculateFalloff();

        // search parents: hitboxes (ragdoll bone colliders) sit below the object that owns Health
        var health = collision.collider.GetComponentInParent<Health>();
        if (health != null && !firedByEnemy)
            health.TakeDamage(currentDamage);

        var pHealth = collision.collider.GetComponentInParent<PlayerHealth>();
        if (pHealth != null && firedByEnemy)
            pHealth.TakeDamage(currentDamage);

        Destroy(gameObject);
    }   

    // hitboxes on living enemies are triggers — damage them here, but pass through
    // scenery triggers (pickup zones etc.) untouched
    void OnTriggerEnter(Collider other)
    {
        if (hasHit) return;
        var health = firedByEnemy ? null : other.GetComponentInParent<Health>();
        var pHealth = firedByEnemy ? other.GetComponentInParent<PlayerHealth>() : null;
        if (health == null && pHealth == null) return; // scenery trigger or friendly body — pass through

        hasHit = true;
        if (debugLog)
            Debug.Log($"BULLET '{name}' trigger-hit '{other.name}' root='{other.transform.root.name}'");

        float currentDamage = CalculateFalloff();
        if (health != null) health.TakeDamage(currentDamage);
        if (pHealth != null) pHealth.TakeDamage(currentDamage);
        Destroy(gameObject);
    }

    float CalculateFalloff()
    {
        // Decrease damage based on time flying and lifetime left
        float ageRatio = (Time.time - spawnTime) / lifetime;
        return Mathf.Lerp(damage, damage * minDamagePercent, ageRatio);
    }
}