using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

/// <summary>
/// Vault/ledge climb on the jump button, extended to AI. Three real clips cover three height
/// bands (Step Up, Vault Over, Wall Climb); which one plays and how far it moves the character
/// is worked out from the clip's OWN measured hip motion, scaled to match the ledge actually
/// detected, rather than a fixed generic arc:
///
///  - Each clip is imported "in place" like every other animation in this project (no baked
///    horizontal root motion) - this script drives the actual root position every frame,
///    matching the shape of the clip's own hip curve (a straight climb for Step/Wall, a genuine
///    rise-then-settle arc for Over - see VaultProfile below) scaled to the real detected rise,
///    so a shorter or taller-than-average ledge still looks proportionate instead of the clip
///    visually clipping through the geometry or hanging in the air above it.
///  - The right hand keeps its normal weapon grip throughout; the LEFT hand and arm follow the
///    clip (see CharacterAnimationDriver.CrossFadeBase) - it reaches for the ledge the way the
///    animation actually shows, blended smoothly in and back out by WeaponHandIK rather than
///    snapping.
///
/// Player: reuses the existing Jump key (own InputAction, same pattern as PlayerShove/
/// PlayerMelee), and takes over from PlayerMovement for the climb's duration (mouse-look
/// included - the camera doesn't move during the climb). [DefaultExecutionOrder(-100)]
/// guarantees this component's Update runs BEFORE PlayerMovement's on the same frame, so
/// disabling PlayerMovement here reliably pre-empts its own jump on the same keypress rather
/// than racing it.
///
/// AI: no jump button to hook, so this uses a "stuck" heuristic instead - wants to move
/// (NavMeshAgent.desiredVelocity is non-zero) but isn't actually moving, for aiStuckTime
/// seconds. This is a first pass, not validated against the actual level geometry/NavMesh
/// setup - whether AI ever genuinely encounters a vaultable obstacle on their path (rather than
/// the NavMesh just routing around it, which is more usual) depends entirely on the level, so
/// this may need tuning or may rarely fire at all.
///
/// Attach to the same GameObject as PlayerMovement (player) or the NavMeshAgent (enemies/
/// allies) - same root Enemy.cs/FriendlyAI.cs/PlayerMovement already live on.
/// </summary>
[DefaultExecutionOrder(-100)]
public class VaultClimb : MonoBehaviour
{
    // One entry per Animator state built by AnimationSystemBuilder. netRise/peakRise/peakTime are the
    // clip's OWN hip motion (meters above its start, and when the peak happens, 0-1 through the clip) -
    // measured directly from the source animation, not guessed:
    //   Step Up:    0.567s, rises 0.449m fairly steadily, no real overshoot (peak 0.453m at 90%).
    //   Vault Over: 0.933s, settles 0.418m higher than it started, but arcs up to 0.652m at 66% first -
    //               a genuine hop-over, not a straight climb.
    //   Wall Climb: 1.600s, rises 2.142m, small overshoot to 2.256m right at the top (97%).
    // minHeight/maxHeight is the detected-ledge band each one is used for.
    [System.Serializable]
    public struct VaultProfile
    {
        public string state;
        public float duration;
        public float netRise, peakRise, peakTime;
        public float minHeight, maxHeight;
    }

    public VaultProfile[] profiles =
    {
        new VaultProfile { state = "Vault_StepUp", duration = 0.567f, netRise = 0.449f, peakRise = 0.453f, peakTime = 0.90f, minHeight = 0.10f, maxHeight = 0.50f },
        new VaultProfile { state = "Vault_Over",   duration = 0.933f, netRise = 0.418f, peakRise = 0.652f, peakTime = 0.66f, minHeight = 0.50f, maxHeight = 0.90f },
        new VaultProfile { state = "Vault_Wall",   duration = 1.600f, netRise = 2.142f, peakRise = 2.256f, peakTime = 0.97f, minHeight = 0.90f, maxHeight = 2.20f },
    };

