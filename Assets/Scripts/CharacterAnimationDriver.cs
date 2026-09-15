using UnityEngine;

public enum CharacterWeaponClass
{
    Unarmed = 0,
    Pistol = 1,
    Rifle = 2
}

/// <summary>
/// Talks to the Animator built by AnimationSystemBuilder (Assets/Animations/CharacterAnimator.controller).
/// Base layer picks the right full-body directional blend tree for the current weapon;
/// UpperBody layer (masked to arms/spine/head) handles Aim/Fire/Reload/Melee without
/// ever interrupting the legs. Put this next to the Animator, same as before.
///
/// Public API is intentionally unchanged from the old version (SetWeaponFrom, PlayShoot,
/// PlayReload) so WeaponController/PlayerSetup keep working with no edits; PlayMelee/
/// SetAiming/SetCrouching are new hooks used by PlayerMelee/WeaponADS/PlayerMovement.
/// </summary>
[RequireComponent(typeof(Animator))]
public class CharacterAnimationDriver : MonoBehaviour
{
    public CharacterWeaponClass defaultWeaponClass = CharacterWeaponClass.Unarmed;

    Animator animator;
    CharacterWeaponClass currentWeaponClass;

    static readonly int WeaponClassHash = Animator.StringToHash("WeaponClass");
    static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
    static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");
    static readonly int AimingHash = Animator.StringToHash("Aiming");
    static readonly int FireHash = Animator.StringToHash("Fire");
    static readonly int ReloadHash = Animator.StringToHash("Reload");
    static readonly int MeleeHash = Animator.StringToHash("Melee");
    static readonly int DeadHash = Animator.StringToHash("Dead");
    static readonly int InjuredHash = Animator.StringToHash("Injured");
    static readonly int MoveXHash = Animator.StringToHash("MoveX");
    static readonly int MoveYHash = Animator.StringToHash("MoveY");

    void Awake()
    {
        var animators = GetComponentsInChildren<Animator>(true);
        for (int i = animators.Length - 1; i >= 0; i--)
        {
            if (animators[i].runtimeAnimatorController != null)
            {
                animator = animators[i];
                break;
            }
        }
        currentWeaponClass = defaultWeaponClass;
        if (animator != null) animator.SetInteger(WeaponClassHash, (int)currentWeaponClass);
    }

    /// <summary>Straight from CharacterLocomotion each frame - local-space move direction.</summary>
    public void SetMove(float x, float y)
    {
        if (!CanAnimate()) return;
        animator.SetFloat(MoveXHash, x);
        animator.SetFloat(MoveYHash, y);
    }

    public void SetGrounded(bool grounded)
    {
        if (CanAnimate()) animator.SetBool(IsGroundedHash, grounded);
    }

    public void SetCrouching(bool crouching)
    {
        if (CanAnimate()) animator.SetBool(IsCrouchingHash, crouching);
    }

    public void SetAiming(bool aiming)
    {
        if (CanAnimate()) animator.SetBool(AimingHash, aiming);
    }

    public void SetWeaponClass(CharacterWeaponClass weaponClass)
    {
        currentWeaponClass = weaponClass;
        if (CanAnimate()) animator.SetInteger(WeaponClassHash, (int)weaponClass);
    }

    /// <summary>Convenience used by PlayerSetup/WeaponController - infers weapon class from the gun.</summary>
    public void SetWeaponFrom(WeaponController weapon)
    {
        if (weapon == null) { SetWeaponClass(CharacterWeaponClass.Unarmed); return; }
        SetWeaponClass(weapon.isAutomatic || weapon.isShotgun ? CharacterWeaponClass.Rifle : CharacterWeaponClass.Pistol);
    }

    public void PlayShoot() { if (CanAnimate()) animator.SetTrigger(FireHash); }
    public void PlayReload() { if (CanAnimate()) animator.SetTrigger(ReloadHash); }
    public void PlayMelee() { if (CanAnimate()) animator.SetTrigger(MeleeHash); }

    public void SetDead(bool dead) { if (CanAnimate()) animator.SetBool(DeadHash, dead); }
    public void SetInjured(bool injured) { if (CanAnimate()) animator.SetBool(InjuredHash, injured); }

    bool CanAnimate() => animator != null && animator.runtimeAnimatorController != null;

    public CharacterWeaponClass CurrentWeaponClass => currentWeaponClass;
}