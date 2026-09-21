using UnityEngine;

/// <summary>
/// Blends the camera with what the body is actually doing, so the torso stops passing
/// through the camera during the injured locomotion.
///
/// It follows the HIPS bone's deviation from its own slowly-averaged resting spot, not
/// the head: the head sits inside the UpperBody mask, so reload/fire/melee clips throw
/// it around and a head-driven camera lurches every time you reload. The hips are driven
/// by the base locomotion layer only, and the injured walk's hunch and sway show up
/// there clearly enough to follow.
///
/// Defaults are conservative - injuredFollow plus the small forward push are usually
/// enough to keep the chest out of the near plane. Pair it with TorsoMotionDampener if
/// the injured torso swing itself is still too strong.
///
/// Read in Update (one frame old) on purpose: doing it in LateUpdate would move the
/// camera, and therefore the camera-parented weapon, AFTER the IK pass had already put
/// the hands on the gun - the exact mismatch that reads as jitter.
///
/// SETUP: put it on the camera, next to CameraLean/CameraRecoil.
/// </summary>
[DefaultExecutionOrder(-160)]
public class CameraBodyFollow : MonoBehaviour
{
    [Header("Follow Amounts (0-1 of the hips' own movement)")]
    [Tooltip("How much the camera copies normally. Keep low or 0 - normal locomotion shouldn't move the view much.")]
    [Range(0f, 1f)] public float normalFollow = 0.08f;
    [Tooltip("How much the camera copies while injured, so it hunches along with the body instead of the body hunching into it.")]
    [Range(0f, 1f)] public float injuredFollow = 0.45f;
    [Tooltip("Extra forward push (metres) while injured, since the injured pose leans the chest straight into the camera.")]
    public float injuredForwardPush = 0.07f;

    [Header("Limits")]
    public float maxOffset = 0.15f;
    [Tooltip("Seconds of smoothing on the offset.")]
    public float smoothTime = 0.12f;
    [Tooltip("How fast the resting reference tracks the hips. Low on purpose - posture over seconds, not the per-step bob.")]
    public float restingDrift = 0.5f;
    [Tooltip("How fast the injured/normal blend crossfades.")]
    public float injuredBlendSpeed = 2f;

    [Header("References")]
    [Tooltip("Auto-found on the player root if left empty.")]
    public PlayerMovement playerMovement;
    public PlayerHealth playerHealth;
    [Tooltip("Health fraction at or below which the injured blend is used. Match PlayerSetup.InjuredHealthFraction.")]
    [Range(0f, 1f)] public float injuredHealthFraction = 0.3f;

    Transform animRoot;
    Transform hipsBone;
    Vector3 restingLocal;
    bool hasResting;
    Vector3 currentOffset, offsetVelocity;
    float injuredBlend;
    float targetInjured;

    void Start()
    {
        if (playerMovement == null) playerMovement = GetComponentInParent<PlayerMovement>();
        if (playerMovement == null) { enabled = false; return; }

        if (playerHealth == null) playerHealth = playerMovement.GetComponent<PlayerHealth>();
        if (playerHealth != null) playerHealth.onHealthChanged += OnHealthChanged;

        foreach (var a in playerMovement.GetComponentsInChildren<Animator>(true))
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

    void OnHealthChanged(float fraction) => targetInjured = fraction <= injuredHealthFraction ? 1f : 0f;

    void Update()
    {
        injuredBlend = Mathf.MoveTowards(injuredBlend, targetInjured, injuredBlendSpeed * Time.deltaTime);

        Vector3 local = animRoot.InverseTransformPoint(hipsBone.position);
        if (!hasResting) { restingLocal = local; hasResting = true; }
        restingLocal = Vector3.Lerp(restingLocal, local, restingDrift * Time.deltaTime);

        Vector3 deviation = local - restingLocal;
        // The torso's own sideways sway is cancelled by TorsoPoseDriver; following it here
        // would swing the camera (and gun) against a torso that is no longer moving.
        var stab = TorsoPoseDriver.Instance;
        if (stab != null && stab.stabilizeBody) deviation.x *= 1f - stab.stabilizeSway;

        float follow = Mathf.Lerp(normalFollow, injuredFollow, injuredBlend);
        Vector3 world = animRoot.TransformVector(deviation) * follow;
        if (transform.parent != null)
            world += transform.parent.forward * (injuredForwardPush * injuredBlend);

        Vector3 targetLocal = transform.parent != null
            ? transform.parent.InverseTransformVector(world)
            : world;
        targetLocal = Vector3.ClampMagnitude(targetLocal, maxOffset);

        currentOffset = Vector3.SmoothDamp(currentOffset, targetLocal, ref offsetVelocity,
            Mathf.Max(0.01f, smoothTime), Mathf.Infinity, Time.deltaTime);
        playerMovement.SetCameraPositionOffset(currentOffset);
    }

    void OnDisable() => playerMovement?.SetCameraPositionOffset(Vector3.zero);

    void OnDestroy()
    {
        if (playerHealth != null) playerHealth.onHealthChanged -= OnHealthChanged;
    }
}