    [Header("Detection")]
    [Tooltip("Layers checked for both the obstacle and its top surface. Defaults to Everything - narrow this to your environment/geometry layer(s) in the Inspector so it can't snag on other characters or props.")]
    public LayerMask vaultLayers = ~0;
    public float checkDistance = 0.8f;
    [Tooltip("Height above the feet the forward probe is cast from - roughly chest height, so a low kerb underfoot doesn't register as a wall.")]
    public float obstacleCheckHeight = 1.0f;
    [Tooltip("Clearance capsule height checked on top of the ledge before committing, so it doesn't climb up into something with no room to stand.")]
    public float standHeight = 1.8f;
    public float standRadius = 0.3f;
    [Tooltip("How far past the ledge edge to land, so the character ends up standing clear of the lip rather than balanced right on it.")]
    public float landForwardOffset = 0.35f;
    [Tooltip("Detected ledge height is compared against each profile's min/max after scaling by this, so a ledge a little outside a band still uses the closest clip rather than being rejected outright.")]
    [Range(1f, 1.3f)] public float bandTolerance = 1.15f;
    [Tooltip("How far the scale-to-fit above can stretch a clip's own motion before it's rejected as too far outside what that clip can sell.")]
    public Vector2 scaleClamp = new Vector2(0.7f, 1.4f);

    [Header("Timing")]
    [Tooltip("Crossfade into the vault clip, seconds.")]
    public float enterFade = 0.15f;
    [Tooltip("Minimum time between vault attempts, successful or not - stops the AI stuck-check (or a held jump key) retrying every single frame.")]
    public float cooldown = 0.5f;

    [Header("AI auto-trigger (the player uses the jump button above instead)")]
    public float aiStuckSpeedThreshold = 0.15f;
    public float aiStuckTime = 0.6f;

    PlayerMovement pm;
    CharacterController cc;
    NavMeshAgent agent;
    CharacterAnimationDriver driver;

    InputAction jumpAction;
    bool vaulting;
    float cooldownTimer;
    float aiStuckTimer;

    [Header("Debug (read-only)")]
    [SerializeField] bool debugVaulting;
    [SerializeField] string debugLastProfile;

    void Awake()
    {
        pm = GetComponent<PlayerMovement>();
        cc = GetComponent<CharacterController>();
        agent = GetComponent<NavMeshAgent>();
        driver = GetComponentInChildren<CharacterAnimationDriver>();

        if (pm != null)
        {
            jumpAction = new InputAction("VaultJump", binding: PlayerInputMap.Jump);
            jumpAction.Enable();
        }
    }

    void OnDestroy() => jumpAction?.Disable();

    void Update()
    {
        if (vaulting) { debugVaulting = true; return; }
        debugVaulting = false;

        if (cooldownTimer > 0f) { cooldownTimer -= Time.deltaTime; return; }

        if (pm != null)
        {
            if (jumpAction != null && jumpAction.WasPressedThisFrame() && pm.IsGrounded)
                TryStartVault();
        }
        else if (agent != null)
        {
            bool wantsToMove = agent.enabled && agent.isOnNavMesh && !agent.pathPending && agent.desiredVelocity.sqrMagnitude > 0.1f;
            bool barelyMoving = agent.velocity.magnitude < aiStuckSpeedThreshold;
            aiStuckTimer = (wantsToMove && barelyMoving) ? aiStuckTimer + Time.deltaTime : 0f;
            if (aiStuckTimer >= aiStuckTime) { aiStuckTimer = 0f; TryStartVault(); }
        }
    }

