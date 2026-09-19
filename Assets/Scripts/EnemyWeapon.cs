using System.Collections;
using UnityEngine;

/// <summary>
/// Gives an enemy a visible held weapon and lets it drop as a world pickup on
/// death, the same way WeaponDrop.cs does for the player.
///
/// Originally this statically parented the weapon to the RightHand bone, but
/// that meant the gun (and by extension the hands) looked exactly as good or
/// bad as whatever pose the current locomotion/aim animation happened to be
/// in - the same "animation doesn't match the weapon" problem the player's
/// WeaponHandIK was built to solve. This does the same thing for enemies:
/// there's no camera to attach to, so the weapon is instead parented to a
/// small anchor transform fixed relative to the enemy's own root (in front of
/// the chest) - stable regardless of animation, the same role the camera
/// plays for the player - and WeaponHandIK (already fully generic - it only
/// needs a humanoid Animator and grip transforms, nothing player-specific) is
/// added to the enemy to IK the hands onto the weapon's own grip points every
/// frame.
/// </summary>
// No RequireComponent(EnemyAI) any more: FriendlyAI uses this too, and an ally is not
// an enemy. The one thing that needed EnemyAI (auto-wiring muzzlePoint) is null-checked.
public class EnemyWeapon : MonoBehaviour
{
    [Header("Held weapon (visual only)")]
    [Tooltip("One of the player's held-weapon prefabs, e.g. AR.prefab or Mono19.prefab.")]
    public GameObject weaponPrefab;

    // Field order/grouping matches WeaponADS on the player (Positions, then Rotations)
    // so the two inspectors read the same way - hip/rest and ads/aim are the same idea
    // in both, just relative to a different parent (the enemy's anchor here, the camera
    // there).
    [Header("Positions")]
    [Tooltip("Local position of the weapon anchor while resting/running - the enemy equivalent of WeaponADS.hipPosition. Defaults match the player's tuned pistol values as a sensible starting point; tune by eye in Play Mode since this is relative to the anchor (see restPositionOffset's own placement below), not the camera, so the same numbers won't necessarily look identical.")]
    public Vector3 restPositionOffset = new Vector3(0.01f, -0.2f, 0.16f);
    [Tooltip("Local position of the weapon anchor while aiming - the enemy equivalent of WeaponADS.adsPosition.")]
    public Vector3 aimPositionOffset = new Vector3(0f, -0.11f, 0.2f);

    [Header("Rotations")]
    [Tooltip("Local rotation of the weapon anchor while resting/running - the enemy equivalent of WeaponADS.hipRotation.")]
    public Vector3 restRotationOffset = new Vector3(-1.2f, 2.2f, 35f);
    [Tooltip("Local rotation of the weapon anchor while aiming - the enemy equivalent of WeaponADS.adsRotation.")]
    public Vector3 aimRotationOffset = Vector3.zero;

    [Header("Settings")]
    [Tooltip("How fast the anchor blends between resting and aiming.")]
    public float aimBlendSpeed = 8f;

    [Header("Lowered (out of combat)")]
    [Tooltip("Local position of the weapon anchor while the enemy isn't in combat - gun down at the waist, the same idea as the player's X key. Patrolling with a rifle levelled at nothing looks wrong.")]
    public Vector3 loweredPositionOffset = new Vector3(0.12f, 0.85f, 0.18f);
    [Tooltip("Local rotation of the weapon anchor while out of combat - tilt the muzzle down.")]
    public Vector3 loweredRotationOffset = new Vector3(35f, 0f, 0f);
    [Tooltip("How fast the gun raises/lowers when combat starts or ends.")]
    public float lowerBlendSpeed = 4f;

    [Header("Ammo / Reload")]
    [Tooltip("Rounds before this enemy has to reload. Set per weapon type on the prefab.")]
    public int magazineSize = 30;
    [Tooltip("How long a reload takes. Match it roughly to the reload animation.")]
    public float reloadTime = 2.2f;
    [Tooltip("Local offset from the hips where the off hand reaches for a fresh magazine - same body-relative grab point the player's reload uses.")]
    public Vector3 reloadGrabOffset = new Vector3(-0.15f, 0f, 0.12f);
    public Vector3 reloadGrabRotation = new Vector3(0f, 0f, 0f);
    [Tooltip("Fraction of the reload spent travelling to the mag pouch, and then to the magwell. The rest is the trip back to the grip.")]
    [Range(0f, 1f)] public float handGrabTime = 0.25f;
    [Range(0f, 1f)] public float handSeatTime = 0.6f;

