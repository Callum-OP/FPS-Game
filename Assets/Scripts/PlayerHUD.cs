using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using System.Collections;

public class PlayerHUD : MonoBehaviour
{
    [Header("Health")]
    public Slider healthSlider;
    public Image damageFlash;
    public float flashDuration = 0.3f;

    [Header("Ammo")]
    public TextMeshProUGUI ammoText;
    public TextMeshProUGUI reloadText;

    [Header("Game Over")]
    public GameObject gameOverScreen;
    public float gameOverDelay = 1.5f;

    [Header("References")]
    public PlayerHealth playerHealth;
    public WeaponController weaponController;

    void Start()
    {
        // Subscribe to health events
        playerHealth.onHealthChanged += UpdateHealthBar;
        playerHealth.onDamaged       += TriggerDamageFlash;
        playerHealth.onDeath         += TriggerGameOver;

        // Subscribe to weapon events
        weaponController.onAmmoChanged  += UpdateAmmo;
        weaponController.onReloadStart  += ShowReloading;
        weaponController.onReloadEnd    += HideReloading;

        // Init
        gameOverScreen.SetActive(false);
        damageFlash.color = Color.clear;

        UpdateHealthBar(1f);
        UpdateAmmo(weaponController.GetCurrentAmmo(), weaponController.GetMaxAmmo());
    }

    // Health
    void UpdateHealthBar(float fraction)
    {
        healthSlider.value = fraction;

        // Tint slider red as health gets low
        Image fill = healthSlider.fillRect.GetComponent<Image>();
        if (fill != null)
            fill.color = Color.Lerp(Color.red, Color.green, fraction);
    }

    void TriggerDamageFlash()
    {
        StopCoroutine("DamageFlashRoutine");
        StartCoroutine("DamageFlashRoutine");
    }

    IEnumerator DamageFlashRoutine()
    {
        // Flash red then fade out
        damageFlash.color = new Color(1f, 0f, 0f, 0.3f);
        float elapsed = 0f;
        while (elapsed < flashDuration)
        {
            elapsed += Time.deltaTime;
            damageFlash.color = new Color(1f, 0f, 0f,
                Mathf.Lerp(0.3f, 0f, elapsed / flashDuration));
            yield return null;
        }
        damageFlash.color = Color.clear;
    }

    // Ammo
    void UpdateAmmo(int current, int max) =>
        ammoText.text = $"{current} / {max}";

    void ShowReloading()
    {
        if (reloadText != null) reloadText.gameObject.SetActive(true);
        ammoText.text = "";
    }

    void HideReloading()
    {
        if (reloadText != null) reloadText.gameObject.SetActive(false);
        UpdateAmmo(weaponController.GetCurrentAmmo(), weaponController.GetMaxAmmo());
    }

    // Game over
    void TriggerGameOver()
    {
        StartCoroutine(GameOverRoutine());
    }

    IEnumerator GameOverRoutine()
    {
        yield return new WaitForSeconds(gameOverDelay);
        gameOverScreen.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Time.timeScale = 0.3f;  // Slow down on death
    }

    public void RestartGame()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    void OnDestroy()
    {
        if (playerHealth != null)
        {
            playerHealth.onHealthChanged -= UpdateHealthBar;
            playerHealth.onDamaged -= TriggerDamageFlash;
            playerHealth.onDeath -= TriggerGameOver;
        }

        if (weaponController != null)
        {
            weaponController.onAmmoChanged -= UpdateAmmo;
            weaponController.onReloadStart -= ShowReloading;
            weaponController.onReloadEnd -= HideReloading;
        }
    }
}