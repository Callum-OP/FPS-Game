using UnityEngine;
using System.Collections;

public class PlayerSetup : MonoBehaviour
{
    [Header("Assign These Only")]
    public Camera fpCamera;
    public WeaponController activeWeapon;

    // Auto found
    private PlayerMovement playerMovement;
    private PlayerHealth playerHealth;
    private CameraRecoil cameraRecoil;
    private WeaponRecoil weaponRecoil;
    private WeaponADS weaponADS;
    private WeaponSway weaponSway;
    private WeaponCant weaponCant;
    private CameraLean cameraLean;
    private LowerWeapon lowerWeapon;
    private PlayerHUD playerHUD;
    private AmmoHUD ammoHUD;

    void Awake()
    {
        GatherComponents();
        WireEverything();
    }

    void GatherComponents()
    {
        if (fpCamera == null) fpCamera = Camera.main;

        playerMovement = GetComponent<PlayerMovement>();
        playerHealth = GetComponent<PlayerHealth>();
        cameraRecoil = fpCamera.GetComponent<CameraRecoil>();
        weaponRecoil = fpCamera.GetComponentInChildren<WeaponRecoil>();
        weaponADS = fpCamera.GetComponentInChildren<WeaponADS>();
        weaponSway = fpCamera.GetComponentInChildren<WeaponSway>();
        weaponCant = fpCamera.GetComponentInChildren<WeaponCant>();
        cameraLean = fpCamera.GetComponent<CameraLean>();
        lowerWeapon = fpCamera.GetComponentInChildren<LowerWeapon>();

        // Find HUD scripts anywhere in scene
        playerHUD = FindFirstObjectByType<PlayerHUD>();
        ammoHUD = FindFirstObjectByType<AmmoHUD>();

        // Log what was found
        Debug.Log($"PlayerSetup found — " +
            $"Movement:{playerMovement != null} " +
            $"Health:{playerHealth != null} " +
            $"CameraRecoil:{cameraRecoil != null} " +
            $"WeaponRecoil:{weaponRecoil != null} " +
            $"ADS:{weaponADS != null} " +
            $"HUD:{playerHUD != null}");
    }

    void WireEverything()
    {
        WireCamera();
        WireMovement();
        WireHUD();

        if (activeWeapon != null)
            WireWeapon(activeWeapon);
    }

    void WireCamera()
    {
        if (playerMovement != null)
            playerMovement.cameraTransform = fpCamera.transform;

        if (weaponADS != null)
        {
            weaponADS.fpCamera = fpCamera;
            weaponADS.weaponCant = weaponCant;
        }

        if (weaponSway != null)
            weaponSway.weaponADS = weaponADS;

        if (cameraLean != null)
            cameraLean.weaponCant = weaponCant;

        if (lowerWeapon != null)
        {
            lowerWeapon.playerMovement = playerMovement;
            lowerWeapon.weaponADS = weaponADS;
        }

        if (cameraRecoil != null)
            cameraRecoil.weaponADS = weaponADS;
    }

    void WireMovement()
    {
        if (playerMovement == null) return;
        playerMovement.weaponCant = weaponCant;
    }

    void WireHUD()
    {
        if (playerHUD != null)
            playerHUD.playerHealth = playerHealth;
    }

    public void WireWeapon(WeaponController weapon)
    {
        if (weapon == null) return;

        // Unsubscribe old weapon from HUD
        if (activeWeapon != null && playerHUD != null)
        {
            activeWeapon.onAmmoChanged -= playerHUD.UpdateAmmo;
            activeWeapon.onReloadStart -= playerHUD.ShowReloading;
            activeWeapon.onReloadEnd -= playerHUD.HideReloading;
        }

        activeWeapon = weapon;

        // Wire weapon to player systems
        weapon.fpCamera     = fpCamera;
        weapon.cameraRecoil = cameraRecoil;
        weapon.weaponRecoil = weaponRecoil;

        // Wire weapon to HUD
        if (playerHUD != null)
        {
            weapon.onAmmoChanged += playerHUD.UpdateAmmo;
            weapon.onReloadStart += playerHUD.ShowReloading;
            weapon.onReloadEnd += playerHUD.HideReloading;
            weapon.onAmmoChanged?.Invoke(
                weapon.GetCurrentAmmo(), weapon.GetMaxAmmo());
        }

        if (ammoHUD != null)
            ammoHUD.weapon = weapon;

        Debug.Log($"PlayerSetup: Wired {weapon.name}");
    }

    public void SwapWeapon(WeaponController newWeapon)
    {
        // Drop current weapon if holding one
        if (activeWeapon != null)
        {
            WeaponDrop drop = GetComponent<WeaponDrop>();
            drop?.Drop();
        }

        newWeapon.gameObject.SetActive(true);
        WireWeapon(newWeapon);
    }
}
