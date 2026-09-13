using UnityEngine;

/// <summary>
/// Fixes the "shooting animation doesn't match the weapon" problem WITHOUT rebuilding
/// the camera/body/weapon hierarchy.
///
/// The weapon stays a child of the camera (so aim/recoil/sway/lean all keep working
/// exactly as before). This script runs after the Animator has posed the arms from the
/// retargeted Mixamo clips, and uses Unity's built-in Animator IK pass to bend the hand
/// bones the rest of the way onto two small empty transforms you place on the weapon
/// itself - RightHandGrip (and optionally LeftHandGrip for two-handed weapons). Because
/// it runs after animation and reads the weapon's live position, it stays correct no
/// matter how the camera pitches or how far the baked clip's hand position is from the
/// weapon's actual grip - unlike trying to re-align the whole body/camera to fix it.
///
/// SETUP (per weapon prefab):
///  1. Add an empty child GameObject where the right hand should sit on the grip,
///     rotated so its local +Z (forward) points the same way the hand's fingers curl
///     around the grip. Name it e.g. "RightHandGrip".
///  2. For two-handed weapons (rifles), add a second empty at the forward hand/foregrip,
///     named e.g. "LeftHandGrip".
///  3. Assign these to WeaponController.rightHandGrip / leftHandGrip in the Inspector.
///  4. Put this script on the SAME GameObject as the player's Animator (the body), next
///     to CharacterAnimationDriver. PlayerSetup wires it automatically on weapon switch.
///  5. Re-run Tools/FPS Game/Build Animation System once after pulling this change - it
///     now also enables the IK pass on the base Animator layer, which this script needs.
///
/// Tune elbowHint if the elbows pop to a weird angle - it's an optional world-space
/// point the forearm bends towards (roughly out to the character's side and slightly
/// forward works for most rigs).
/// </summary>
[RequireComponent(typeof(Animator))]
public class WeaponHandIK : MonoBehaviour
{
    [Tooltip("How fast the IK weight blends in/out when weapons change or grips are cleared.")]
    public float blendSpeed = 8f;

    [Tooltip("Optional world-space elbow hint transforms (leave empty if not needed).")]
    public Transform rightElbowHint;
    public Transform leftElbowHint;

    Animator anim;
    Transform rightGrip;
    Transform leftGrip;
    WeaponHandIKAnimatorBridge animatorBridge;
    float rightWeight;
    float leftWeight;

    void Awake()
    {
        foreach (var candidate in GetComponentsInChildren<Animator>(true))
        {
            if (candidate.avatar != null && candidate.avatar.isHuman)
            {
                anim = candidate;
                break;
            }
        }

        if (anim == null)
            Debug.LogError($"{name} could not find a humanoid Animator in its hierarchy. Weapon hand IK is disabled.", this);
        else if (anim.gameObject != gameObject)
        {
            animatorBridge = anim.gameObject.GetComponent<WeaponHandIKAnimatorBridge>();
            if (animatorBridge == null)
                animatorBridge = anim.gameObject.AddComponent<WeaponHandIKAnimatorBridge>();
            animatorBridge.owner = this;
        }
    }

    /// <summary>Called by PlayerSetup whenever the active weapon changes. Pass null for
    /// either hand to release it (weight fades out smoothly, no snapping).</summary>
    public void SetGripTargets(Transform right, Transform left)
    {
        rightGrip = right;
        leftGrip = left;
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (anim == null) return;

        ApplyIK();
    }

    public void ApplyIK()
    {
        if (anim == null) return;

        // Don't fight the melee swing / ragdoll / death poses - only apply while a
        // weapon is actually meant to be held.
        float rightTarget = rightGrip != null ? 1f : 0f;
        float leftTarget = leftGrip != null ? 1f : 0f;
        rightWeight = Mathf.MoveTowards(rightWeight, rightTarget, blendSpeed * Time.deltaTime);
        leftWeight = Mathf.MoveTowards(leftWeight, leftTarget, blendSpeed * Time.deltaTime);

        ApplyHand(AvatarIKGoal.RightHand, rightGrip, rightWeight, rightElbowHint, AvatarIKHint.RightElbow);
        ApplyHand(AvatarIKGoal.LeftHand, leftGrip, leftWeight, leftElbowHint, AvatarIKHint.LeftElbow);
    }

    void ApplyHand(AvatarIKGoal goal, Transform grip, float weight, Transform elbowHint, AvatarIKHint hint)
    {
        anim.SetIKPositionWeight(goal, weight);
        anim.SetIKRotationWeight(goal, weight);
        if (weight <= 0f) return; // grip may already be null while weight fades out

        if (grip != null)
        {
            anim.SetIKPosition(goal, grip.position);
            anim.SetIKRotation(goal, grip.rotation);
        }

        if (elbowHint != null)
        {
            anim.SetIKHintPositionWeight(hint, weight);
            anim.SetIKHintPosition(hint, elbowHint.position);
        }
    }

}

public class WeaponHandIKAnimatorBridge : MonoBehaviour
{
    public WeaponHandIK owner;

    void OnAnimatorIK(int layerIndex)
    {
        owner?.ApplyIK();
    }
}