using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;

public class WeaponController : MonoBehaviour
{
    public Camera fpCamera;
    public GameObject bulletPrefab;
    public Transform muzzlePoint;

    [Header("Hand IK Grips")]
    [Tooltip("Empty child transform positioned/oriented at this weapon's grip - the right hand IK target.")]
    public Transform rightHandGrip;
    [Tooltip("Empty child transform at the foregrip/forward hand spot - leave empty for one-handed weapons.")]
    public Transform leftHandGrip;

    [Header("Ammo")]
    public float fireRate = 0.1f;
    public int maxAmmo = 30;
    public float reloadTime = 1.5f;
    [Tooltip("How many times to repeat the reload sequence, e.g. a shotgun loading one shell at a time instead of a single mag swap. Each cycle refills an equal share of the missing ammo and plays the full reload visuals/animation again - set to 1 for a normal single-mag reload.")]
    public int reloadCycles = 1;
    public bool isAutomatic = false;

    [Header("Shotgun")]
    public bool isShotgun = false;
    public int pelletCount = 8;
    public float spreadAngle = 10f;

    protected float nextFireTime;

    [Header("Recoil")]
    public CameraRecoil cameraRecoil;
    public WeaponRecoil weaponRecoil;

    [Header("Camera Recoil Override")]
    public float camRecoilX = 3f; // Vertical
    public float camRecoilY = 0.5f; // Horizontal
    public float camRecoilZ = 0.5f; // Tilt

    [Header("Audio")]
    public AudioClip gunshotClip;
    public AudioClip reloadClip;
    public AudioClip emptyClickClip;

    public CasingEjector casingEjector;

    [Header("Slide")]
    public GunSlide gunSlide;

    [Header("Reload Visuals")]
    [Tooltip("Optional - drives the procedural mag-out/mag-in hand movement and mag drop/pickup props. Leave unassigned (e.g. on the shotgun) to fall back to a plain reload with no hand repositioning.")]
    public WeaponReloadHandler reloadHandler;

    // HUD
    public System.Action<int, int> onAmmoChanged;
    public System.Action onReloadStart;
    public System.Action onReloadEnd;

    private InputAction fireAction;
    private InputAction reloadAction;
    private int currentAmmo;
    private bool isReloading;
    CharacterAnimationDriver characterAnimation;

    [Header("Muzzle Flash")]
    public ParticleSystem muzzleFlash;

    void Awake()
    {
        ResolveHandGrips();

        // Auto find camera
        if (fpCamera == null) fpCamera = Camera.main;

        // Configure camera recoil for this weapon
        if (cameraRecoil != null)
            cameraRecoil.Configure(camRecoilX, camRecoilY, camRecoilZ);

        // Auto find recoil scripts if not assigned
        if (cameraRecoil == null)
            cameraRecoil = fpCamera.GetComponent<CameraRecoil>();
        if (weaponRecoil == null)
            weaponRecoil = GetComponentInChildren<WeaponRecoil>();

        fireAction   = new InputAction("Fire",   binding: "<Mouse>/leftButton");
        reloadAction = new InputAction("Reload", binding: "<Keyboard>/r");
        fireAction.Enable();
        reloadAction.Enable();

        currentAmmo = maxAmmo;

        if (gunSlide == null)
            gunSlide = GetComponentInChildren<GunSlide>();

        if (reloadHandler == null)
            reloadHandler = GetComponent<WeaponReloadHandler>();

        characterAnimation = FindAnyObjectByType<CharacterAnimationDriver>();
    }

    void ResolveHandGrips()
    {
        if (rightHandGrip == null)
            rightHandGrip = FindChildByName("RightHandGrip");
        if (leftHandGrip == null)
            leftHandGrip = FindChildByName("LeftHandGrip");

        if (rightHandGrip == null)
            Debug.LogWarning($"{name} has no RightHandGrip child. Add a marked child transform or assign WeaponController.rightHandGrip.", this);
    }

    Transform FindChildByName(string childName)
    {
        foreach (var child in GetComponentsInChildren<Transform>(true))
        {
            if (child.name == childName)
                return child;
        }

        return null;
    }

    void Update()
    {
        if (isReloading) return;

        if (reloadAction.WasPressedThisFrame() && currentAmmo < maxAmmo)
        {
            StartCoroutine(Reload());
            return;
        }

        if (currentAmmo <= 0)
        {
            // Play empty click when trying to fire
            bool tryingToFire = isAutomatic
                ? fireAction.ReadValue<float>() > 0.5f
                : fireAction.WasPressedThisFrame();
            if (tryingToFire)
                AudioManager.Instance?.Play(emptyClickClip);

            StartCoroutine(Reload());
            return;
        }

        bool shouldFire = isAutomatic
            ? fireAction.ReadValue<float>() > 0.5f
            : fireAction.WasPressedThisFrame();

        if (shouldFire && Time.time >= nextFireTime)
        {
            nextFireTime = Time.time + fireRate;
            Shoot();
        }
    }

