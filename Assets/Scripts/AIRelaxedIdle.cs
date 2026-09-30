using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Relaxed idle: while an enemy/ally is genuinely idle (CharacterLocomotion.IsIdle) AND out of
/// combat with the weapon actually all the way down (EnemyWeapon.IsCombatReady false AND
/// CombatBlend settled near 0, not just "just told to lower") this does three things:
///
///  1. LOOK-AT. Fades in Unity's built-in Humanoid look-at IK and has the character glance around at a
///     slowly-changing point out in front of itself - the head/eyes turn, the body barely follows. Always on
///     while relaxed; fades out while a special gesture plays (the clip owns the head then).
///
///  2. BASIC GESTURES (the original set). Short masked UpperBody-layer clips - "look away", "weight shift",
///     "sigh", the Pro Melee Axe "idle looking" variants - just to give the torso and head some varied movement.
///     They start on a random timer that runs while the character is relaxed, exactly as they always did, and are
///     left alone by everything below.
///
///  3. SPECIAL GESTURES (new). Full-body clips - "Check Shoe" and "Arm Stretching". They are rarer:
///       - only after specialCalmDelay seconds (20) with no combat at all,
///       - then not again for specialRepeatDelay seconds (60) after one finishes (the basic gestures carry on
///         in the meantime),
///       - the character is HELD STILL for the whole clip (NavMeshAgent stopped) and the clip is allowed to
///         move the torso, head, hips and raised foot freely: TorsoPoseDriver's stabilisers, CharacterMotionPolish's
///         head look and AnatomicalConstraints' joint limits all back off (CharacterAnimationDriver.ClipDriven),
///       - the weapon rides in the animated right hand while the left hand stays on the foregrip
///         (WeaponHandFollow + WeaponHandIK.SetAnimationFollow),
///       - anything that stops the character being relaxed cuts it short: combat, the gun coming up, or the AI
///         becoming alert (it starts looking at something) - the body returns to the weapon pose straight away.
///
/// playIdleGestures switches BOTH kinds off (glance-only); playSpecialGestures switches only the new ones off.
///
/// Does NOT touch Enemy.cs/FriendlyAI.cs. Add it manually next to Animator/CharacterLocomotion/EnemyWeapon on
/// enemy and ally prefabs. Needs Tools > FPS Game > Build Animation System re-run once (the UB_Gesture* and
/// Gesture_* states have to exist on the controller) - the look-at glancing works with no rebuild.
/// </summary>
[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(100)] // after Enemy/FriendlyAI/EnemyWeapon have run their Update, so holding the agent still wins
public class AIRelaxedIdle : MonoBehaviour
{
    public Animator animator;
    public CharacterLocomotion locomotion;
    public EnemyWeapon weapon;
    public WeaponHandIK handIK;
    public CharacterAnimationDriver driver;

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

    // ------------------------------------------------------------------------------------------------
    // Basic gestures (masked UpperBody layer) - the original behaviour
    // ------------------------------------------------------------------------------------------------
    [Header("Basic idle gestures (optional - untick for glance-only, also switches the special ones off)")]
    [Tooltip("If off, this character only glances around (above) and never stops to play a gesture clip - basic or special.")]
    public bool playIdleGestures = true;
    [Tooltip("How often (min/max seconds) a BASIC gesture is triggered while relaxed. The timer only runs while the character is settled and relaxed. Independent of the glance interval above and of the special gestures below.")]
    public Vector2 gestureIntervalRange = new Vector2(8f, 18f);
    [Tooltip("Names checked (via Animator.HasState) against the UpperBody layer at Start - only ones that actually exist get picked from. Matches the UB_Gesture* states AnimationSystemBuilder creates for whichever gest_* clips were found; edit freely to add/remove candidates without touching code.")]
    public string[] gestureStateNames = {
        "UB_GestureLookAway", "UB_GestureWeightShift", "UB_GestureSigh", "UB_GestureThoughtful",
        "UB_GestureMeleeLook1", "UB_GestureMeleeLook2", "UB_GestureUnarmedLook1", "UB_GestureUnarmedLook2",
    };
    [Tooltip("Crossfade time in/out of a basic gesture, seconds.")]
    public float gestureCrossfade = 0.25f;

