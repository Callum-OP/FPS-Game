using UnityEngine;
using UnityEngine.InputSystem;

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
    private Vector3 initialPosition;

    void Awake()
    {
        lookAction = new InputAction("SwayLook", binding: "<Mouse>/delta");
        lookAction.Enable();
    }

    void Start()
    {
        initialPosition = Vector3.zero;
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

        // Sway offsets from zero
        Vector3 targetSwayPos = new Vector3(swayX, swayY, 0f);

        transform.localPosition = Vector3.Lerp(
            transform.localPosition, targetSwayPos, swaySmooth * Time.deltaTime);
    }

    void OnDestroy() => lookAction.Disable();
}