using UnityEngine;

/// <summary>
/// Keeps an AI's hands ON the weapon's grips - the left hand on the foregrip of rifles and other long guns above all.
///
/// WHY THE LEFT HAND FELL SHORT. The AI gun hangs off a fixed anchor (EnemyWeapon) while the arms come from the
/// animation, so the foregrip can end up further from the left shoulder than the arm is long - worst for long guns,
/// and worst in the lowered pose where the gun sits out at the waist. WeaponHandIK already answers that by pulling
/// the gun back, but that answer is (a) capped at maxPullBack, which a long rifle can exceed, and (b) smoothed over
/// ~0.25s so it always lags the pose it is correcting - walking, turning, raising and lowering all open the gap
/// again each frame. Whatever is left over is what showed as the hand not reaching the grip.
///
/// WHAT THIS DOES. Once the body is fully posed (order 299, after every torso/head/limit script and just before
/// WeaponHandIK's final hand lock at 300) it measures each shoulder against the grip that hand is meant to hold and,
/// if a grip is out of reach, slides the whole weapon the smallest distance that brings it back:
///   - TRANSLATION ONLY: the muzzle keeps pointing exactly where the AI is aiming, so shooting is unaffected.
///   - Both hands are solved together (right first, left last), so fixing the left hand cannot drag the right
///     one off its grip.
///   - Instant to apply, eased to release: the correction can never lag behind what is needed, and it fades
///     away smoothly when the need goes (no popping).
///   - Zero when nothing is out of reach - a gun that was already held correctly is not touched, so the
///     animation's own look is unchanged. Only the shortfall is removed.
///   - Stands aside while a clip owns a hand (idle gestures, shove: WeaponHandFollow does its own reach check),
///     and ignores a hand that is reloading (it is going to the magazine, not the grip).
///
/// OWNERSHIP: the anchor is rewritten from scratch by EnemyWeapon.Update every frame and never read back, so
/// shifting it here in LateUpdate cannot feed into itself (same rule as WeaponHandFollow).
///
/// Added automatically by EnemyWeapon. Nothing to set up by hand.
/// </summary>
[DefaultExecutionOrder(299)]
public class WeaponReachAssist : MonoBehaviour
{
    public WeaponHandIK handIK;
    public EnemyWeapon weapon;

    [Tooltip("Master switch.")]
    public bool enabledAssist = true;
    [Tooltip("Metres beyond the 'comfortable' reach (WeaponHandIK.armReachSafety x arm length) a hand may stretch to its grip. Small: a nearly straight arm still looks fine, a fully locked one does not.")]
    public float extraStretch = 0.02f;
    [Tooltip("Most the weapon may ever be shifted, metres. A safety net - normal corrections are a few centimetres.")]
    public float maxShift = 0.4f;
    [Tooltip("How quickly the correction relaxes once it is no longer needed (per second). It is applied instantly whatever this is.")]
    public float releaseSpeed = 8f;

    [Header("Debug (read-only)")]
    [SerializeField] float debugShift;

    Vector3 applied; // world-space shift in effect last frame (only used to ease the release)

    void Awake()
    {
        if (handIK == null) handIK = GetComponent<WeaponHandIK>();
        if (weapon == null) weapon = GetComponentInParent<EnemyWeapon>();
        if (weapon == null) weapon = GetComponentInChildren<EnemyWeapon>();
    }

    struct Reach
    {
        public bool active;
        public Vector3 grip, shoulder;
        public float limit;
    }

    // Pulls t (a weapon shift) just far enough that this hand's grip is back within its limit. Projection onto the
    // sphere around the shoulder = the smallest possible move, straight towards the shoulder.
    static Vector3 Push(Vector3 t, Reach r)
    {
        if (!r.active) return t;
        Vector3 d = r.shoulder - (r.grip + t);
        float dist = d.magnitude;
        if (dist > r.limit && dist > 1e-5f) t += d / dist * (dist - r.limit);
        return t;
    }

    Vector3 Solve(Vector3 start, Reach right, Reach left)
    {
        Vector3 t = start;
        // Alternating projections onto the two reach spheres; left last so it wins if they cannot both be met.
        for (int i = 0; i < 4; i++) { t = Push(t, right); t = Push(t, left); }
        if (t.magnitude > maxShift) t = t.normalized * maxShift;
        return t;
    }

    void LateUpdate()
    {
        debugShift = 0f;
        if (handIK == null || weapon == null) return;
        Transform anchor = weapon.Anchor;
        if (anchor == null) return;

        float dt = Time.deltaTime;
        float releaseK = 1f - Mathf.Exp(-releaseSpeed * dt);

        // A clip owns the right hand (gesture / shove): WeaponHandFollow is moving the weapon and checks reach itself.
        bool clipOwnsGun = handIK.RightFollow > 0.01f;
        if (clipOwnsGun) { applied = Vector3.zero; return; }

        Reach right = default, left = default;
        if (enabledAssist && !clipOwnsGun)
        {
            Transform rs = handIK.RightShoulderBone, ls = handIK.LeftShoulderBone;
            Transform rg = handIK.CurrentRightGrip, lg = handIK.CurrentLeftGrip;
            float rReach = handIK.RightArmReach, lReach = handIK.LeftArmReach;

            right.active = rs != null && rg != null && rReach < Mathf.Infinity && handIK.RightGripWeight > 0.3f && !handIK.RightGripOverridden;
            left.active  = ls != null && lg != null && lReach < Mathf.Infinity && handIK.LeftGripWeight  > 0.3f && !handIK.LeftGripOverridden;
            if (right.active) { right.grip = rg.position; right.shoulder = rs.position; right.limit = rReach + extraStretch; }
            if (left.active)  { left.grip  = lg.position; left.shoulder  = ls.position; left.limit  = lReach + extraStretch; }
        }

        // What is needed right now, from scratch...
        Vector3 required = Solve(Vector3.zero, right, left);
        // ...the previous shift easing towards it (so the release is smooth)...
        applied = Vector3.Lerp(applied, required, releaseK);
        // ...topped up instantly if that still is not enough (so the reach can never lag).
        Vector3 shift = Solve(applied, right, left);
        applied = shift;

        if (shift.sqrMagnitude < 1e-8f) return;
        anchor.position += shift;
        debugShift = shift.magnitude;
    }
}
