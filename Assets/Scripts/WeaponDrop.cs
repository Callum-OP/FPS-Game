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
    [Tooltip("The sledgehammer pickup (SledgehammerPickup prefab). Optional: the Sledgehammer prefab also carries its own pickup reference (MeleeWeapon.pickupPrefab), which is used when this is empty.")]
    public GameObject SledgehammerWorldPrefab;

    private InputAction dropAction;

    void Awake()
    {
        dropAction = new InputAction("Drop", binding: PlayerInputMap.Drop);
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

        DropSpecific(playerSetup.activeWeapon.gameObject);
    }

    /// <summary>Drops one particular held weapon - used when picking a new one up into a
    /// slot that's already full, so the other slot isn't disturbed.</summary>
    public void DropSpecific(GameObject heldWeapon)
    {
        if (heldWeapon == null) return;
        Debug.Log($"Dropping: {heldWeapon.name}");

        // A melee weapon carries its own pickup prefab (MeleeWeapon.pickupPrefab) - no need for a field per weapon here.
        var melee = heldWeapon.GetComponent<MeleeWeapon>();
        // Order: the slot on this component (so it can be overridden per player), then the pickup the weapon prefab itself
        // points at, then the name lookup.
        GameObject worldPrefab = GetWorldPrefab(heldWeapon.name);
        if (worldPrefab == null && melee != null) worldPrefab = melee.pickupPrefab;
        Debug.Log($"World prefab found: {worldPrefab != null}");
        if (worldPrefab == null)
            Debug.LogWarning(melee != null
                ? $"Drop: {heldWeapon.name} has no pickup assigned - drag SledgehammerPickup into 'Sledgehammer World Prefab' on WeaponDrop (or set 'pickupPrefab' on the weapon's MeleeWeapon; if it is set, the pickup prefab failed to import), so nothing can be spawned and the weapon is just removed."
                : $"Drop: no world prefab is assigned for {heldWeapon.name} (check the *WorldPrefab fields on WeaponDrop), so nothing can be spawned and the weapon is just removed.");

        if (worldPrefab != null)
        {
            Vector3 dropPos = transform.position + Vector3.up * 0.5f;
            GameObject world = Instantiate(worldPrefab, dropPos, Quaternion.identity);
            Debug.Log($"Spawned world object at {dropPos}");

            Rigidbody rb = world.GetComponent<Rigidbody>();
            if (rb != null)
                rb.linearVelocity = transform.forward * 2f + Vector3.up * 1f;
        }

        bool wasActive = playerSetup.activeWeapon != null
            && playerSetup.activeWeapon.gameObject == heldWeapon;

        GetComponent<WeaponInventory>()?.Remove(heldWeapon);
        Destroy(heldWeapon);
        if (wasActive) playerSetup.UnequipWeapon();
    }

    GameObject GetWorldPrefab(string weaponName)
    {
        if (weaponName.Contains("Sledgehammer")) return SledgehammerWorldPrefab;
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