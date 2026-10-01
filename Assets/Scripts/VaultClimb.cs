using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

/// <summary>
/// Vault / mantle / climb on the jump button (player) or the "stuck" heuristic (AI).
///
/// HOW GAMES DO IT (and what this now does). Nearly every game with this (Brink's SMART system, Titanfall, Apex,
/// Uncharted, the Unreal "motion warping" parkour kits...) follows the same recipe, and it is NOT "play a clip and
/// hope":
///   1. DETECT with traces: a forward trace finds a wall, a trace straight DOWN from above that wall finds the
///      ledge top, the difference is the obstacle height, and a capsule test checks there is room to stand.
///      Height (and sometimes depth) picks the move: step up / vault / climb.
///   2. The CHARACTER IS MOVED BY CODE, collision off, along a path fitted to the REAL ledge - "motion warping":
///      the clip only supplies the limbs, the root follows the world. Authored clips never match real geometry,
///      so a fixed root-motion clip either floats above the ledge or clips through it.
///   3. The path is UP FIRST, THEN FORWARD: the body rises until the feet clear the lip, and only then travels
///      over it. Moving forward while still low is what puts a body through the wall.
///   4. First person: nothing special - the camera is on the body, so it is lifted with it; good games add a small
///      camera dip so it doesn't feel like an elevator.
///
/// THINGS THAT WERE WRONG (why "no animation plays"):
///   - Execution order. PlayerMovement runs at -150 and this was -100, so on the jump key press PlayerMovement had
///     ALREADY jumped (IsGrounded false) before this looked, and the vault never started. Now -200.
///   - The forward probe was cast at 1.0m, so anything lower than that (every "low cover" / step / Over vault) was
///     stepped over by the ray and never detected. Now a stack of rays from knee height up finds the lowest hit.
///   - The vault clips were imported with "Bake Into Pose: Y", so the body rose in the pose AND the script lifted
///     the root again (double lift), and their forward travel (0.5-2.2m) and body turn were baked in too, which carried the model out in front of the camera and gun. They are now imported with the vertical motion extracted (the builder does
///     it; see ClipDef.extractRoot) so this script is the only thing moving the character (up AND forward - see the builder).
///   - XZ and Y moved together from the first frame, so the body travelled through the obstacle while still low.
///
/// MOTION. Approach (rise, slide in to the wall, turn square to it) -> forward (over/onto it). Thin obstacles that
/// can be crossed (low cover, a wall under ~1m) are vaulted to the FAR side; deeper ones (boxes, ledges, tall
/// walls) are mantled onto the top. Height picks the clip: Step Up / Vault Over / Wall Climb, each with its own
/// measured hip arc (see VaultProfile).
///
/// Attach to the same GameObject as PlayerMovement (player; PlayerMovement now adds it by itself if it is missing)
/// or the NavMeshAgent (enemies/allies).
/// </summary>
[DefaultExecutionOrder(-200)]
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
    [Tooltip("Layers checked for both the obstacle and its top surface. Defaults to Everything - narrow this to your environment/geometry layer(s) in the Inspector. Characters (anything with a Health) and this character's own colliders are always ignored.")]
    public LayerMask vaultLayers = ~0;
    [Tooltip("How far past the edge of the character's capsule to look for an obstacle, metres.")]
    public float checkDistance = 0.6f;
    [Tooltip("Vertical spacing of the forward probe rays (they start at knee height and go up to the tallest climbable ledge). The LOWEST one that hits is the obstacle.")]
    public float rayStep = 0.15f;
    [Tooltip("Widest angle (degrees) between where you face and the wall's normal for a vault to start. Stops vaulting diagonally along a wall.")]
    public float maxWallAngle = 50f;
    [Tooltip("Clearance capsule height checked where the character will land, so it doesn't climb up into something with no room to stand.")]
    public float standHeight = 1.8f;
    public float standRadius = 0.3f;
    [Tooltip("When mantling ONTO a surface: how far past the ledge edge to land, so the character ends up standing clear of the lip rather than balanced right on it.")]
    public float landForwardOffset = 0.35f;
    [Tooltip("Detected ledge height is compared against each profile's min/max after scaling by this, so a ledge a little outside a band still uses the closest clip rather than being rejected outright.")]
    [Range(1f, 1.3f)] public float bandTolerance = 1.15f;
    [Tooltip("How far the scale-to-fit above can stretch a clip's own motion before it's rejected as too far outside what that clip can sell.")]
    public Vector2 scaleClamp = new Vector2(0.7f, 1.4f);

    [Header("Vault over thin obstacles (low cover, short walls)")]
    [Tooltip("Obstacles that are thin enough are vaulted OVER (landing on the far side) instead of climbed onto. Off = always climb on top.")]
    public bool vaultOverThinObstacles = true;
    [Tooltip("Ledges between these heights (metres) can be vaulted over when thin enough - waist/chest-high cover.")]
    public Vector2 vaultOverHeightRange = new Vector2(0.45f, 1.05f);
    [Tooltip("An obstacle deeper than this (metres, measured across its top) is climbed onto instead of vaulted over.")]
    public float maxVaultDepth = 1.1f;
    [Tooltip("Furthest drop (metres) below the obstacle's top that a vault will land on the far side; past this it climbs on top instead.")]
    public float maxVaultDrop = 1.6f;
    [Tooltip("How far past the far edge to land, metres.")]
    public float farLandOffset = 0.35f;

    [Header("Motion")]
    [Tooltip("Crossfade into the vault clip, seconds.")]
    public float enterFade = 0.15f;
    [Tooltip("Minimum time between vault attempts, successful or not - stops the AI stuck-check (or a held jump key) retrying every single frame.")]
    public float cooldown = 0.5f;
    [Tooltip("Turn to face the wall squarely during the approach (only if already within 'maxWallAngle' of it).")]
    public bool alignToWall = true;
    [Tooltip("How far (m) the body's centre stops from the wall face while rising - roughly the capsule radius.")]
    public float wallStandOff = 0.05f;
    [Tooltip("Player camera dips by this many degrees (looking down) as the body goes over the top, then comes back - it stops the view feeling like a lift. 0 = off.")]
    public float cameraDip = 4f;

    [Header("AI auto-trigger (the player uses the jump button above instead)")]
    public float aiStuckSpeedThreshold = 0.15f;
    public float aiStuckTime = 0.6f;

    [Header("Debug")]
    [Tooltip("Log why a jump did / didn't become a vault.")]
    public bool logAttempts = false;
    [SerializeField] bool debugVaulting;
    [SerializeField] string debugLastProfile;
    [Tooltip("Result of the last attempt, e.g. 'no obstacle in front' / 'ledge too high (2.6m)' / 'OK'.")]
    [SerializeField] string debugLastResult;

    PlayerMovement pm;
    CharacterController cc;
    NavMeshAgent agent;
    CharacterAnimationDriver driver;

    InputAction jumpAction;
    bool vaulting;
    float cooldownTimer;
    float aiStuckTimer;
    bool warnedMissingStates;

    static readonly RaycastHit[] hitBuf = new RaycastHit[24];
    static readonly Collider[] overlapBuf = new Collider[16];

    // Everything the routine needs, worked out up front from the real geometry.
    struct Plan
    {
        public VaultProfile profile;
        public Vector3 wallDir;      // horizontal, pointing INTO the wall
        public Vector3 hangRoot;     // root position while rising against the wall (XZ used)
        public Vector3 landRoot;     // where the root ends up
        public float peakRootY;      // highest the root goes (ledge + arc)
        public float edgeFrac;       // 0 = settle onto the top as it moves forward; >0 = hold height until this far across (far-side vault)
        public bool farSide;
        public float ledgeHeight;
    }

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

    void OnDisable()
    {
        // Never leave the character frozen / collision-less if this is switched off mid-vault (death, pooling...).
        if (vaulting) Finish(transform.position, false);
    }

    void Update()
    {
        if (vaulting) { debugVaulting = true; return; }
        debugVaulting = false;

        if (cooldownTimer > 0f) { cooldownTimer -= Time.deltaTime; return; }

        if (pm != null)
        {
            if (jumpAction != null && jumpAction.WasPressedThisFrame() && pm.IsGrounded && !pm.IsCrouching && !pm.MovementLocked)
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

    // ------------------------------------------------------------------------------------------------
    // Detection
    // ------------------------------------------------------------------------------------------------
    void Report(string result)
    {
        debugLastResult = result;
        if (logAttempts) Debug.Log("[VaultClimb] " + result, this);
    }

    bool IsSelf(Collider c) => c.transform == transform || c.transform.IsChildOf(transform);

    // First valid hit along a ray: not us, not another character. wallSurface = steep faces only; otherwise
    // walkable (upward-facing) surfaces only.
    bool CastFirst(Vector3 origin, Vector3 dir, float dist, bool wallSurface, out RaycastHit best)
    {
        best = default;
        int n = Physics.RaycastNonAlloc(origin, dir, hitBuf, dist, vaultLayers, QueryTriggerInteraction.Ignore);
        float bestD = float.MaxValue;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            var h = hitBuf[i];
            if (h.collider == null || IsSelf(h.collider)) continue;
            if (h.collider.GetComponentInParent<Health>() != null) continue;
            if (wallSurface ? h.normal.y > 0.35f : h.normal.y < 0.7f) continue;
            if (h.distance < bestD) { bestD = h.distance; best = h; found = true; }
        }
        return found;
    }

    bool HasRoom(Vector3 feetPoint)
    {
        Vector3 b = feetPoint + Vector3.up * (standRadius + 0.05f);
        Vector3 t = feetPoint + Vector3.up * Mathf.Max(standRadius + 0.06f, standHeight - standRadius);
        int n = Physics.OverlapCapsuleNonAlloc(b, t, standRadius, overlapBuf, vaultLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = overlapBuf[i];
            if (c == null || IsSelf(c) || c.isTrigger) continue;
            if (c.GetComponentInParent<Health>() != null) continue;
            return false;
        }
        return true;
    }

    float FeetY() => cc != null && cc.enabled ? cc.bounds.min.y : transform.position.y;

    float MaxProfileHeight()
    {
        float m = 0f;
        foreach (var p in profiles) m = Mathf.Max(m, p.maxHeight);
        return m * bandTolerance;
    }

    bool AnyProfileAvailable()
    {
        foreach (var p in profiles)
            if (!string.IsNullOrEmpty(p.state) && driver != null && driver.HasBaseState(p.state)) return true;
        return false;
    }

    void TryStartVault()
    {
        if (driver == null) driver = GetComponentInChildren<CharacterAnimationDriver>();
        if (driver == null) { Report("no CharacterAnimationDriver found"); return; }
        if (!AnyProfileAvailable())
        {
            Report("no Vault_* states on the Animator controller");
            if (!warnedMissingStates)
            {
                warnedMissingStates = true;
                Debug.LogWarning("[VaultClimb] The Animator controller has no Vault_StepUp / Vault_Over / Vault_Wall states - re-run Tools > FPS Game > Build Animation System.", this);
            }
            return;
        }

        float feetY = FeetY();
        float rootToFeet = transform.position.y - feetY;
        float radius = cc != null ? cc.radius : 0.3f;
        Vector3 up = Vector3.up;
        Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, up).normalized;
        Vector3 axis = transform.position; axis.y = feetY;
        float maxH = MaxProfileHeight();
        float minH = Mathf.Max(0.12f, (cc != null ? cc.stepOffset : 0.3f) + 0.05f); // anything the controller steps up on its own isn't a vault
        float reach = radius + checkDistance;

        // 1. The obstacle: the LOWEST ray that hits something wall-like.
        RaycastHit wall = default;
        bool gotWall = false;
        for (float h = minH; h <= maxH + 0.001f; h += Mathf.Max(0.05f, rayStep))
        {
            if (CastFirst(axis + up * h, fwd, reach, true, out wall)) { gotWall = true; break; }
        }
        if (!gotWall) { Report("no obstacle in front"); return; }

        Vector3 wallDir = -Vector3.ProjectOnPlane(wall.normal, up);
        if (wallDir.sqrMagnitude < 1e-4f) wallDir = fwd;
        wallDir.Normalize();
        if (Vector3.Angle(wallDir, fwd) > maxWallAngle) { Report("facing the wall too diagonally (" + Mathf.RoundToInt(Vector3.Angle(wallDir, fwd)) + " deg)"); return; }

        // 2. Its top: straight down from above, just past the face.
        Vector3 probe = wall.point + wallDir * 0.2f;
        float topStart = feetY + maxH + 0.4f;
        if (!CastFirst(new Vector3(probe.x, topStart, probe.z), Vector3.down, maxH + 0.7f, false, out var top))
        { Report("no ledge top found within reach (wall too tall?)"); return; }
        if (top.point.y < wall.point.y - 0.02f) { Report("not a ledge"); return; }

        float ledgeHeight = top.point.y - feetY;
        if (!TryPickProfile(ledgeHeight, out var profile))
        { Report("ledge height " + ledgeHeight.ToString("F2") + "m is outside what the clips cover"); return; }

        // 3. Where it ends up: the far side (thin obstacle) or on top.
        var plan = new Plan { profile = profile, wallDir = wallDir, ledgeHeight = ledgeHeight };
        float ledgeTopY = top.point.y;
        Vector3 landFeet;
        float scale = Mathf.Clamp(ledgeHeight / Mathf.Max(0.05f, profile.netRise), scaleClamp.x, scaleClamp.y);
        float arc = Mathf.Max(0.06f, (profile.peakRise - profile.netRise) * scale);

        bool farSide = false;
        landFeet = default;
        float depth = 0f;
        if (vaultOverThinObstacles && ledgeHeight >= vaultOverHeightRange.x && ledgeHeight <= vaultOverHeightRange.y)
            farSide = TryFarSide(wall.point, wallDir, ledgeTopY, out landFeet, out depth);

        if (!farSide)
        {
            Vector3 onTop = top.point + wallDir * landForwardOffset;
            // Make sure that spot is still ON the obstacle (a thin wall's top can be narrower than the offset).
            if (!(CastFirst(new Vector3(onTop.x, ledgeTopY + 0.35f, onTop.z), Vector3.down, 0.7f, false, out var under) && Mathf.Abs(under.point.y - ledgeTopY) < 0.25f))
                onTop = top.point + wallDir * 0.1f;
            landFeet = new Vector3(onTop.x, ledgeTopY + 0.02f, onTop.z);
        }
        if (!HasRoom(landFeet)) { Report("no room to stand where it would land"); return; }

        plan.farSide = farSide;
        plan.landRoot = new Vector3(landFeet.x, landFeet.y + rootToFeet, landFeet.z);

        // Hang point: the body's centre against the wall face, never behind where it already is.
        Vector3 hang = wall.point - wallDir * (radius + wallStandOff);
        hang.y = transform.position.y;
        if (Vector3.Dot(hang - transform.position, wallDir) < 0f) hang = transform.position;
        plan.hangRoot = hang;

        float arcUsed = farSide ? Mathf.Max(arc, 0.12f) : arc;
        plan.peakRootY = Mathf.Max(ledgeTopY + rootToFeet + arcUsed, plan.landRoot.y);

        // Far-side vault: keep the body up until it is past the far edge (depth measured from the wall face).
        if (farSide)
        {
            // Distances along the wall direction, from where the body hangs: to the wall face, then across to the far edge.
            float total = Vector3.Dot(plan.landRoot - hang, wallDir);
            float toFarEdge = Vector3.Dot(wall.point - hang, wallDir) + 0.2f + depth;
            plan.edgeFrac = Mathf.Clamp01(toFarEdge / Mathf.Max(0.1f, total));
        }

        Report("OK: " + profile.state + (farSide ? " (over, " + depth.ToString("F2") + "m deep)" : " (onto)") + " ledge " + ledgeHeight.ToString("F2") + "m");
        StartCoroutine(VaultRoutine(plan));
    }

    // Walk across the obstacle's top in small steps looking for where it stops. Found within maxVaultDepth and
    // there is ground to land on = vault over it. Returns where the feet land, and how deep it was.
    bool TryFarSide(Vector3 wallPoint, Vector3 wallDir, float ledgeTopY, out Vector3 landFeet, out float depth)
    {
        landFeet = default;
        depth = 0f;
        const float step = 0.15f;
        for (float d = 0.2f + step; d <= 0.2f + maxVaultDepth + step; d += step)
        {
            Vector3 p = wallPoint + wallDir * d;
            bool onTop = CastFirst(new Vector3(p.x, ledgeTopY + 0.35f, p.z), Vector3.down, 0.6f, false, out var th)
                         && Mathf.Abs(th.point.y - ledgeTopY) < 0.25f;
            if (onTop) continue;

            // First sample past the far edge: land a little beyond it, on whatever ground is there.
            Vector3 lp = p + wallDir * farLandOffset;
            if (CastFirst(new Vector3(lp.x, ledgeTopY + 0.35f, lp.z), Vector3.down, 0.35f + maxVaultDrop, false, out var gh))
            {
                landFeet = gh.point + Vector3.up * 0.02f;
                depth = d - 0.2f;
                return true;
            }
            return false; // nothing to land on - climb onto it instead
        }
        return false; // too deep to vault over - climb onto it instead
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

    // ------------------------------------------------------------------------------------------------
    // The vault itself
    // ------------------------------------------------------------------------------------------------
    IEnumerator VaultRoutine(Plan plan)
    {
        vaulting = true;
        cooldownTimer = cooldown;
        debugLastProfile = plan.profile.state;

        if (pm != null) pm.SetMovementLocked(true);
        if (cc != null) cc.enabled = false;
        if (agent != null) agent.enabled = false;

        // The controller is off, so "grounded" would read false and drop the Animator into its jump states once the
        // vault clip exits. Hold it true for the whole thing (and a beat after).
        driver.HoldGrounded(plan.profile.duration + enterFade + 0.6f);

        // Right hand keeps the weapon grip; left follows the clip's own reach for the ledge.
        driver.CrossFadeBase(plan.profile.state, enterFade, 0f, 1f);

        Vector3 start = transform.position;
        Quaternion startRot = transform.rotation;
        Quaternion faceWall = Quaternion.LookRotation(plan.wallDir, Vector3.up);
        bool canTurn = alignToWall && Vector3.Angle(transform.forward, plan.wallDir) <= maxWallAngle;

        // Phase split: how much of the clip is spent rising before travelling forward. Taller = more rising.
        float tUp = Mathf.Clamp(0.45f + 0.27f * Mathf.InverseLerp(0.5f, 2.2f, plan.ledgeHeight), 0.45f, 0.72f);
        const float overlap = 0.05f;              // forward travel starts a touch before the top of the rise, so it flows
        float tFwd = tUp - overlap;
        float approachEnd = tUp * 0.7f;           // reaches the wall while still rising, well before it moves over it
        float s0 = plan.farSide ? Mathf.Clamp01(plan.edgeFrac - 0.05f) : 0f;

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / Mathf.Max(0.05f, plan.profile.duration);
            float c = Mathf.Clamp01(t);

            // --- horizontal: slide in to the wall, then travel over it ---
            float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(c / Mathf.Max(0.01f, approachEnd)));
            float f = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((c - tFwd) / Mathf.Max(0.01f, 1f - tFwd)));
            Vector3 xz = Vector3.Lerp(start, plan.hangRoot, a);
            xz = Vector3.Lerp(xz, plan.landRoot, f);

            // --- vertical: rise to the peak first; descend (as it moves forward) to the landing height ---
            float rise = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(c / tUp));
            float yRise = Mathf.Lerp(start.y, plan.peakRootY, rise);
            float fy = s0 >= 0.999f ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((f - s0) / Mathf.Max(0.01f, 1f - s0)));
            float yFwd = Mathf.Lerp(plan.peakRootY, plan.landRoot.y, fy);
            float y = Mathf.Min(yRise, yFwd);

            transform.position = new Vector3(xz.x, y, xz.z);
            if (canTurn) transform.rotation = Quaternion.Slerp(startRot, faceWall, a);

            // Small camera dip over the top so it doesn't feel like an elevator (player only).
            if (pm != null && cameraDip > 0.01f)
            {
                float u = Mathf.Clamp01((c - (tUp - 0.15f)) / Mathf.Max(0.01f, 1f - (tUp - 0.15f)));
                pm.SetCameraActionPitch(-cameraDip * Mathf.Sin(Mathf.PI * u));
            }

            yield return null;
        }

        Finish(plan.landRoot, true);
    }

    void Finish(Vector3 finalPos, bool completed)
    {
        transform.position = finalPos;

        if (driver != null)
        {
            driver.EndFullBodyAction();
            driver.HoldGrounded(0.4f); // the exit crossfade back to the weapon pose is still running
        }

        if (cc != null) cc.enabled = true;
        if (pm != null)
        {
            pm.SetCameraActionPitch(0f);
            pm.SetMovementLocked(false);
        }
        if (agent != null)
        {
            agent.enabled = true;
            if (agent.isOnNavMesh) agent.Warp(transform.position); // re-sync the agent's internal position after moving it by hand
        }

        vaulting = false;
    }
}
