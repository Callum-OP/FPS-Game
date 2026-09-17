using System.Collections.Generic;
using UnityEngine;

public class Explosive : MonoBehaviour
{
    [Header("Explosion")]
    public bool useTimer = false;
    public float fuseTime = 3f;
    public float explosionRadius = 8f;
    public float explosionForce = 600f;

    [Header("Damage")]
    [Tooltip("Damage at the centre of the blast. Falls off to zero at explosionRadius.")]
    public float maxDamage = 120f;
    [Tooltip("How the damage falls off with distance. 1 = linear; higher concentrates it near the centre.")]
    public float damageFalloffPower = 1.6f;
    [Tooltip("Require line of sight to the target. Stops blasts killing through walls and floors.")]
    public bool requireLineOfSight = true;
    [Tooltip("What counts as blocking the blast for the line-of-sight test.")]
    public LayerMask blastBlockers = ~0;
    [Tooltip("Fraction of maxDamage at or above which the victim is torn apart rather than just killed - see Dismemberment.")]
    [Range(0f, 2f)] public float dismemberDamageFraction = 0.75f;

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

    [Header("Shrapnel Advanced")]
    public float spawnRadiusOffset = 0.5f; // Distance from center to spawn
    public bool randomizeSize = false; // Toggle for different sizes of shrapnel
    public float minSizeScale = 0.5f;
    public float maxSizeScale = 2.0f;

    [Header("Effects")]
    public GameObject explosionEffect;
    public AudioClip explosionSound;

    [Header("Effect Scaling")]
    [Tooltip("Scale the explosion effect prefab with the blast radius, so a big barrel doesn't produce the same small puff as a grenade.")]
    public bool scaleEffectToRadius = true;
    [Tooltip("Multiplier on that scaling - tune per effect prefab.")]
    public float effectScale = 0.2f;

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

        // Spawn visual effects. The Easy Weapons "Explosion 1/2" prefabs drop straight in
        // here - set explosionEffect to one of those and scale it with effectScale to match
        // the blast radius rather than leaving a small canned puff on a large explosion.
        if (explosionEffect != null)
        {
            GameObject fx = Instantiate(explosionEffect, transform.position, Quaternion.identity);
            if (scaleEffectToRadius) fx.transform.localScale *= Mathf.Max(0.1f, explosionRadius * effectScale);
            Destroy(fx, 5f);
        }

        // Scorch the ground and nearby walls, using the same decal system the bullets use.
        ImpactEffects.SpawnScorch(transform.position, explosionRadius);

        // Play sound
        if (explosionSound != null)
            AudioManager.Instance?.Play(explosionSound);

        // Physics & damage radius check.
        //
        // Damage is applied ONCE PER CHARACTER, not once per collider. A rigged body has
        // a dozen bone hitboxes inside the blast radius, so the previous "foreach collider"
        // shape would have hit each of them - except it never applied damage at all, which
        // is why barrels and grenades were harmless. Both problems are fixed by resolving
        // each collider up to whatever owns its Health and keeping a set of who's been hit.
        var damaged = new HashSet<GameObject>();
        Collider[] hits = Physics.OverlapSphere(transform.position, explosionRadius);
        foreach (Collider hit in hits)
        {
            if (hit.gameObject == gameObject) continue;

            // Chain reaction
            if (hit.TryGetComponent(out Explosive other))
                other.Explode();

            // Push nearby objects
            if (hit.TryGetComponent(out Rigidbody rb))
                rb.AddExplosionForce(explosionForce, transform.position, explosionRadius, 0.6f, ForceMode.Impulse);

            // Stun enemies
            EnemyAI enemy = hit.GetComponentInParent<EnemyAI>();
            if (enemy != null) enemy.Stun(stunDuration);

            ApplyBlastDamage(hit, damaged);
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

    void ApplyBlastDamage(Collider hit, HashSet<GameObject> alreadyDamaged)
    {
        var health = hit.GetComponentInParent<Health>();
        var playerHealth = hit.GetComponentInParent<PlayerHealth>();
        if (health == null && playerHealth == null) return;

        GameObject owner = health != null ? health.gameObject : playerHealth.gameObject;
        if (!alreadyDamaged.Add(owner)) return;

        // Measure to the closest point on the collider, not the object's pivot - a pivot
        // at the feet makes a blast at head height look like it missed.
        Vector3 target = hit.ClosestPoint(transform.position);
        float distance = Vector3.Distance(transform.position, target);
        if (distance > explosionRadius) return;

        if (requireLineOfSight && !HasLineOfSight(target, hit)) return;

        float falloff = Mathf.Pow(1f - Mathf.Clamp01(distance / explosionRadius), damageFalloffPower);
        float damage = maxDamage * falloff;
        if (damage < 1f) return;

        // Tell the body it was an explosion BEFORE the damage lands, since the damage is
        // what triggers death: explosive deaths skip the death animation, go straight to
        // ragdoll, and come apart if the hit was hard enough.
        var ragdoll = owner.GetComponentInChildren<Ragdoll>();
        if (ragdoll == null) ragdoll = owner.GetComponent<Ragdoll>();
        if (ragdoll != null)
            ragdoll.NotifyExplosion(transform.position, explosionForce,
                damage >= maxDamage * dismemberDamageFraction);

        if (health != null) health.TakeDamage(damage);
        else playerHealth.TakeDamage(damage);
    }

    bool HasLineOfSight(Vector3 target, Collider targetCollider)
    {
        Vector3 dir = target - transform.position;
        float dist = dir.magnitude;
        if (dist < 0.05f) return true;

        // Everything between here and the victim, so the victim's own colliders (and this
        // barrel's) don't count as cover.
        RaycastHit[] between = Physics.RaycastAll(transform.position, dir / dist, dist - 0.05f,
            blastBlockers, QueryTriggerInteraction.Ignore);
        foreach (var h in between)
        {
            if (h.collider == targetCollider) continue;
            if (h.collider.transform.IsChildOf(targetCollider.transform.root)) continue;
            if (h.collider.transform.IsChildOf(transform)) continue;
            return false;
        }
        return true;
    }

    void FireShrapnel()
    {
        if (shrapnelPrefab == null) return;

        for (int i = 0; i < shrapnelCount; i++)
        {
            // Pick a random direction
            Vector3 randomDir = Random.onUnitSphere;
            
            // Move spawn point further away based on the slider/value
            Vector3 spawnPos = transform.position + (randomDir * spawnRadiusOffset); 
            
            GameObject shrapnel = Instantiate(shrapnelPrefab, spawnPos, Quaternion.LookRotation(randomDir));

            // Size variation
            if (randomizeSize)
            {
                float randomScale = Random.Range(minSizeScale, maxSizeScale);
                shrapnel.transform.localScale = Vector3.one * randomScale;
            }

            // Set projectile stats
            Projectile proj = shrapnel.GetComponent<Projectile>();
            if (proj != null)
            {
                proj.speed = shrapnelSpeed;
                proj.damage = shrapnelDamage;
                proj.lifetime = shrapnelLifetime;
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