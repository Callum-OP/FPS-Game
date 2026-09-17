using UnityEngine;
using UnityEngine.InputSystem;

public class WeaponPickup : MonoBehaviour
{
    [Header("Which held weapon this pickup gives the player")]
    public GameObject heldWeaponPrefab;

    /// <summary>True on any frame the player is standing inside ANY WeaponPickup's
    /// trigger. PlayerAllySwap checks this so a dropped weapon under your feet always
    /// takes priority over swapping guns with a nearby teammate on the same key.</summary>
    public static bool AnyPlayerInRangeThisFrame;

    private bool playerInRange = false;
    private PlayerSetup playerSetup;
    private InputAction pickupAction;

    void Awake()
    {
        pickupAction = new InputAction("Pickup", binding: PlayerInputMap.Pickup);
        pickupAction.Enable();
    }

    void Update()
    {
        if (playerInRange) AnyPlayerInRangeThisFrame = true;

        // Pickup has its own key (1) now, separate from reload (R), so no sharing/
        // suppression logic is needed any more.
        if (playerInRange && pickupAction.WasPressedThisFrame())
            Pickup();
    }

    void Pickup()
    {
        if (playerSetup == null) return;

        var inventory = playerSetup.GetComponent<WeaponInventory>();

        GameObject held = Instantiate(heldWeaponPrefab, playerSetup.fpCamera.transform);
        held.name = heldWeaponPrefab.name; // keep the prefab name - the slot and the world-drop lookup both match on it
        held.transform.localPosition = Vector3.zero;
        held.transform.localRotation = Quaternion.identity;
        held.SetActive(true);

        if (inventory != null)
        {
            // Only whatever was already in THIS weapon's slot gets dropped - the rifle
            // stays on your back when you pick up a pistol. That's the whole point of
            // carrying two.
            GameObject displaced = inventory.Store(held, equipImmediately: true, sourcePrefab: heldWeaponPrefab);
            if (displaced != null)
            {
                playerSetup.GetComponent<WeaponDrop>()?.DropSpecific(displaced);
                inventory.Remove(displaced);
            }
        }
        else
        {
            playerSetup.GetComponent<WeaponDrop>()?.Drop();
            playerSetup.WireWeapon(held.GetComponent<WeaponController>());
        }

        Destroy(gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        playerInRange = true;
        playerSetup   = other.GetComponent<PlayerSetup>();
    }

    void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        playerInRange = false;
        playerSetup   = null;
    }

    void OnDestroy() => pickupAction.Disable();
}