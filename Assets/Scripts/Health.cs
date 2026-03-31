using UnityEngine;
using UnityEngine.Events;

public class Health : MonoBehaviour
{
    public float maxHealth = 100f;
    public bool destroyOnDeath = true;
    private float currentHealth;
    public UnityEvent onDeath;
    public UnityEvent<float> onDamaged;

    void Start()
    {
        currentHealth = maxHealth;
    }

    public void TakeDamage(float amount)
    {
        currentHealth = Mathf.Max(currentHealth - amount, 0f);
        Debug.Log($"{name} took {amount} damage — {currentHealth} HP remaining");
        onDamaged?.Invoke(currentHealth / maxHealth);
        if (currentHealth <= 0f) Die();
    }

    public void Heal(float amount)
    {
        currentHealth = Mathf.Min(currentHealth + amount, maxHealth);
    }

    public float GetHealthPercent() => currentHealth / maxHealth;

    void Die()
    {
        Debug.Log($"{name} died!");
        onDeath?.Invoke();
        if (destroyOnDeath)
            Destroy(gameObject);
    }
}