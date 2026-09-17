using UnityEngine;

/// <summary>
/// Keeps the player's own head out of the camera without deleting it from the world.
///
/// The old version shrank the head bone to nothing every LateUpdate, which works for the
/// first-person view but also removes the head from the shadow, from the death animation,
/// and from anything else that sees the body - hence "my shadow has no head".
///
/// castFullBodyShadow fixes that with a shadow proxy: a second, invisible copy of the
/// body mesh (shadowCastingMode = ShadowsOnly) driven by its own duplicate skeleton, which
/// is a straight copy of the real one every frame EXCEPT that its head is left at full
/// size. So the camera sees a headless body it never looks at, and the light sees a
/// complete one. It costs one extra skinned mesh and a bone-copy loop, which for one
/// character is nothing.
///
/// The proxy can't be shared with the real renderer, because bone scale lives on the
/// bone transform - one skeleton can't be both shrunk and not shrunk. That's why the
/// skeleton is duplicated rather than the renderer alone.
///
/// The head is also restored automatically while the body is dead, so the death animation
/// and the third-person death camera show a complete character.
///
/// SETUP: nothing new - PlayerSetup already calls Activate(). Turn castFullBodyShadow off
/// if you'd rather not pay for the proxy.
/// </summary>
public class HeadHider : MonoBehaviour
{
    [Tooltip("Keep a complete body for shadows (and anything else that isn't the first-person camera) by driving an invisible shadow-only duplicate whose head is intact.")]
    public bool castFullBodyShadow = true;
    [Tooltip("Restore the real head when the character dies, so the death animation and death camera aren't looking at a headless body.")]
    public bool restoreHeadOnDeath = true;

    Transform head;
    Vector3 origScale = Vector3.one;
    bool active;
    bool suppressed;   // head temporarily restored (death)

    Transform proxyRoot;
    Transform[] realBones, proxyBones;

    void Start()
    {
        var anim = GetComponent<Animator>();
        if (anim == null || !anim.isHuman) { enabled = false; return; }
        head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) { enabled = false; return; }
        origScale = head.localScale;

        var health = GetComponentInParent<PlayerHealth>();
        if (health != null && restoreHeadOnDeath)
            health.onHealthChanged += f => { if (f <= 0f) suppressed = true; };
    }

    /// <summary>Call once on whichever instance is actually the local player's body.</summary>
    public void Activate()
    {
        active = true;
        if (castFullBodyShadow && proxyRoot == null) BuildShadowProxy();
    }

    void BuildShadowProxy()
    {
        var source = GetComponentInChildren<SkinnedMeshRenderer>();
        if (source == null || source.rootBone == null) return;

        // Duplicate the skeleton. Renderers and behaviours are stripped - it exists only
        // to be posed.
        Transform skeletonRoot = source.rootBone;
        var clone = Instantiate(skeletonRoot.gameObject, skeletonRoot.parent);
        clone.name = "ShadowSkeleton";
        foreach (var comp in clone.GetComponentsInChildren<Component>(true))
        {
            if (comp is Transform) continue;
            Destroy(comp);
        }
        proxyRoot = clone.transform;

        // Map real bone -> proxy bone by hierarchy path, so the copy survives duplicate
        // bone names on different limbs.
        realBones = source.bones;
        proxyBones = new Transform[realBones.Length];
        for (int i = 0; i < realBones.Length; i++)
            proxyBones[i] = FindMatching(skeletonRoot, proxyRoot, realBones[i]);

        var proxyGo = new GameObject("ShadowProxyMesh");
        proxyGo.transform.SetParent(source.transform.parent, false);
        var proxy = proxyGo.AddComponent<SkinnedMeshRenderer>();
        proxy.sharedMesh = source.sharedMesh;
        proxy.sharedMaterials = source.sharedMaterials;
        proxy.bones = proxyBones;
        proxy.rootBone = FindMatching(skeletonRoot, proxyRoot, source.rootBone);
        proxy.localBounds = source.localBounds;
        proxy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        proxy.updateWhenOffscreen = true;

        // The visible body no longer needs to cast - the proxy does it, with a head.
        source.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    static Transform FindMatching(Transform realRoot, Transform proxyRootTransform, Transform realBone)
    {
        if (realBone == null) return null;

        // Walk up to build the path from the skeleton root, then walk the same indices
        // down the copy.
        var indices = new System.Collections.Generic.List<int>();
        Transform t = realBone;
        while (t != null && t != realRoot)
        {
            indices.Add(t.GetSiblingIndex());
            t = t.parent;
        }
        if (t == null) return null; // not under the skeleton root

        Transform p = proxyRootTransform;
        for (int i = indices.Count - 1; i >= 0; i--)
        {
            if (indices[i] >= p.childCount) return null;
            p = p.GetChild(indices[i]);
        }
        return p;
    }

    // every frame, after animation/pose writes - some of them restore bone scale
    void LateUpdate()
    {
        if (!active || head == null) return;

        // Pose the shadow copy from the real skeleton BEFORE the head is shrunk, so the
        // shadow keeps its head.
        if (proxyBones != null)
        {
            for (int i = 0; i < realBones.Length; i++)
            {
                if (realBones[i] == null || proxyBones[i] == null) continue;
                proxyBones[i].localPosition = realBones[i].localPosition;
                proxyBones[i].localRotation = realBones[i].localRotation;
                proxyBones[i].localScale = realBones[i].localScale;
            }
            // ...except the head, which is left alone.
            Transform proxyHead = FindProxyHead();
            if (proxyHead != null) proxyHead.localScale = origScale;
        }

        head.localScale = suppressed ? origScale : origScale * 0.001f;
    }

    Transform cachedProxyHead;
    Transform FindProxyHead()
    {
        if (cachedProxyHead != null) return cachedProxyHead;
        if (realBones == null) return null;
        for (int i = 0; i < realBones.Length; i++)
            if (realBones[i] == head) { cachedProxyHead = proxyBones[i]; break; }
        return cachedProxyHead;
    }

    // a zero-scale head bone wrecks ragdoll physics (degenerate collider + joint
    // frames), so whoever disables this on death gets the real head back
    void OnDisable()
    {
        if (head != null) head.localScale = origScale;
    }
}