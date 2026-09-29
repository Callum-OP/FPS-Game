using UnityEngine;

/// <summary>
/// Relaxed idle: while an enemy/ally is genuinely idle (CharacterLocomotion.IsIdle) AND out of
/// combat with the weapon actually all the way down (EnemyWeapon.IsCombatReady false AND
/// CombatBlend settled near 0, not just "just told to lower") this:
///
///  1. Fades in Unity's built-in Humanoid look-at IK and has the character glance around at a
///     slowly-changing point out in front of itself - the head/eyes turn, the body barely
///     follows. Always on while relaxed; not affected by playIdleGestures below.
///
///  2. If playIdleGestures is true, ALSO occasionally crossfades into a full-body idle-gesture
///     clip (see CharacterAnimationDriver.CrossFadeBase) - "Check Shoe" (right hand only: the
///     left stays on the weapon grip) or "Arm Stretching" (both hands - only offered while
///     unarmed or holding a pistol, since a rifle needs both hands on the grip to make sense
///     held). Only whichever of those clips actually exist on the controller (checked via
///     Animator.HasState) are ever picked, so a name that doesn't match the live project is
///     silently skipped rather than breaking anything.
///
/// playIdleGestures is exposed separately (default true) so a character can be set to just
/// glance around without ever stopping to play a gesture clip.
///
/// Does NOT touch Enemy.cs/FriendlyAI.cs - purely additive. Add it manually next to Animator/
/// CharacterLocomotion/EnemyWeapon on enemy and ally prefabs. DOES need the animation system
/// rebuilt at least once for the gesture half (the Gesture_* states have to exist on the
/// controller) - the look-at glancing half works with no rebuild.
/// </summary>
[RequireComponent(typeof(Animator))]
public class AIRelaxedIdle : MonoBehaviour
{
    public Animator animator;
    public CharacterLocomotion locomotion;
    public EnemyWeapon weapon;
    public CharacterAnimationDriver driver;

    [Header("When it's allowed to relax")]
    [Tooltip("Extra seconds of being fully idle AND fully weapon-lowered before glancing/gestures start.")]
    public float settleDelay = 1f;
    [Tooltip("CombatBlend must be below this (see EnemyWeapon) to count as 'fully lowered'.")]
    public float combatBlendThreshold = 0.02f;

    [Header("Glance behaviour (always on while relaxed)")]
    public Vector2 glanceIntervalRange = new Vector2(2.5f, 5f);
    public float glanceYawRange = 50f;
    public float glancePitchRange = 15f;
    public float glanceDistance = 3f;
    public float weightSmoothing = 0.6f;

    [Header("Look-at weights (see Animator.SetLookAtWeight)")]
    [Range(0f, 1f)] public float bodyWeight = 0.1f;
    [Range(0f, 1f)] public float headWeight = 0.6f;
    [Range(0f, 1f)] public float eyesWeight = 1f;
    [Range(0f, 1f)] public float clampWeight = 0.5f;

    [System.Serializable]
    public struct GestureOption
    {
        public string state;
        [Tooltip("How much of each arm follows the clip - the Right Hand gesture leaves the left on the weapon grip; Both Hands frees both.")]
        [Range(0f, 1f)] public float rightFollow, leftFollow;
        [Tooltip("If set, this gesture is only offered while unarmed or holding a pistol - a rifle needs both hands to make sense held.")]
        public bool pistolOrUnarmedOnly;
    }

    [Header("Idle gestures (optional - untick for glance-only)")]
    public bool playIdleGestures = true;
    public Vector2 gestureIntervalRange = new Vector2(8f, 18f);
    [Tooltip("Candidates checked at Start via Animator.HasState - only ones that actually exist on the controller are used.")]
    public GestureOption[] gestureOptions =
    {
        new GestureOption { state = "Gesture_CheckShoe",  rightFollow = 1f, leftFollow = 0f, pistolOrUnarmedOnly = false },
        new GestureOption { state = "Gesture_ArmStretch", rightFollow = 1f, leftFollow = 1f, pistolOrUnarmedOnly = true },
    };
    public float gestureFallbackDuration = 3f;

    float settleTimer, glanceTimer, lookWeight;
    Vector3 glanceLocalDir;

    GestureOption[] validGestures;
    int currentGestureHash = -1;
    float gestureTimer, gestureEndTime;
    bool gesturing;

    [Header("Debug (read-only)")]
    [SerializeField] bool debugIsGlancing;
    [SerializeField] float debugLookWeight;
    [SerializeField] bool debugIsGesturing;
    [SerializeField] int debugValidGestureCount;

