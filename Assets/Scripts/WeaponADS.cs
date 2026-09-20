using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// SINGLE OWNER of the weapon root's local position/rotation.
///
/// Everything else that wants to move the weapon root (sway, walk/run bob, the hand-IK
/// pull-back) now pushes an offset in here instead of writing the transform, and this
/// script composes them all once per Update from its own internal state. Two things
/// that matters for:
///  - Nothing reads the transform back as its own smoothing input any more, so the
///    offsets can't feed back into each other (the old ADS lerp read
///    transform.localPosition, which by then contained sway + pull-back - that loop is
///    what made the gun shake/jump while looking up and walking).
///  - The weapon is at its final pose for the frame BEFORE the Animator's IK pass runs,
///    so WeaponHandIK solves the hands against the position that actually gets rendered
///    rather than one that changes again in LateUpdate. That one-frame mismatch was the
///    rest of the visible hand jitter.
/// </summary>
[DefaultExecutionOrder(-80)]
public class WeaponADS : MonoBehaviour
{
    [Header("Body Animation")]
    public CharacterAnimationDriver animationDriver; // auto-found on player if empty

    [Header("Positions")]
    public Vector3 hipPosition = new Vector3(0f, -0.1f, 0f);
    public Vector3 adsPosition = new Vector3(0f, 0.03f, 0.2f);

    [Header("Rotations")]
    public Vector3 hipRotation = new Vector3(0f, 0f, 35f);
    public Vector3 adsRotation = new Vector3(0f, 0f, 0f);

    [Header("Settings")]
    public float adsSpeed = 10f; // How fast it snaps to ADS
    public float hipSpeed = 7f; // Slightly slower return to hip

    [Header("FOV")]
    public Camera fpCamera;
    public float hipFOV = 60f;
    public float adsFOV = 50f; // Zoom in slightly when aiming

    [Header("Cant")]
    public WeaponCant weaponCant;

    private InputAction aimAction;
    private bool isAiming = false;

    // AI mode (EnemyWeapon): the same component that holds the player's tuned hip/ADS
    // pose also holds allies' and enemies' guns, so they sit exactly like the player's.
    // Aim comes from code instead of the mouse, and it never touches the player's
    // camera FOV or the player's animation driver.
    bool externalControl;
    bool externalAim;
    public void SetExternalControl(bool on)
    {
        externalControl = on;
        if (!on) return;
        aimAction?.Disable();
        fpCamera = null;
        animationDriver = null;
    }
    public void SetExternalAim(bool aiming) => externalAim = aiming;

    // Own state - the aim lerp runs on these, never on the live transform.
    private Vector3 basePosition;
    private Quaternion baseRotation = Quaternion.identity;

    // Offset channels pushed in by other components.
    private Vector3 swayOffset;        // WeaponSway (local space)
    private Vector3 bobOffset;         // WeaponMovementBob (local space)
    private Quaternion bobRotation = Quaternion.identity;
    private Vector3 pullBackWorld;     // WeaponHandIK (world space direction/magnitude)
    private float poseBlend;           // WeaponInventory holster/draw: 0 = normal pose, 1 = fully at poseWorldPos/Rot
    private Vector3 poseWorldPos;
    private Quaternion poseWorldRot = Quaternion.identity;

    void Awake()
    {
        if (fpCamera == null) fpCamera = Camera.main;
        if (animationDriver == null) animationDriver = GetComponentInParent<CharacterAnimationDriver>();
        if (animationDriver == null) animationDriver = FindFirstObjectByType<CharacterAnimationDriver>();

        // Right mouse to aim - see PlayerInputMap for the full layout (shift is sprint now).
        aimAction = new InputAction("Aim", binding: PlayerInputMap.Aim);
        aimAction.Enable();
    }

    void Start()
    {
        // Start in hip position
        basePosition = hipPosition;
        baseRotation = Quaternion.Euler(hipRotation);
        ApplyTransform();
    }

    // ------------------------------------------------------------------
    // Offset channels. Each caller owns exactly one and just keeps it up to date;
    // order between them is irrelevant because they're composed here, not stacked
    // onto the transform.
    // ------------------------------------------------------------------

    /// <summary>Local-space sway offset (WeaponSway).</summary>
    public void SetSwayOffset(Vector3 localOffset) => swayOffset = localOffset;

    /// <summary>Local-space walk/run bob offset and rotation (WeaponMovementBob).</summary>
    public void SetBobOffset(Vector3 localOffset, Quaternion localRotation)
    {
        bobOffset = localOffset;
        bobRotation = localRotation;
    }

    /// <summary>World-space pull-back applied when the hands can't reach the grip
    /// (WeaponHandIK). Converted into this transform's parent space here.</summary>
    public void SetPullBackWorldOffset(Vector3 worldOffset) => pullBackWorld = worldOffset;

    /// <summary>Blends the finished weapon pose towards a world-space pose (holster / draw
    /// animation). Composed last, here, so this script stays the single owner of the transform.</summary>
    public void SetWorldPoseBlend(float blend, Vector3 worldPos, Quaternion worldRot)
    {
        poseBlend = Mathf.Clamp01(blend);
        poseWorldPos = worldPos;
        poseWorldRot = worldRot;
    }

    /// <summary>The aim/hip pose with no offsets applied - what the weapon would sit at
    /// if nothing were nudging it.</summary>
    public Vector3 BaseLocalPosition => basePosition;

    void Update()
    {
        bool wasAiming = isAiming;
        isAiming = externalControl ? externalAim : aimAction.ReadValue<float>() > 0.5f;
        if (isAiming != wasAiming && !externalControl) animationDriver?.SetAiming(isAiming);

        Vector3 targetPos = isAiming ? adsPosition : hipPosition;
        Vector3 targetRot = isAiming ? adsRotation : hipRotation;
        float   speed     = isAiming ? adsSpeed    : hipSpeed;

        // Get cant fraction from WeaponCant
        float cantFraction = weaponCant != null ? weaponCant.GetCantFraction() : 0f;
        bool  isCanted     = weaponCant != null && weaponCant.IsCanted();

        if (isAiming)
        {
            targetRot.z = 0f;
        }
        else
        {
            // Reduces hip fire rotation when canting left
            targetRot.z = isCanted && cantFraction > 0f
                ? Mathf.Lerp(hipRotation.z, 0f, cantFraction)
                : hipRotation.z;
        }

        basePosition = Vector3.Lerp(basePosition, targetPos, speed * Time.deltaTime);
        baseRotation = Quaternion.Lerp(baseRotation, Quaternion.Euler(targetRot), speed * Time.deltaTime);

        ApplyTransform();

        if (fpCamera != null)
            fpCamera.fieldOfView = Mathf.Lerp(
                fpCamera.fieldOfView,
                isAiming ? adsFOV : hipFOV,
                speed * Time.deltaTime);
    }

    void ApplyTransform()
    {
        Vector3 pullLocal = transform.parent != null
            ? transform.parent.InverseTransformVector(pullBackWorld)
            : pullBackWorld;

        transform.localPosition = basePosition + swayOffset + bobOffset + pullLocal;
        transform.localRotation = baseRotation * bobRotation;

        if (poseBlend > 0f)
        {
            transform.position = Vector3.Lerp(transform.position, poseWorldPos, poseBlend);
            transform.rotation = Quaternion.Slerp(transform.rotation, poseWorldRot, poseBlend);
        }
    }

    public bool IsAiming() => isAiming;

    void OnDestroy() => aimAction.Disable();
}