    void Shoot()
    {
        if (bulletPrefab == null) { Debug.LogError("bulletPrefab not assigned!"); return; }
        if (muzzlePoint == null)  { Debug.LogError("muzzlePoint not assigned!");  return; }

        currentAmmo--;
        onAmmoChanged?.Invoke(currentAmmo, maxAmmo);
        characterAnimation?.PlayShoot();

        AudioManager.Instance?.Play(gunshotClip);

        // If there is a gun slide
        gunSlide?.OnFire();

        // Apply weapon recoil first, then camera recoil scales with it
        weaponRecoil?.ApplyRecoil();
        cameraRecoil?.ApplyRecoil();

        // Get crosshair target
        Ray ray = fpCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 targetPoint = Physics.Raycast(ray, out RaycastHit hit, 300f)
            ? hit.point
            : ray.GetPoint(300f);

        if (isShotgun)
            FireShotgun(targetPoint);
        else
            FireBullet(targetPoint);

        casingEjector?.Eject();

        muzzleFlash?.Play();
    }

    void FireBullet(Vector3 targetPoint)
    {
        Vector3 spawnPos = muzzlePoint.position + muzzlePoint.forward * 0.5f;
        GameObject bullet = Instantiate(bulletPrefab, spawnPos, muzzlePoint.rotation);
        Vector3 aimDir = (targetPoint - fpCamera.transform.position).normalized;
        bullet.transform.forward = aimDir;

        SetupBullet(bullet, aimDir);
        IgnorePlayerColliders(bullet);
    }

    void FireShotgun(Vector3 targetPoint)
    {
        // Arrange pellets in a circle around muzzle point
        for (int i = 0; i < pelletCount; i++)
        {
            // Space pellets evenly in a circle
            float angle = (360f / pelletCount) * i;
            float radius = 0.05f; // How spread out the spawn points are

            Vector3 offset = new Vector3(
                Mathf.Cos(angle * Mathf.Deg2Rad) * radius,
                Mathf.Sin(angle * Mathf.Deg2Rad) * radius,
                0f);

            Vector3 spawnPos = muzzlePoint.position
                + muzzlePoint.forward * 0.5f
                + muzzlePoint.TransformDirection(offset);

            // Base aim direction toward crosshair
            Vector3 aimDir = (targetPoint - fpCamera.transform.position).normalized;

            // Add directional spread per pellet
            aimDir += new Vector3(
                Random.Range(-spreadAngle, spreadAngle) * 0.01f,
                Random.Range(-spreadAngle, spreadAngle) * 0.01f,
                0f);
            aimDir.Normalize();

            GameObject bullet = Instantiate(bulletPrefab, spawnPos, muzzlePoint.rotation);
            bullet.transform.forward = aimDir;

            Rigidbody rb = bullet.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.useGravity = false;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.linearVelocity = aimDir * 80f;
            }

            Collider bulletCol = bullet.GetComponent<Collider>();
            if (bulletCol != null)
            {
                GameObject player = GameObject.FindGameObjectWithTag("Player");
                if (player != null)
                    foreach (Collider col in player.GetComponentsInChildren<Collider>())
                        Physics.IgnoreCollision(bulletCol, col);

                foreach (Collider col in fpCamera.GetComponentsInChildren<Collider>())
                    Physics.IgnoreCollision(bulletCol, col);

                // Ignore other pellets from this shot
                foreach (GameObject other in GameObject.FindGameObjectsWithTag("Bullet"))
                {
                    Collider otherCol = other.GetComponent<Collider>();
                    if (otherCol != null)
                        Physics.IgnoreCollision(bulletCol, otherCol);
                }
            }
        }
    }

    void SetupBullet(GameObject bullet, Vector3 aimDir)
    {
        Rigidbody rb = bullet.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearVelocity = aimDir * 80f;
        }
    }

    void IgnorePlayerColliders(GameObject bullet)
    {
        Collider bulletCol = bullet.GetComponent<Collider>();
        if (bulletCol == null) return;

        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player != null)
            foreach (Collider col in player.GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(bulletCol, col);
    }

    IEnumerator Reload()
    {
        isReloading = true;
        onReloadStart?.Invoke();

        int cycles = Mathf.Max(1, reloadCycles);
        int ammoNeeded = maxAmmo - currentAmmo;
        int ammoPerCycle = Mathf.Max(1, Mathf.CeilToInt((float)ammoNeeded / cycles));

        for (int i = 0; i < cycles && currentAmmo < maxAmmo; i++)
        {
            characterAnimation?.PlayReload();
            reloadHandler?.PlayReload(reloadTime);

            yield return new WaitForSeconds(reloadTime);

            currentAmmo = Mathf.Min(maxAmmo, currentAmmo + ammoPerCycle);
            onAmmoChanged?.Invoke(currentAmmo, maxAmmo);
            AudioManager.Instance?.Play(reloadClip);
        }

        isReloading = false;
        onReloadEnd?.Invoke();
    }

    public int   GetCurrentAmmo()  => currentAmmo;
    public int   GetMaxAmmo()      => maxAmmo;
    public bool  GetIsReloading()  => isReloading;

    void OnDestroy()
    {
        fireAction.Disable();
        reloadAction.Disable();
    }
}