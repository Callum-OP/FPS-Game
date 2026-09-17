using UnityEngine;
using UnityEngine.Events;

public class Health : MonoBehaviour
{
    public float maxHealth = 100f;
    public bool destroyOnDeath = true;
    private float currentHealth;
    public UnityEvent onDeath;
    public UnityEvent<float> onDamaged;

    Ragdoll ragdoll;

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

    void Die()
    {
        Debug.Log($"{name} died!");
        onDeath?.Invoke();
    }
}