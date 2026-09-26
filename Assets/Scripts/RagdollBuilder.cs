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
///
/// JOINT LIMITS (anatomical pass): most joints use axis=local X (the bone-to-child chain
/// axis, as before) with a twist limit for the bone's own internal rotation and swing1/2
/// for its main bend + sideways give. Knees and elbows are the exception - a real knee/
/// elbow only bends one direction (0 degrees to ~140 degrees), which CharacterJoint's
/// swing limits can't express (swing1Limit/swing2Limit are always symmetric +/-limit).
/// So for those two bones only, 'axis' is pointed down the BEND direction (local Z)
/// instead of the chain direction, and the (asymmetric) twist limit is used to enforce
/// "0 to ~140, forward only" - swing1/2 then cover the two directions that should barely
/// move at all (no sideways knee wobble, no internal shin twist). Values are a reasonable
/// starting point, not measured against this project's actual rig - tune by eye in Play
/// Mode with a ragdoll death, like everything else placement/feel-related here.
/// </summary>
public static class RagdollBuilder
{
    class BoneSpec
    {
        public HumanBodyBones bone;
        public HumanBodyBones? parent; // null = ragdoll root (Hips) - no CharacterJoint
        public float radiusRatio;      // capsule radius as a fraction of this bone's own measured length
        public Vector3 axis;           // CharacterJoint twist axis, LOCAL space
        public Vector3 swingAxis;      // CharacterJoint swing axis, LOCAL space (must be roughly perpendicular to axis)
        public float lowTwist, highTwist; // degrees - the asymmetric limit, used as the hinge bend for knee/elbow
        public float swing1, swing2;      // degrees - symmetric

        public BoneSpec(HumanBodyBones b, HumanBodyBones? p, float radiusRatio, Vector3 axis, Vector3 swingAxis,
                         float lowTwist, float highTwist, float swing1, float swing2)
        {
            bone = b; parent = p; this.radiusRatio = radiusRatio;
            this.axis = axis; this.swingAxis = swingAxis;
            this.lowTwist = lowTwist; this.highTwist = highTwist;
            this.swing1 = swing1; this.swing2 = swing2;
        }
    }

    static readonly Vector3 X = Vector3.right;
    static readonly Vector3 Y = Vector3.up;
    static readonly Vector3 Z = Vector3.forward;

    // Rough humanoid limb-thickness-to-length ratios plus anatomical joint limits.
    // Mass is computed from each built capsule's actual volume (see BoneDensity) rather
    // than a fixed number, same reasoning as before: a value tuned for one model's scale
    // doesn't generalise to a differently-scaled/proportioned one.
    static readonly BoneSpec[] Bones =
    {
        new BoneSpec(HumanBodyBones.Hips,    null,                    0.30f, X, Y,  0f,  0f,  0f,  0f), // root - no joint
        new BoneSpec(HumanBodyBones.Spine,   HumanBodyBones.Hips,     0.45f, X, Y, -20f, 20f, 30f, 20f),
        new BoneSpec(HumanBodyBones.Chest,   HumanBodyBones.Spine,    0.50f, X, Y, -15f, 15f, 25f, 15f),
        new BoneSpec(HumanBodyBones.Head,    HumanBodyBones.Chest,    0.45f, X, Y, -70f, 70f, 40f, 20f),

        // Shoulders: most freedom of any joint (raising/reaching/aiming), chain-axis twist.
        new BoneSpec(HumanBodyBones.LeftUpperArm,  HumanBodyBones.Chest,         0.18f, X, Y, -60f, 60f, 100f, 80f),
        new BoneSpec(HumanBodyBones.RightUpperArm, HumanBodyBones.Chest,         0.18f, X, Y, -60f, 60f, 100f, 80f),
        // Elbows: hinge - axis pointed down the BEND direction (Z) so the asymmetric twist
        // limit gives "0 to 145, forward only"; swing1/2 (old chain-axis + lateral) locked tight.
        new BoneSpec(HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftUpperArm,  0.16f, Z, Y,   0f, 145f,  8f, 8f),
        new BoneSpec(HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm, 0.16f, Z, Y,   0f, 145f,  8f, 8f),

        // Hips: generous forward/back swing, tighter ab/adduction, modest internal rotation.
        new BoneSpec(HumanBodyBones.LeftUpperLeg,  HumanBodyBones.Hips,          0.22f, X, Y, -20f, 20f, 110f, 35f),
        new BoneSpec(HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips,          0.22f, X, Y, -20f, 20f, 110f, 35f),
        // Knees: same hinge treatment as elbows - "0 to 140, forward only", everything else tight.
        new BoneSpec(HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftUpperLeg,  0.18f, Z, Y,   0f, 140f,  6f, 6f),
        new BoneSpec(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg, 0.18f, Z, Y,   0f, 140f,  6f, 6f),
    };

