using UnityEngine;

/// <summary>
/// Phase 3 (relaxed idle): while an enemy/ally is genuinely idle (CharacterLocomotion.IsIdle)
/// AND out of combat with the weapon actually all the way down (EnemyWeapon.IsCombatReady
/// false AND CombatBlend settled near 0, not just "just told to lower") this fades in Unity's
/// built-in Humanoid look-at IK and has the character glance around at a slowly-changing point
/// out in front of itself - the head/eyes turn, the body barely follows. Reads as "standing
/// around, looking about" rather than the previous dead-eyed stare-forward idle.
///
/// Does NOT touch Enemy.cs/FriendlyAI.cs or the Animator Controller - purely additive, same
/// spirit as TurnInPlace/AnatomicalConstraints. Add it manually next to Animator/
/// CharacterLocomotion/EnemyWeapon on enemy and ally prefabs; nothing needs re-running in
/// Tools > FPS Game > Build Animation System for this piece.
///
/// Idle GESTURE clips (face-scratch etc. from Gestures Pack Basic / Pro Melee Axe's "idle
/// looking" variants) are the other half of Phase 3's "relaxed idle" ask and are NOT in this
/// file - that needs real ClipDef entries in AnimationSystemBuilder pointing at whatever those
/// clips are actually called once imported, which isn't something to guess at blind. Tell me
/// the clip/folder names (or which pack they're in) and that's a quick follow-up on top of this.
/// </summary>
[RequireComponent(typeof(Animator))]
public class AIRelaxedIdle : MonoBehaviour
{
    public Animator animator;
    public CharacterLocomotion locomotion;
    public EnemyWeapon weapon;

    [Header("When it's allowed to look around")]
    [Tooltip("Extra seconds of being fully idle AND fully weapon-lowered before glancing starts - stops a character that just stopped walking or just finished lowering their gun from snapping straight into a look-around.")]
    public float settleDelay = 1f;
    [Tooltip("CombatBlend must be below this (see EnemyWeapon) to count as 'fully lowered' - keeps glancing from starting while the gun is still mid-way through lowering.")]
    public float combatBlendThreshold = 0.02f;

    [Header("Glance behaviour")]
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
    [Tooltip("Unity's own clamp on how far the head/eyes are allowed to turn from the forward direction before the body has to help - keeps a sharp side-glance from twisting the neck through an unnatural angle. 0.5 is Unity's default.")]
    [Range(0f, 1f)] public float clampWeight = 0.5f;

    float settleTimer;
    float glanceTimer;
    float lookWeight;
    Vector3 glanceLocalDir; // unit-ish direction (already scaled by glanceDistance), relative to this transform's own axes

    [Header("Debug (read-only)")]
    [SerializeField] bool debugIsGlancing;
    [SerializeField] float debugLookWeight;

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

        PickNewGlanceTarget();
    }

    void Update()
    {
        if (animator == null) return;

        bool idle = locomotion == null || locomotion.IsIdle;
        bool weaponDown = weapon == null || (!weapon.IsCombatReady && weapon.CombatBlend < combatBlendThreshold);
        bool relaxed = idle && weaponDown;

        settleTimer = relaxed ? settleTimer + Time.deltaTime : 0f;
        bool glancing = relaxed && settleTimer >= settleDelay;

        glanceTimer -= Time.deltaTime;
        if (glancing && glanceTimer <= 0f) PickNewGlanceTarget();

        float targetWeight = glancing ? 1f : 0f;
        lookWeight = Mathf.MoveTowards(lookWeight, targetWeight, Time.deltaTime / Mathf.Max(weightSmoothing, 0.01f));

        debugIsGlancing = glancing;
        debugLookWeight = lookWeight;
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