using UnityEngine;

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

    private Vector3 currentRotation;
    private Vector3 targetRotation;

    void Update()
    {
        // Decay target back to zero
        targetRotation = Vector3.Lerp(targetRotation, Vector3.zero,
            recoverySpeed * Time.deltaTime);

        // Smoothly apply to current rotation
        currentRotation = Vector3.Slerp(currentRotation, targetRotation,
            recoilSpeed * Time.deltaTime);

        // Apply on top of whatever the camera is already doing
        transform.localRotation = Quaternion.Euler(currentRotation)
            * transform.localRotation;
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