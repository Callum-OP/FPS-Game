using UnityEngine;

public class PlayerSetup : MonoBehaviour
{
    [Header("Assign These Only")]
    public Camera fpCamera;
    public WeaponController activeWeapon;

    // Auto found
    [HideInInspector] public PlayerMovement playerMovement;
    [HideInInspector] public PlayerHealth playerHealth;
    [HideInInspector] public CameraRecoil cameraRecoil;
    [HideInInspector] public WeaponRecoil weaponRecoil;
    [HideInInspector] public WeaponADS weaponADS;
    [HideInInspector] public WeaponSway weaponSway;
    [HideInInspector] public WeaponCant weaponCant;
    [HideInInspector] public CameraLean cameraLean;
    [HideInInspector] public LowerWeapon lowerWeapon;
    [HideInInspector] public PlayerHUD playerHUD;
    [HideInInspector] public AmmoHUD ammoHUD;

    void Awake()
    {
        GatherComponents();
        WireStatic();

        if (activeWeapon != null)
            WireWeapon(activeWeapon);
    }

    void GatherComponents()
    {
        if (fpCamera == null) fpCamera = Camera.main;

        playerMovement = GetComponent<PlayerMovement>();
        playerHealth = GetComponent<PlayerHealth>();

        // These are on the camera itself
        cameraRecoil = fpCamera.GetComponent<CameraRecoil>();
        cameraLean = fpCamera.GetComponent<CameraLean>();

        playerHUD = FindFirstObjectByType<PlayerHUD>();
        ammoHUD = FindFirstObjectByType<AmmoHUD>();

        // Weapon specific components gathered from active weapon
        if (activeWeapon != null)
            GatherWeaponComponents(activeWeapon);
    }

    void GatherWeaponComponents(WeaponController weapon)
    {
        // All these live in the weapon hierarchy
        weaponCant = weapon.GetComponentInChildren<WeaponCant>(true);
        weaponRecoil = weapon.GetComponentInChildren<WeaponRecoil>(true);
        weaponADS = weapon.GetComponentInChildren<WeaponADS>(true);
        weaponSway = weapon.GetComponentInChildren<WeaponSway>(true);
        lowerWeapon = weapon.GetComponentInChildren<LowerWeapon>(true);

        Debug.Log($"Gathered from weapon {weapon.name} — " +
            $"Cant:{weaponCant != null} " +
            $"Recoil:{weaponRecoil != null} " +
            $"ADS:{weaponADS != null}");
    }

    // Things that never change regardless of weapon
    void WireStatic()
    {
        if (playerMovement != null)
            playerMovement.cameraTransform = fpCamera.transform;

        if (playerHUD != null)
            playerHUD.playerHealth = playerHealth;
    }

    // Everything that needs rewiring when weapon changes
    void WireDynamic(WeaponController weapon)
    {
        // PlayerMovement
        if (playerMovement != null)
        {
            playerMovement.weaponCant = weaponCant;
        }

        // CameraRecoil
        if (cameraRecoil != null)
            cameraRecoil.weaponADS = weaponADS;

        // CameraLean
        if (cameraLean != null)
        {
            cameraLean.weaponCant = weaponCant;
            cameraLean.weaponHolder = weapon.transform;
        }

        // WeaponCant
        if (weaponCant != null)
            weaponCant.weaponADS = weaponADS;

        // WeaponSway
        if (weaponSway != null)
            weaponSway.weaponADS = weaponADS;

        // WeaponRecoil
        if (weaponRecoil != null)
            weaponRecoil.weaponADS = weaponADS;

        // LowerWeapon
        if (lowerWeapon != null)
        {
            lowerWeapon.playerMovement = playerMovement;
            lowerWeapon.weaponADS = weaponADS;
        }

        // WeaponADS
        if (weaponADS != null)
        {
            weaponADS.fpCamera = fpCamera;
            weaponADS.weaponCant = weaponCant;
        }
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

        // Regather weapon components from NEW weapon
        GatherWeaponComponents(weapon);

        // Wire weapon core
        weapon.fpCamera = fpCamera;
        weapon.cameraRecoil = cameraRecoil;
        if (cameraRecoil != null)
            cameraRecoil.Configure(
                weapon.camRecoilX,
                weapon.camRecoilY,
                weapon.camRecoilZ);
        weapon.weaponRecoil = weaponRecoil;

        // Rewire everything with fresh references
        WireDynamic(weapon);

        // Wire HUD
        if (playerHUD != null)
        {
            weapon.onAmmoChanged += playerHUD.UpdateAmmo;
            weapon.onReloadStart += playerHUD.ShowReloading;
            weapon.onReloadEnd += playerHUD.HideReloading;
            playerHUD.UpdateAmmo(weapon.GetCurrentAmmo(), weapon.GetMaxAmmo());
        }

        if (ammoHUD != null)
            ammoHUD.weapon = weapon;
    }

    public void SwapWeapon(WeaponController newWeapon)
    {
        if (activeWeapon != null)
        {
            WeaponDrop drop = GetComponent<WeaponDrop>();
            drop?.Drop();
        }

        newWeapon.gameObject.SetActive(true);
        WireWeapon(newWeapon);
    }
}