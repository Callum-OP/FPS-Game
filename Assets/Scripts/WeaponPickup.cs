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

    [Tooltip("Seconds after this pickup appears before it can be picked up. Stops a weapon you've just dropped or swapped away (which lands under your feet) being grabbed straight back by the same key press or a second one.")]
    public float pickupDelay = 0.75f;

    private float spawnTime;
    /// <summary>Seconds since this pickup appeared (AI uses it so they don't snatch a weapon the instant it's dropped).</summary>
    public float Age => Time.unscaledTime - spawnTime;
    private bool playerInRange = false;
    private PlayerSetup playerSetup;
    private InputAction pickupAction;

    void Awake()
    {
        pickupAction = new InputAction("Pickup", binding: PlayerInputMap.Pickup);
        pickupAction.Enable();
        spawnTime = Time.unscaledTime;
    }

    void Update()
    {
        // Not ready yet (just dropped): behave as if it isn't there, so the key press
        // can still go to an ally swap instead.
        if (Time.unscaledTime - spawnTime < pickupDelay) return;

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

        // Shared cooldown (also covers two pickups overlapping under your feet both
        // firing on the same key press - the first starts the cooldown, the second is refused).
        if (inventory != null)
        {
            if (!inventory.CanSwitch) return;
            inventory.StartSwitchCooldown();
        }

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