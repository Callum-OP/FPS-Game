using UnityEngine;

/// <summary>
/// Lets the held weapon ride in the ANIMATED right hand while a full-body gesture plays (idle gestures like
/// "Check Shoe"), with the left hand staying on the foregrip of the weapon wherever it goes.
///
/// Normally the gun is the master: it hangs off EnemyWeapon's anchor (fixed to the body root) and WeaponHandIK
/// snaps the hands onto its grips. That is right for shooting, but during a gesture the right hand is handed to the
/// clip (WeaponHandIK.SetAnimationFollow) - and a hand that leaves the grip while the gun stays put looks like the
/// hand let go and the gun is floating. This flips the relationship for the right hand only, while the gesture plays:
///
///   1. The weapon anchor is re-posed so the weapon's RIGHT grip lands on the right hand bone exactly as the clip
///      animated it (position and rotation), blended in and out with WeaponHandIK.RightFollow so the gun eases
///      into the hand instead of snapping.
///   2. The LEFT hand needs no special handling: WeaponHandIK's strict lock (order 300, right after this) puts it on
///      the left grip every frame, and the left grip now moves with the weapon. The one thing it cannot do is
///      reach a foregrip that has travelled beyond its arm, so if the foregrip would end up out of reach the weapon
///      is swung about the RIGHT grip (the right hand stays exactly on it) towards the left shoulder, only as far
///      as needed and never past maxReachRotation.
///
/// OWNERSHIP: the anchor is written by EnemyWeapon.Update every frame as an absolute pose and never read back, so
/// overriding it here in LateUpdate (order 299: after every body-posing script, just before WeaponHandIK's final
/// hand lock) cannot feed back into itself - next frame starts clean. Only ever active between SetActive(true) and
/// the moment the right hand has been handed back to the grip, so shoves, reloads and combat are untouched.
///
/// WORKS FOR THE PLAYER TOO. The player's gun hangs off the camera (WeaponADS) rather than an EnemyWeapon anchor;
/// with no EnemyWeapon on the character the weapon's own root is moved instead - same maths, same ownership rule
/// (WeaponADS rewrites its local pose from scratch every Update, so nothing feeds back).
///
/// Created on demand by CharacterAnimationDriver.SetWeaponFollowsHand (idle gestures and the light shove).
/// Nothing to set up by hand.
/// </summary>
[DefaultExecutionOrder(299)]
public class WeaponHandFollow : MonoBehaviour
{
    public WeaponHandIK handIK;
    public EnemyWeapon weapon;

    [Header("Right hand (the weapon follows it)")]
    [Tooltip("How much of the clip's right-hand POSITION the weapon takes. 1 = the grip sits exactly on the hand.")]
    [Range(0f, 1f)] public float positionFollow = 1f;
    [Tooltip("How much of the clip's right-hand ROTATION the weapon takes. 1 = the weapon turns with the wrist (a hand hanging at the side points the muzzle down and back, which is what carrying a rifle in one hand looks like).")]
    [Range(0f, 1f)] public float rotationFollow = 1f;

    [Header("Left hand (stays on the foregrip)")]
    [Tooltip("If the foregrip would end up beyond the left arm's reach, swing the weapon about the right grip towards the left shoulder until it is back in reach.")]
    public bool keepOffHandInReach = true;
    [Tooltip("Most the weapon may be swung (degrees) to keep the foregrip in reach. Past this the left hand just falls short like it does everywhere else.")]
    public float maxReachRotation = 40f;

    [Header("Debug (read-only)")]
    [SerializeField] float debugFollow;
    [SerializeField] float debugReachRotation;

    bool wanted;      // a gesture that hands the right hand to the clip is playing
    bool releasing;   // the gesture ended but the right hand is still blending back to the grip
    bool warned;      // the "can't follow" warning is logged once per component, not every frame

    /// <summary>True while a gesture that gives the right hand to the clip is playing. Turn off when it ends - the
    /// follow then blends out with the hand rather than stopping dead.</summary>
    public void SetActive(bool value)
    {
        if (value) { wanted = true; releasing = false; }
        else if (wanted) { wanted = false; releasing = true; }
    }

    void Awake()
    {
        if (handIK == null) handIK = GetComponent<WeaponHandIK>();
        if (handIK == null) handIK = GetComponentInParent<WeaponHandIK>();
        if (handIK == null) handIK = GetComponentInChildren<WeaponHandIK>();
        if (weapon == null) weapon = GetComponentInParent<EnemyWeapon>();
        if (weapon == null) weapon = GetComponentInChildren<EnemyWeapon>();
        // No EnemyWeapon = the player: the gun root is found from the grip at runtime (see WeaponRoot).
    }

