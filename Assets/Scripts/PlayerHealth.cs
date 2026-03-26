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

    [Header("Audio")]
    public AudioClip[] hurtClips;
    public AudioClip deathClip;

    private float lastHurtTime = -999f;
    public float hurtSoundCooldown = 1.5f;

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

    // Player is hit
    public void TakeDamage(float amount)
    {
        if (isDead) return;

        currentHealth = Mathf.Max(currentHealth - amount, 0f);
        regenTimer = regenDelay;

        onHealthChanged?.Invoke(currentHealth / maxHealth);
        onDamaged?.Invoke();

        // Play random hurt sound with cooldown
        if (Time.time - lastHurtTime >= hurtSoundCooldown)
        {
            lastHurtTime = Time.time;
            if (hurtClips.Length > 0)
                AudioManager.Instance?.Play(
                    hurtClips[Random.Range(0, hurtClips.Length)]);
        }

            if (currentHealth <= 0f) Die();
        }

    public void Heal(float amount)
    {
        if (isDead) return;
        currentHealth = Mathf.Min(currentHealth + amount, maxHealth);
        onHealthChanged?.Invoke(currentHealth / maxHealth);
    }

    // Player is dead
    void Die()
    {
        isDead = true;
        onDeath?.Invoke();

        // Play dead sound
        AudioManager.Instance?.Play(deathClip);
    }

    public float GetHealthFraction() => currentHealth / maxHealth;
    public bool IsDead() => isDead;
}