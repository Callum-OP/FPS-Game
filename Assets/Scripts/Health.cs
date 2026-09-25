using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

public class Health : MonoBehaviour
{
    public float maxHealth = 100f;
    public bool destroyOnDeath = true;
    private float currentHealth;
    public UnityEvent onDeath;
    public UnityEvent<float> onDamaged;

    Ragdoll ragdoll;
    bool isDead;

    void Start()
    {
        currentHealth = maxHealth;
        ragdoll = GetComponentInChildren<Ragdoll>();
        if (ragdoll == null) ragdoll = GetComponent<Ragdoll>();
    }

    /// <summary>Plain damage - no physical shove. Used for anything that isn't a
    /// directional hit (poison, fall damage, scripted events).</summary>
    public void TakeDamage(float amount) => TakeDamage(amount, Vector3.zero, 0f);

    /// <summary>Damage with a direction and a knockback amount. Every hit's force is
    /// handed to the Ragdoll and ACCUMULATES there rather than being judged in
    /// isolation - a single rifle round is a small shove, but several shotgun pellets
    /// landing in the same frame add up to a real one, which is what decides whether
    /// the death plays its animation or skips straight to a body flying backward. See
    /// Ragdoll.AccumulateHitForce.</summary>
    public void TakeDamage(float amount, Vector3 hitDirection, float hitForce)
    {
        if (isDead) return; // corpses take no further damage (stops repeat death events / knockback on bodies)
        currentHealth = Mathf.Max(currentHealth - amount, 0f);
        Debug.Log($"{name} took {amount} damage — {currentHealth} HP remaining");
        onDamaged?.Invoke(currentHealth / maxHealth);

        if (hitForce > 0f) ragdoll?.AccumulateHitForce(hitDirection, hitForce);
        if (currentHealth <= 0f) Die();
    }

    public void Heal(float amount)
    {
        currentHealth = Mathf.Min(currentHealth + amount, maxHealth);
        onDamaged?.Invoke(currentHealth / maxHealth);
    }

    public float GetHealthPercent() => currentHealth / maxHealth;

    /// <summary>A physical shove (PlayerShove, right-click) - not damage, not the death-time ragdoll
    /// knockback: just slides a LIVING body backward over `duration` and briefly stuns it (reusing
    /// EnemyAI.Stun where present), the way an actual push would. Uses whatever NavMeshAgent this
    /// character moves with; does nothing for anything that doesn't have one (the player never
    /// receives this - PlayerMelee/PlayerShove only ever target Health, and the player uses
    /// PlayerHealth instead).</summary>
    public void PushBack(Vector3 worldDirection, float distance, float duration)
    {
        if (isDead || worldDirection.sqrMagnitude < 0.0001f) return;
        var agent = GetComponent<NavMeshAgent>();
        if (agent == null) agent = GetComponentInParent<NavMeshAgent>();
        if (agent == null) return;

        GetComponent<EnemyAI>()?.Stun(duration);
        GetComponentInChildren<CharacterAnimationDriver>()?.PlayHit();
        StartCoroutine(PushRoutine(agent, worldDirection.normalized, Mathf.Max(0f, distance), Mathf.Max(0.05f, duration)));
    }

    IEnumerator PushRoutine(NavMeshAgent agent, Vector3 dir, float distance, float duration)
    {
        bool wasStopped = agent.isStopped;
        agent.isStopped = true;
        // Fast out, easing off - most of the slide happens in the first fraction of a second, same
        // shape a person stumbling backward from a shove actually moves.
        float slide = Mathf.Min(duration, 0.35f);
        float t = 0f;
        while (t < slide && agent != null && agent.enabled && agent.isOnNavMesh)
        {
            float dt = Time.deltaTime;
            t += dt;
            float ease = 1f - Mathf.Clamp01(t / slide);
            agent.Move(dir * (distance / slide) * dt * (0.4f + 1.6f * ease));
            yield return null;
        }
        float remaining = duration - t;
        if (remaining > 0f) yield return new WaitForSeconds(remaining);
        if (agent != null && agent.enabled) agent.isStopped = wasStopped;
    }

    void Die()
    {
        isDead = true;
        Debug.Log($"{name} died!");
        onDeath?.Invoke();
    }
}