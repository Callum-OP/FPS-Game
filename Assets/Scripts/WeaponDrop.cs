using UnityEngine;
using UnityEngine.InputSystem;

public class WeaponDrop : MonoBehaviour
{
    public PlayerSetup playerSetup;

    [Header("World prefabs")]
    public GameObject Mono19WorldPrefab;
    public GameObject BreacherM4WorldPrefab;
    public GameObject HummingbirdWorldPrefab;
    public GameObject ARWorldPrefab;
    public GameObject ARScopedWorldPrefab;

    private InputAction dropAction;

    void Awake()
    {
        dropAction = new InputAction("Drop", binding: "<Keyboard>/g");
        dropAction.Enable();
    }

    void Update()
    {
        if (dropAction.WasPressedThisFrame())
            Drop();
    }

    public void Drop()
    {
        if (playerSetup == null || playerSetup.activeWeapon == null)
        {
            Debug.Log("Drop: no active weapon");
            return;
        }

        WeaponController weapon = playerSetup.activeWeapon;
        Debug.Log($"Dropping: {weapon.gameObject.name}");

        GameObject worldPrefab = GetWorldPrefab(weapon.gameObject.name);
        Debug.Log($"World prefab found: {worldPrefab != null}");

        if (worldPrefab != null)
        {
            Vector3 dropPos = transform.position + Vector3.up * 0.5f;
            GameObject world = Instantiate(worldPrefab, dropPos, Quaternion.identity);
            Debug.Log($"Spawned world object at {dropPos}");

            Rigidbody rb = world.GetComponent<Rigidbody>();
            if (rb != null)
                rb.linearVelocity = transform.forward * 2f + Vector3.up * 1f;
        }

        weapon.gameObject.SetActive(false);
        playerSetup.UnequipWeapon();
    }

    GameObject GetWorldPrefab(string weaponName)
    {
        if (weaponName.Contains("Mono19")) return Mono19WorldPrefab;
        if (weaponName.Contains("BreacherM4")) return BreacherM4WorldPrefab;
        if (weaponName.Contains("Hummingbird")) return HummingbirdWorldPrefab;
        // Check ARScoped before AR - "ARScoped" also contains "AR", so the more
        // specific name has to be matched first or it's unreachable.
        if (weaponName.Contains("ARScoped")) return ARScopedWorldPrefab;
        if (weaponName.Contains("AR")) return ARWorldPrefab;
        return null;
    }

    void OnDestroy() => dropAction.Disable();
}