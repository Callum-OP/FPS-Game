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
[RequireComponent(typeof(EnemyAI))]
public class EnemyWeapon : MonoBehaviour
{
    [Header("Held weapon (visual only)")]
    [Tooltip("One of the player's held-weapon prefabs, e.g. AR.prefab or Mono19.prefab.")]
    public GameObject weaponPrefab;
    [Tooltip("Local position of the weapon anchor relative to the enemy's root while resting/running - roughly chest height, slightly forward. Tune by eye in Play Mode.")]
    public Vector3 restPositionOffset = new Vector3(0.15f, 1.1f, 0.3f);
    [Tooltip("Local rotation of the weapon anchor relative to the enemy's root while resting/running.")]
    public Vector3 restRotationOffset;
    [Tooltip("Local position of the weapon anchor while aiming - same idea as the player's WeaponADS.adsPosition. Usually just restPositionOffset raised/pushed forward slightly.")]
    public Vector3 aimPositionOffset = new Vector3(0.1f, 1.18f, 0.4f);
    [Tooltip("Local rotation of the weapon anchor while aiming.")]
    public Vector3 aimRotationOffset;
    [Tooltip("How fast the anchor blends between resting and aiming.")]
    public float aimBlendSpeed = 8f;

    [Header("Drop on death")]
    [Tooltip("The matching *Pickup prefab, e.g. ARPickup.prefab - what actually spawns in the world.")]
    public GameObject worldPickupPrefab;

    EnemyAI enemyAI;
    WeaponHandIK handIK;
    GameObject weaponInstance;
    Transform anchor;
    bool aiming;
    float aimBlend;
    bool dropped;

    void Start()
    {
        if (weaponPrefab == null) return;

        enemyAI = GetComponent<EnemyAI>();

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

        weaponInstance = Instantiate(weaponPrefab, anchor);
        weaponInstance.transform.localPosition = Vector3.zero;
        weaponInstance.transform.localRotation = Quaternion.identity;

        // Pull the grip/muzzle transforms off the weapon's own WeaponController
        // before stripping every script it brought with it (WeaponController,
        // WeaponADS, WeaponCant... all read player input/camera state, which
        // an enemy doesn't have and shouldn't react to).
        WeaponController wc = weaponInstance.GetComponent<WeaponController>();
        if (wc != null)
        {
            handIK.SetGripTargets(wc.rightHandGrip, wc.leftHandGrip);
            // The gun now sits in a fixed, known spot instead of wherever the hand
            // animation happened to leave it, so its own muzzle point is a more
            // reliable bullet-spawn reference than a hand-placed empty transform -
            // auto-wire it in rather than leaving Enemy.muzzlePoint stale.
            if (wc.muzzlePoint != null && enemyAI != null)
                enemyAI.muzzlePoint = wc.muzzlePoint;
        }

        foreach (var mb in weaponInstance.GetComponentsInChildren<MonoBehaviour>(true))
            mb.enabled = false;
    }

    void Update()
    {
        if (anchor == null) return;

        float target = aiming ? 1f : 0f;
        aimBlend = Mathf.MoveTowards(aimBlend, target, aimBlendSpeed * Time.deltaTime);

        anchor.localPosition = Vector3.Lerp(restPositionOffset, aimPositionOffset, aimBlend);
        anchor.localRotation = Quaternion.Slerp(
            Quaternion.Euler(restRotationOffset), Quaternion.Euler(aimRotationOffset), aimBlend);
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
}