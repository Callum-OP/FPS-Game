using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Drops whatever the player is holding. Which pickup appears in the world comes from the weapon itself:
/// WeaponController.pickupPrefab (guns and melee weapons alike). There is nothing to register here when you make a new weapon -
/// give the weapon prefab its Pickup Prefab and it just works. A grenade in hand is dropped the same way from GrenadeController.pickupPrefab.
/// </summary>
public class WeaponDrop : MonoBehaviour
{
    public PlayerSetup playerSetup;

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
        if (playerSetup == null) return;

        if (playerSetup.activeWeapon == null)
        {
            // Nothing in the hands but maybe a grenade: drop one of those.
            var grenades = GetComponent<GrenadeController>();
            if (grenades != null && grenades.DropHeldGrenade()) return;
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

        var weapon = heldWeapon.GetComponent<WeaponController>();
        GameObject worldPrefab = weapon != null ? weapon.pickupPrefab : null;
        if (worldPrefab == null)
            Debug.LogWarning($"Drop: {heldWeapon.name} has no Pickup Prefab (WeaponController > Pickup), so nothing can be spawned and the weapon is just removed.");

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

    void OnDestroy() => dropAction.Disable();
}