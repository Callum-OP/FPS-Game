using System.Collections.Generic;
using UnityEngine;

public class Explosive : MonoBehaviour
{
    [Header("Explosion")]
    public bool useTimer = false;
    public float fuseTime = 3f;
    public float explosionRadius = 8f;
    public float explosionForce = 600f;

    [Header("Stun")]
    public float stunRadius = 10f;
    public float stunDuration = 3f;

    [Header("Shrapnel")]
    public bool fireShrapnel = true;
    public GameObject shrapnelPrefab;
    public int shrapnelCount = 15;
    public float shrapnelSpeed = 40f;
    public float shrapnelDamage = 25f;
    public float shrapnelLifetime = 1.5f;

    [Header("Effects")]
    public GameObject explosionEffect;
    public AudioClip explosionSound;

    [Header("Destroyed Version")]
    public GameObject destroyedPrefab;

    private bool hasExploded = false;

    void Start()
    {
        // It explodes when Health reaches 0 if it is not timed
        Health health = GetComponent<Health>();
        if (health != null)
            health.onDeath.AddListener(Explode);

        // If timed, start the fuse timer
        if (useTimer)
            Invoke(nameof(Explode), fuseTime);
    }

    public void Explode()
    {
        if (hasExploded) return;
        hasExploded = true;

        // Spawn visual effects
        if (explosionEffect != null)
        {
            GameObject fx = Instantiate(explosionEffect, transform.position, Quaternion.identity);
            Destroy(fx, 3f);
        }

        // Play sound
        if (explosionSound != null)
            AudioManager.Instance?.Play(explosionSound);

        // Physics & damage radius check
        Collider[] hits = Physics.OverlapSphere(transform.position, explosionRadius);
        foreach (Collider hit in hits)
        {
            if (hit.gameObject == gameObject) continue; 

            // Chain reaction
            if (hit.TryGetComponent(out Explosive other))
                other.Explode();

            // Push nearby objects
            if (hit.TryGetComponent(out Rigidbody rb))
                rb.AddExplosionForce(explosionForce, transform.position, explosionRadius);

            // Stun enemies
            EnemyAI enemy = hit.GetComponent<EnemyAI>();
            if (enemy != null)
                enemy.Stun(stunDuration);
        }

        // Fire shrapnel
        if (fireShrapnel)
            FireShrapnel();

        // Spawn destroyed version
        if (destroyedPrefab != null)
        {
            GameObject destroyed = Instantiate(destroyedPrefab, transform.position, transform.rotation);
            Rigidbody[] childRbs = destroyed.GetComponentsInChildren<Rigidbody>();
            foreach (Rigidbody rb in childRbs)
            {
                rb.AddExplosionForce(explosionForce, transform.position, explosionRadius);
            }
        }

        Destroy(gameObject);
    }

    void FireShrapnel()
    {
        if (shrapnelPrefab == null) return;

        for (int i = 0; i < shrapnelCount; i++)
        {
            // Pick a random direction
            Vector3 randomDir = Random.onUnitSphere;

            // Move spawn point 0.5 meters away from center so they don't touch the grenade or each other
            Vector3 spawnPos = transform.position + (randomDir * 0.5f); 
            
            GameObject shrapnel = Instantiate(shrapnelPrefab, spawnPos, Quaternion.LookRotation(randomDir));

            // Set projectile stats
            Projectile proj = shrapnel.GetComponent<Projectile>();
            if (proj != null)
            {
                proj.speed = shrapnelSpeed;
                proj.damage = shrapnelDamage;
                proj.lifetime = 1.5f;
            }
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, explosionRadius);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, stunRadius);
    }
}