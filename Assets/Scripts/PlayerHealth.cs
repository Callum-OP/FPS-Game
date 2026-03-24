using UnityEngine;
using UnityEngine.Events;

public class PlayerHealth : MonoBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;
    private float currentHealth;

    [Header("Regeneration")]
    public bool canRegen = true;
    public float regenDelay = 5f; // How long before regen
    public float regenRate = 5f;

    private float regenTimer;
    private bool isDead = false;

    // Events for HUD
    public System.Action<float> onHealthChanged;
    public System.Action onDeath;
    public System.Action onDamaged;

    void Start()
    {
        currentHealth = maxHealth;
        onHealthChanged?.Invoke(1f);
    }

    void Update()
    {
        if (isDead || !canRegen) return;

        // Count down regen delay
        if (regenTimer > 0f)
        {
            regenTimer -= Time.deltaTime;
            return;
        }

        // Regen health
        if (currentHealth < maxHealth)
        {
            currentHealth = Mathf.Min(currentHealth + regenRate * Time.deltaTime,
                maxHealth);
            onHealthChanged?.Invoke(currentHealth / maxHealth);
        }
    }

    public void TakeDamage(float amount)
    {
        if (isDead) return;

        currentHealth = Mathf.Max(currentHealth - amount, 0f);
        regenTimer = regenDelay;

        onHealthChanged?.Invoke(currentHealth / maxHealth);
        onDamaged?.Invoke();

        Debug.Log($"Player took {amount} damage — {currentHealth} HP remaining");

        if (currentHealth <= 0f) Die();
    }

    public void Heal(float amount)
    {
        if (isDead) return;
        currentHealth = Mathf.Min(currentHealth + amount, maxHealth);
        onHealthChanged?.Invoke(currentHealth / maxHealth);
    }

    void Die()
    {
        isDead = true;
        onDeath?.Invoke();
        Debug.Log("Player died!");
    }

    public float GetHealthFraction() => currentHealth / maxHealth;
    public bool IsDead() => isDead;
}