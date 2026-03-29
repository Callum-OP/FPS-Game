using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;

public class WeaponController : MonoBehaviour
{
    public Camera fpCamera;
    public GameObject bulletPrefab;
    public Transform muzzlePoint;

    [Header("Ammo")]
    public float fireRate = 0.1f;
    public int maxAmmo = 30;
    public float reloadTime = 1.5f;
    public bool isAutomatic = false;

    [Header("Shotgun")]
    public bool isShotgun = false;
    public int pelletCount = 8;
    public float spreadAngle = 10f;

    protected float nextFireTime;

    [Header("Recoil")]
    public CameraRecoil cameraRecoil;
    public WeaponRecoil weaponRecoil;

    [Header("Audio")]
    public AudioClip gunshotClip;
    public AudioClip reloadClip;
    public AudioClip emptyClickClip;

    public CasingEjector casingEjector;

    // HUD
    public System.Action<int, int> onAmmoChanged;
    public System.Action onReloadStart;
    public System.Action onReloadEnd;

    private InputAction fireAction;
    private InputAction reloadAction;
    private int currentAmmo;
    private bool isReloading;

    void Awake()
    {
        // Auto find camera
        if (fpCamera == null) fpCamera = Camera.main;

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

        AudioManager.Instance?.Play(gunshotClip);

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
        // Spawn several bullets like shotgun pellets
        for (int i = 0; i < pelletCount; i++)
        {
            // Same spawn position and direction as normal bullet
            Vector3 spawnPos = muzzlePoint.position + muzzlePoint.forward * 0.5f;
            Vector3 aimDir = (targetPoint - fpCamera.transform.position).normalized;

            // Add random spread on top
            aimDir += new Vector3(
                Random.Range(-spreadAngle, spreadAngle) * 0.01f,
                Random.Range(-spreadAngle, spreadAngle) * 0.01f,
                0f);
            aimDir.Normalize();

            // Spawn bullet
            GameObject bullet = Instantiate(bulletPrefab, spawnPos, muzzlePoint.rotation);
            bullet.transform.forward = aimDir;

            Rigidbody rb = bullet.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.useGravity = false;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.linearVelocity = aimDir * 80f;
            }

            // Ignore player colliders
            Collider bulletCol = bullet.GetComponent<Collider>();
            if (bulletCol != null)
            {
                GameObject player = GameObject.FindGameObjectWithTag("Player");
                if (player != null)
                    foreach (Collider col in player.GetComponentsInChildren<Collider>())
                        Physics.IgnoreCollision(bulletCol, col);
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
        Debug.Log("Reloading...");

        yield return new WaitForSeconds(reloadTime);

        currentAmmo = maxAmmo;
        isReloading = false;
        onReloadEnd?.Invoke();
        onAmmoChanged?.Invoke(currentAmmo, maxAmmo);
        AudioManager.Instance?.Play(reloadClip);
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