using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Editor-only tool: Tools > FPS Game > Create Character Prefab From Selection.
///
/// Wraps up everything the shared "YBotBody" model currently needs by hand -
/// Humanoid rig check, the shared CharacterAnimator.controller, a
/// CharacterAnimationDriver, and a built ragdoll (via RagdollBuilder.Build) -
/// into one click, and saves the result as a reusable prefab asset. Swapping in
/// a different character model later is then: import it, run this tool, drop
/// the resulting prefab into Player.prefab/Enemy.prefab in place of the old
/// body - the same drop-in role YBotBody plays today.
///
/// Select the model IN A SCENE first (drag its FBX in if it isn't there yet) -
/// same requirement as Build Ragdoll: components can't be added to a raw
/// model/prefab asset directly, only to a scene instance.
///
/// This deliberately does NOT add player-only or enemy-only pieces
/// (CharacterController/NavMeshAgent, WeaponHandIK, HeadHider, hand grip
/// transforms, PlayerSetup/EnemyAI...) - a bare model has no way of saying
/// which role it's for. Add those afterwards exactly like you do for YBotBody
/// today, on whichever of Player.prefab/Enemy.prefab nests the new prefab.
/// </summary>
public static class CharacterPrefabBuilder
{
    const string OutDir = "Assets/Prefabs/Characters";
    const string ControllerPath = "Assets/Animations/CharacterAnimator.controller";

    [MenuItem("Tools/FPS Game/Create Character Prefab From Selection")]
    static void CreateFromSelection()
    {
        GameObject go = Selection.activeGameObject;
        if (go == null)
        {
            Debug.LogError("Create Character Prefab: select the model's root GameObject in the scene first " +
                "(drag the FBX in if it's not there yet).");
            return;
        }

        if (PrefabUtility.IsPartOfPrefabAsset(go))
        {
            Debug.LogError($"Create Character Prefab: '{go.name}' is a raw model/prefab asset, not a scene " +
                "instance. Drag it into the scene first, same as Build Ragdoll requires.", go);
            return;
        }

        // Same "don't grab a non-humanoid parent Animator" search used by RagdollBuilder
        // and DeathCam - kept consistent so all three tools agree on which Animator is
        // the real rig.
        Animator anim = null;
        foreach (var candidate in go.GetComponentsInChildren<Animator>(true))
        {
            if (candidate.avatar != null && candidate.avatar.isHuman)
            {
                anim = candidate;
                break;
            }
        }
        if (anim == null)
        {
            Debug.LogError($"Create Character Prefab: no Humanoid Animator found on '{go.name}' or its " +
                "children - set the model's Rig to Humanoid in its import settings first.", go);
            return;
        }

        Undo.SetCurrentGroupName("Create Character Prefab");
        int group = Undo.GetCurrentGroup();

        // Shared Animator Controller - every character (player or enemy) uses the same one.
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
        {
            Debug.LogWarning($"Create Character Prefab: {ControllerPath} not found - run Tools > FPS Game > " +
                "Build Animation System first, then re-run this (or assign the controller by hand afterwards).");
        }
        else
        {
            Undo.RecordObject(anim, "Assign Animator Controller");
            anim.runtimeAnimatorController = controller;
        }

        // CharacterAnimationDriver - the go-between CharacterAnimationDriver/EnemyAI talk to.
        if (go.GetComponentInChildren<CharacterAnimationDriver>() == null)
            Undo.AddComponent<CharacterAnimationDriver>(anim.gameObject);

        // Ragdoll - the runtime on/off switch, plus the actual bone Rigidbodies/
        // Colliders/CharacterJoints RagdollBuilder creates.
        if (go.GetComponentInChildren<Ragdoll>() == null)
        {
            Ragdoll ragdoll = Undo.AddComponent<Ragdoll>(go);
            ragdoll.animator = anim;
        }
        RagdollBuilder.Build(go);

        Undo.CollapseUndoOperations(group);

        // Save as a reusable prefab asset - same drop-in role YBotBody plays today.
        Directory.CreateDirectory(OutDir);
        string path = AssetDatabase.GenerateUniqueAssetPath($"{OutDir}/{go.name}.prefab");
        GameObject asset = PrefabUtility.SaveAsPrefabAsset(go, path);

        Debug.Log($"Create Character Prefab: saved '{path}'. Drop it into Player.prefab/Enemy.prefab in place " +
            "of the old body model, then re-add whichever role-specific pieces that character needs " +
            "(CharacterController or NavMeshAgent, WeaponHandIK/HeadHider for a player rig, hand grip " +
            "transforms for weapons, etc.) - same as setting up YBotBody today.", asset);
    }
}