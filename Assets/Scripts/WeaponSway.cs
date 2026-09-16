using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Computes mouse sway and hands it to WeaponADS, which owns the weapon root transform
/// and composes every offset in one place. The old undo-last-then-add-new trick in
/// LateUpdate worked around the fight with ADS, but it still moved the weapon AFTER the
/// Animator's IK pass had already solved the hands against the earlier position - a
/// one-frame mismatch that reads as the hands jittering/sliding off the gun while
/// looking around and walking. Pushing the offset instead removes both problems.
/// </summary>
[DefaultExecutionOrder(-90)]
public class WeaponSway : MonoBehaviour
{
    [Header("Sway Settings")]
    public float swayAmount = 0.02f;
    public float swaySmooth = 6f; // How fast it recovers
    public float maxSwayAmount = 0.06f; // Limit that stops over-swing

    [Header("ADS Reduction")]
    public WeaponADS weaponADS;
    public float adsSwayMultiplier = 0.2f;

    private InputAction lookAction;
    private Vector3 currentSwayOffset;

    void Awake()
    {
        lookAction = new InputAction("SwayLook", binding: "<Mouse>/delta");
        lookAction.Enable();
    }

    void Start()
    {
        if (weaponADS == null) weaponADS = GetComponent<WeaponADS>();
        if (weaponADS == null) weaponADS = GetComponentInParent<WeaponADS>();
    }

    void Update()
    {
        Vector2 lookInput = lookAction.ReadValue<Vector2>();

        float multiplier = (weaponADS != null && weaponADS.IsAiming())
            ? adsSwayMultiplier : 1f;

        float swayX = Mathf.Clamp(-lookInput.x * swayAmount * multiplier,
            -maxSwayAmount, maxSwayAmount);
        float swayY = Mathf.Clamp(-lookInput.y * swayAmount * multiplier,
            -maxSwayAmount, maxSwayAmount);

        Vector3 targetSwayOffset = new Vector3(swayX, swayY, 0f);
        currentSwayOffset = Vector3.Lerp(currentSwayOffset, targetSwayOffset, swaySmooth * Time.deltaTime);

        weaponADS?.SetSwayOffset(currentSwayOffset);
    }

    void OnDestroy() => lookAction.Disable();
}