using UnityEngine;

/// <summary>
/// Phase 3 (relaxed idle): while an enemy/ally is genuinely idle (CharacterLocomotion.IsIdle)
/// AND out of combat with the weapon actually all the way down (EnemyWeapon.IsCombatReady
/// false AND CombatBlend settled near 0, not just "just told to lower") this:
///
///  1. Fades in Unity's built-in Humanoid look-at IK and has the character glance around at a
///     slowly-changing point out in front of itself - the head/eyes turn, the body barely
///     follows. Always on while relaxed; not affected by playIdleGestures below.
///
///  2. If playIdleGestures is true, ALSO occasionally crossfades the masked UpperBody layer
///     (the same layer Aim/Fire/Reload/Melee already use) into a one-shot idle gesture clip -
///     "look away gesture", "weight shift" etc. from Gestures Pack Basic, and the "idle
///     looking" variants from Pro Melee Axe Pack Callum's original Phase 3 ask specifically
///     named - then back to UB_Idle when it's done. Only whichever of those clips actually
///     got imported (see the gest_* ClipDef entries in AnimationSystemBuilder) are ever
///     picked, checked at runtime via Animator.HasState, so a name that doesn't match what's
///     in the live project is silently skipped rather than breaking anything.
///
/// playIdleGestures is exposed separately (default true) so a character can be set to just
/// glance around without ever stopping to play a gesture clip, per Callum's ask.
///
/// Does NOT touch Enemy.cs/FriendlyAI.cs - purely additive, same spirit as TurnInPlace/
/// AnatomicalConstraints. Add it manually next to Animator/CharacterLocomotion/EnemyWeapon
/// on enemy and ally prefabs. DOES need Tools > FPS Game > Build Animation System re-run at
/// least once for the gesture half (the UB_Gesture* states have to exist on the controller) -
/// the look-at glancing half works with no rebuild.
/// </summary>
[RequireComponent(typeof(Animator))]
public class AIRelaxedIdle : MonoBehaviour
{
    public Animator animator;
    public CharacterLocomotion locomotion;
    public EnemyWeapon weapon;
    public WeaponHandIK handIK;

    [Header("When it's allowed to relax")]
    [Tooltip("Extra seconds of being fully idle AND fully weapon-lowered before glancing/gestures start - stops a character that just stopped walking or just finished lowering their gun from snapping straight into it.")]
    public float settleDelay = 1f;
    [Tooltip("CombatBlend must be below this (see EnemyWeapon) to count as 'fully lowered' - keeps relaxing from starting while the gun is still mid-way through lowering.")]
    public float combatBlendThreshold = 0.02f;

    [Header("Glance behaviour (always on while relaxed)")]
    [Tooltip("How often (min/max seconds) a new glance target is picked while relaxed.")]
    public Vector2 glanceIntervalRange = new Vector2(2.5f, 5f);
    [Tooltip("Horizontal degrees either side of forward the glance can aim.")]
    public float glanceYawRange = 50f;
    [Tooltip("Vertical degrees up/down from level the glance can aim.")]
    public float glancePitchRange = 15f;
    [Tooltip("Distance out in front to place the glance target - just needs to be far enough that the head/eye rotation reads clearly, doesn't need to hit anything real.")]
    public float glanceDistance = 3f;
    [Tooltip("Seconds (smoothing time) for the look-at weight to fade in when glancing starts and out when it stops.")]
    public float weightSmoothing = 0.6f;

    [Header("Look-at weights (see Animator.SetLookAtWeight)")]
    [Range(0f, 1f)] public float bodyWeight = 0.1f;
    [Range(0f, 1f)] public float headWeight = 0.6f;
    [Range(0f, 1f)] public float eyesWeight = 1f;
    [Tooltip("Unity's own clamp on how far the head/eyes are allowed to turn from the forward direction before the body has to help. 0.5 is Unity's default.")]
    [Range(0f, 1f)] public float clampWeight = 0.5f;

