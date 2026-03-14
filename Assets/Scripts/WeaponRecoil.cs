using UnityEngine;

public class WeaponRecoil : MonoBehaviour
{
    [Header("Positional Recoil")]
    public float kickBack   = 0.05f; // How far gun moves back
    public float kickUp     = 0.06f; // How far gun moves up

    [Header("Rotational Recoil")]
    public float rotationKick = 18f; // How much gun rotates up

    [Header("Recovery")]
    public float recoverySpeed = 10f;

    [Header("ADS")]
    public WeaponADS weaponADS;
    public float adsRecoilMultiplier = 0.2f;

    private Vector3 originalPos;
    private Quaternion originalRot;
    private Vector3 targetPos;
    private Quaternion targetRot;

    void Start()
    {
        originalPos = transform.localPosition;
        originalRot = transform.localRotation;
        targetPos = originalPos;
        targetRot = originalRot;
    }

    void Update()
    {
        // Recover back to original
        targetPos = Vector3.Lerp(targetPos, originalPos,
            recoverySpeed * Time.deltaTime);
        targetRot = Quaternion.Lerp(targetRot, originalRot,
            recoverySpeed * Time.deltaTime);

        transform.localPosition = targetPos;
        transform.localRotation = targetRot;
    }

    public void ApplyRecoil()
    {
        float multiplier = (weaponADS != null && weaponADS.IsAiming())
            ? adsRecoilMultiplier : 1f;

        // Kick back and up
        targetPos -= new Vector3(0f, -kickUp, kickBack) * multiplier;

        // Rotate gun upward
        targetRot *= Quaternion.Euler(-rotationKick * multiplier, 0f, 0f);
    }
}