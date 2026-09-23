using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// SINGLE OWNER of the GunHolder transform (the weapon mesh pivot).
///
/// WeaponReloadHandler's reload move and WeaponHandIK's pull-back both used to write
/// this same transform additively in LateUpdate while this script lerped it from its
/// own current value in Update - so each frame this script smoothed toward the target
/// starting from a position that already contained their offsets, and they then
/// subtracted an offset that had partly been lerped away. That compounding error is a
/// direct cause of the gun drifting/jumping while walking and looking up. Now the lerp
/// runs on internal state and the reload move is pushed in through SetReloadOffset().
///
/// loweredHeight is the requested tunable: it sets how far down the gun sits while
/// lowered, and is live-editable in Play Mode.
/// </summary>
[DefaultExecutionOrder(-95)]
public class LowerWeapon : MonoBehaviour
{
    [Header("Lowered Pose")]
    [Tooltip("Height of the gun while lowered (local Y). Negative drops it further. This overrides loweredPosition.y, so it's the one number to change to raise/lower the resting gun - editable live in Play Mode.")]
    public float loweredHeight = -0.4f;
    [Tooltip("Lowered position. Its Y is ignored - loweredHeight above sets the height instead.")]
    public Vector3 loweredPosition = new Vector3(0f, -0.4f, 0.3f);
    public Vector3 loweredRotation = new Vector3(30f, 0f, 0f); // Tilt down

    [Header("Settings")]
    public float lowerSpeed = 8f;
    public float raisedSpeed = 10f;
    [Tooltip("Speed multiplier while lowered - this IS the sprint replacement now that there's no dedicated sprint key. Lowered further from 1.5 since even that was reading as faster than the old sprint used to be.")]
    public float fastWalkMultiplier = 1.15f; // Speed boost

    [Header("References")]
    public PlayerMovement playerMovement;
    public WeaponADS weaponADS;
    public WeaponController weaponController;

    private InputAction fireAction;

    private bool isLowered = false;
    private Vector3 originalPosition;
    private Quaternion originalRotation;

    // Own state - never read back off the transform.
    private Vector3 basePosition;
    private Quaternion baseRotation = Quaternion.identity;

    // Reload move pushed in by WeaponReloadHandler.
    private Vector3 reloadOffsetPos;
    private Quaternion reloadOffsetRot = Quaternion.identity;

    void Awake()
    {
        fireAction  = new InputAction("FireCheck",   binding: PlayerInputMap.Fire);

        fireAction.Enable();
    }

    bool restCaptured;

    /// <summary>Records the pivot's authored (un-lowered) pose. Called as early as possible
    /// (WeaponInventory.Store) so the rest pose can never be captured mid-lowered.</summary>
    public void CaptureRest()
    {
        if (restCaptured) return;
        originalPosition = transform.localPosition;
        originalRotation = transform.localRotation;
        restCaptured = true;
    }

    void Start()
    {
        CaptureRest();
        basePosition = originalPosition;
        baseRotation = originalRotation;
    }

    /// <summary>The held (non-reload) local pose - WeaponReloadHandler measures its move
    /// against this rather than against the live transform.</summary>
    public Vector3 BaseLocalPosition => basePosition;
    public Quaternion BaseLocalRotation => baseRotation;

    /// <summary>Additive reload move, composed here so it can't fight the lower/raise
    /// lerp. Pass Vector3.zero / Quaternion.identity to clear it.</summary>
    public void SetReloadOffset(Vector3 localPositionDelta, Quaternion localRotationDelta)
    {
        reloadOffsetPos = localPositionDelta;
        reloadOffsetRot = localRotationDelta;
    }

    void Update()
    {
        // Key 2 is read by WeaponInventory (tap = ToggleLowered, hold = holster).

        // Cancel lowered if player fires or aims
        if (isLowered)
        {
            bool aiming = weaponADS != null && weaponADS.IsAiming();
            bool firing = fireAction.WasPressedThisFrame();

            if (aiming || firing)
                isLowered = false;
        }

        float speed = isLowered ? lowerSpeed : raisedSpeed;

        Vector3 targetPos = isLowered
            ? new Vector3(loweredPosition.x, loweredHeight, loweredPosition.z)
            : originalPosition;
        Quaternion targetRot = isLowered ? Quaternion.Euler(loweredRotation) : originalRotation;

        basePosition = Vector3.Lerp(basePosition, targetPos, speed * Time.deltaTime);
        baseRotation = Quaternion.Lerp(baseRotation, targetRot, speed * Time.deltaTime);

        transform.localPosition = basePosition + reloadOffsetPos;
        transform.localRotation = reloadOffsetRot * baseRotation;

        // Apply speed boost to PlayerMovement
        if (playerMovement != null)
            playerMovement.SetSpeedMultiplier(isLowered ? fastWalkMultiplier : 1f);
    }

    public bool IsLowered() => isLowered;
    public void ToggleLowered() => isLowered = !isLowered;
    public void SetLowered(bool lowered) => isLowered = lowered;

    /// <summary>Snaps the gun pivot back to its normal pose and clears the lowered state. Used
    /// by holstering, which no longer carries a lowered pose with it.</summary>
    public void ResetToRest()
    {
        CaptureRest();
        isLowered = false;
        basePosition = originalPosition;
        baseRotation = originalRotation;
        reloadOffsetPos = Vector3.zero;
        reloadOffsetRot = Quaternion.identity;
        transform.localPosition = originalPosition;
        transform.localRotation = originalRotation;
    }

    void OnDestroy()
    {
        fireAction.Disable();
    }
}