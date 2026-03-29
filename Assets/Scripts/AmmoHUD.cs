using UnityEngine;
using TMPro;

public class AmmoHUD : MonoBehaviour
{
    public TextMeshProUGUI ammoText;
    public WeaponController weapon;

    void Start()
    {
        if (weapon == null) { Debug.LogError("WeaponController not assigned (AmmoHUD)"); return; }

        weapon.onAmmoChanged += UpdateAmmo;
        weapon.onReloadStart += ShowReloading;
        weapon.onReloadEnd   += HideReloading;

        // Initial ammo display
        UpdateAmmo(weapon.GetCurrentAmmo(), weapon.GetMaxAmmo());
    }

    // AmmoHUD.cs
    void Update()
    {
        if (weapon == null)
        {
            ammoText.text = "";
            return;
        }
        ammoText.text = weapon.GetIsReloading()
            ? "RELOADING..."
            : $"{weapon.GetCurrentAmmo()} / {weapon.GetMaxAmmo()}";
    }

    void UpdateAmmo(int current, int max) => ammoText.text = $"{current} / {max}";
    void ShowReloading()                  => ammoText.text = "RELOADING...";
    void HideReloading()                  => UpdateAmmo(weapon.GetCurrentAmmo(), weapon.GetMaxAmmo());

    void OnDestroy()
    {
        if (weapon == null) return;
        weapon.onAmmoChanged -= UpdateAmmo;
        weapon.onReloadStart -= ShowReloading;
        weapon.onReloadEnd   -= HideReloading;
    }
}