using UnityEngine;

/// <summary>
/// Moves the gun with the walk/run animation instead of leaving it hanging dead still.
///
/// It reads the HIPS bone's deviation from its own slowly-averaged resting spot. Hips,
/// specifically - not the chest:
///  - The hips are driven by the base (locomotion) layer only. The chest is inside the
///    UpperBody mask, so a reload/fire clip swings it hard, and a bob driven off it
///    lurches the gun forward mid-reload and then crawls back as the average catches up.
///    That's the "gun teleports forward away from the hands while reloading and walking"
///    - it only showed up while walking because the bob scales with movement speed.
///  - Hip bob is also what real FPS weapon bob is modelled on, so it lines up with the
///    footfalls the way Call of Duty's does.
///
/// Amounts are deliberately small and clamped hard. Sideways movement is scaled down
/// separately (lateralScale) because left/right weapon travel is the most obvious and
/// least pleasant kind - modern shooters use a few millimetres of it at most.
///
/// Output goes to WeaponADS, which owns the weapon root and composes every offset in one
/// place, and it's applied in Update so the hand IK solves onto the gun after the bob.
///
/// SETUP: one per weapon root (next to WeaponADS). Everything else auto-resolves.
/// </summary>
[DefaultExecutionOrder(-90)]
public class WeaponMovementBob : MonoBehaviour
{
    [Header("Amounts (small on purpose)")]
    [Tooltip("Fraction of the hips' own movement the gun copies. Around 0.2-0.4 reads like a modern shooter; past ~0.6 it starts to look floaty.")]
    [Range(0f, 1f)] public float positionAmount = 0.3f;
    [Tooltip("Extra scale on the sideways (left/right) part only. Low by default - lateral weapon travel is the most noticeable and least natural-looking axis.")]
    [Range(0f, 1f)] public float lateralScale = 0.25f;
    [Tooltip("Degrees of tilt per metre of offset. Small values add life without the muzzle wandering.")]
    public float rotationAmount = 35f;
    [Tooltip("Multiplier while aiming down sights.")]
    [Range(0f, 1f)] public float aimMultiplier = 0.2f;

    [Header("Speed Gate")]
    [Tooltip("Planar speed at which the bob is at full strength.")]
    public float fullEffectSpeed = 4f;
    [Tooltip("Bob amount while standing still.")]
    [Range(0f, 1f)] public float idleAmount = 0.15f;

    [Header("Limits")]
    [Tooltip("Hard cap on the offset in metres. Keep this small - it's the backstop that stops any animation spike throwing the gun across the screen.")]
    public float maxOffset = 0.03f;
    [Tooltip("Hard cap on the sideways part specifically, in metres.")]
    public float maxLateralOffset = 0.012f;
    [Tooltip("Seconds of smoothing. Higher is softer and laggier.")]
    public float smoothTime = 0.08f;
    [Tooltip("How fast the resting reference tracks the hips. Low on purpose - it should absorb posture changes over seconds, not the per-step motion this exists to show.")]
    public float restingDrift = 0.5f;

    [Header("References")]
    public WeaponADS weaponADS;
    public PlayerMovement playerMovement;
    [Tooltip("Set automatically on allies/enemies (no PlayerMovement there) - their NavMeshAgent supplies the speed instead.")]
    public UnityEngine.AI.NavMeshAgent aiAgent;

    Transform animRoot;
    Transform hipsBone;
    Vector3 restingLocal;
    bool hasResting;
    Vector3 currentOffset, offsetVelocity;
    Quaternion currentRotation = Quaternion.identity;

    void Start()
    {
        if (weaponADS == null) weaponADS = GetComponent<WeaponADS>();
        if (weaponADS == null) weaponADS = GetComponentInParent<WeaponADS>();
        if (playerMovement == null) playerMovement = GetComponentInParent<PlayerMovement>();
        if (playerMovement == null && aiAgent == null) aiAgent = GetComponentInParent<UnityEngine.AI.NavMeshAgent>();
        if (weaponADS == null || (playerMovement == null && aiAgent == null)) { enabled = false; return; }

        Transform bodyRoot = playerMovement != null ? playerMovement.transform : aiAgent.transform;
        foreach (var a in bodyRoot.GetComponentsInChildren<Animator>(true))
        {
            if (a.avatar != null && a.avatar.isHuman)
            {
                animRoot = a.transform;
                hipsBone = a.GetBoneTransform(HumanBodyBones.Hips);
                break;
            }
        }
        if (hipsBone == null) enabled = false;
    }

    void Update()
    {
        // Hips position in the body's own space - walking across the level isn't
        // "movement" for this purpose, only movement relative to the body is.
        Vector3 local = animRoot.InverseTransformPoint(hipsBone.position);
        if (!hasResting) { restingLocal = local; hasResting = true; }
        restingLocal = Vector3.Lerp(restingLocal, local, restingDrift * Time.deltaTime);

        Vector3 deviation = local - restingLocal;

        float speed = playerMovement != null
            ? playerMovement.PlanarVelocity.magnitude
            : new Vector2(aiAgent.velocity.x, aiAgent.velocity.z).magnitude;
        float speedScale = Mathf.Lerp(idleAmount, 1f, Mathf.Clamp01(speed / Mathf.Max(0.01f, fullEffectSpeed)));
        float aimScale = weaponADS.IsAiming() ? aimMultiplier : 1f;
        float scale = positionAmount * speedScale * aimScale;

        // Body-space deviation converted into the weapon's parent space (the camera).
        Vector3 world = animRoot.TransformVector(deviation) * scale;
        Vector3 targetLocal = transform.parent != null
            ? transform.parent.InverseTransformVector(world)
            : world;

        // Sideways handled separately and kept tiny - see lateralScale.
        targetLocal.x = Mathf.Clamp(targetLocal.x * lateralScale, -maxLateralOffset, maxLateralOffset);
        targetLocal = Vector3.ClampMagnitude(targetLocal, maxOffset);

        currentOffset = Vector3.SmoothDamp(currentOffset, targetLocal, ref offsetVelocity,
            Mathf.Max(0.01f, smoothTime), Mathf.Infinity, Time.deltaTime);

        // A little tilt from the same signal: vertical bob pitches the muzzle, sideways
        // sway rolls it.
        Quaternion targetRot = Quaternion.Euler(
            -currentOffset.y * rotationAmount,
            0f,
            -currentOffset.x * rotationAmount);
        currentRotation = Quaternion.Slerp(currentRotation, targetRot, 1f - Mathf.Exp(-12f * Time.deltaTime));

        weaponADS.SetBobOffset(currentOffset, currentRotation);
    }

    void OnDisable()
    {
        weaponADS?.SetBobOffset(Vector3.zero, Quaternion.identity);
    }
}