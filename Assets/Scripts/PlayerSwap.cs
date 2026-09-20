using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Press the pickup key (1) near a teammate to trade weapons with them.
///
/// Uses the same key as picking up a dropped weapon rather than a new one, per request -
/// arbitrated so a weapon actually lying under your feet always wins: WeaponPickup marks
/// PlayerInRangeThisFrame while the player is in ITS trigger, and this checks that flag
/// (consumed in LateUpdate, which Unity always runs after every script's Update, so the
/// flag is guaranteed settled for the whole frame before this reads it) before acting on
/// the same keypress.
///
/// The swap trades PREFAB REFERENCES, not the live weapon instances - the ally's
/// EnemyWeapon already tracks the prefab it's holding, and WeaponInventory now does the
/// same for the player (see ActivePrefab). Trading references and re-instantiating fresh
/// copies on both sides is far simpler and more robust than trying to reparent and
/// re-enable a live weapon instance across the player/AI script boundary, at the minor
/// (arguably fitting) cost of both sides getting a full magazine after the swap.
///
/// SETUP: add to the Player root next to WeaponInventory.
/// </summary>
public class PlayerAllySwap : MonoBehaviour
{
    [Tooltip("How close a teammate has to be to swap weapons with.")]
    public float swapRange = 2.5f;

    public WeaponInventory inventory;
    public PlayerSetup playerSetup;

    InputAction pickupAction;

    void Awake()
    {
        pickupAction = PlayerInputMap.Make("AllySwapPickup", PlayerInputMap.Pickup);
    }

    void Start()
    {
        if (inventory == null) inventory = GetComponent<WeaponInventory>();
        if (playerSetup == null) playerSetup = GetComponent<PlayerSetup>();
    }

    // See the class comment for why this runs in LateUpdate rather than Update.
    void LateUpdate()
    {
        bool weaponPickupHandledThisKey = WeaponPickup.AnyPlayerInRangeThisFrame;
        WeaponPickup.AnyPlayerInRangeThisFrame = false; // reset for next frame - this is the sole consumer

        if (weaponPickupHandledThisKey) return;
        if (!pickupAction.WasPressedThisFrame()) return;
        if (inventory == null) return;
        if (!inventory.CanSwitch) return; // shared delay - see WeaponInventory.switchCooldown

        FriendlyAI nearest = FindNearestAlly();
        if (nearest != null) SwapWith(nearest);
        else Debug.Log($"{name}: no ally within {swapRange}m to swap weapons with.", this);
    }

    FriendlyAI FindNearestAlly()
    {
        FriendlyAI best = null;
        float bestDist = swapRange;
        foreach (var ally in FindObjectsByType<FriendlyAI>(FindObjectsSortMode.None))
        {
            if (ally == null || !ally.enabled) continue; // FriendlyAI disables itself on death
            float d = Vector3.Distance(transform.position, ally.transform.position);
            if (d <= bestDist) { bestDist = d; best = ally; }
        }
        return best;
    }