    [Header("Drop on death")]
    [Tooltip("The matching *Pickup prefab, e.g. ARPickup.prefab - what actually spawns in the world.")]
    public GameObject worldPickupPrefab;

    EnemyAI enemyAI;
    FriendlyAI friendlyAI; // set instead of enemyAI when this is on an ally
    WeaponHandIK handIK;
    GameObject weaponInstance;
    Transform anchor;
    bool aiming;
    float aimBlend;
    bool combatReady;
    float combatBlend;
    bool dropped;
    int ammo;
    bool reloading;
    Transform reloadHandAnchor;
    CharacterAnimationDriver animationDriver;
    Transform weaponReloadPoint; // the gun's own magwell point, if the prefab has one

    void Start()
    {
        enemyAI = GetComponent<EnemyAI>();     // may be null on an ally
        friendlyAI = GetComponent<FriendlyAI>(); // may be null on an enemy
        ammo = Mathf.Max(1, magazineSize);
        animationDriver = GetComponentInChildren<CharacterAnimationDriver>();

        if (weaponPrefab != null) SpawnWeapon(weaponPrefab);
    }

    /// <summary>Builds the anchor (once) and instantiates the held weapon. Split out of
    /// Start() so EquipWeapon can call it again for a different prefab - the anchor,
    /// WeaponHandIK and everything else about how the gun is held stays the same, only
    /// the weapon model and its grip/muzzle points change.</summary>
    void SpawnWeapon(GameObject prefab)
    {
        Animator anim = GetComponentInChildren<Animator>();
        if (anim == null)
        {
            Debug.LogWarning($"EnemyWeapon on '{name}': no Animator found - can't attach the weapon.", this);
            return;
        }

        // WeaponHandIK is fully generic (player-agnostic) - it just needs to live
        // next to a humanoid Animator, same requirement CharacterAnimationDriver has.
        handIK = anim.GetComponent<WeaponHandIK>();
        if (handIK == null) handIK = anim.gameObject.AddComponent<WeaponHandIK>();

        if (anchor == null)
        {
            // A fixed anchor on the enemy's own root plays the same role the camera
            // plays for the player: something stable to hang the gun off that isn't
            // dragged around by whatever the current animation pose is doing to the
            // hands. The root's own facing is already what EnemyAI turns to aim
            // horizontally, so the anchor turns with it for free.
            GameObject anchorGo = new GameObject("WeaponHoldAnchor");
            anchorGo.transform.SetParent(transform, false);
            anchorGo.transform.localPosition = restPositionOffset;
            anchorGo.transform.localEulerAngles = restRotationOffset;
            anchor = anchorGo.transform;
        }

        weaponInstance = Instantiate(prefab, anchor);
        weaponInstance.transform.localPosition = Vector3.zero;
        weaponInstance.transform.localRotation = Quaternion.identity;

        // Pull the grip/muzzle transforms off the weapon's own WeaponController
        // before stripping every script it brought with it (WeaponController,
        // WeaponADS, WeaponCant... all read player input/camera state, which
        // an enemy doesn't have and shouldn't react to).
        var reloadHandler = weaponInstance.GetComponent<WeaponReloadHandler>();
        weaponReloadPoint = reloadHandler != null ? reloadHandler.handReloadPoint : null;

        WeaponController wc = weaponInstance.GetComponent<WeaponController>();
        if (wc != null)
        {
            handIK.SetGripTargets(wc.rightHandGrip, wc.leftHandGrip);
            // The gun now sits in a fixed, known spot instead of wherever the hand
            // animation happened to leave it, so its own muzzle point is a more
            // reliable bullet-spawn reference than a hand-placed empty transform -
            // auto-wire it in rather than leaving Enemy.muzzlePoint stale.
            if (wc.muzzlePoint != null)
            {
                if (enemyAI != null) enemyAI.muzzlePoint = wc.muzzlePoint;
                // This was missing entirely for allies, which is why they never fired -
                // FriendlyAI.TryShoot bails out silently when muzzlePoint is null, and
                // nothing was ever setting it.
                if (friendlyAI != null) friendlyAI.muzzlePoint = wc.muzzlePoint;
            }

            // Ammo/reload timing follows whatever gun is actually equipped, rather than
            // staying fixed at whatever magazineSize/reloadTime happened to be set on
            // this component originally - matters once EquipWeapon can swap the gun
            // out at runtime (see FriendlyAI's auto-pickup).
            if (wc.maxAmmo > 0) magazineSize = wc.maxAmmo;
            if (wc.reloadTime > 0f) reloadTime = wc.reloadTime;
        }

        ammo = Mathf.Max(1, magazineSize);

        foreach (var mb in weaponInstance.GetComponentsInChildren<MonoBehaviour>(true))
            mb.enabled = false;
    }

