using System;
using System.Collections.Generic;
using UnityEngine;

public enum CharacterWeaponClass
{
    Unarmed,
    Pistol,
    Rifle
}

[Serializable]
public class CharacterAnimationProfile
{
    public CharacterWeaponClass weaponClass;
    public AnimationClip idle;
    public AnimationClip walk;
    public AnimationClip run;
    public AnimationClip shoot;
    public AnimationClip reload;
}

[RequireComponent(typeof(Animator))]
public class CharacterAnimationDriver : MonoBehaviour
{
    [Header("Source clips in Locomotion.controller")]
    public AnimationClip sourceIdle;
    public AnimationClip sourceWalk;
    public AnimationClip sourceRun;

    [Header("Mixamo profiles")]
    public List<CharacterAnimationProfile> profiles = new List<CharacterAnimationProfile>();
    public CharacterWeaponClass defaultWeaponClass = CharacterWeaponClass.Unarmed;

    Animator animator;
    AnimatorOverrideController overrideController;
    UpperBodyPose upperBodyPose;
    CharacterWeaponClass currentWeaponClass;
    static readonly int ShootHash = Animator.StringToHash("Shoot");
    static readonly int ReloadHash = Animator.StringToHash("Reload");

    void Awake()
    {
        animator = GetComponent<Animator>();
        upperBodyPose = GetComponent<UpperBodyPose>();
        currentWeaponClass = defaultWeaponClass;
        ApplyProfile(currentWeaponClass);
    }

    public void SetWeaponClass(CharacterWeaponClass weaponClass)
    {
        currentWeaponClass = weaponClass;
        ApplyProfile(weaponClass);
    }

    public void SetWeaponFrom(WeaponController weapon)
    {
        if (weapon == null)
        {
            SetWeaponClass(CharacterWeaponClass.Unarmed);
            return;
        }

        SetWeaponClass(weapon.isAutomatic || weapon.isShotgun
            ? CharacterWeaponClass.Rifle
            : CharacterWeaponClass.Pistol);
    }

    public void PlayShoot()
    {
        PlayAction(ShootHash, "Shoot");
    }

    public void PlayReload()
    {
        PlayAction(ReloadHash, "Reload");
    }

    void PlayAction(int triggerHash, string stateName)
    {
        CharacterAnimationProfile profile = FindProfile(currentWeaponClass);
        AnimationClip clip = profile != null ? profile.shoot : null;
        if (stateName == "Reload" && profile != null) clip = profile.reload;
        if (clip == null) return;

        animator.SetTrigger(triggerHash);
    }

    void ApplyProfile(CharacterWeaponClass weaponClass)
    {
        if (animator == null) return;

        CharacterAnimationProfile profile = FindProfile(weaponClass);
        if (profile == null) return;

        if (overrideController == null)
        {
            RuntimeAnimatorController baseController = animator.runtimeAnimatorController;
            if (baseController == null) return;
            overrideController = new AnimatorOverrideController(baseController);
            animator.runtimeAnimatorController = overrideController;
        }

        SetOverride(sourceIdle, profile.idle);
        SetOverride(sourceWalk, profile.walk);
        SetOverride(sourceRun, profile.run);

        if (upperBodyPose != null)
            upperBodyPose.hasGun = weaponClass != CharacterWeaponClass.Unarmed;
    }

    void SetOverride(AnimationClip source, AnimationClip replacement)
    {
        if (source != null && replacement != null)
            overrideController[source] = replacement;
    }

    CharacterAnimationProfile FindProfile(CharacterWeaponClass weaponClass)
    {
        for (int i = 0; i < profiles.Count; i++)
            if (profiles[i] != null && profiles[i].weaponClass == weaponClass)
                return profiles[i];
        return null;
    }
}