    [Header("Idle gestures (optional - untick for glance-only)")]
    [Tooltip("If off, this character only glances around (above) and never stops to play a full gesture clip. Enemies/allies you want relaxed without standing around doing something specific should have this unticked.")]
    public bool playIdleGestures = true;
    [Tooltip("How often (min/max seconds) a gesture is triggered while relaxed and playIdleGestures is on. Independent of the glance interval above.")]
    public Vector2 gestureIntervalRange = new Vector2(8f, 18f);
    [Tooltip("Names checked (via Animator.HasState) against the UpperBody layer at Start - only ones that actually exist get picked from. Matches the UB_Gesture* states AnimationSystemBuilder creates for whichever gest_* clips were found; edit freely to add/remove candidates without touching code.")]
    public string[] gestureStateNames = {
        "UB_GestureLookAway", "UB_GestureWeightShift", "UB_GestureSigh", "UB_GestureThoughtful",
        "UB_GestureMeleeLook1", "UB_GestureMeleeLook2", "UB_GestureUnarmedLook1", "UB_GestureUnarmedLook2",
    };
    [Tooltip("Crossfade time in/out of a gesture, seconds.")]
    public float gestureCrossfade = 0.25f;
    [Tooltip("Assumed length of a gesture clip if it can't be read from the Animator state directly (fallback only - normally the actual clip length is used).")]
    public float gestureFallbackDuration = 3f;

    float settleTimer;
    float glanceTimer;
    float lookWeight;
    Vector3 glanceLocalDir; // already scaled by glanceDistance, relative to this transform's own axes

    int upperBodyLayer = -1;
    int[] validGestureHashes;
    string[] validGestureNames;
    int currentGestureHash;
    float gestureTimer;
    bool gesturing;
    float gestureEndTime;

    [Header("Debug (read-only)")]
    [SerializeField] bool debugIsGlancing;
    [SerializeField] float debugLookWeight;
    [SerializeField] bool debugIsGesturing;
    [SerializeField] int debugValidGestureCount;

