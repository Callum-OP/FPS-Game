using UnityEngine;
using UnityEngine.InputSystem;

public class WeaponADS : MonoBehaviour
{
    [Header("Positions")]
    public Vector3 hipPosition = new Vector3(0f, -0.1f, 0f);
    public Vector3 adsPosition = new Vector3(0f, 0.03f, 0.2f);

    [Header("Rotations")]
    public Vector3 hipRotation = new Vector3(0f, 0f, 35f);
    public Vector3 adsRotation = new Vector3(0f, 0f, 0f);

    [Header("Settings")]
    public float adsSpeed = 10f; // How fast it snaps to ADS
    public float hipSpeed = 7f; // Slightly slower return to hip

    [Header("FOV")]
    public Camera fpCamera;
    public float hipFOV = 60f;
    public float adsFOV = 50f; // Zoom in slightly when aiming

    [Header("Cant")]
    public WeaponCant weaponCant;

    private InputAction aimAction;
    private bool isAiming = false;

    void Awake()
    {
        if (fpCamera == null) fpCamera = Camera.main;

        // Use left shift to aim
        aimAction = new InputAction("Aim", binding: "<Keyboard>/leftShift");
        aimAction.Enable();
    }

    void Start()
    {
        // Start in hip position
        transform.localPosition = hipPosition;
        transform.localEulerAngles = hipRotation;
    }

    void Update()
    {
        isAiming = aimAction.ReadValue<float>() > 0.5f;

        Vector3 targetPos = isAiming ? adsPosition : hipPosition;
        Vector3 targetRot = isAiming ? adsRotation : hipRotation;
        float   speed     = isAiming ? adsSpeed    : hipSpeed;

        // Get cant fraction from WeaponCant
        float cantFraction = weaponCant != null ? weaponCant.GetCantFraction() : 0f;
        float cantAngle    = weaponCant != null ? weaponCant.cantAngle : 0f;

        // Reduces hip fire rotation when canting left
        if (cantFraction > 0f)
            targetRot.z = Mathf.Lerp(hipRotation.z, 0f, cantFraction);

        // For looking up and down
        transform.localPosition = Vector3.Lerp(
            transform.localPosition, targetPos, speed * Time.deltaTime);

        transform.localRotation = Quaternion.Lerp(
            transform.localRotation,
            Quaternion.Euler(targetRot),
            speed * Time.deltaTime);

        fpCamera.fieldOfView = Mathf.Lerp(
            fpCamera.fieldOfView,
            isAiming ? adsFOV : hipFOV,
            speed * Time.deltaTime);
    }

    public bool IsAiming() => isAiming;

    void OnDestroy() => aimAction.Disable();
}