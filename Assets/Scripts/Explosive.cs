using UnityEngine;

public class Explosive : MonoBehaviour
{
    [Header("Explosion")]
    public float explosionRadius = 8f;
    public float explosionDamage = 75f;
    public float explosionForce = 600f;

    [Header("Shrapnel")]
    public bool fireShrapnel = true;
    public GameObject shrapnelPrefab;
    public int shrapnelCount = 15;
    public float shrapnelSpeed = 40f;
    public float shrapnelDamage = 25f;

    [Header("Effects")]
    public GameObject explosionEffect;
    public AudioClip explosionSound;

    [Header("Destroyed Version")]
    public GameObject destroyedPrefab;

    private bool hasExploded = false;

    void Start()
    {
        Health health = GetComponent<Health>();
        if (health != null)
            health.onDeath.AddListener(Explode);
        else
            Debug.LogError($"{name} has no Health component!");
    }

    public void Explode()
    {
        if (hasExploded) return;
        hasExploded = true;

        // 1. Spawn Visual Effects
        if (explosionEffect != null)
        {
            GameObject fx = Instantiate(explosionEffect, transform.position, Quaternion.identity);
            Destroy(fx, 3f);
        }

        AudioManager.Instance?.Play(explosionSound);

        // 2. Physics & Damage Radial Check
        Collider[] hits = Physics.OverlapSphere(transform.position, explosionRadius);
        foreach (Collider hit in hits)
        {
            if (hit.gameObject == gameObject) continue; // Don't hit self

            float dist = Vector3.Distance(transform.position, hit.transform.position);
            float falloff = 1f - Mathf.Clamp01(dist / explosionRadius);

            // Damage logic
            hit.GetComponent<Health>()?.TakeDamage(explosionDamage * falloff);
            hit.GetComponent<PlayerHealth>()?.TakeDamage(explosionDamage * falloff);

            // Chain reaction
            if (hit.TryGetComponent(out Explosive other))
                other.Explode();

            // Push nearby objects
            if (hit.TryGetComponent(out Rigidbody rb))
                rb.AddExplosionForce(explosionForce, transform.position, explosionRadius);
        }

        // 3. Shrapnel Logic
        if (fireShrapnel && shrapnelPrefab != null)
        {
            for (int i = 0; i < shrapnelCount; i++)
            {
                Vector3 dir = Random.onUnitSphere;
                GameObject shrapnel = Instantiate(shrapnelPrefab, transform.position, Quaternion.identity);
                shrapnel.transform.forward = dir;

                if (shrapnel.TryGetComponent(out Rigidbody sRb))
                {
                    sRb.useGravity = false;
                    sRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    sRb.linearVelocity = dir * shrapnelSpeed;
                }

                if (shrapnel.TryGetComponent(out Projectile p)) 
                    p.damage = shrapnelDamage;

                Destroy(shrapnel, 3f);
            }
        }

        // 4. Spawn Destroyed Version & Cleanup
        if (destroyedPrefab != null)
        {
            GameObject destroyed = Instantiate(destroyedPrefab, transform.position, transform.rotation);
            
            // Apply force to all pieces of the destroyed prefab
            Rigidbody[] childRbs = destroyed.GetComponentsInChildren<Rigidbody>();
            foreach (Rigidbody rb in childRbs)
            {
                rb.AddExplosionForce(explosionForce, transform.position, explosionRadius);
            }
            
            // Optional: Destroy the "wreckage" after some time to save performance
            Destroy(destroyed, 10f);
        }

        // CRITICAL: Remove the original object from the scene
        Destroy(gameObject);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, explosionRadius);
    }
}