    void Start()
    {
        if (animator == null)
        {
            // Same resolution order as TurnInPlace/AnatomicalConstraints - ask
            // CharacterAnimationDriver for the Animator it already correctly picked out
            // (this project's rigs can carry more than one Animator - see the recurring
            // "wrong Animator" issue noted throughout this project's history) before
            // falling back to a plain Humanoid-with-a-controller hunt.
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
        if (locomotion == null) locomotion = GetComponentInChildren<CharacterLocomotion>();
        if (weapon == null) weapon = GetComponentInParent<EnemyWeapon>();
        if (weapon == null) weapon = GetComponentInChildren<EnemyWeapon>();
        if (handIK == null) handIK = GetComponentInParent<WeaponHandIK>();
        if (handIK == null) handIK = GetComponentInChildren<WeaponHandIK>();

        if (animator == null)
        {
            Debug.LogWarning("[AIRelaxedIdle] No Humanoid Animator found in this hierarchy.", this);
            enabled = false;
            return;
        }

        // OnAnimatorIK only fires on the GameObject that actually holds the Animator -
        // WeaponHandIK hit exactly this issue, solved with WeaponHandIKAnimatorBridge.
        // Reuse the same fix rather than inventing a second bridge component.
        if (animator.gameObject != gameObject)
        {
            var bridge = animator.gameObject.GetComponent<AIRelaxedIdleAnimatorBridge>();
            if (bridge == null) bridge = animator.gameObject.AddComponent<AIRelaxedIdleAnimatorBridge>();
            bridge.owner = this;
        }

        upperBodyLayer = animator.GetLayerIndex("UpperBody");

        if (upperBodyLayer >= 0 && gestureStateNames != null)
        {
            var names = new System.Collections.Generic.List<string>();
            var hashes = new System.Collections.Generic.List<int>();
            foreach (var n in gestureStateNames)
            {
                if (string.IsNullOrEmpty(n)) continue;
                int h = Animator.StringToHash(n);
                if (animator.HasState(upperBodyLayer, h)) { names.Add(n); hashes.Add(h); }
            }
            validGestureNames = names.ToArray();
            validGestureHashes = hashes.ToArray();
        }
        else
        {
            validGestureNames = new string[0];
            validGestureHashes = new int[0];
        }
        debugValidGestureCount = validGestureNames.Length;
        if (playIdleGestures && validGestureNames.Length == 0)
            Debug.Log("[AIRelaxedIdle] playIdleGestures is on but none of gestureStateNames exist on the UpperBody layer - re-run Tools > FPS Game > Build Animation System, or check the gest_* clips actually imported (see the Console output from that tool).", this);

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

        // --- glancing (always on while settled) ---
        glanceTimer -= Time.deltaTime;
        if (settled && glanceTimer <= 0f) PickNewGlanceTarget();

        float targetWeight = settled ? 1f : 0f;
        lookWeight = Mathf.MoveTowards(lookWeight, targetWeight, Time.deltaTime / Mathf.Max(weightSmoothing, 0.01f));

        // --- gestures (optional, one-shot on the UpperBody layer) ---
        if (gesturing)
        {
            // Once the crossfade has actually landed on the gesture state, correct the end
            // time from its real length instead of the fallback guess used to seed it.
            var info = animator.GetCurrentAnimatorStateInfo(upperBodyLayer);
            if (info.shortNameHash == currentGestureHash && info.length > 0.01f)
                gestureEndTime = Time.time + Mathf.Max(0f, (1f - info.normalizedTime) * info.length);

            // Bail early if the character stopped being relaxed mid-gesture (e.g. spotted a
            // target) - hand control back immediately rather than waiting the clip out.
            if (!relaxed || Time.time >= gestureEndTime) EndGesture();
        }
        else if (settled && playIdleGestures && validGestureNames.Length > 0)
        {
            gestureTimer -= Time.deltaTime;
            if (gestureTimer <= 0f) StartGesture();
        }
        else
        {
            gestureTimer = Mathf.Max(gestureTimer, 0f); // don't let it run down while not eligible
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
        // Pitch as a rotation around the local right axis so positive pitch looks up.
        Quaternion rot = Quaternion.Euler(-pitch, yaw, 0f);
        glanceLocalDir = rot * Vector3.forward * glanceDistance;
    }

    void PickNewGestureTimer()
    {
        gestureTimer = Random.Range(gestureIntervalRange.x, gestureIntervalRange.y);
    }

    void StartGesture()
    {
        int pick = Random.Range(0, validGestureNames.Length);
        currentGestureHash = validGestureHashes[pick];
        animator.CrossFadeInFixedTime(validGestureNames[pick], gestureCrossfade, upperBodyLayer);
        handIK?.SetSuppressed(true);
        gesturing = true;

        float duration = gestureFallbackDuration;
        // AnimationClip isn't reachable by name here without keeping our own lookup table, so
        // fall back to the configured duration - close enough for a one-shot gesture, and it's
        // corrected the moment relaxed becomes false anyway (see Update's early EndGesture).
        gestureEndTime = Time.time + gestureCrossfade + duration;
    }

    void EndGesture()
    {
        gesturing = false;
        handIK?.SetSuppressed(false);
        if (upperBodyLayer >= 0) animator.CrossFadeInFixedTime("UB_Idle", gestureCrossfade, upperBodyLayer);
        PickNewGestureTimer();
    }

    /// <summary>Called from OnAnimatorIK, either directly (if this component sits on the same
    /// GameObject as the Animator) or via AIRelaxedIdleAnimatorBridge forwarding from the real
    /// (possibly nested) Animator's GameObject.</summary>
    public void ApplyLookAt()
    {
        if (animator == null) return;

        animator.SetLookAtWeight(lookWeight, bodyWeight, headWeight, eyesWeight, clampWeight);
        if (lookWeight > 0.001f)
        {
            // Anchored on the head bone (works regardless of this character's actual height)
            // and aimed using the ROOT's forward/right/up, not wherever the head currently
            // happens to be looking - using the head's own current rotation here would create
            // a feedback loop (this frame's look-at output feeding next frame's glance
            // direction) that could drift or oscillate.
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Vector3 origin = head != null ? head.position : transform.position + Vector3.up * 1.6f;
            Vector3 worldTarget = origin + transform.TransformDirection(glanceLocalDir);
            animator.SetLookAtPosition(worldTarget);
        }
    }

    void OnAnimatorIK(int layerIndex)
    {
        ApplyLookAt();
    }
}

public class AIRelaxedIdleAnimatorBridge : MonoBehaviour
{
    public AIRelaxedIdle owner;
    void OnAnimatorIK(int layerIndex) => owner?.ApplyLookAt();
}