    // ------------------------------------------------------------------------------------------------
    // Special gestures (full-body)
    // ------------------------------------------------------------------------------------------------
    [System.Serializable]
    public struct GestureOption
    {
        public string state;
        [Tooltip("How much of the right arm follows the clip. Above 0.5 the held weapon rides in the animated right hand (WeaponHandFollow). The Right Hand gestures leave the left hand on the foregrip; the Both Hands ones free it too.")]
        [Range(0f, 1f)] public float rightFollow;
        [Tooltip("How much of the left arm follows the clip (0 = stays on the weapon's foregrip, which moves with the weapon).")]
        [Range(0f, 1f)] public float leftFollow;
        [Tooltip("If set, this gesture is only offered while unarmed or holding a pistol - a rifle needs both hands to make sense held.")]
        public bool pistolOrUnarmedOnly;
    }

    [Header("Special full-body gestures (Check Shoe, Arm Stretching)")]
    [Tooltip("Untick to keep only the basic gestures above.")]
    public bool playSpecialGestures = true;
    [Tooltip("Seconds of continuous no-combat before the first special gesture may play. Combat (gun raised) restarts this.")]
    public float specialCalmDelay = 20f;
    [Tooltip("Seconds to wait after a special gesture ends before one may play again. The basic gestures keep playing in the meantime.")]
    public float specialRepeatDelay = 60f;
    [Tooltip("Off: the repeat delay is shared (after ANY special gesture, wait). On: each special gesture has its own delay, so a different one may follow straight away.")]
    public bool perGestureRepeatDelay = false;
    [Tooltip("Stop the NavMeshAgent for the length of a special gesture, so the character stays exactly where it is (combat or becoming alert releases it at once). Needs no changes to Enemy/FriendlyAI.")]
    public bool holdStillDuringSpecial = true;
    [Tooltip("Wandering/patrolling while calm does not stop a special gesture from being due: when one is due and the character is on the move it is brought to a stop first (it slows down naturally), then the gesture plays. Off = only start one if it happens to be standing still already.")]
    public bool stopToPerformSpecial = true;
    [Tooltip("Longest (seconds) to wait for a character to come to a stop before giving up for now.")]
    public float stopWaitTimeout = 2f;
    [Tooltip("Crossfade into a special gesture, seconds.")]
    public float specialCrossfade = 0.25f;
    [Tooltip("Candidates checked at Start via Animator.HasState - only ones that actually exist on the controller are used.")]
    public GestureOption[] gestureOptions =
    {
        new GestureOption { state = "Gesture_CheckShoe",  rightFollow = 1f, leftFollow = 0f, pistolOrUnarmedOnly = false },
        new GestureOption { state = "Gesture_ArmStretch", rightFollow = 1f, leftFollow = 1f, pistolOrUnarmedOnly = true },
    };
    [Tooltip("Used only until the real clip length can be read off the playing state (then the actual remaining time is used).")]
    public float gestureFallbackDuration = 6f;

    // --- look-at ---
    float settleTimer, glanceTimer, lookWeight;
    Vector3 glanceLocalDir;

    // --- basic gestures ---
    int upperBodyLayer = -1;
    int[] validGestureHashes;
    string[] validGestureNames;
    int currentGestureHash;
    float gestureTimer;
    bool gesturing;

    // --- special gestures ---
    GestureOption[] validSpecials;
    float[] specialReadyAt;          // per gesture, Time.time it may next play (used when perGestureRepeatDelay)
    float sharedReadyAt;             // Time.time any special may next play
    float calmTimer;                 // seconds of continuous no-combat
    int lastSpecialIndex = -1;
    bool special;                    // a special gesture is playing
    int specialIndex = -1;
    int specialHash = -1;
    float specialEndTime;

    // --- hold still ---
    NavMeshAgent agent;
    bool holding, stoppedBeforeHold;
    bool pendingStop;                // a special gesture is due and the character is being brought to a stop for it
    float pendingTimer;
    EnemyAI enemyAI;                 // either may be null; only used to ask "is this AI still calm?"
    FriendlyAI friendlyAI;

    [Header("Debug (read-only)")]
    [SerializeField] bool debugIsGlancing;
    [SerializeField] float debugLookWeight;
    [SerializeField] bool debugIsGesturing;
    [SerializeField] int debugValidGestureCount;
    [SerializeField] bool debugIsSpecial;
    [SerializeField] int debugValidSpecialCount;
    [SerializeField] float debugCalmTime;
    [SerializeField] float debugSpecialReadyIn;

