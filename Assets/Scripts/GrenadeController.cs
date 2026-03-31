using UnityEngine;
using UnityEngine.InputSystem;

public class GrenadeController : MonoBehaviour
{
    [Header("Grenade")]
    public GameObject grenadePrefab;
    public Transform throwPoint;
    public float throwForce = 15f;
    public float throwUpward = 5f;
    public int maxGrenades = 3;

    private int currentGrenades;
    private InputAction throwAction;

    void Awake()
    {
        throwAction = new InputAction("Throw", binding: "<Keyboard>/j");
        throwAction.Enable();
        currentGrenades = maxGrenades;
    }

    void Update()
    {
        if (throwAction.WasPressedThisFrame() && currentGrenades > 0)
            ThrowGrenade();
    }

    void ThrowGrenade()
    {
        if (grenadePrefab == null || throwPoint == null) return;

        currentGrenades--;

        GameObject grenade = Instantiate(grenadePrefab,
            throwPoint.position, throwPoint.rotation);

        Rigidbody rb = grenade.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = throwPoint.forward * throwForce
                + Vector3.up * throwUpward;
            rb.AddTorque(Random.insideUnitSphere * 5f, ForceMode.Impulse);
        }

        Debug.Log($"Grenade thrown — {currentGrenades} remaining");
    }

    public int GetCurrentGrenades() => currentGrenades;
    public int GetMaxGrenades()     => maxGrenades;

    void OnDestroy() => throwAction.Disable();
}