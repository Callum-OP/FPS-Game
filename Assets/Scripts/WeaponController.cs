using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;

public class WeaponController : MonoBehaviour
{
    public Camera fpCamera;
    public GameObject bulletPrefab;
    public Transform muzzlePoint;

    [Header("Ammo")]
    public int maxAmmo = 30;
    public float reloadTime = 1.5f;
    public bool isAutomatic = false;

    [Header("Recoil")]
    public CameraRecoil cameraRecoil;
    public WeaponRecoil weaponRecoil;

    public CasingEjector casingEjector;

    // HUD
    public System.Action<int, int> onAmmoChanged;
    public System.Action onReloadStart;
    public System.Action onReloadEnd;

    private InputAction fireAction;
    private InputAction reloadAction;
    private int currentAmmo;
    private bool isReloading;

    [Header("Audio")]
    public AudioClip gunshotClip;
    public AudioClip reloadClip;
    public AudioClip emptyClickClip;

    void Awake()
    {
        if (fpCamera == null) fpCamera = Camera.main;

        fireAction = new InputAction("Fire",   binding: "<Mouse>/leftButton");
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
            StartCoroutine(Reload());
            return;
        }

        bool shouldFire = isAutomatic
            ? fireAction.ReadValue<float>() > 0.5f
            : fireAction.WasPressedThisFrame();

        if (shouldFire)
            Shoot();
    }

    void Shoot()
    {
        if (bulletPrefab == null) { Debug.LogError("bulletPrefab not assigned!"); return; }
        if (muzzlePoint == null)  { Debug.LogError("muzzlePoint not assigned!");  return; }

        currentAmmo--;
        onAmmoChanged?.Invoke(currentAmmo, maxAmmo);

        // Gunshot sound
        AudioManager.Instance?.Play(gunshotClip);

        // Fire recoil on both camera and gun
        cameraRecoil?.ApplyRecoil();
        weaponRecoil?.ApplyRecoil();

        // Get crosshair target
        Ray ray = fpCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 targetPoint = Physics.Raycast(ray, out RaycastHit hit, 300f)
            ? hit.point
            : ray.GetPoint(300f);

        // Spawn bullet
        Vector3 spawnPos = muzzlePoint.position + muzzlePoint.forward * 0.5f;
        GameObject bullet = Instantiate(bulletPrefab, spawnPos, muzzlePoint.rotation);
        bullet.transform.forward = (targetPoint - spawnPos).normalized;

        // Spawn casing
        casingEjector.Eject();

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
        Debug.Log("Reloaded!");

        // Gunshot sound
        AudioManager.Instance?.Play(reloadClip);
    }

    public int  GetCurrentAmmo()  => currentAmmo;
    public int  GetMaxAmmo()      => maxAmmo;
    public bool GetIsReloading()  => isReloading;

    void OnDestroy()
    {
        fireAction.Disable();
        reloadAction.Disable();
    }
}