    void Start()
    {
        if (driver == null) driver = GetComponentInParent<CharacterAnimationDriver>();
        if (driver == null) driver = GetComponentInChildren<CharacterAnimationDriver>();

        if (animator == null)
        {
            // Same resolution order as TurnInPlace/AnatomicalConstraints - ask
            // CharacterAnimationDriver for the Animator it already correctly picked out
            // (this project's rigs can carry more than one Animator - see the recurring
            // "wrong Animator" issue noted throughout this project's history) before
            // falling back to a plain Humanoid-with-a-controller hunt.
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
        if (handIK == null && driver != null) handIK = driver.Hands;
        if (handIK == null) handIK = GetComponentInParent<WeaponHandIK>();
        if (handIK == null) handIK = GetComponentInChildren<WeaponHandIK>();
        agent = GetComponentInParent<NavMeshAgent>();
        if (agent == null) agent = GetComponentInChildren<NavMeshAgent>();
        enemyAI = GetComponentInParent<EnemyAI>();
        friendlyAI = GetComponentInParent<FriendlyAI>();

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

        // ---- basic gestures: UpperBody layer states ----
        upperBodyLayer = animator.GetLayerIndex("UpperBody");
        if (upperBodyLayer >= 0)
        {
            // Ensure the UpperBody layer weight is set to 1 so animations are visible
            animator.SetLayerWeight(upperBodyLayer, 1f);

            if (gestureStateNames != null)
            {
                var names = new System.Collections.Generic.List<string>();
                var hashes = new System.Collections.Generic.List<int>();
                foreach (var n in gestureStateNames)
                {
                    if (string.IsNullOrEmpty(n)) continue;

                    int h = Animator.StringToHash(n);
                    int layerPrefixedHash = Animator.StringToHash("UpperBody." + n);

                    if (animator.HasState(upperBodyLayer, h)) { names.Add(n); hashes.Add(h); }
                    else if (animator.HasState(upperBodyLayer, layerPrefixedHash)) { names.Add(n); hashes.Add(layerPrefixedHash); }
                }
                validGestureNames = names.ToArray();
                validGestureHashes = hashes.ToArray();
            }
        }
        if (validGestureNames == null) { validGestureNames = new string[0]; validGestureHashes = new int[0]; }
        debugValidGestureCount = validGestureNames.Length;
        if (playIdleGestures && validGestureNames.Length == 0)
            Debug.Log("[AIRelaxedIdle] No basic gestures found on the UpperBody layer (gestureStateNames) - re-run Tools > FPS Game > Build Animation System, or check the gest_* clips actually imported (see the Console output from that tool).", this);

        // ---- special gestures: full-body base-layer states ----
        var valid = new System.Collections.Generic.List<GestureOption>();
        if (driver != null && gestureOptions != null)
            foreach (var g in gestureOptions)
                if (!string.IsNullOrEmpty(g.state) && driver.HasBaseState(g.state)) valid.Add(g);
        validSpecials = valid.ToArray();
        specialReadyAt = new float[validSpecials.Length];
        debugValidSpecialCount = validSpecials.Length;
        if (playIdleGestures && playSpecialGestures && validSpecials.Length == 0)
            Debug.Log("[AIRelaxedIdle] playSpecialGestures is on but none of gestureOptions exist on the base layer - rebuild the animation system, or check the Check Shoe / Arm Stretching clips actually imported.", this);

        PickNewGlanceTarget();
        PickNewGestureTimer();
    }

    void Update()
    {
        if (animator == null) return;
        float dt = Time.deltaTime;

        bool idle = locomotion == null || locomotion.IsIdle;
        bool weaponDown = weapon == null || (!weapon.IsCombatReady && weapon.CombatBlend < combatBlendThreshold);
        bool relaxed = idle && weaponDown;

        settleTimer = relaxed ? settleTimer + dt : 0f;
        bool settled = relaxed && settleTimer >= settleDelay;

        // "No combat" for the special gestures is looser than "relaxed": walking a patrol route with the gun down
        // still counts as calm; only raising the gun (combat) restarts the count.
        calmTimer = weaponDown ? calmTimer + dt : 0f;

        // --- glancing (always on while settled, but the clip owns the head during a special gesture) ---
        glanceTimer -= dt;
        if (settled && glanceTimer <= 0f) PickNewGlanceTarget();

        float targetWeight = (settled && !special) ? 1f : 0f;
        float lookFade = special ? 0.2f : Mathf.Max(weightSmoothing, 0.01f);
        lookWeight = Mathf.MoveTowards(lookWeight, targetWeight, dt / lookFade);

        // --- special gesture in progress ---
        if (special)
        {
            UpdateSpecial(relaxed);
        }
        // --- basic gesture in progress ---
        else if (gesturing)
        {
            var info = animator.GetCurrentAnimatorStateInfo(upperBodyLayer);

            // Interrupt immediately if the character stops being relaxed mid-gesture
            if (!relaxed)
            {
                EndGesture();
            }
            // Once we have completed the transition into the target gesture state...
            else if (info.shortNameHash == currentGestureHash && !animator.IsInTransition(upperBodyLayer))
            {
                // Wait for the non-looping gesture clip to complete (normalizedTime >= 0.95)
                if (info.normalizedTime >= 0.95f) EndGesture();
            }
        }
        else if (playIdleGestures)
        {
            // A special gesture that is due goes first. Whether the character is wandering about does NOT matter
            // for whether it is due (only calm time does) - if it is on the move it is brought to a stop first.
            bool startedOrPending = playSpecialGestures && UpdateSpecialDue(idle, dt);

            // Otherwise the basic gestures run exactly as before: timer counts while settled.
            if (!startedOrPending && settled && validGestureNames.Length > 0)
            {
                gestureTimer -= dt;
                if (gestureTimer <= 0f) StartGesture();
            }
        }

        // A character being held still keeps being held still, however late the AI's own Update ran this frame.
        // (While only being brought to a stop it is left to slow down naturally rather than freezing on the spot.)
        if (holding && agent != null && agent.enabled && agent.isOnNavMesh)
        {
            agent.isStopped = true;
            if (special) agent.velocity = Vector3.zero;
        }

        debugIsGlancing = settled;
        debugLookWeight = lookWeight;
        debugIsGesturing = gesturing || special;
        debugIsSpecial = special;
        debugCalmTime = calmTimer;
        debugSpecialReadyIn = Mathf.Max(Mathf.Max(0f, sharedReadyAt - Time.time), Mathf.Max(0f, specialCalmDelay - calmTimer));
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

    // ---------------------------------------------------------------------------------------------
    // Basic gestures - unchanged from the original behaviour
    // ---------------------------------------------------------------------------------------------
    void StartGesture()
    {
        int pick = Random.Range(0, validGestureNames.Length);
        currentGestureHash = validGestureHashes[pick];
        animator.CrossFadeInFixedTime(validGestureNames[pick], gestureCrossfade, upperBodyLayer);
        handIK?.SetSuppressed(true);
        gesturing = true;
    }

    void EndGesture()
    {
        gesturing = false;
        handIK?.SetSuppressed(false);
        if (upperBodyLayer >= 0) animator.CrossFadeInFixedTime("UB_Idle", gestureCrossfade, upperBodyLayer);
        PickNewGestureTimer();
    }

    // ---------------------------------------------------------------------------------------------
    // Special gestures
    // ---------------------------------------------------------------------------------------------
    // The AI itself says whether it is still calm: false once it has spotted/heard something, is in a fight or cover,
    // or (allies) the player has wandered out of range. Neither AI = nothing to ask, so calm.
    bool AICalm()
    {
        if (enemyAI != null && !enemyAI.IsCalm) return false;
        if (friendlyAI != null && !friendlyAI.IsCalm) return false;
        return true;
    }

    bool SpecialDue()
    {
        if (validSpecials == null || validSpecials.Length == 0 || driver == null) return false;
        if (calmTimer < specialCalmDelay || Time.time < sharedReadyAt) return false;
        if (driver.InFullBodyAction || !AICalm()) return false;
        // Becoming alert (it is looking at something) is not calm, even before the gun comes up.
        var polish = driver.Polish;
        if (polish != null && polish.HasLookTarget) return false;
        return true;
    }

    /// <summary>Returns true while a special gesture is starting or being waited for (so the basic gestures hold off).</summary>
    bool UpdateSpecialDue(bool idle, float dt)
    {
        if (pendingStop)
        {
            if (!SpecialDue()) { CancelPending(0f); return false; }
            pendingTimer += dt;
            if (idle)
            {
                if (TryStartSpecial()) { pendingStop = false; return true; }
                CancelPending(10f);
                return false;
            }
            if (pendingTimer > stopWaitTimeout) { CancelPending(5f); return false; }
            return true;
        }

        if (!SpecialDue()) return false;
        if (idle) return TryStartSpecial();
        if (!stopToPerformSpecial) return false;

        // On the move: ask it to come to a stop. Its own AI may keep re-issuing "go" every frame, which is why the
        // hold is enforced from Update (see there) rather than set once.
        pendingStop = true;
        pendingTimer = 0f;
        BeginHold(false);
        return true;
    }

    void CancelPending(float backoff)
    {
        pendingStop = false;
        EndHold(restore: true);
        if (backoff > 0f) sharedReadyAt = Mathf.Max(sharedReadyAt, Time.time + backoff);
    }

    bool TryStartSpecial()
    {
        bool canBothHands = driver.CurrentWeaponClass != CharacterWeaponClass.Rifle;
        var pool = new System.Collections.Generic.List<int>();
        for (int i = 0; i < validSpecials.Length; i++)
        {
            if (validSpecials[i].pistolOrUnarmedOnly && !canBothHands) continue;
            if (perGestureRepeatDelay && Time.time < specialReadyAt[i]) continue;
            pool.Add(i);
        }
        if (pool.Count == 0) return false;
        // Not the same one twice running if there is a choice.
        if (pool.Count > 1 && lastSpecialIndex >= 0) pool.Remove(lastSpecialIndex);

        int idx = pool[Random.Range(0, pool.Count)];
        var pick = validSpecials[idx];
        if (!driver.CrossFadeBase(pick.state, specialCrossfade, pick.rightFollow, pick.leftFollow)) return false;

        special = true;
        specialIndex = idx;
        specialHash = Animator.StringToHash(pick.state);
        specialEndTime = Time.time + specialCrossfade + gestureFallbackDuration; // corrected from the real clip length once it is playing

        // The clip owns the body; the gun rides in the animated right hand; the character stays put.
        driver.SetClipDriven(1f);
        // The gun rides in the animated right hand (created on demand by the driver, so it works however late the
        // WeaponHandIK came into being).
        driver.SetWeaponFollowsHand(pick.rightFollow > 0.5f);
        BeginHold(true);
        return true;
    }

    void UpdateSpecial(bool relaxed)
    {
        // Real remaining time, read off the state as soon as the crossfade into it has finished.
        var info = animator.GetCurrentAnimatorStateInfo(0);
        if (info.shortNameHash == specialHash && info.length > 0.01f)
            specialEndTime = Time.time + Mathf.Max(0f, (1f - info.normalizedTime) * info.length);

        // Alert = looking at something (investigating / chasing / has a target) even if the gun isn't up yet.
        var polish = driver != null ? driver.Polish : null;
        bool alert = polish != null && polish.HasLookTarget;

        // Also cut short when the AI itself stops being calm: combat, something heard/seen, or the player wandering off.
        if (!relaxed || alert || !AICalm())
        {
            EndSpecial(interrupted: true);
        }
        // Start easing the procedural layers back in as the clip's own exit crossfade begins, so both blend together.
        else if (Time.time >= specialEndTime - CharacterAnimationDriver.OneShotExitFade)
        {
            EndSpecial(interrupted: false);
        }
    }

    void EndSpecial(bool interrupted)
    {
        int idx = specialIndex;
        special = false;
        specialIndex = -1;
        specialHash = -1;
        lastSpecialIndex = idx;

        if (driver != null)
        {
            // Cut short: put the whole body back to the weapon pose now. Played out: the state exits by itself.
            if (interrupted) driver.ReturnToWeaponPose(0.25f);
            driver.EndFullBodyAction();
            driver.SetClipDriven(0f);
        }
        // Interrupted by combat/alert: the AI has already decided what the agent should do this frame - leave it.
        EndHold(restore: !interrupted);

        float ready = Time.time + specialRepeatDelay;
        sharedReadyAt = ready;
        if (idx >= 0 && idx < specialReadyAt.Length) specialReadyAt[idx] = ready;
    }

    void BeginHold(bool killVelocity)
    {
        if (!holdStillDuringSpecial || agent == null || !agent.enabled || !agent.isOnNavMesh) return;
        if (!holding)
        {
            stoppedBeforeHold = agent.isStopped;
            holding = true;
        }
        agent.isStopped = true;
        if (killVelocity) agent.velocity = Vector3.zero;
    }

    void EndHold(bool restore)
    {
        if (!holding) return;
        holding = false;
        if (restore && agent != null && agent.enabled && agent.isOnNavMesh) agent.isStopped = stoppedBeforeHold;
    }

    void OnDisable()
    {
        // Never leave a character frozen or half-way through a gesture if this is switched off (death, pooling...).
        pendingStop = false;
        if (special) EndSpecial(interrupted: true);
        else EndHold(restore: true);
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
