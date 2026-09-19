using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Carry a rifle and a pistol at once, and switch between them with 1 and 3.
///
/// The weapon that isn't in hand is reparented onto a holster point on the body -
/// rifle across the back, pistol on the hip - rather than being hidden, so you can see
/// what you're carrying (and so can anyone looking at your shadow). The holster points
/// are looked up through CharacterAttachPoints by name, and created at sensible spots on
/// the spine/hip bones if the body doesn't define them, so this works without any prefab
/// setup and can be tuned properly later by adding the named points.
///
/// Switching is instant (the gun is just moved) because there's no draw animation in the
/// Mixamo packs. If one gets added later, hook it in EquipSlot: play the clip, then swap
/// the parent at the point the hand reaches the holster. The pistol draw could also
/// reuse the reload hand path - WeaponHandIK.SetReloadOverride already knows how to walk
/// the off hand to a body-relative point and back, which is exactly the motion.
///
/// SETUP: add to the Player root next to PlayerSetup. Weapons already in the scene get
/// adopted on Start; anything picked up afterwards goes to its class's slot.
/// </summary>
[DefaultExecutionOrder(-140)]
public class WeaponInventory : MonoBehaviour
{
    public enum Slot { Primary, Secondary }

    [Header("References")]
    public PlayerSetup playerSetup;
    [Tooltip("The rigged body with the Animator - holster points are found or created on its bones.")]
    public Animator bodyAnimator;

    [Header("Holster Points")]
    [Tooltip("Assign existing transforms here to use them directly (e.g. points already set up on the body). Leave empty to fall back to a named CharacterAttachPoints entry, or fail that, an auto-created point on the spine/hip bone below.")]
    public Transform primaryHolsterPoint;
    public Transform secondaryHolsterPoint;

    [Header("Holster placement (used only if the above aren't assigned and the body has no named attach point)")]
    public string primaryHolsterPointName = "BackHolster";
    public string secondaryHolsterPointName = "HipHolster";
    public Vector3 backHolsterOffset = new Vector3(-0.12f, 0.05f, -0.18f);
    public Vector3 backHolsterRotation = new Vector3(0f, 90f, 25f);
    public Vector3 hipHolsterOffset = new Vector3(-0.18f, 0f, 0.02f);
    public Vector3 hipHolsterRotation = new Vector3(0f, 90f, 0f);

    [Header("Starting Weapon")]
    [Tooltip("The prefab that matches whatever weapon is already equipped in the scene at Start (e.g. AR.prefab). Without this, that weapon has no recorded source prefab - Store() only records one for pickups it instantiates itself - so ActivePrefab is null until the player picks something up, and PlayerAllySwap silently refuses to trade a weapon it can't hand to the ally. Leave empty if the player starts unarmed.")]
    public GameObject startingWeaponPrefab;

    GameObject primary, secondary;
    GameObject primaryPrefab, secondaryPrefab; // source prefabs, for handing a weapon to something else (see PlayerAllySwap)
    Slot activeSlot = Slot.Primary;
    Transform primaryHolster, secondaryHolster;
    InputAction toggleWeapon;

    void Awake()
    {
        // Single toggle key (3) rather than a select-key per slot.
        toggleWeapon = PlayerInputMap.Make("ToggleWeapon", PlayerInputMap.ToggleWeapon);
    }

    void Start()
    {
        if (playerSetup == null) playerSetup = GetComponent<PlayerSetup>();
        if (bodyAnimator == null)
            foreach (var a in GetComponentsInChildren<Animator>(true))
                if (a.avatar != null && a.avatar.isHuman) { bodyAnimator = a; break; }

        primaryHolster = ResolveHolster(primaryHolsterPoint, primaryHolsterPointName, HumanBodyBones.Spine, backHolsterOffset, backHolsterRotation);
        secondaryHolster = ResolveHolster(secondaryHolsterPoint, secondaryHolsterPointName, HumanBodyBones.Hips, hipHolsterOffset, hipHolsterRotation);

        // Adopt whatever the player already has equipped.
        if (playerSetup != null && playerSetup.activeWeapon != null)
            Store(playerSetup.activeWeapon.gameObject, true, startingWeaponPrefab);
    }

    Transform ResolveHolster(Transform assigned, string pointName, HumanBodyBones fallbackBone, Vector3 offset, Vector3 rotation)
    {
        // An explicitly assigned transform always wins - it's exactly what the person
        // set up by hand, so there's nothing to look up or create.
        if (assigned != null) return assigned;

        var points = GetComponentInChildren<CharacterAttachPoints>();
        var named = points != null ? points.Get(pointName) : null;
        if (named != null) return named;

        if (bodyAnimator == null) return transform;
        Transform bone = bodyAnimator.GetBoneTransform(fallbackBone);
        if (bone == null) return transform;

        var go = new GameObject(pointName);
        go.transform.SetParent(bone, false);
        go.transform.localPosition = offset;
        go.transform.localEulerAngles = rotation;
        return go.transform;
    }

