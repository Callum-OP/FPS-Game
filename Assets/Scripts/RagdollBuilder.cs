using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Editor-only tool: Tools > FPS Game > Build Ragdoll.
///
/// Ragdoll.cs (runtime) expects the skeleton's core bones to already have a Rigidbody +
/// Collider (+ CharacterJoint back to their parent) on them - it just flips them between
/// kinematic/disabled (while alive, animator-driven) and dynamic (on death). Nothing in
/// the project ever actually created those components, so at death Ragdoll.cs finds zero
/// Rigidbodies, does nothing physically, and the animator just freezes wherever it was -
/// which is exactly the "no death animation, character just stands there / falls over
/// stiff like a statue" bug.
///
/// This tool builds them once per character. Select the character's root GameObject (the
/// one with the Humanoid Animator - the Player rig, or an enemy prefab instance) and run
/// Tools > FPS Game > Build Ragdoll. Works off Animator.GetBoneTransform (HumanBodyBones),
/// so it works on any humanoid-retargeted rig using this project's Y Bot model, not just
/// one specific FBX's bone names.
///
/// Safe to re-run: any bone that already has a Rigidbody is left completely alone, so
/// re-running after tweaking something else won't duplicate components.
///
/// Note: run this on a PREFAB INSTANCE IN A SCENE (or opened in Prefab Mode), not on the
/// raw FBX/model asset - Rigidbody/Collider/CharacterJoint can't be added to an imported
/// model file directly.
/// </summary>
public static class RagdollBuilder
{
    class BoneSpec
    {
        public HumanBodyBones bone;
        public HumanBodyBones? parent; // null = ragdoll root (Hips) - no CharacterJoint
        public float mass;
        public float radius;
        public BoneSpec(HumanBodyBones b, HumanBodyBones? p, float m, float r)
        { bone = b; parent = p; mass = m; radius = r; }
    }

    // Core bones only - enough for a believable collapse without needing a joint per finger.
    static readonly BoneSpec[] Bones =
    {
        new BoneSpec(HumanBodyBones.Hips,          null,                          8f,   0.16f),
        new BoneSpec(HumanBodyBones.Spine,         HumanBodyBones.Hips,           6f,   0.15f),
        new BoneSpec(HumanBodyBones.Chest,         HumanBodyBones.Spine,          6f,   0.16f),
        new BoneSpec(HumanBodyBones.Head,          HumanBodyBones.Chest,          3f,   0.12f),

        new BoneSpec(HumanBodyBones.LeftUpperArm,  HumanBodyBones.Chest,          2f,   0.06f),
        new BoneSpec(HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftUpperArm,   1.5f, 0.05f),
        new BoneSpec(HumanBodyBones.RightUpperArm, HumanBodyBones.Chest,          2f,   0.06f),
        new BoneSpec(HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm,  1.5f, 0.05f),

        new BoneSpec(HumanBodyBones.LeftUpperLeg,  HumanBodyBones.Hips,           4f,   0.09f),
        new BoneSpec(HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftUpperLeg,   3f,   0.07f),
        new BoneSpec(HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips,           4f,   0.09f),
        new BoneSpec(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg,  3f,   0.07f),
    };

    [MenuItem("Tools/FPS Game/Build Ragdoll")]
    static void BuildForSelection()
    {
        GameObject go = Selection.activeGameObject;
        if (go == null)
        {
            Debug.LogError("Build Ragdoll: select the character's root GameObject (the one with the Humanoid Animator) first.");
            return;
        }

        // Some rigs (e.g. this project's Enemy prefab) have a non-humanoid Animator
        // sitting on a parent object above the actual body rig's Animator - just taking
        // GetComponent<Animator>() on the selected object can find that one first and
        // wrongly report "no Humanoid Animator" even though the real one is right there
        // in a child. Search every Animator in the hierarchy and use the first Humanoid one.
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
            Debug.LogError($"Build Ragdoll: no Humanoid Animator found on '{go.name}' or its children.", go);
            return;
        }

