using System.Collections;
using UnityEngine;
using UnityEngine.AI;

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

    [Header("Holster Points (visual, for whichever weapon isn't currently held)")]
    [Tooltip("Where a carried PRIMARY-class weapon (anything that isn't a pistol) rests when not held - the ally/enemy equivalent of the player's back holster. Falls back to a \"BackHolster\" CharacterAttachPoints entry if left empty, or this GameObject's own root if neither exists (tune by eye in Play Mode in that case).")]
    public Transform primaryHolsterPoint;
    [Tooltip("Where a carried SECONDARY-class weapon (pistols - matched the same way WeaponInventory.SlotFor does on the player) rests when not held - the equivalent of the player's hip holster. Same fallback chain as primaryHolsterPoint, but looks for \"HipHolster\".")]
    public Transform secondaryHolsterPoint;

    [Header("Player-Identical Hold")]
    [Tooltip("Hold the gun exactly the way the player does: the weapon's own WeaponADS (the hip/ADS pose you tuned on the player), its walk bob and its hand pull-back all run on the AI, hung off a virtual 'eye' placed where the player's camera sits relative to the body. Untick for the old fixed-anchor hold.")]
    public bool useEyeRigHold = true;
    [Tooltip("Max degrees the gun (eye rig) pitches up/down to follow its target - the equivalent of the player looking up/down.")]
    public float maxAimPitch = 40f;
    [Tooltip("Degrees per second the pitch follows the target.")]
    public float aimPitchSpeed = 240f;

    [Header("Weapon Switching")]
    [Tooltip("In combat, an empty magazine swaps to the carried spare (e.g. a pistol) instead of reloading, if the spare has rounds. Out of combat they go back to their primary.")]
    public bool switchInsteadOfReload = true;
    [Tooltip("Seconds a weapon swap takes (holster one, draw the other).")]
    public float switchTime = 0.7f;
    [Tooltip("Seconds out of combat before returning from a sidearm to the primary.")]
    public float returnToPrimaryDelay = 1.5f;

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
    GameObject secondaryInstance; // the actual holstered weapon on the body, if any
    int secondaryAmmo; // remembered so swapping back doesn't just hand back a full mag

    // Player-identical hold
    WeaponADS weaponADS;
    Vector3 eyeLocalPosition;
    float eyePitch;
    bool hasAimPoint;
    Vector3 aimPoint;
    float calmTimer;

    // Scavenging
    WeaponPickup scavTarget;
    float scavTimer, scavSearchTimer;

    static bool hasEyeSample;
    static Vector3 eyeInModel;

    void Start()
    {
        enemyAI = GetComponent<EnemyAI>();     // may be null on an ally
        friendlyAI = GetComponent<FriendlyAI>(); // may be null on an enemy
        ammo = Mathf.Max(1, magazineSize);
        animationDriver = GetComponentInChildren<CharacterAnimationDriver>();
        if (animationDriver != null) TorsoPoseDriver.EnsureOn(animationDriver.GetComponent<Animator>());

        if (weaponPrefab != null) SpawnWeapon(weaponPrefab);
    }

    /// <summary>Builds the anchor (once) and instantiates the held weapon. Split out of
    /// Start() so EquipWeapon can call it again for a different prefab - the anchor,
    /// WeaponHandIK and everything else about how the gun is held stays the same, only
    /// the weapon model and its grip/muzzle points change.</summary>
    void SpawnWeapon(GameObject prefab)
    {
        if (!EnsureAnchorAndHandIK()) return;

        weaponInstance = Instantiate(prefab, anchor);
        weaponInstance.transform.localPosition = Vector3.zero;
        weaponInstance.transform.localRotation = Quaternion.identity;

        WireHeldInstance(weaponInstance);
        ammo = Mathf.Max(1, magazineSize); // fresh instance - full mag, same as picking the weapon up new

        foreach (var mb in weaponInstance.GetComponentsInChildren<MonoBehaviour>(true))
            mb.enabled = false;

        ApplyAIHold(weaponInstance);
    }

    bool EnsureAnchorAndHandIK()
    {
        Animator anim = GetComponentInChildren<Animator>();
        if (anim == null)
        {
            Debug.LogWarning($"EnemyWeapon on '{name}': no Animator found - can't attach the weapon.", this);
            return false;
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
        eyeLocalPosition = ComputeEyeLocal(anim);

        return true;
    }

    /// <summary>Pulls the grip/muzzle/ammo-config wiring off an already-instantiated
    /// weapon and points this component's own AI-facing state (magazineSize, reloadTime,
    /// muzzlePoint, hand IK grips) at it. Shared by a freshly spawned weapon and a
    /// secondary being brought back out of its holster - either way this is now the
    /// weapon actually being aimed and fired, so everything downstream needs to agree on
    /// which one that is.</summary>
    void WireHeldInstance(GameObject instance)
    {
        // Pull the grip/muzzle transforms off the weapon's own WeaponController
        // before stripping every script it brought with it (WeaponController,
        // WeaponADS, WeaponCant... all read player input/camera state, which
        // an enemy doesn't have and shouldn't react to).
        var reloadHandler = instance.GetComponent<WeaponReloadHandler>();
        weaponReloadPoint = reloadHandler != null ? reloadHandler.handReloadPoint : null;

        WeaponController wc = instance.GetComponent<WeaponController>();
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
    }

    /// <summary>Everything player-input-driven on a held weapon stays off - except its own
    /// hip/ADS pose holder and walk bob, which are switched to AI control so the gun sits
    /// and moves exactly as it does for the player (see useEyeRigHold). Also used when a
    /// holstered weapon is drawn again, since holstering disables every script on it.</summary>
    void ApplyAIHold(GameObject instance)
    {
        weaponADS = null;
        if (!useEyeRigHold || instance == null) return;
        weaponADS = instance.GetComponent<WeaponADS>();
        if (weaponADS == null) return;

        weaponADS.SetExternalControl(true);
        weaponADS.enabled = true;
        var bob = instance.GetComponent<WeaponMovementBob>();
        if (bob != null) { bob.aiAgent = GetComponent<NavMeshAgent>(); bob.enabled = true; }
    }

    // ------------------------------------------------------------------
    // Player-identical hold
    // ------------------------------------------------------------------

    /// <summary>Where the player's camera sits, re-expressed on THIS body (in root-local
    /// space). Sampled once from the player's own model space, so it holds for any
    /// character that shares the player's rig. Falls back to just in front of the head.</summary>
    Vector3 ComputeEyeLocal(Animator anim)
    {
        if (!hasEyeSample)
        {
            var pm = FindFirstObjectByType<PlayerMovement>();
            if (pm != null && pm.cameraTransform != null && !pm.IsCrouching)
            {
                foreach (var a in pm.GetComponentsInChildren<Animator>(true))
                {
                    if (a.avatar == null || !a.avatar.isHuman) continue;
                    eyeInModel = a.transform.InverseTransformPoint(pm.cameraTransform.position);
                    hasEyeSample = true;
                    break;
                }
            }
        }

        if (hasEyeSample)
            return transform.InverseTransformPoint(anim.transform.TransformPoint(eyeInModel));

        Transform head = anim.GetBoneTransform(HumanBodyBones.Head);
        Vector3 world = head != null
            ? head.position + anim.transform.up * 0.08f + anim.transform.forward * 0.1f
            : transform.position + Vector3.up * 1.6f;
        return transform.InverseTransformPoint(world);
    }

    /// <summary>Tell the weapon what it's aiming at so the gun pitches up/down towards it
    /// like the player looking up/down. Call every frame from the AI (has=false when idle).</summary>
    public void SetAimPoint(bool has, Vector3 worldPoint)
    {
        hasAimPoint = has;
        aimPoint = worldPoint;
    }

    void UpdateEyePitch()
    {
        float target = 0f;
        if (hasAimPoint)
        {
            Vector3 d = aimPoint - transform.TransformPoint(eyeLocalPosition);
            float horiz = new Vector2(d.x, d.z).magnitude;
            target = Mathf.Clamp(-Mathf.Atan2(d.y, horiz) * Mathf.Rad2Deg, -maxAimPitch, maxAimPitch);
        }
        eyePitch = Mathf.MoveTowardsAngle(eyePitch, target, aimPitchSpeed * Time.deltaTime);
    }

    // ------------------------------------------------------------------
    // Weapon classes (holster class matching is IsSecondaryClass, further down)
    // ------------------------------------------------------------------

    static CharacterWeaponClass ClassFor(GameObject prefab)
    {
        var wc = prefab != null ? prefab.GetComponent<WeaponController>() : null;
        if (wc == null) return CharacterWeaponClass.Rifle;
        return wc.isAutomatic || wc.isShotgun ? CharacterWeaponClass.Rifle : CharacterWeaponClass.Pistol;
    }

    /// <summary>True if the held weapon or the carried spare is made from this prefab.</summary>
    public bool IsCarried(GameObject prefab)
    {
        if (prefab == null) return false;
        return (weaponPrefab != null && weaponPrefab.name == prefab.name)
            || (secondaryWeaponPrefab != null && secondaryWeaponPrefab.name == prefab.name);
    }

    // Rough DPS estimate - damage per pellet times pellets, over the time between shots.
    public static float ScoreOf(WeaponController wc)
    {
        if (wc == null || wc.bulletPrefab == null) return 0f;
        var proj = wc.bulletPrefab.GetComponent<Projectile>();
        float dmg = proj != null ? proj.damage : 0f;
        int pellets = Mathf.Max(1, wc.pelletCount);
        float rate = Mathf.Max(0.01f, wc.fireRate);
        return dmg * pellets / rate;
    }

    // ------------------------------------------------------------------
    // Scavenging - shared by allies (FriendlyAI) and enemies (EnemyAI)
    // ------------------------------------------------------------------

    /// <summary>Looks for a strictly better weapon lying on the ground, walks to it and
    /// takes it (the old held weapon becomes the holstered spare). Call every frame; pass
    /// allowed=false whenever the AI shouldn't be doing this (in combat, alerted...).</summary>
    /// <param name="delay">Seconds a pickup has to be the chosen target before it's taken.</param>
    /// <param name="reach">Flat distance at which the weapon is grabbed.</param>
    /// <param name="minPickupAge">Ignore pickups that appeared less than this long ago.</param>
    public void UpdateScavenge(bool allowed, float radius, float delay, float reach,
                               float minPickupAge, NavMeshAgent agent, float moveSpeed)
    {
        if (!allowed || dropped || reloading || weaponInstance == null) { scavTarget = null; return; }

        scavSearchTimer -= Time.deltaTime;
        if (scavSearchTimer <= 0f)
        {
            scavSearchTimer = 0.3f;

            WeaponPickup best = null;
            float bestScore = ScoreOf(GetWeaponController());
            foreach (var pickup in FindObjectsByType<WeaponPickup>(FindObjectsSortMode.None))
            {
                if (pickup == null || pickup.heldWeaponPrefab == null) continue;
                if (pickup.Age < minPickupAge) continue;
                if (Vector3.Distance(transform.position, pickup.transform.position) > radius) continue;
                if (IsCarried(pickup.heldWeaponPrefab)) continue;

                float score = ScoreOf(pickup.heldWeaponPrefab.GetComponent<WeaponController>());
                if (score > bestScore) { best = pickup; bestScore = score; }
            }

            if (best != scavTarget) { scavTarget = best; scavTimer = delay; }
        }

        if (scavTarget == null) return;

        Vector3 to = scavTarget.transform.position - transform.position;
        to.y = 0f;
        if (to.magnitude > reach)
        {
            if (agent != null && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.speed = moveSpeed;
                Vector3 dest = scavTarget.transform.position;
                if (NavMesh.SamplePosition(dest, out NavMeshHit hit, 2f, NavMesh.AllAreas)) dest = hit.position;
                agent.SetDestination(dest);
            }
            return;
        }

        scavTimer -= Time.deltaTime;
        if (scavTimer > 0f) return;

        GameObject prefab = scavTarget.heldWeaponPrefab;
        Destroy(scavTarget.gameObject);
        scavTarget = null;
        EquipWeapon(prefab);
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
    /// fly. The weapon being replaced is HOLSTERED (visible on the body, same idea as the
    /// player's back/hip holster) as this ally's secondary rather than being discarded,
    /// so a trade doesn't just erase whatever they were already carrying, and it doesn't
    /// just fall to the ground either - it gives them their own two-weapon inventory, the
    /// same idea as the player's WeaponInventory Primary/Secondary slots. If a secondary
    /// is already held, IT is the one destroyed (there's nowhere left to put it) - this
    /// should be rare in practice since SwapWith on the player side only ever trades the
    /// ACTIVE weapon.</summary>
    /// <param name="stashOutgoing">True (default) holsters the replaced weapon as the spare.
    /// Pass false when the replaced weapon is being handed to someone else (a swap with the
    /// player): it's destroyed instead, and any carried spare is left alone - holstering it
    /// too is what used to duplicate it.</param>
    public void EquipWeapon(GameObject newHeldWeaponPrefab, bool stashOutgoing = true)
    {
        if (animationDriver != null) animationDriver.GetComponent<TorsoPoseDriver>()?.PlayWeaponSwitch();
        if (newHeldWeaponPrefab == null) return;

        // A reload or weapon swap already under way belongs to the old gun.
        StopAllCoroutines();
        handIK?.SetReloadOverride(null, null);
        reloading = false;
        aiming = false;

        if (!stashOutgoing)
        {
            if (weaponInstance != null) Destroy(weaponInstance);
            weaponInstance = null;
            weaponPrefab = newHeldWeaponPrefab;
            SpawnWeapon(newHeldWeaponPrefab);
            animationDriver?.SetWeaponClass(ClassFor(newHeldWeaponPrefab));
            return;
        }

        // Figure out which of the ally's two slots (whatever's HELD, whatever's
        // CARRIED/holstered) is the same class as the incoming weapon - that's the one
        // being traded away, and the other slot must be left completely alone. Getting
        // this wrong is what used to destroy the holstered pistol and dump the previous
        // rifle at the ally's feet whenever a rifle was swapped for another rifle: the
        // old code always treated the currently-HELD weapon as the one to holster and
        // always destroyed whatever was already carried, regardless of which slot the
        // new weapon actually belonged in.
        bool newIsSecondary = IsSecondaryClass(newHeldWeaponPrefab);
        bool heldMatches = weaponPrefab != null && IsSecondaryClass(weaponPrefab) == newIsSecondary;
        bool carriedMatches = secondaryWeaponPrefab != null && IsSecondaryClass(secondaryWeaponPrefab) == newIsSecondary;

        if (heldMatches)
        {
            // Same class as what's in hand right now - just replace it. The carried
            // spare, if any, is a different class and is untouched.
            if (weaponInstance != null) Destroy(weaponInstance);
        }
        else if (carriedMatches)
        {
            // Same class as the carried spare - THAT'S what's being replaced, not the
            // held weapon. The held weapon isn't going anywhere except onto the body,
            // since the incoming weapon is what's coming into hand now.
            if (secondaryInstance != null) Destroy(secondaryInstance);
            secondaryInstance = null;
            secondaryWeaponPrefab = null;

            if (weaponInstance != null)
            {
                secondaryInstance = weaponInstance;
                secondaryWeaponPrefab = weaponPrefab;
                secondaryAmmo = ammo;
                HolsterSecondaryVisual();
            }
        }
        else
        {
            // Neither slot is this weapon's class yet (e.g. nothing's been carried
            // before) - holster whatever's currently held, discarding any carried spare
            // to make room (there's still only one carry slot).
            if (secondaryInstance != null) Destroy(secondaryInstance);
            if (weaponInstance != null)
            {
                secondaryInstance = weaponInstance;
                secondaryWeaponPrefab = weaponPrefab;
                secondaryAmmo = ammo;
                HolsterSecondaryVisual();
            }
        }

        weaponPrefab = newHeldWeaponPrefab;
        weaponInstance = null;

        SpawnWeapon(newHeldWeaponPrefab);
        animationDriver?.SetWeaponClass(ClassFor(newHeldWeaponPrefab)); // pistol vs rifle hold follows the gun in hand
    }

    /// <summary>Brings the carried secondary into the ally's hands, storing whatever was
    /// previously held as the new secondary. Reuses the actual holstered instance (rather
    /// than destroying it and instantiating a fresh one, the way EquipWeapon does for a
    /// genuinely new weapon) so its remaining ammo carries over instead of coming back as
    /// a full mag. No-op if there's no secondary to swap to. Not called automatically
    /// anywhere yet (e.g. on running dry with no time to reload) - hook it into
    /// FriendlyAI's combat state if that behaviour is wanted.</summary>
    public void SwapToSecondary()
    {
        if (secondaryWeaponPrefab == null) return;

        GameObject incomingPrefab = secondaryWeaponPrefab;
        GameObject incomingInstance = secondaryInstance;
        int incomingAmmo = secondaryAmmo;

        // The outgoing weapon becomes the new secondary - holstered, not destroyed.
        secondaryWeaponPrefab = weaponPrefab;
        secondaryInstance = weaponInstance;
        secondaryAmmo = ammo;
        if (secondaryInstance != null) HolsterSecondaryVisual();

        weaponPrefab = incomingPrefab;
        weaponInstance = null;
        reloading = false;
        aiming = false;

        if (incomingInstance != null && EnsureAnchorAndHandIK())
        {
            incomingInstance.transform.SetParent(anchor, false);
            incomingInstance.transform.localPosition = Vector3.zero;
            incomingInstance.transform.localRotation = Quaternion.identity;
            WireHeldInstance(incomingInstance);
            weaponInstance = incomingInstance;
            ApplyAIHold(weaponInstance); // holstering switched its scripts off
            ammo = Mathf.Clamp(incomingAmmo, 0, magazineSize);
        }
        else
        {
            // No instance was ever holstered (e.g. secondaryWeaponPrefab was set directly
            // on the prefab rather than accumulated via a swap) - fall back to spawning
            // fresh, same as a first-time equip.
            SpawnWeapon(incomingPrefab);
        }
        animationDriver?.SetWeaponClass(ClassFor(incomingPrefab));
    }

    IEnumerator SwitchRoutine()
    {
        reloading = true;   // holds fire, and the AI treats it like a reload
        aiming = false;
        yield return new WaitForSeconds(switchTime * 0.5f);
        SwapToSecondary();
        reloading = true;   // SwapToSecondary clears it; hold fire until the draw finishes
        yield return new WaitForSeconds(switchTime * 0.5f);
        reloading = false;
    }

    // The carried spare has rounds to shoot: it was holstered with some left, or it's a
    // fresh one that hasn't been fired yet.
    bool SpareHasRounds => secondaryWeaponPrefab != null && (secondaryInstance == null || secondaryAmmo > 0);

    void HolsterSecondaryVisual()
    {
        if (secondaryInstance == null) return;
        Transform point = ResolveHolster(secondaryWeaponPrefab);
        secondaryInstance.transform.SetParent(point, false);
        secondaryInstance.transform.localPosition = Vector3.zero;
        secondaryInstance.transform.localRotation = Quaternion.identity;
        secondaryInstance.SetActive(true);
        // Already all disabled (SpawnWeapon strips every script off any held instance),
        // but re-assert it in case something re-enabled one - a holstered weapon must
        // never itself run WeaponController/WeaponADS/etc.
        foreach (var mb in secondaryInstance.GetComponentsInChildren<MonoBehaviour>(true))
            mb.enabled = false;
    }

    /// <summary>Pistols go to the hip holster, everything else to the back - matched the
    /// same way WeaponInventory.SlotFor classifies weapons on the player, so an ally
    /// carrying a rifle and a pistol wears them the same way the player does.</summary>
    static bool IsSecondaryClass(GameObject weapon)
    {
        if (weapon == null) return false;
        string n = weapon.name.ToLowerInvariant();
        return n.Contains("mono19") || n.Contains("pistol");
    }

    Transform ResolveHolster(GameObject weaponPrefabOrInstance)
    {
        bool secondaryClass = IsSecondaryClass(weaponPrefabOrInstance);
        Transform assigned = secondaryClass ? secondaryHolsterPoint : primaryHolsterPoint;
        if (assigned != null) return assigned;

        var points = GetComponentInChildren<CharacterAttachPoints>();
        var named = points != null ? points.Get(secondaryClass ? "HipHolster" : "BackHolster") : null;
        if (named != null) return named;

        return transform; // last resort - assign primaryHolsterPoint/secondaryHolsterPoint for a real spot
    }

    /// <summary>The currently held weapon's WeaponController, for comparing this
    /// weapon against a candidate pickup (see FriendlyAI's auto-upgrade logic).</summary>
    public WeaponController GetWeaponController() =>
        weaponInstance != null ? weaponInstance.GetComponent<WeaponController>() : null;

    void Update()
    {
        if (anchor == null) return;

        // Back to the primary once the fighting's over (a sidearm is for emergencies).
        if (combatReady) calmTimer = 0f; else calmTimer += Time.deltaTime;
        if (!combatReady && calmTimer > returnToPrimaryDelay && !reloading && !dropped
            && secondaryWeaponPrefab != null && IsSecondaryClass(weaponPrefab) && !IsSecondaryClass(secondaryWeaponPrefab))
            StartCoroutine(SwitchRoutine());

        // Aiming only counts while the gun is actually up.
        float target = (aiming && !reloading) ? 1f : 0f;
        aimBlend = Mathf.MoveTowards(aimBlend, target, aimBlendSpeed * Time.deltaTime);
        combatBlend = Mathf.MoveTowards(combatBlend, combatReady ? 1f : 0f, lowerBlendSpeed * Time.deltaTime);

        if (weaponADS != null)
        {
            // Player-identical hold: the weapon's own WeaponADS composes hip/ADS + bob +
            // pull-back under this anchor; the anchor itself acts as the player's camera,
            // sitting at eye position and pitching towards the target.
            weaponADS.SetExternalAim(aiming && !reloading);
            UpdateEyePitch();

            // Lowered pose: same world pose the gun had before (the anchor is offset to
            // cancel WeaponADS's hip offset, so it still ends up where loweredPositionOffset says).
            Quaternion hipRot = Quaternion.Euler(weaponADS.hipRotation);
            Quaternion lowRot = Quaternion.Euler(loweredRotationOffset) * Quaternion.Inverse(hipRot);
            Vector3 lowPos = loweredPositionOffset - lowRot * weaponADS.hipPosition;

            anchor.localPosition = Vector3.Lerp(lowPos, eyeLocalPosition, combatBlend);
            anchor.localRotation = Quaternion.Slerp(lowRot, Quaternion.Euler(eyePitch, 0f, 0f), combatBlend);
            return;
        }

        // Legacy fixed-anchor hold (no WeaponADS on the prefab, or useEyeRigHold off).
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
    /// <summary>Gun pitch towards the target, degrees, + = down (same convention as the player's camera pitch).</summary>
    public float EyePitch => eyePitch;
    public bool IsAimingNow => aiming && !reloading;
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

        // In a fight, an empty gun swaps to the spare (if it has rounds) rather than
        // spending seconds reloading in the open.
        if (switchInsteadOfReload && combatReady && ammo <= 0 && SpareHasRounds)
        {
            StartCoroutine(SwitchRoutine());
            return;
        }
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

                Vector3 seatPos = weaponReloadPoint != null ? weaponReloadPoint.position
                    : (gripPoint != null ? gripPoint.position : gripStart);
                Quaternion seatRot = weaponReloadPoint != null ? weaponReloadPoint.rotation
                    : (gripPoint != null ? gripPoint.rotation : gripStartRot);

                Vector3 fromPos, toPos; Quaternion fromRot, toRot; float segStart, segEnd;
                if (frac <= handGrabTime)
                {
                    // Live grip, not the snapshot from reload start - the gun moves as the
                    // AI turns/pitches, and a stale world position drags the hand back there.
                    fromPos = gripPoint != null ? gripPoint.position : gripStart;
                    fromRot = gripPoint != null ? gripPoint.rotation : gripStartRot;
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
        if (secondaryInstance != null)
            secondaryInstance.SetActive(false);
    }

    void OnDestroy()
    {
        if (reloadHandAnchor != null) Destroy(reloadHandAnchor.gameObject);
    }
}