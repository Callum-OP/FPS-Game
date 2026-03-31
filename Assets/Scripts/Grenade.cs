using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class Grenade : MonoBehaviour
{
    [Header("Timer")]
    public float fuseTime = 3f;

    [Header("Stun")]
    public float stunRadius = 10f;
    public float stunDuration = 3f;
    public float shockwaveForce = 500f;

    [Header("Shrapnel")]
    public bool hasShrapnel = false;
    public GameObject shrapnelPrefab;
    public int shrapnelCount = 20;
    public float shrapnelSpeed = 40f;
    public float shrapnelDamage = 50f;

    [Header("Effects")]
    public GameObject explosionEffect;
    public AudioClip explosionSound;

    private bool hasExploded = false;

    void Start()
    {
        StartCoroutine(FuseRoutine());
    }

    void OnCollisionEnter(Collision collision)
    {
        // Bounce off walls naturally — only explode on timer
    }

    IEnumerator FuseRoutine()
    {
        yield return new WaitForSeconds(fuseTime);
        Explode();
    }

    void Explode()
    {
        if (hasExploded) return;
        hasExploded = true;

        // Spawn effect
        if (explosionEffect != null)
        {
            GameObject fx = Instantiate(explosionEffect,
                transform.position, Quaternion.identity);
            Destroy(fx, 3f);
        }

        if (explosionSound != null)
            AudioManager.Instance?.Play(explosionSound);

        // Stun and shockwave nearby objects
        ApplyStunAndShockwave();

        // Fire shrapnel if enabled
        if (hasShrapnel)
            FireShrapnel();

        Destroy(gameObject);
    }

    void ApplyStunAndShockwave()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, stunRadius);

        foreach (Collider hit in hits)
        {
            // Shockwave all rigidbodies
            Rigidbody rb = hit.GetComponent<Rigidbody>();
            if (rb != null)
                rb.AddExplosionForce(shockwaveForce, transform.position,
                    stunRadius, 1f, ForceMode.Impulse);

            // Stun enemies
            EnemyAI enemy = hit.GetComponent<EnemyAI>();
            if (enemy != null)
                enemy.Stun(stunDuration);
        }
    }

    void FireShrapnel()
    {
        if (shrapnelPrefab == null) return;

        for (int i = 0; i < shrapnelCount; i++)
        {
            // Fire in random directions for a sphere of shrapnel
            Vector3 dir = Random.onUnitSphere;

            // Bias downward slightly so shrapnel hits ground targets
            dir.y = Mathf.Abs(dir.y) * -0.5f + dir.y * 0.5f;

            GameObject shrapnel = Instantiate(shrapnelPrefab,
                transform.position, Quaternion.identity);
            shrapnel.transform.forward = dir;

            Rigidbody rb = shrapnel.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.useGravity = false;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.linearVelocity = dir * shrapnelSpeed;
            }

            // Set shrapnel damage
            Projectile p = shrapnel.GetComponent<Projectile>();
            if (p != null) p.damage = shrapnelDamage;

            Destroy(shrapnel, 3f);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, stunRadius);
    }
}