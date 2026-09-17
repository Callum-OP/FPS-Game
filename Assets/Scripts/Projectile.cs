using UnityEngine;

public class Projectile : MonoBehaviour
{
    public float speed = 80f;
    public float damage = 25f;
    public float lifetime = 5f;
    
    [Range(0, 1)]
    public float minDamagePercent = 0.1f; // 10%

    [Tooltip("Physical shove this round gives a body on a killing/near-killing hit. A rifle round's default is small on its own; a shotgun's per-pellet force is meant to be smaller still but the pellets hit in the same frame and ADD UP on the Ragdoll, so a shotgun blast naturally ends up shoving much harder than any single rifle round without needing a special case per weapon type.")]
    public float knockbackForce = 5f;

    public static bool debugLog = false; // set by test tools to trace impacts

    [Tooltip("Set by EnemyAI on its own bullets: damages only the player and the player's allies, never other enemies.")]
    public bool firedByEnemy = false;
    [Tooltip("Set by FriendlyAI on its own bullets: damages enemies, never the player or other allies.")]
    public bool firedByFriendly = false;

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

        var contact = collision.contacts[0];
        ImpactEffects.SpawnImpact(contact.point, contact.normal, collision.collider.transform);

        ApplyDamage(collision.collider, currentDamage, rb.linearVelocity.normalized);
        Destroy(gameObject);
    }   

    // hitboxes on living enemies are triggers — damage them here, but pass through
    // scenery triggers (pickup zones etc.) untouched
    void OnTriggerEnter(Collider other)
    {
        if (hasHit) return;
        if (!CanDamage(other)) return; // scenery trigger or a body on our own side - pass through

        hasHit = true;
        if (debugLog)
            Debug.Log($"BULLET '{name}' trigger-hit '{other.name}' root='{other.transform.root.name}'");

        // Triggers give no contact point, so approximate one on the collider's surface.
        Vector3 point = other.ClosestPoint(transform.position);
        ImpactEffects.SpawnImpact(point, (transform.position - point).normalized, other.transform);

        ApplyDamage(other, CalculateFalloff(), transform.forward);
        Destroy(gameObject);
    }

    // Who this bullet is allowed to hurt. Three sides: the player (and their allies),
    // the enemies, and neutral scenery. Allies are identified by carrying a FriendlyAI -
    // without this an enemy bullet would sail straight through your own squad, since
    // they own a Health rather than a PlayerHealth.
    bool CanDamage(Collider col)
    {
        var allyAI = col.GetComponentInParent<FriendlyAI>();
        bool isAlly = allyAI != null;
        var health = col.GetComponentInParent<Health>();
        var pHealth = col.GetComponentInParent<PlayerHealth>();
        if (health == null && pHealth == null) return false;

        if (firedByEnemy) return pHealth != null || isAlly;
        if (firedByFriendly) return health != null && !isAlly && pHealth == null;

        // Player's own bullets. An ally is normally immune so you can't accidentally
        // gun down your own squad, but each FriendlyAI can opt in via
        // allowFriendlyFireFromPlayer for testing/experimentation.
        if (isAlly) return allyAI.allowFriendlyFireFromPlayer;
        return health != null;
    }

    void ApplyDamage(Collider col, float amount, Vector3 travelDirection)
    {
        if (!CanDamage(col)) return;

        var pHealth = col.GetComponentInParent<PlayerHealth>();
        if (pHealth != null) { pHealth.TakeDamage(amount, travelDirection, knockbackForce); return; }

        col.GetComponentInParent<Health>()?.TakeDamage(amount, travelDirection, knockbackForce);
    }

    float CalculateFalloff()
    {
        // Decrease damage based on time flying and lifetime left
        float ageRatio = (Time.time - spawnTime) / lifetime;
        return Mathf.Lerp(damage, damage * minDamagePercent, ageRatio);
    }
}