using UnityEngine;

/// <summary>
/// Real turn-in-place footwork: while the character is idle (see CharacterLocomotion.IsIdle)
/// and its facing is changing - because PlayerMovement's camera-follow rotation, or Enemy.cs/
/// FriendlyAI.cs's own Quaternion.Slerp-toward-target rotation, is turning the transform -
/// this fades in the TurnInPlace Animator layer (built by AnimationSystemBuilder.BuildTurnLayer,
/// masked to legs only) and feeds it a signed turn rate, so the legs actually pivot-step
/// instead of the whole body silently swivelling under a locomotion tree that has no idea
/// a turn is happening.
///
/// Deliberately does NOT rotate anything itself - it only WATCHES the transform's yaw and
/// reflects it. Whatever is already turning the character (camera look, AI facing) keeps
/// doing exactly what it did before this component existed, so PlayerMovement/Enemy.cs/
/// FriendlyAI.cs need no changes at all.
///
/// Attach next to CharacterLocomotion on the same object as the Animator - generic, works
/// on Player and enemies/allies alike. Requires the animation system to have been rebuilt
/// with the TurnInPlace layer (Tools/FPS Game/Build Animation System) and that controller
/// re-assigned - logs a warning and disables itself if the layer isn't found.
/// </summary>
[RequireComponent(typeof(Animator))]
public class TurnInPlace : MonoBehaviour
{
    public Animator animator;
    public CharacterLocomotion locomotion;

    [Tooltip("Yaw rate (deg/s) below which the character counts as not turning at all.")]
    public float minTurnRate = 12f;
    [Tooltip("Yaw rate (deg/s) at which the turn layer is fully weighted (1). Between minTurnRate and this, weight ramps linearly - a slow drift barely shows, a fast spin-around shows fully.")]
    public float fullTurnRate = 140f;
    [Tooltip("Cap passed to the Animator's TurnSpeed parameter (deg/s). Only the SIGN matters to the blend tree (see AnimationSystemBuilder.Turn1D), so this just keeps the value sane.")]
    public float maxTurnSpeedParam = 200f;
    [Tooltip("How quickly the layer weight itself follows the target above (seconds, smoothing time).")]
    public float weightSmoothing = 0.08f;
    [Tooltip("Time constant (seconds) for smoothing the measured yaw rate. Replaces the old fixed sample window - this is now a continuous exponential average, not a periodic reset, so it can't create rhythmic zero-dips while turning.")]
    public float sampleWindow = 0.1f;
    [Tooltip("How quickly the TurnSpeed parameter itself is allowed to change (deg/s per second). Keeps a single noisy frame of the smoothed rate from snapping the blend tree.")]
    public float paramSmoothing = 600f;

    int layerIndex = -1;
    float layerWeight;
    float prevYaw;
    float smoothedRate;   // continuous EMA of yaw rate, deg/s
    float paramValue;     // what's actually sent to the Animator, further damped
    static readonly int TurnSpeedHash = Animator.StringToHash("TurnSpeed");

    [Header("Debug (read-only, watch these in Play Mode)")]
    [Tooltip("The Animator this component ended up controlling - select THIS object's row in the Animator window's target dropdown to see its real live state, not whatever object you had selected before.")]
    [SerializeField] Animator debugResolvedAnimator;
    [Tooltip("Measured yaw rate, deg/s. Should move whenever the character visibly turns.")]
    [SerializeField] float debugTurnRate;
    [Tooltip("Current TurnInPlace layer weight. If this stays at 0 while turning, the state machine can show a turn state as 'active' with zero visible effect - weight is what actually matters.")]
    [SerializeField] float debugLayerWeight;

    void Start()
    {
        if (animator == null)
        {
            // Same reasoning as AnatomicalConstraints/TorsoMotionDampener: ask
            // CharacterAnimationDriver for the Animator it already correctly resolved (it
            // specifically filters out the stray, no-controller Animator some rigs in this
            // project carry on the root - see the comment in CharacterAnimationDriver.Awake),
            // falling back to a plain Humanoid-with-a-controller hunt only if there's no
            // driver on this object at all.
            var driver = GetComponentInChildren<CharacterAnimationDriver>();
            if (driver != null) animator = driver.BodyAnimator;
        }
        if (animator == null)
        {
            foreach (var candidate in GetComponentsInChildren<Animator>(true))
            {
                if (candidate.runtimeAnimatorController != null && candidate.avatar != null && candidate.avatar.isHuman) { animator = candidate; break; }
            }
        }
        if (locomotion == null) locomotion = GetComponentInParent<CharacterLocomotion>();

        if (animator == null)
        {
            Debug.LogWarning("[TurnInPlace] No Humanoid Animator found in this hierarchy.", this);
            enabled = false;
            return;
        }

        layerIndex = animator.GetLayerIndex("TurnInPlace");
        if (layerIndex < 0)
        {
            Debug.LogWarning("[TurnInPlace] No 'TurnInPlace' layer on this Animator - re-run Tools/FPS Game/Build Animation System and re-assign the controller.", this);
            enabled = false;
            return;
        }

        prevYaw = transform.eulerAngles.y;
        animator.SetLayerWeight(layerIndex, 0f);
        debugResolvedAnimator = animator;
    }

    void Update()
    {
        float yaw = transform.eulerAngles.y;

        // Instantaneous rate this frame, then folded into a continuous exponential moving
        // average using sampleWindow as the time constant. This replaces the old "reset the
        // baseline every sampleWindow seconds" approach: that reset made the very next frame's
        // reading effectively a single-frame instantaneous sample again, and if that one frame
        // happened to land between rotation updates (mouse-look ticks, AI Slerp steps) the
        // reading came back as 0 - rhythmically, once per window, even during a continuous
        // turn. That periodic zero is what was snapping the blend tree back toward the idle
        // pose and made a turn look like it kept resetting. An EMA has no reset point, so it
        // can't produce that artifact - it just smoothly tracks the true rate.
        float instRate = Time.deltaTime > 1e-5f ? Mathf.DeltaAngle(prevYaw, yaw) / Time.deltaTime : 0f;
        prevYaw = yaw;
        float rateSmoothing = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(sampleWindow, 0.01f));
        smoothedRate = Mathf.Lerp(smoothedRate, instRate, rateSmoothing);

        bool idle = locomotion == null || locomotion.IsIdle;
        float absRate = Mathf.Abs(smoothedRate);
        float target = (idle && absRate > minTurnRate)
            ? Mathf.InverseLerp(minTurnRate, fullTurnRate, absRate)
            : 0f;

        layerWeight = Mathf.MoveTowards(layerWeight, target, Time.deltaTime / Mathf.Max(weightSmoothing, 0.01f));
        animator.SetLayerWeight(layerIndex, layerWeight);

        // Second layer of smoothing on the value actually sent to the Animator - belt and
        // braces against any single-frame blip in smoothedRate still visibly twitching the
        // blend tree, without adding noticeable lag (paramSmoothing is a deg/s-per-second rate,
        // not a time constant, so it only caps how fast the param can change).
        float targetParam = Mathf.Clamp(smoothedRate, -maxTurnSpeedParam, maxTurnSpeedParam);
        paramValue = Mathf.MoveTowards(paramValue, targetParam, paramSmoothing * Time.deltaTime);
        animator.SetFloat(TurnSpeedHash, paramValue);

        debugTurnRate = smoothedRate;
        debugLayerWeight = layerWeight;
    }
}