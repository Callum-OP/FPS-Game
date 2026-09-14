using UnityEngine;
using UnityEngine.InputSystem;

// NOTE: This lives on the same GameObject/transform as WeaponADS (both are on the
// weapon root). WeaponADS's Update() sets transform.localPosition/localRotation
// outright each frame to move between hip and ADS poses. If this script also wrote
// transform.localPosition outright in its own Update(), the two would stomp each other
// every frame in whatever order Unity happens to run them - which is exactly what made
// aiming look "bouncy"/less attached: sway would periodically overwrite the zoomed-in
// ADS position outright instead of nudging it. Fixed by making sway purely additive
// (undo-last-then-add-new, like WeaponReloadHandler's lift) and applying it in
// LateUpdate, which Unity always runs after every script's Update() - so it always
// composes on top of whatever ADS just set, rather than racing it.
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
    private Vector3 currentSwayOffset; // smoothed offset, recovers toward zero
    private Vector3 appliedSwayOffset; // what's currently added onto transform.localPosition

    void Awake()
    {
        lookAction = new InputAction("SwayLook", binding: "<Mouse>/delta");
        lookAction.Enable();
    }

    void LateUpdate()
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

        // Undo last frame's sway, then add this frame's - stays additive on top of
        // whatever WeaponADS's Update() already set this frame instead of overwriting it.
        transform.localPosition += currentSwayOffset - appliedSwayOffset;
        appliedSwayOffset = currentSwayOffset;
    }

    void OnDestroy() => lookAction.Disable();
}