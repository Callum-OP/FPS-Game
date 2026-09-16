using UnityEngine;

/// <summary>
/// Computes the recoil kick and pushes it into PlayerMovement (now the single owner of
/// the camera transform) instead of multiplying it onto transform.localRotation itself.
/// The old version compounded its own output back into the transform every frame and
/// was then partially erased by PlayerMovement/CameraLean, which is a large part of the
/// camera (and therefore gun/hand) jitter.
/// </summary>
[DefaultExecutionOrder(-160)]
public class CameraRecoil : MonoBehaviour
{
    [Header("Recoil")]
    public float recoilX = 3f; // Upward kick
    public float recoilY = 0.5f; // Sideways kick
    public float recoilZ = 0.5f; // Tilt kick

    [Header("Recovery")]
    public float recoverySpeed = 8f;
    public float recoilSpeed = 20f; // How fast the kick happens

    [Header("ADS")]
    public WeaponADS weaponADS;
    public float adsRecoilMultiplier = 0.4f;

    [Header("References")]
    [Tooltip("Auto-found on the player root if left empty.")]
    public PlayerMovement playerMovement;

    private Vector3 currentRotation;
    private Vector3 targetRotation;

    void Start()
    {
        if (playerMovement == null) playerMovement = GetComponentInParent<PlayerMovement>();
    }

    void Update()
    {
        // Decay target back to zero
        targetRotation = Vector3.Lerp(targetRotation, Vector3.zero,
            recoverySpeed * Time.deltaTime);

        // Smoothly approach it
        currentRotation = Vector3.Slerp(currentRotation, targetRotation,
            recoilSpeed * Time.deltaTime);

        playerMovement?.SetCameraRotationOffset(Quaternion.Euler(currentRotation));
    }

    public void Configure(float x, float y, float z)
    {
        recoilX = x;
        recoilY = y;
        recoilZ = z;
    }

    public void ApplyRecoil()
    {
        float multiplier = (weaponADS != null && weaponADS.IsAiming())
            ? adsRecoilMultiplier : 1f;

        targetRotation += new Vector3(
            -recoilX * multiplier,
            Random.Range(-recoilY, recoilY) * multiplier,
            Random.Range(-recoilZ, recoilZ) * multiplier);
    }
}