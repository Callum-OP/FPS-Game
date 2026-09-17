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

        FriendlyAI nearest = FindNearestAlly();
        if (nearest != null) SwapWith(nearest);
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
        if (allyWeapon == null) return;

        GameObject allyPrefab = allyWeapon.weaponPrefab;
        GameObject playerPrefab = inventory.ActivePrefab;

        // The weapon the player started the game already holding was never given a
        // recorded prefab (nothing instantiated it through Store), so there's nothing
        // to hand the ally in that one specific case - decline the swap rather than
        // silently doing half of it.
        if (allyPrefab == null || playerPrefab == null) return;

        allyWeapon.EquipWeapon(playerPrefab);

        GameObject held = Instantiate(allyPrefab, playerSetup != null && playerSetup.fpCamera != null
            ? playerSetup.fpCamera.transform : Camera.main.transform);
        held.name = allyPrefab.name;
        held.transform.localPosition = Vector3.zero;
        held.transform.localRotation = Quaternion.identity;
        held.SetActive(true);

        GameObject displaced = inventory.Store(held, equipImmediately: true, sourcePrefab: allyPrefab);
        if (displaced != null)
        {
            // The player's OTHER slot (not the one just handed to the ally) still needs
            // to go somewhere if it happens to collide with the new gun's slot - drop it
            // as a world pickup, same as picking up any other weapon off the ground.
            playerSetup?.GetComponent<WeaponDrop>()?.DropSpecific(displaced);
            inventory.Remove(displaced);
        }
    }

    void OnDestroy() => pickupAction.Disable();
}