    void Start()
    {
        if (animator == null)
        {
            var d = GetComponentInChildren<CharacterAnimationDriver>();
            if (d != null) animator = d.BodyAnimator;
        }
        if (animator == null)
        {
            foreach (var candidate in GetComponentsInChildren<Animator>(true))
                if (candidate.runtimeAnimatorController != null && candidate.avatar != null && candidate.avatar.isHuman) { animator = candidate; break; }
        }
        if (locomotion == null) locomotion = GetComponentInParent<CharacterLocomotion>();
        if (locomotion == null) locomotion = GetComponentInChildren<CharacterLocomotion>();
        if (weapon == null) weapon = GetComponentInParent<EnemyWeapon>();
        if (weapon == null) weapon = GetComponentInChildren<EnemyWeapon>();
        if (driver == null) driver = GetComponentInParent<CharacterAnimationDriver>();
        if (driver == null) driver = GetComponentInChildren<CharacterAnimationDriver>();

        if (animator == null)
        {
            Debug.LogWarning("[AIRelaxedIdle] No Humanoid Animator found in this hierarchy.", this);
            enabled = false;
            return;
        }

        if (animator.gameObject != gameObject)
        {
            var bridge = animator.gameObject.GetComponent<AIRelaxedIdleAnimatorBridge>();
            if (bridge == null) bridge = animator.gameObject.AddComponent<AIRelaxedIdleAnimatorBridge>();
            bridge.owner = this;
        }

        var valid = new System.Collections.Generic.List<GestureOption>();
        if (driver != null && gestureOptions != null)
            foreach (var g in gestureOptions)
                if (!string.IsNullOrEmpty(g.state) && driver.HasBaseState(g.state)) valid.Add(g);
        validGestures = valid.ToArray();
        debugValidGestureCount = validGestures.Length;
        if (playIdleGestures && validGestures.Length == 0)
            Debug.Log("[AIRelaxedIdle] playIdleGestures is on but none of gestureOptions exist on the base layer - rebuild the animation system, or check the gest_* clips actually imported.", this);

        PickNewGlanceTarget();
        PickNewGestureTimer();
    }

    void Update()
    {
        if (animator == null) return;

        bool idle = locomotion == null || locomotion.IsIdle;
        bool weaponDown = weapon == null || (!weapon.IsCombatReady && weapon.CombatBlend < combatBlendThreshold);
        bool relaxed = idle && weaponDown;

        settleTimer = relaxed ? settleTimer + Time.deltaTime : 0f;
        bool settled = relaxed && settleTimer >= settleDelay;

        glanceTimer -= Time.deltaTime;
        if (settled && glanceTimer <= 0f) PickNewGlanceTarget();

        float targetWeight = settled ? 1f : 0f;
        lookWeight = Mathf.MoveTowards(lookWeight, targetWeight, Time.deltaTime / Mathf.Max(weightSmoothing, 0.01f));

        if (gesturing)
        {
            if (currentGestureHash >= 0)
            {
                var info = animator.GetCurrentAnimatorStateInfo(0);
                if (info.shortNameHash == currentGestureHash && info.length > 0.01f)
                    gestureEndTime = Time.time + Mathf.Max(0f, (1f - info.normalizedTime) * info.length);
            }
            if (!relaxed || Time.time >= gestureEndTime) EndGesture();
        }
        else if (settled && playIdleGestures && validGestures.Length > 0 && (driver == null || !driver.InFullBodyAction))
        {
            gestureTimer -= Time.deltaTime;
            if (gestureTimer <= 0f) StartGesture();
        }

        debugIsGlancing = settled;
        debugLookWeight = lookWeight;
        debugIsGesturing = gesturing;
    }

    void PickNewGlanceTarget()
    {
        glanceTimer = Random.Range(glanceIntervalRange.x, glanceIntervalRange.y);
        float yaw = Random.Range(-glanceYawRange, glanceYawRange);
        float pitch = Random.Range(-glancePitchRange, glancePitchRange);
        Quaternion rot = Quaternion.Euler(-pitch, yaw, 0f);
        glanceLocalDir = rot * Vector3.forward * glanceDistance;
    }

    void PickNewGestureTimer() => gestureTimer = Random.Range(gestureIntervalRange.x, gestureIntervalRange.y);

    void StartGesture()
    {
        bool canBothHands = driver == null || driver.CurrentWeaponClass != CharacterWeaponClass.Rifle;
        var pool = new System.Collections.Generic.List<GestureOption>();
        foreach (var g in validGestures)
            if (!g.pistolOrUnarmedOnly || canBothHands) pool.Add(g);
        if (pool.Count == 0) { PickNewGestureTimer(); return; }

        var pick = pool[Random.Range(0, pool.Count)];
        if (driver != null && driver.CrossFadeBase(pick.state, 0.25f, pick.rightFollow, pick.leftFollow))
        {
            currentGestureHash = Animator.StringToHash(pick.state);
            gesturing = true;
            gestureEndTime = Time.time + 0.25f + gestureFallbackDuration; // corrected from the real clip length once Update sees it playing
        }
        else PickNewGestureTimer();
    }

    void EndGesture()
    {
        gesturing = false;
        currentGestureHash = -1;
        driver?.EndFullBodyAction();
        PickNewGestureTimer();
    }

    /// <summary>Called from OnAnimatorIK, either directly or via AIRelaxedIdleAnimatorBridge forwarding
    /// from the real (possibly nested) Animator's GameObject.</summary>
    public void ApplyLookAt()
    {
        if (animator == null) return;

        animator.SetLookAtWeight(lookWeight, bodyWeight, headWeight, eyesWeight, clampWeight);
        if (lookWeight > 0.001f)
        {
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Vector3 origin = head != null ? head.position : transform.position + Vector3.up * 1.6f;
            Vector3 worldTarget = origin + transform.TransformDirection(glanceLocalDir);
            animator.SetLookAtPosition(worldTarget);
        }
    }

    void OnAnimatorIK(int layerIndex) => ApplyLookAt();
}

public class AIRelaxedIdleAnimatorBridge : MonoBehaviour
{
    public AIRelaxedIdle owner;
    void OnAnimatorIK(int layerIndex) => owner?.ApplyLookAt();
}