    [Header("Secondary (carried, not held)")]
    [Tooltip("A spare weapon this ally is carrying but not actively using. Set on the prefab to start an ally with a backup, or left for EquipWeapon to fill automatically - see its comment.")]
    public GameObject secondaryWeaponPrefab;

    /// <summary>The spare weapon this ally is carrying, if any - the ally-side equivalent
    /// of WeaponInventory.GetSlot(Secondary) on the player.</summary>
    public GameObject SecondaryPrefab => secondaryWeaponPrefab;

    /// <summary>Swaps the held weapon for a different held-weapon prefab (one of the
    /// player's own, e.g. what a WeaponPickup would hand the player, or what
    /// PlayerAllySwap trades in) - used by FriendlyAI to upgrade an ally's weapon on the
    /// fly. The weapon being replaced is kept as this ally's secondary rather than being
    /// discarded, so a trade doesn't just erase whatever they were already carrying - it
    /// gives them their own two-weapon inventory, the same idea as the player's
    /// WeaponInventory Primary/Secondary slots. If a secondary is already held, IT is the
    /// one discarded (there's nowhere left to put it) - this should be rare in practice
    /// since SwapWith on the player side only ever trades the ACTIVE weapon.</summary>
    public void EquipWeapon(GameObject newHeldWeaponPrefab)
    {
        if (newHeldWeaponPrefab == null) return;

        reloading = false;
        aiming = false;

        if (weaponPrefab != null && weaponPrefab != newHeldWeaponPrefab)
            secondaryWeaponPrefab = weaponPrefab;

        weaponPrefab = newHeldWeaponPrefab;

        if (weaponInstance != null) Destroy(weaponInstance);
        weaponInstance = null;

        SpawnWeapon(newHeldWeaponPrefab);
    }

    /// <summary>Brings the carried secondary into the ally's hands, storing whatever was
    /// previously held as the new secondary. No-op if there's no secondary to swap to.
    /// Not called automatically anywhere yet (e.g. on running dry with no time to
    /// reload) - hook it into FriendlyAI's combat state if that behaviour is wanted.</summary>
    public void SwapToSecondary()
    {
        if (secondaryWeaponPrefab == null) return;

        GameObject incoming = secondaryWeaponPrefab;
        GameObject outgoing = weaponPrefab;
        secondaryWeaponPrefab = outgoing; // may be null if this is the ally's first weapon
        weaponPrefab = null; // prevents EquipWeapon from stashing 'outgoing' a second time
        EquipWeapon(incoming);
    }

    /// <summary>The currently held weapon's WeaponController, for comparing this
    /// weapon against a candidate pickup (see FriendlyAI's auto-upgrade logic).</summary>
    public WeaponController GetWeaponController() =>
        weaponInstance != null ? weaponInstance.GetComponent<WeaponController>() : null;

    void Update()
    {
        if (anchor == null) return;

        // Aiming only counts while the gun is actually up.
        float target = (aiming && !reloading) ? 1f : 0f;
        aimBlend = Mathf.MoveTowards(aimBlend, target, aimBlendSpeed * Time.deltaTime);
        combatBlend = Mathf.MoveTowards(combatBlend, combatReady ? 1f : 0f, lowerBlendSpeed * Time.deltaTime);

        // Two blends, composed: lowered -> ready -> aiming. Nothing else writes this
        // transform, same single-owner rule the player's weapon follows.
        Vector3 readyPos = Vector3.Lerp(restPositionOffset, aimPositionOffset, aimBlend);
        Quaternion readyRot = Quaternion.Slerp(
            Quaternion.Euler(restRotationOffset), Quaternion.Euler(aimRotationOffset), aimBlend);

        anchor.localPosition = Vector3.Lerp(loweredPositionOffset, readyPos, combatBlend);
        anchor.localRotation = Quaternion.Slerp(Quaternion.Euler(loweredRotationOffset), readyRot, combatBlend);
    }

    /// <summary>Gun up (in combat) or down at the waist (patrolling/idle). Called by EnemyAI
    /// as it changes state.</summary>
    public void SetCombatReady(bool value) => combatReady = value;

    public bool IsReloading => reloading;
    public bool HasAmmo => ammo > 0;
    public int Ammo => ammo;

    /// <summary>Spends a round. Returns false if the magazine is empty - EnemyAI uses that
    /// as its cue to go and reload (ideally behind something).</summary>
    public bool TryConsumeAmmo()
    {
        if (reloading || ammo <= 0) return false;
        ammo--;
        return true;
    }

