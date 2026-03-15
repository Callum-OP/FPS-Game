using UnityEngine;
using UnityEngine.InputSystem;

public class WeaponCant : MonoBehaviour
{
    [Header("Cant Settings")]
    public float cantAngle = 35f;
    public float cantSpeed = 8f;

    [Header("ADS")]
    public WeaponADS weaponADS;
    public float adsCantMultiplier = 0.5f; // Reduce cant angle when aiming

    private InputAction cantLeftAction;
    private InputAction cantRightAction;
    private float currentCantAngle = 0f;
    private float targetCantAngle  = 0f;

    // Toggle state
    private bool cantedLeft  = false;
    private bool cantedRight = false;

    void Awake()
    {
        cantLeftAction  = new InputAction("CantLeft",  binding: "<Keyboard>/q");
        cantRightAction = new InputAction("CantRight", binding: "<Keyboard>/e");
        cantLeftAction.Enable();
        cantRightAction.Enable();
    }

    void Update()
    {
        // Toggle left
        if (cantLeftAction.WasPressedThisFrame())
        {
            if (cantedLeft)
            {
                cantedLeft = false;
            }
            else
            {
                cantedLeft  = true;
                cantedRight = false;
            }
        }

        // Toggle right
        if (cantRightAction.WasPressedThisFrame())
        {
            if (cantedRight)
            {
                cantedRight = false;
            }
            else
            {
                cantedRight = true;
                cantedLeft  = false;
            }
        }

        if (cantedLeft)
            targetCantAngle = cantAngle;
        else if (cantedRight)
            targetCantAngle = -cantAngle;
        else
            targetCantAngle = 0f;

        currentCantAngle = Mathf.Lerp(currentCantAngle, targetCantAngle,
            cantSpeed * Time.deltaTime);

        // Reduce physical cant when aiming
        bool isAiming  = weaponADS != null && weaponADS.IsAiming();
        float appliedAngle = isAiming
            ? currentCantAngle * adsCantMultiplier
            : currentCantAngle;

        transform.localRotation = Quaternion.Euler(0f, 0f, appliedAngle);
    }

    public float GetCantFraction() =>
        cantAngle != 0 ? currentCantAngle / cantAngle : 0f;

    // Returns true if any cant is active
    public bool IsCanted() => cantedLeft || cantedRight;

    void OnDestroy()
    {
        cantLeftAction.Disable();
        cantRightAction.Disable();
    }
}