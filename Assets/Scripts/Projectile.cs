using UnityEngine;

public class Projectile : MonoBehaviour
{
    public float speed = 80f;
    public float damage = 25f;

    private Rigidbody rb;
    private bool hasHit = false;

    void Start()
    {
        // Set bullet velocity
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.linearVelocity = transform.forward * speed;
        Destroy(gameObject, 5f);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (hasHit) return;
        hasHit = true;

        Health health = collision.gameObject.GetComponent<Health>();
        if (health != null)
        {
            health.TakeDamage(damage);
            Debug.Log($"Dealt {damage} damage to {collision.gameObject.name}");
        }

        Destroy(gameObject);
    }
}