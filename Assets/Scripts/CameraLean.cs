using UnityEngine;

/// <summary>
/// Computes the lean and pushes it into PlayerMovement, which is now the single owner
/// of the camera transform. It no longer writes transform.localRotation/localPosition
/// itself - that's what used to fight PlayerMovement's pitch and CameraRecoil's kick
/// every frame (and it read localEulerAngles.x back in, which wraps to ~300 degrees
/// when looking up, so the fight got worse exactly when looking up).
/// </summary>
[DefaultExecutionOrder(-160)]
public class CameraLean : MonoBehaviour
{
    [Header("Lean Settings")]
    public float leanAngle = 25f; // Tilt
    public float leanYAngle = 10f;
    public float leanShift = 0.5f;
    public float leanSpeed = 6f;

    [Header("References")]
    public WeaponCant weaponCant;
    public Transform weaponHolder; // kept for Inspector compatibility, no longer written to
    [Tooltip("Auto-found on the player root if left empty.")]
    public PlayerMovement playerMovement;

    private float currentLean = 0f;
    private float currentShift = 0f;

    void Start()
    {
        if (playerMovement == null) playerMovement = GetComponentInParent<PlayerMovement>();
        if (playerMovement == null)
            Debug.LogWarning($"{name}: no PlayerMovement found in parents - camera lean will not be applied.", this);
    }

    void Update()
    {
        float cantFraction = weaponCant != null ? weaponCant.GetCantFraction() : 0f;

        currentLean = Mathf.Lerp(currentLean, leanAngle * cantFraction,
            leanSpeed * Time.deltaTime);

        float leanFraction = leanAngle != 0 ? currentLean / leanAngle : 0f;

        currentShift = Mathf.Lerp(currentShift, -leanShift * leanFraction,
            leanSpeed * Time.deltaTime);

        playerMovement?.SetCameraLean(currentLean, -leanYAngle * leanFraction, currentShift);
    }
}