    void SwapWith(FriendlyAI ally)
    {
        var allyWeapon = ally.GetComponent<EnemyWeapon>();
        if (allyWeapon == null)
        {
            Debug.LogWarning($"{ally.name} has no EnemyWeapon component - can't swap with it.", ally);
            return;
        }

        GameObject allyPrefab = allyWeapon.weaponPrefab;
        GameObject playerPrefab = inventory.ActivePrefab;

        // The weapon the player started the game already holding was never given a
        // recorded prefab (nothing instantiated it through Store) UNLESS
        // WeaponInventory.startingWeaponPrefab is set - there's nothing to hand the ally
        // in that one specific case otherwise, so decline the swap rather than silently
        // doing half of it. Logged rather than silent so this misconfiguration is
        // actually visible instead of looking like the whole feature is broken.
        if (allyPrefab == null)
        {
            Debug.LogWarning($"{ally.name}'s EnemyWeapon has no weaponPrefab assigned - nothing to swap for.", ally);
            return;
        }
        if (playerPrefab == null)
        {
            Debug.LogWarning($"{name}'s active weapon has no recorded source prefab - " +
                $"if this is the weapon you started the scene holding, set WeaponInventory.startingWeaponPrefab " +
                $"in the Inspector to that weapon's prefab (e.g. AR.prefab) so a swap has something to hand over.", this);
            return;
        }

        // GIFT: the ally only has a pistol and you're holding a primary (and still have a
        // pistol of your own to fall back on) - hand your primary over for nothing in
        // return. They holster their pistol as a spare and carry your weapon.
        if (allyPrefab.name != playerPrefab.name
            && allyWeapon.SecondaryPrefab == null
            && inventory.SlotFor(allyPrefab) == WeaponInventory.Slot.Secondary
            && inventory.SlotFor(playerPrefab) == WeaponInventory.Slot.Primary
            && inventory.GetSlot(WeaponInventory.Slot.Secondary) != null)
        {
            inventory.StartSwitchCooldown();

            GameObject gifted = inventory.Active;
            if (gifted != null)
            {
                if (playerSetup != null && playerSetup.activeWeapon != null
                    && playerSetup.activeWeapon.gameObject == gifted)
                    playerSetup.UnequipWeapon();
                inventory.Remove(gifted);
                Destroy(gifted);
            }

            allyWeapon.EquipWeapon(playerPrefab, stashOutgoing: true); // pistol becomes their spare
            inventory.Equip(WeaponInventory.Slot.Secondary);           // you carry on with your pistol
            Debug.Log($"Gave {playerPrefab.name} to {ally.name}; they keep their {allyPrefab.name} as a spare.", this);
            return;
        }

        // Refuse anything that would leave the same weapon on both sides (or twice on
        // the player) - a trade should only ever move weapons, never copy them.
        if (allyPrefab.name == playerPrefab.name)
        {
            Debug.Log($"{ally.name} is already holding a {allyPrefab.name} - nothing to swap.", this);
            return;
        }
        if (inventory.Carries(allyPrefab))
        {
            Debug.Log($"You already carry a {allyPrefab.name} - swap refused so it isn't duplicated.", this);
            return;
        }
        if (allyWeapon.SecondaryPrefab != null && allyWeapon.SecondaryPrefab.name == playerPrefab.name)
        {
            Debug.Log($"{ally.name} is already carrying a {playerPrefab.name} - swap refused so it isn't duplicated.", this);
            return;
        }

        inventory.StartSwitchCooldown();

        // The weapon going to the ally leaves the player entirely. It used to be left
        // behind (holstered, or dropped as a "displaced" pickup when both weapons shared
        // a slot), which is where the duplicate rifles came from.
        GameObject given = inventory.Active;
        if (given != null)
        {
            if (playerSetup != null && playerSetup.activeWeapon != null
                && playerSetup.activeWeapon.gameObject == given)
                playerSetup.UnequipWeapon();
            inventory.Remove(given);
            Destroy(given);
        }

        // stashOutgoing: false - the ally's old weapon is going to the player, so the
        // ally must not also keep it as a spare (that was the duplicate pistol).
        allyWeapon.EquipWeapon(playerPrefab, stashOutgoing: false);

        GameObject held = Instantiate(allyPrefab, playerSetup != null && playerSetup.fpCamera != null
            ? playerSetup.fpCamera.transform : Camera.main.transform);
        held.name = allyPrefab.name;
        held.transform.localPosition = Vector3.zero;
        held.transform.localRotation = Quaternion.identity;
        held.SetActive(true);

        // Only a weapon already in the incoming weapon's slot can be displaced now (e.g.
        // you gave a rifle and got a pistol while carrying a different pistol) - that one
        // is dropped as a normal world pickup, same as picking a weapon up off the floor.
        GameObject displaced = inventory.Store(held, equipImmediately: true, sourcePrefab: allyPrefab);
        if (displaced != null)
        {
            playerSetup?.GetComponent<WeaponDrop>()?.DropSpecific(displaced);
            inventory.Remove(displaced);
        }
    }

    void OnDestroy() => pickupAction.Disable();
}