    /// <summary>Starts a reload, if one isn't already running.</summary>
    public void Reload()
    {
        if (reloading || weaponInstance == null) return;
        StartCoroutine(ReloadRoutine());
    }

    // Mirrors the player's WeaponReloadHandler at a smaller scale: the off hand leaves
    // the foregrip, dips to a mag pouch on the hip, comes up to the magwell, and returns.
    // The same WeaponHandIK reload-override hook the player uses drives it, so it works
    // with whatever gun the enemy is holding without per-weapon setup.
    IEnumerator ReloadRoutine()
    {
        reloading = true;
        aiming = false;
        animationDriver?.PlayReload();

        Transform grabPoint = handIK != null
            ? handIK.GetOrCreateReloadGrabPoint(reloadGrabOffset, reloadGrabRotation)
            : null;

        if (handIK != null && grabPoint != null)
        {
            if (reloadHandAnchor == null)
                reloadHandAnchor = new GameObject("EnemyReloadHandAnchor").transform;

            WeaponController wc = weaponInstance != null ? weaponInstance.GetComponent<WeaponController>() : null;
            Transform gripPoint = wc != null ? wc.leftHandGrip : null;
            Vector3 gripStart = gripPoint != null ? gripPoint.position : transform.position;
            Quaternion gripStartRot = gripPoint != null ? gripPoint.rotation : transform.rotation;

            reloadHandAnchor.SetPositionAndRotation(gripStart, gripStartRot);
            handIK.SetReloadOverride(null, reloadHandAnchor);

            float t = 0f;
            while (t < reloadTime)
            {
                t += Time.deltaTime;
                float frac = reloadTime > 0f ? Mathf.Clamp01(t / reloadTime) : 1f;

                Vector3 seatPos = weaponReloadPoint != null ? weaponReloadPoint.position : gripStart;
                Quaternion seatRot = weaponReloadPoint != null ? weaponReloadPoint.rotation : gripStartRot;

                Vector3 fromPos, toPos; Quaternion fromRot, toRot; float segStart, segEnd;
                if (frac <= handGrabTime)
                {
                    fromPos = gripStart; fromRot = gripStartRot;
                    toPos = grabPoint.position; toRot = grabPoint.rotation;
                    segStart = 0f; segEnd = handGrabTime;
                }
                else if (frac <= handSeatTime)
                {
                    fromPos = grabPoint.position; fromRot = grabPoint.rotation;
                    toPos = seatPos; toRot = seatRot;
                    segStart = handGrabTime; segEnd = handSeatTime;
                }
                else
                {
                    fromPos = seatPos; fromRot = seatRot;
                    toPos = gripPoint != null ? gripPoint.position : gripStart;
                    toRot = gripPoint != null ? gripPoint.rotation : gripStartRot;
                    segStart = handSeatTime; segEnd = 1f;
                }

                float segT = Mathf.Clamp01((frac - segStart) / Mathf.Max(0.0001f, segEnd - segStart));
                segT = segT * segT * (3f - 2f * segT); // smoothstep, so each leg eases rather than snapping
                reloadHandAnchor.SetPositionAndRotation(
                    Vector3.Lerp(fromPos, toPos, segT), Quaternion.Slerp(fromRot, toRot, segT));

                yield return null;
            }

            handIK.SetReloadOverride(null, null);
        }
        else
        {
            yield return new WaitForSeconds(reloadTime);
        }

        ammo = Mathf.Max(1, magazineSize);
        reloading = false;
    }

    /// <summary>Called from EnemyAI alongside CharacterAnimationDriver.SetAiming(), same
    /// event, so the held weapon raises/settles in step with the aim animation.</summary>
    public void SetAiming(bool value) => aiming = value;

    /// <summary>Called from EnemyAI.OnDeath - drops the world pickup and hides the held mesh.</summary>
    public void Drop()
    {
        if (dropped) return;
        dropped = true;

        // Let go of the grips so the hands relax into the death/ragdoll pose
        // instead of continuing to reach for a now-hidden weapon.
        handIK?.SetGripTargets(null, null);

        if (worldPickupPrefab != null)
        {
            Vector3 dropPos = transform.position + Vector3.up * 0.5f;
            GameObject world = Instantiate(worldPickupPrefab, dropPos, Quaternion.identity);
            Rigidbody rb = world.GetComponent<Rigidbody>();
            if (rb != null)
                rb.linearVelocity = Vector3.up * 1.5f + Random.insideUnitSphere * 0.5f;
        }

        if (weaponInstance != null)
            weaponInstance.SetActive(false);
    }

    void OnDestroy()
    {
        if (reloadHandAnchor != null) Destroy(reloadHandAnchor.gameObject);
    }
}