    void Update()
    {
        if (toggleWeapon.WasPressedThisFrame()) ToggleActive();
    }

    /// <summary>Which slot a weapon belongs in. Pistols go to secondary, everything else
    /// to primary - matched on the weapon's own class if it declares one, otherwise on
    /// the prefab name.</summary>
    public Slot SlotFor(GameObject weapon)
    {
        // Matched on the prefab name - there's no per-weapon class field to read yet.
        // If one gets added to WeaponController later, check it here first.
        // Hummingbird is an SMG (two-handed, shoulder weapon) despite the small-sounding
        // name - it belongs on the back with the rifle, not the hip with the pistol.
        string n = weapon.name.ToLowerInvariant();
        if (n.Contains("mono19") || n.Contains("pistol")) return Slot.Secondary;
        return Slot.Primary;
    }

    /// <summary>Puts a newly picked-up weapon into its slot and equips it. Whatever was
    /// already in that slot is returned so the caller can drop it as a world pickup.
    /// sourcePrefab is optional - pass it when known (WeaponPickup has it directly) so
    /// ActivePrefab/PrefabFor can hand this weapon on to something else later (see
    /// PlayerAllySwap) without needing to re-derive a prefab from a live instance.</summary>
    public GameObject Store(GameObject weapon, bool equipImmediately, GameObject sourcePrefab = null)
    {
        Slot slot = SlotFor(weapon);
        GameObject displaced = slot == Slot.Primary ? primary : secondary;
        if (displaced == weapon) displaced = null;

        if (slot == Slot.Primary) { primary = weapon; primaryPrefab = sourcePrefab; }
        else { secondary = weapon; secondaryPrefab = sourcePrefab; }

        if (equipImmediately) Equip(slot);
        else Holster(weapon, slot);

        return displaced;
    }

    public GameObject GetSlot(Slot slot) => slot == Slot.Primary ? primary : secondary;
    public GameObject Active => activeSlot == Slot.Primary ? primary : secondary;
    /// <summary>Source prefab for the currently active weapon, if it's known (null for
    /// whatever the player started the scene already holding, since nothing recorded a
    /// prefab for that one).</summary>
    public GameObject ActivePrefab => activeSlot == Slot.Primary ? primaryPrefab : secondaryPrefab;

    public void Remove(GameObject weapon)
    {
        if (primary == weapon) { primary = null; primaryPrefab = null; }
        if (secondary == weapon) { secondary = null; secondaryPrefab = null; }
    }

    public void Equip(Slot slot)
    {
        GameObject target = slot == Slot.Primary ? primary : secondary;
        if (target == null) return;

        // Holster the other one.
        GameObject other = slot == Slot.Primary ? secondary : primary;
        if (other != null && other != target)
            Holster(other, slot == Slot.Primary ? Slot.Secondary : Slot.Primary);

        activeSlot = slot;

        Transform cam = playerSetup != null && playerSetup.fpCamera != null
            ? playerSetup.fpCamera.transform : Camera.main.transform;

        target.transform.SetParent(cam, false);
        target.transform.localPosition = Vector3.zero;
        target.transform.localRotation = Quaternion.identity;
        target.SetActive(true);
        SetWeaponScripts(target, true);

        var controller = target.GetComponent<WeaponController>();
        if (controller != null) playerSetup?.WireWeapon(controller);
    }

    /// <summary>Puts a weapon on the body without equipping it - used when switching and
    /// when the grenade is out.</summary>
    public void Holster(GameObject weapon, Slot slot)
    {
        if (weapon == null) return;
        Transform point = slot == Slot.Primary ? primaryHolster : secondaryHolster;
        weapon.transform.SetParent(point, false);
        weapon.transform.localPosition = Vector3.zero;
        weapon.transform.localRotation = Quaternion.identity;
        weapon.SetActive(true);
        SetWeaponScripts(weapon, false);
    }

    /// <summary>Both weapons on the body - for throwing a grenade, which needs the hands.</summary>
    public void HolsterAll()
    {
        Holster(primary, Slot.Primary);
        Holster(secondary, Slot.Secondary);
        playerSetup?.UnequipWeapon();
    }

    /// <summary>Back to whatever was last in hand.</summary>
    public void RestoreActive() => Equip(activeSlot);

    /// <summary>Swaps to whichever slot isn't currently active. If only one slot is
    /// filled this is a no-op - there's nothing to swap to.</summary>
    public void ToggleActive()
    {
        Slot other = activeSlot == Slot.Primary ? Slot.Secondary : Slot.Primary;
        if (GetSlot(other) != null) Equip(other);
    }

    // A holstered weapon must not read input, sway, recoil, or take part in the hand IK -
    // it's scenery hanging off a bone. Its own scripts are the only thing that would make
    // it behave otherwise, so they go off together.
    void SetWeaponScripts(GameObject weapon, bool enabled)
    {
        foreach (var mb in weapon.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb is Transform) continue;
            mb.enabled = enabled;
        }
    }

    void OnDestroy()
    {
        toggleWeapon.Disable();
    }
}