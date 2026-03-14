using UnityEngine;
using UnityEngine.InputSystem;

public class WeaponCant : MonoBehaviour
{
    [Header("Cant Settings")]
    public float cantAngle = 35f;
    public float cantSpeed = 8f;

    [Header("ADS")]
    public WeaponADS weaponADS;
    public float adsCantMultiplier = 0.5f;  // Reduce cant angle when aiming

    private InputAction cantLeftAction;
    private InputAction cantRightAction;
    private float currentCantAngle = 0f;
    private float targetCantAngle  = 0f;

    void Awake()
    {
        cantLeftAction  = new InputAction("CantLeft",  binding: "<Keyboard>/q");
        cantRightAction = new InputAction("CantRight", binding: "<Keyboard>/e");
        cantLeftAction.Enable();
        cantRightAction.Enable();
    }

    void Update()
    {
        bool cantingLeft  = cantLeftAction.ReadValue<float>()  > 0.5f;
        bool cantingRight = cantRightAction.ReadValue<float>() > 0.5f;

        if (cantingLeft && !cantingRight)
            targetCantAngle = cantAngle;
        else if (cantingRight && !cantingLeft)
            targetCantAngle = -cantAngle;
        else
            targetCantAngle = 0f;

        currentCantAngle = Mathf.Lerp(currentCantAngle, targetCantAngle,
            cantSpeed * Time.deltaTime);

        // Reduce physical cant rotation when aiming
        bool isAiming = weaponADS != null && weaponADS.IsAiming();
        float appliedAngle = isAiming
            ? currentCantAngle * adsCantMultiplier
            : currentCantAngle;

        transform.localRotation = Quaternion.Euler(0f, 0f, appliedAngle);
    }

    public float GetCantFraction() =>
        cantAngle != 0 ? currentCantAngle / cantAngle : 0f;

    void OnDestroy()
    {
        cantLeftAction.Disable();
        cantRightAction.Disable();
    }
}