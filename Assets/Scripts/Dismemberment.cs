using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Takes a ragdoll apart when an explosion kills it at close range.
///
/// A skinned mesh can't actually be cut at runtime without a lot of machinery, so this
/// does what most games do for this: the severed limb's joint is destroyed so the bone
/// chain flies free, and the limb's bones are shrunk to nothing at the sever point while
/// a detached stand-in mesh (severedLimbPrefab, or just the collider's shape) is thrown
/// clear. From any normal viewing distance, with the blast effect on top, it reads
/// correctly - which is the whole goal here, not anatomical accuracy.
///
/// If severedLimbPrefab is left empty it still works: the limb simply separates and is
/// hidden, which looks like it was blown off rather than left as a stump. Set
/// showStumpGeometry false if the shrinking bones look odd on your rig.
///
/// SETUP: add it next to the Animator on the enemy/player body prefab. Ragdoll calls it.
/// </summary>
public class Dismemberment : MonoBehaviour
{
    [Header("What can come off")]
    public bool allowArms = true;
    public bool allowLegs = true;
    [Tooltip("The head coming off is by far the most noticeable - leave it off if it reads as too much.")]
    public bool allowHead = false;

    [Header("How much")]
    [Tooltip("Minimum limbs removed when a blast is close enough to dismember.")]
    public int minLimbs = 1;
    [Tooltip("Maximum limbs removed.")]
    public int maxLimbs = 2;
    [Tooltip("Limbs nearer the blast are more likely to be the ones that go.")]
    public bool preferNearestLimbs = true;

    [Header("Appearance")]
    [Tooltip("Optional stand-in mesh thrown clear where the limb was. Leave empty and the limb just disappears - still reads fine.")]
    public GameObject severedLimbPrefab;
    [Tooltip("Shrink the severed bones to nothing so no stretched geometry is left behind.")]
    public bool showStumpGeometry = true;
    [Tooltip("Optional blood burst at the sever point.")]
    public GameObject severEffect;
    public float severImpulse = 4f;

    Animator anim;
    bool alreadySevered;

    void Start()
    {
        anim = GetComponent<Animator>();
        if (anim == null || !anim.isHuman) enabled = false;
    }

    /// <summary>Called by Ragdoll when an explosion kills this body at close range.</summary>
    public void SeverRandomLimbs(Vector3 blastCentre)
    {
        if (alreadySevered || anim == null) return;
        alreadySevered = true;

        var candidates = new List<HumanBodyBones>();
        if (allowArms) { candidates.Add(HumanBodyBones.LeftLowerArm); candidates.Add(HumanBodyBones.RightLowerArm); }
        if (allowLegs) { candidates.Add(HumanBodyBones.LeftLowerLeg); candidates.Add(HumanBodyBones.RightLowerLeg); }
        if (allowHead) candidates.Add(HumanBodyBones.Head);
        if (candidates.Count == 0) return;

        if (preferNearestLimbs)
        {
            candidates.Sort((a, b) =>
            {
                Transform ta = anim.GetBoneTransform(a), tb = anim.GetBoneTransform(b);
                float da = ta != null ? Vector3.Distance(ta.position, blastCentre) : float.MaxValue;
                float db = tb != null ? Vector3.Distance(tb.position, blastCentre) : float.MaxValue;
                return da.CompareTo(db);
            });
        }
        else
        {
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
        }

        int count = Mathf.Clamp(Random.Range(minLimbs, maxLimbs + 1), 0, candidates.Count);
        for (int i = 0; i < count; i++)
            Sever(candidates[i], blastCentre);
    }

    /// <summary>Detaches one limb at the given bone.</summary>
    public void Sever(HumanBodyBones bone, Vector3 blastCentre)
    {
        Transform t = anim.GetBoneTransform(bone);
        if (t == null) return;

        // Breaking the joint is what actually separates the limb from the ragdoll -
        // everything else here is dressing.
        var joint = t.GetComponent<Joint>();
        if (joint != null) Destroy(joint);

        var rb = t.GetComponent<Rigidbody>();
        Vector3 away = (t.position - blastCentre).normalized;
        if (away.sqrMagnitude < 0.01f) away = Random.onUnitSphere;

        if (severEffect != null)
            Destroy(Instantiate(severEffect, t.position, Quaternion.LookRotation(away)), 4f);

        if (severedLimbPrefab != null)
        {
            var limb = Instantiate(severedLimbPrefab, t.position, t.rotation);
            var limbRb = limb.GetComponent<Rigidbody>();
            if (limbRb != null)
            {
                limbRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                limbRb.interpolation = RigidbodyInterpolation.Interpolate;
                limbRb.linearVelocity = away * severImpulse;
                limbRb.angularVelocity = Random.insideUnitSphere * 8f;
            }
            Destroy(limb, 30f);
        }

        if (showStumpGeometry)
        {
            // Collapse the severed chain rather than leaving it stretched across the
            // gap - the skinned mesh is one piece, so the vertices weighted to these
            // bones have to go somewhere.
            foreach (Transform child in t.GetComponentsInChildren<Transform>())
                child.localScale = Vector3.one * 0.001f;
        }
        else if (rb != null)
        {
            rb.linearVelocity = away * severImpulse;
        }
    }
}