    // kg per cubic metre of capsule volume. Not meant to be biologically accurate -
    // just a constant that keeps mass proportional to size for WHATEVER model this
    // runs on, so a bone's mass and its collider size always agree with each other.
    const float BoneDensity = 300f;

    [MenuItem("Tools/FPS Game/Build Ragdoll")]
    static void BuildForSelection()
    {
        GameObject go = Selection.activeGameObject;
        if (go == null)
        {
            Debug.LogError("Build Ragdoll: select the character's root GameObject (the one with the Humanoid Animator) first.");
            return;
        }
        Build(go);
    }

    /// <summary>Builds ragdoll bones/joints on a scene GameObject. Shared with CharacterPrefabBuilder.</summary>
    public static void Build(GameObject go)
    {
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

        // Hips branches three ways (spine + two legs) so it has no single well-defined
        // "chain length" the way every other bone here does via its own child - size its
        // radius off the whole torso+head span instead.
        float hipsRadiusReference = ReferenceLength(anim);

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
            rigidbodies[spec.bone] = rb;

            float radiusReference = spec.bone == HumanBodyBones.Hips ? hipsRadiusReference : -1f;
            CapsuleCollider cap = AddCapsule(t, spec.radiusRatio, radiusReference);

            // Mass from the capsule's actual volume, not a fixed number - see BoneDensity.
            float cylinderLength = Mathf.Max(0f, cap.height - 2f * cap.radius);
            float volume = Mathf.PI * cap.radius * cap.radius * cylinderLength
                         + (4f / 3f) * Mathf.PI * cap.radius * cap.radius * cap.radius;
            rb.mass = Mathf.Max(0.5f, volume * BoneDensity);

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
            joint.axis = spec.axis;
            joint.swingAxis = spec.swingAxis;
            joint.lowTwistLimit = new SoftJointLimit { limit = spec.lowTwist };
            joint.highTwistLimit = new SoftJointLimit { limit = spec.highTwist };
            joint.swing1Limit = new SoftJointLimit { limit = spec.swing1 };
            joint.swing2Limit = new SoftJointLimit { limit = spec.swing2 };
            // Projection was previously enabled here to keep joints from stretching
            // under extreme force, but projection is a direct position SNAP applied
            // outside the normal physics step - it completely bypasses collision
            // detection. A ragdoll spawning in an overlapping death pose can easily
            // exceed the projection distance the instant physics turns on, teleporting
            // a bone (and everything downstream of it in the joint chain) straight
            // through floor/wall geometry in one frame - this is almost certainly why
            // enemies were intermittently falling through several floors at once with
            // no apparent collision response. A joint stretching slightly for a frame
            // while the solver catches up is far preferable to a silent teleport.
            joint.enableProjection = false;
        }

        Undo.CollapseUndoOperations(group);
        Debug.Log($"Build Ragdoll on '{go.name}': {created} bone(s) built, {skipped} already had a Rigidbody (left untouched). " +
                  "Ragdoll.cs keeps all of this kinematic/disabled until death, same as before - this just gives it something to switch on.");
    }

    static float ReferenceLength(Animator anim)
    {
        Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
        Transform head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (hips != null && head != null)
            return Vector3.Distance(hips.position, head.position);
        return 1f; // sane fallback for a rig missing Head entirely
    }

    static CapsuleCollider AddCapsule(Transform bone, float radiusRatio, float radiusReferenceLength = -1f)
    {
        // Aim the capsule at the bone's first child - this holds for the strict chains
        // used here (Hips>Spine>Chest>Head, UpperArm>LowerArm, UpperLeg>LowerLeg). A bone
        // with no usable child just gets a small sphere-like capsule so this never throws
        // on an unusual rig.
        CapsuleCollider cap = Undo.AddComponent<CapsuleCollider>(bone.gameObject);

        Transform end = bone.childCount > 0 ? bone.GetChild(0) : null;
        Vector3 localEnd = end != null ? bone.InverseTransformPoint(end.position) : Vector3.zero;
        float length = end != null ? localEnd.magnitude : 0f;

        // Radius scales with the bone's own measured length by default, so it adapts
        // automatically to whatever scale/proportions a given model uses - a fixed
        // absolute radius (the old approach) only ever looked right on the one rig it
        // was tuned against. On a very differently-scaled model it could end up wildly
        // too big or too small for that model's actual bone spacing, and an oversized/
        // undersized capsule fighting a mass that doesn't match it is exactly what was
        // flinging custom characters' ragdolls into the air on death. Hips passes in
        // radiusReferenceLength (see ReferenceLength) instead of using its own length,
        // since it doesn't have one well-defined chain length to measure.
        float referenceLength = radiusReferenceLength > 0f ? radiusReferenceLength : length;
        float radius = Mathf.Max(0.02f, referenceLength * radiusRatio);
        cap.radius = radius;

        if (end == null || length < 0.01f)
        {
            cap.height = radius * 2f;
            cap.direction = 0;
            return cap;
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
        return cap;
    }
}