    // What gets moved to put the right grip on the hand: an AI's anchor (the gun hangs off it), or - for the player,
    // whose gun hangs off the camera - the weapon's own root object.
    Transform WeaponRoot(Transform grip)
    {
        if (weapon != null && weapon.Anchor != null) return weapon.Anchor;
        if (grip == null) return null;
        var ads = grip.GetComponentInParent<WeaponADS>();
        if (ads != null) return ads.transform;
        var wc = grip.GetComponentInParent<WeaponController>();
        return wc != null ? wc.transform : null;
    }

    void Warn(string why)
    {
        if (warned) return;
        warned = true;
        Debug.LogWarning("[WeaponHandFollow] Can't make the weapon follow the right hand: " + why + ".", this);
    }

    void LateUpdate()
    {
        debugFollow = 0f; debugReachRotation = 0f;
        if (!wanted && !releasing) return;
        if (handIK == null) { Warn("no WeaponHandIK found"); return; }

        float f = handIK.RightFollow;
        if (!wanted && f < 0.001f) { releasing = false; return; }
        if (f < 0.001f) return;

        Transform hand = handIK.GetHandBone(true);
        Transform grip = handIK.CurrentRightGrip;
        Transform anchor = WeaponRoot(grip);
        if (anchor == null || hand == null || grip == null)
        {
            Warn(anchor == null ? "no weapon root (no EnemyWeapon anchor / WeaponADS / WeaponController above the grip)"
                 : hand == null ? "no right hand bone" : "no right grip on the weapon");
            return;
        }
        debugFollow = f;

        // --- 1. put the right grip on the animated right hand ---
        // (WeaponHandIK's lock makes hand.rotation == grip.rotation, so grip pose <-> hand pose is a rigid match.)
        Quaternion gripRotLocal = Quaternion.Inverse(anchor.rotation) * grip.rotation;
        Vector3 gripPosLocal = anchor.InverseTransformPoint(grip.position);

        Quaternion targetRot = hand.rotation * Quaternion.Inverse(gripRotLocal);
        Quaternion newRot = Quaternion.Slerp(anchor.rotation, targetRot, f * rotationFollow);
        // Position is solved against the rotation actually applied, so the grip stays on the hand even when
        // rotationFollow < 1.
        Vector3 targetPos = hand.position - newRot * gripPosLocal;
        Vector3 newPos = Vector3.Lerp(anchor.position, targetPos, f * positionFollow);
        anchor.SetPositionAndRotation(newPos, newRot);

        // --- 2. keep the foregrip within the left arm's reach ---
        float leftLock = 1f - handIK.LeftFollow;
        Transform leftGrip = handIK.CurrentLeftGrip;
        Transform shoulder = handIK.LeftShoulderBone;
        float reach = handIK.LeftArmReach;
        if (!keepOffHandInReach || leftLock < 0.01f || leftGrip == null || shoulder == null || reach >= Mathf.Infinity) return;

        float limit = reach + handIK.LockStretch;
        Vector3 shoulderPos = shoulder.position;
        Vector3 pivot = grip.position;                 // the right grip: rotating about it can't pull the right hand off
        Vector3 r = leftGrip.position - pivot;
        if ((pivot + r - shoulderPos).magnitude <= limit || r.sqrMagnitude < 1e-6f) return;

        Quaternion full = Quaternion.FromToRotation(r, shoulderPos - pivot);   // foregrip swung straight at the shoulder
        full.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) { angle = 360f - angle; axis = -axis; }
        if (angle < 0.01f) return;

        // Smallest swing that brings the foregrip back into reach (distance falls monotonically along the swing).
        float hi = Mathf.Min(1f, maxReachRotation / angle), lo = 0f;
        if ((pivot + Quaternion.AngleAxis(angle * hi, axis) * r - shoulderPos).magnitude > limit)
            lo = hi;                                    // even the capped swing is short - use all of it
        else
            for (int i = 0; i < 7; i++)
            {
                float mid = 0.5f * (lo + hi);
                if ((pivot + Quaternion.AngleAxis(angle * mid, axis) * r - shoulderPos).magnitude > limit) lo = mid; else hi = mid;
            }
        float t = hi;
        Quaternion swing = Quaternion.AngleAxis(angle * t * leftLock, axis);
        debugReachRotation = angle * t * leftLock;

        anchor.rotation = swing * anchor.rotation;
        anchor.position = pivot + swing * (anchor.position - pivot);
        // The wrist turns with the weapon it is holding (the fingers ride along as its children).
        hand.rotation = swing * hand.rotation;
    }
}
