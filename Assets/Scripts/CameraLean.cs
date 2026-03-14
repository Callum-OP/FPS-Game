using UnityEngine;

public class CameraLean : MonoBehaviour
{
    [Header("Lean Settings")]
    public float leanAngle = 35f; // Tilt
    public float leanYAngle = 10f;
    public float leanShift = 0.5f;
    public float leanSpeed = 6f;

    [Header("References")]
    public WeaponCant weaponCant;
    public Transform weaponHolder;

    private Vector3 originalCameraPos;
    private Vector3 originalWeaponPos;
    private float currentLean = 0f;

    void Start()
    {
        originalCameraPos = transform.localPosition;
        if (weaponHolder != null)
            originalWeaponPos = weaponHolder.localPosition;
    }

    void Update()
    {
        float cantFraction = weaponCant != null ? weaponCant.GetCantFraction() : 0f;

        currentLean = Mathf.Lerp(currentLean, leanAngle * cantFraction,
            leanSpeed * Time.deltaTime);

        float leanFraction = leanAngle != 0 ? currentLean / leanAngle : 0f;

        // Diagonal rotation
        float currentX = transform.localEulerAngles.x;
        transform.localRotation = Quaternion.Euler(
            currentX,
            -leanYAngle  *  leanFraction, // Rotate
            currentLean); // Tilt

        // Shift camera sideways
        transform.localPosition = Vector3.Lerp(
            transform.localPosition,
            originalCameraPos + new Vector3(-leanShift * leanFraction, 0f, 0f),
            leanSpeed * Time.deltaTime);
    }
}