    void TryStartVault()
    {
        if (driver == null) return;

        Vector3 origin = transform.position;
        Vector3 forward = transform.forward;

        if (!Physics.Raycast(origin + Vector3.up * obstacleCheckHeight, forward, out var hit, checkDistance, vaultLayers, QueryTriggerInteraction.Ignore))
            return;

        Vector3 probeStart = hit.point + forward * 0.25f + Vector3.up * (profiles.Length > 0 ? MaxProfileHeight() : 2.2f);
        float probeDist = MaxProfileHeight() + 0.5f;
        if (!Physics.Raycast(probeStart, Vector3.down, out var topHit, probeDist, vaultLayers, QueryTriggerInteraction.Ignore))
            return; // nothing within range under the probe - either a gap or a wall too tall

        float ledgeHeight = topHit.point.y - origin.y;
        if (!TryPickProfile(ledgeHeight, out var profile)) return;

        Vector3 landPoint = topHit.point + forward * landForwardOffset + Vector3.up * 0.02f;
        Vector3 capsuleBase = landPoint + Vector3.up * standRadius;
        Vector3 capsuleTop = landPoint + Vector3.up * (standHeight - standRadius);
        if (Physics.CheckCapsule(capsuleBase, capsuleTop, standRadius, vaultLayers, QueryTriggerInteraction.Ignore))
            return; // no headroom - don't climb up into something

        StartCoroutine(VaultRoutine(profile, landPoint));
    }

    float MaxProfileHeight()
    {
        float m = 0f;
        foreach (var p in profiles) m = Mathf.Max(m, p.maxHeight);
        return m * bandTolerance;
    }

    bool TryPickProfile(float ledgeHeight, out VaultProfile profile)
    {
        profile = default;
        VaultProfile best = default;
        float bestDist = float.MaxValue;
        bool found = false;
        foreach (var p in profiles)
        {
            if (string.IsNullOrEmpty(p.state) || driver == null || !driver.HasBaseState(p.state)) continue;
            float lo = p.minHeight / bandTolerance, hi = p.maxHeight * bandTolerance;
            if (ledgeHeight < lo || ledgeHeight > hi) continue;
            float mid = (p.minHeight + p.maxHeight) * 0.5f;
            float dist = Mathf.Abs(ledgeHeight - mid);
            if (dist < bestDist) { bestDist = dist; best = p; found = true; }
        }
        if (!found) return false;

        float s = ledgeHeight / Mathf.Max(0.05f, best.netRise);
        if (s < scaleClamp.x || s > scaleClamp.y) return false; // too far outside what this clip can sell

        profile = best;
        return true;
    }

    IEnumerator VaultRoutine(VaultProfile profile, Vector3 landPoint)
    {
        vaulting = true;
        cooldownTimer = cooldown;
        debugLastProfile = profile.state;

        if (pm != null) pm.enabled = false;
        if (cc != null) cc.enabled = false;
        if (agent != null) agent.enabled = false;

        // Right hand keeps the weapon grip; left follows the clip's own reach for the ledge.
        driver.CrossFadeBase(profile.state, enterFade, 0f, 1f);

        Vector3 start = transform.position;
        float scale = (landPoint.y - start.y) / Mathf.Max(0.05f, profile.netRise);
        float peakExtra = Mathf.Max(0f, profile.peakRise - profile.netRise) * scale;
        float netRiseScaled = landPoint.y - start.y;

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / Mathf.Max(0.05f, profile.duration);
            float clamped = Mathf.Clamp01(t);

            float h;
            if (clamped <= profile.peakTime)
            {
                float u = Mathf.SmoothStep(0f, 1f, profile.peakTime > 0.001f ? clamped / profile.peakTime : 1f);
                h = u * (netRiseScaled + peakExtra);
            }
            else
            {
                float u = Mathf.SmoothStep(0f, 1f, (clamped - profile.peakTime) / Mathf.Max(0.001f, 1f - profile.peakTime));
                h = Mathf.Lerp(netRiseScaled + peakExtra, netRiseScaled, u);
            }

            float flatT = Mathf.SmoothStep(0f, 1f, clamped);
            Vector3 flatXZ = Vector3.Lerp(new Vector3(start.x, 0f, start.z), new Vector3(landPoint.x, 0f, landPoint.z), flatT);
            transform.position = new Vector3(flatXZ.x, start.y + h, flatXZ.z);

            yield return null;
        }
        transform.position = landPoint;

        driver.EndFullBodyAction();

        if (cc != null) cc.enabled = true;
        if (pm != null) pm.enabled = true;
        if (agent != null)
        {
            agent.enabled = true;
            if (agent.isOnNavMesh) agent.Warp(transform.position); // re-sync the agent's internal position after moving it by hand
        }

        vaulting = false;
    }
}