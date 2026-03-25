using UnityEngine;
using UnityEngine.InputSystem;

public class LowerWeapon : MonoBehaviour
{
    [Header("Positions")]
    public Vector3 loweredPosition = new Vector3(0f, -0.4f, 0.3f);  // Gun drops
    public Vector3 loweredRotation = new Vector3(30f, 0f, 0f); // Tilt down

    [Header("Settings")]
    public float lowerSpeed = 8f;
    public float raisedSpeed = 10f;
    public float fastWalkMultiplier = 1.5f; // Speed boost

    [Header("References")]
    public PlayerMovement playerMovement;
    public WeaponADS weaponADS;
    public WeaponController weaponController;

    private InputAction lowerAction;
    private InputAction fireAction;
    private InputAction aimAction;

    private bool isLowered = false;
    private Vector3 originalPosition;
    private Vector3 originalRotation;

    void Awake()
    {
        lowerAction = new InputAction("LowerWeapon", binding: "<Keyboard>/x");
        fireAction  = new InputAction("FireCheck",   binding: "<Mouse>/leftButton");

        lowerAction.Enable();
        fireAction.Enable();
    }

    void Start()
    {
        originalPosition = transform.localPosition;
        originalRotation = transform.localEulerAngles;
    }

    void Update()
    {
        // Toggle lower on X press
        if (lowerAction.WasPressedThisFrame())
            isLowered = !isLowered;

        // Cancel lowered if player fires or aims
        if (isLowered)
        {
            bool aiming = weaponADS != null && weaponADS.IsAiming();
            bool firing = fireAction.WasPressedThisFrame();

            if (aiming || firing)
                isLowered = false;
        }

        float speed = isLowered ? lowerSpeed : raisedSpeed;

        Vector3 targetPos = isLowered ? loweredPosition : originalPosition;
        Vector3 targetRot = isLowered ? loweredRotation : originalRotation;

        transform.localPosition = Vector3.Lerp(
            transform.localPosition, targetPos, speed * Time.deltaTime);

        transform.localRotation = Quaternion.Lerp(
            transform.localRotation,
            Quaternion.Euler(targetRot),
            speed * Time.deltaTime);

        // Apply speed boost to PlayerMovement
        if (playerMovement != null)
            playerMovement.SetSpeedMultiplier(isLowered ? fastWalkMultiplier : 1f);
    }

    public bool IsLowered() => isLowered;

    void OnDestroy()
    {
        lowerAction.Disable();
        fireAction.Disable();
    }
}