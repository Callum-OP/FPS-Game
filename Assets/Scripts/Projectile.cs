using UnityEngine;

public class Projectile : MonoBehaviour
{
    public float speed = 80f;
    public float damage = 25f;
    public float lifetime = 5f;
    
    [Range(0, 1)]
    public float minDamagePercent = 0.1f; // 10%

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

        float currentDamage = CalculateFalloff();

        if (collision.gameObject.TryGetComponent(out Health health))
            health.TakeDamage(currentDamage);
        
        if (collision.gameObject.TryGetComponent(out PlayerHealth pHealth))
            pHealth.TakeDamage(currentDamage);

        Destroy(gameObject);
    }   

    float CalculateFalloff()
    {
        // Decrease damage based on time flying and lifetime left
        float ageRatio = (Time.time - spawnTime) / lifetime;
        return Mathf.Lerp(damage, damage * minDamagePercent, ageRatio);
    }
}