        Undo.SetCurrentGroupName("Build Ragdoll");
        int group = Undo.GetCurrentGroup();

        var rigidbodies = new Dictionary<HumanBodyBones, Rigidbody>();
        int created = 0, skipped = 0;

        // Pass 1: Rigidbody + Collider per bone (joints need every Rigidbody to exist first).
        foreach (var spec in Bones)
        {
            Transform t = anim.GetBoneTransform(spec.bone);
            if (t == null)
            {
                Debug.LogWarning($"Build Ragdoll: '{go.name}' has no {spec.bone} bone mapped in its Avatar - skipping.");
                continue;
            }

            Rigidbody rb = t.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rigidbodies[spec.bone] = rb;
                skipped++;
                continue; // already built - never duplicate
            }

            rb = Undo.AddComponent<Rigidbody>(t.gameObject);
            rb.mass = spec.mass;
            rigidbodies[spec.bone] = rb;

            AddCapsule(t, spec.radius);
            created++;
        }

        // Pass 2: CharacterJoint back to each bone's parent rigidbody.
        foreach (var spec in Bones)
        {
            if (spec.parent == null) continue;
            Transform t = anim.GetBoneTransform(spec.bone);
            if (t == null || !rigidbodies.ContainsKey(spec.bone)) continue;
            if (t.GetComponent<CharacterJoint>() != null) continue; // already built
            if (!rigidbodies.TryGetValue(spec.parent.Value, out Rigidbody parentRb)) continue;

            CharacterJoint joint = Undo.AddComponent<CharacterJoint>(t.gameObject);
            joint.connectedBody = parentRb;
            joint.axis = new Vector3(1f, 0f, 0f);
            joint.swingAxis = new Vector3(0f, 1f, 0f);
            joint.lowTwistLimit = new SoftJointLimit { limit = -20f };
            joint.highTwistLimit = new SoftJointLimit { limit = 20f };
            joint.swing1Limit = new SoftJointLimit { limit = 40f };
            joint.swing2Limit = new SoftJointLimit { limit = 40f };
            joint.enableProjection = true;
        }

        Undo.CollapseUndoOperations(group);
        Debug.Log($"Build Ragdoll on '{go.name}': {created} bone(s) built, {skipped} already had a Rigidbody (left untouched). " +
                  "Ragdoll.cs keeps all of this kinematic/disabled until death, same as before - this just gives it something to switch on.");
    }

    static void AddCapsule(Transform bone, float radius)
    {
        // Aim the capsule at the bone's first child - this holds for the strict chains
        // used here (Hips>Spine>Chest>Head, UpperArm>LowerArm, UpperLeg>LowerLeg). A bone
        // with no usable child just gets a small sphere-like capsule so this never throws
        // on an unusual rig.
        CapsuleCollider cap = Undo.AddComponent<CapsuleCollider>(bone.gameObject);
        cap.radius = radius;

        Transform end = bone.childCount > 0 ? bone.GetChild(0) : null;
        Vector3 localEnd = end != null ? bone.InverseTransformPoint(end.position) : Vector3.zero;
        float length = localEnd.magnitude;

        if (end == null || length < 0.01f)
        {
            cap.height = radius * 2f;
            cap.direction = 0;
            return;
        }

        // Capsule "direction" is whichever local axis the bone chain actually runs along -
        // pick the axis with the largest component instead of always assuming X, so this
        // still looks right on rigs where a bone's local axes aren't perfectly aligned
        // down the chain (e.g. a near-vertical spine).
        Vector3 absLocal = new Vector3(Mathf.Abs(localEnd.x), Mathf.Abs(localEnd.y), Mathf.Abs(localEnd.z));
        int axis = absLocal.x >= absLocal.y && absLocal.x >= absLocal.z ? 0
                 : absLocal.y >= absLocal.z ? 1 : 2;

        cap.direction = axis;
        cap.height = Mathf.Max(length, radius * 2f);
        cap.center = localEnd * 0.5f;
    }
}