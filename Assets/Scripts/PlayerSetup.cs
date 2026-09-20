using UnityEngine;

public class PlayerSetup : MonoBehaviour
{
    [Header("Assign These Only")]
    public Camera fpCamera;
    public WeaponController activeWeapon;
    [Tooltip("Health fraction (0-1) at or below which the Injured locomotion overlay plays.")]
    public float InjuredHealthFraction = 0.3f;
    [Tooltip("Applied to fpCamera.nearClipPlane at Awake. The arms/hands sit very close to a first-person camera, and at smaller character scales (e.g. 1 instead of 1.6) that distance can end up closer than Unity's default near clip plane (0.3), so the near plane silently culls chunks of the upper arm/hand each frame - looking like they clip through or vanish. A small constant like this keeps everything in front of the lens regardless of character scale.")]
    public float armSafeNearClipPlane = 0.01f;

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
    CharacterAnimationDriver characterAnimation;
    WeaponHandIK weaponHandIK;
    WeaponReloadHandler weaponReloadHandler;
    [Tooltip("Logs the resolved CharacterAttachPoints reference and 'ReloadGrab' lookup result every time a weapon is gathered/wired. Turn on if a specific weapon keeps getting a null ReloadGrab while others don't.")]
    public bool debugLogAttachPoints = false;

    CharacterAttachPoints attachPoints;

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
        characterAnimation = GetComponentInChildren<CharacterAnimationDriver>();
        weaponHandIK = GetComponentInChildren<WeaponHandIK>();
        // Manually-placed reload/carry points ("ReloadGrab", "MagHand", etc) live here.
        // Optional - fine if this component or its entries don't exist yet, the reload
        // just skips whichever part it can't find a point for.
        attachPoints = GetComponentInChildren<CharacterAttachPoints>();

        // Only the local player's rigged body should ever hide its head - enemies
        // share the same body prefab and must keep theirs.
        GetComponentInChildren<HeadHider>()?.Activate();

        // These are on the camera itself
        cameraRecoil = fpCamera.GetComponent<CameraRecoil>();
        cameraLean = fpCamera.GetComponent<CameraLean>();

        playerHUD = FindFirstObjectByType<PlayerHUD>();
        ammoHUD = FindFirstObjectByType<AmmoHUD>();

        // Play the death animation state when the player dies - CharacterAnimationDriver
        // already has SetDead(), it just had nothing calling it (Ragdoll.cs listens to
        // this same event separately to handle the physical collapse).
        if (playerHealth != null)
            playerHealth.onDeath += () => characterAnimation?.SetDead(true);

        // Low-health "injured" locomotion overlay - clears itself automatically
        // if health regens back above the threshold.
        if (playerHealth != null)
            playerHealth.onHealthChanged += (frac) => characterAnimation?.SetInjured(frac <= InjuredHealthFraction);

        // Drop the held weapon on death so it isn't left floating attached to the
        // camera once DeathCam detaches it - reuses the same WeaponDrop.Drop() the
        // manual drop key calls, which deactivates the weapon and unequips it.
        // Subscribed here in Awake so it fires before DeathCam's own OnEnable
        // subscription detaches the camera.
        if (playerHealth != null)
        {
            WeaponDrop weaponDrop = GetComponent<WeaponDrop>();
            if (weaponDrop != null)
                playerHealth.onDeath += () => weaponDrop.Drop();
        }

        // Weapon specific components gathered from active weapon
        if (activeWeapon != null)
            GatherWeaponComponents(activeWeapon);
    }

    void GatherWeaponComponents(WeaponController weapon)
    {
        // Re-resolved here (not just cached from Awake) so a weapon swap can't leave this
        // pointing at a destroyed reference - it used to only be found once in Awake,
        // which worked fine for the starting weapon but went silently null (Unity's
        // fake-null on a destroyed object, caught by `?.` rather than throwing) for every
        // weapon picked up afterward if CharacterAttachPoints ever ended up parented
        // under a weapon model instead of the player's own persistent body. It must live
        // on the body, not any weapon - re-fetching here is a safety net, not a fix for
        // that placement mistake if it's the actual cause.
        if (attachPoints == null) attachPoints = GetComponentInChildren<CharacterAttachPoints>();

        if (debugLogAttachPoints)
            Debug.Log($"GatherWeaponComponents({weapon.name}): attachPoints={(attachPoints != null ? attachPoints.name : "NULL")} " +
                $"ReloadGrab={(attachPoints != null ? (attachPoints.Get("ReloadGrab") != null ? attachPoints.Get("ReloadGrab").name : "not found in list") : "n/a")}", this);

        // All these live in the weapon hierarchy
        weaponCant = weapon.GetComponentInChildren<WeaponCant>(true);
        weaponRecoil = weapon.GetComponentInChildren<WeaponRecoil>(true);
        weaponADS = weapon.GetComponentInChildren<WeaponADS>(true);
        weaponSway = weapon.GetComponentInChildren<WeaponSway>(true);
        lowerWeapon = weapon.GetComponentInChildren<LowerWeapon>(true);
        weaponReloadHandler = weapon.GetComponentInChildren<WeaponReloadHandler>(true);
        weaponReloadHandler?.SetHandIK(weaponHandIK);
        weaponReloadHandler?.SetAttachPoints(
            attachPoints?.Get("ReloadGrab"),
            attachPoints?.Get("MagHand"));

        Debug.Log($"Gathered from weapon {weapon.name} — " +
            $"Cant:{weaponCant != null} " +
            $"Recoil:{weaponRecoil != null} " +
            $"ADS:{weaponADS != null}");
    }

    // Things that never change regardless of weapon
    void WireStatic()
    {
        // Fixes arms/hands clipping through the camera at smaller character scales -
        // see the tooltip on armSafeNearClipPlane above for why.
        if (fpCamera != null)
            fpCamera.nearClipPlane = armSafeNearClipPlane;

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
        characterAnimation?.SetWeaponFrom(weapon);
        weaponHandIK?.SetGripTargets(weapon.rightHandGrip, weapon.leftHandGrip);

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

    /// <summary>Called by WeaponDrop after it deactivates the held weapon, so the player
    /// actually goes back to being unarmed instead of the Animator/hand IK still pointing
    /// at whatever grip transform the (now-inactive) weapon last had. Previously WeaponDrop
    /// only cleared PlayerSetup.activeWeapon directly, which skipped all of this.</summary>
    public void UnequipWeapon()
    {
        if (activeWeapon != null && playerHUD != null)
        {
            activeWeapon.onAmmoChanged -= playerHUD.UpdateAmmo;
            activeWeapon.onReloadStart -= playerHUD.ShowReloading;
            activeWeapon.onReloadEnd -= playerHUD.HideReloading;
        }

        activeWeapon = null;
        characterAnimation?.SetWeaponFrom(null); // -> Unarmed
        weaponHandIK?.SetGripTargets(null, null); // hands stop reaching for a grip that no longer exists

        weaponCant = null;
        weaponRecoil = null;
        weaponADS = null;
        weaponSway = null;
        lowerWeapon = null;
        weaponReloadHandler = null;

        if (cameraRecoil != null) cameraRecoil.weaponADS = null;
        if (cameraLean != null) { cameraLean.weaponCant = null; cameraLean.weaponHolder = null; }
        if (playerMovement != null) playerMovement.weaponCant = null;

        if (playerHUD != null) playerHUD.UpdateAmmo(0, 0);
        if (ammoHUD != null) ammoHUD.weapon = null;
    }
}