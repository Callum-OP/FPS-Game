using UnityEngine;

/// <summary>First-person body helper: collapses the head bone so the player's own
/// head/mask never blocks the camera, while arms and torso stay visible.
/// Off by default - call Activate() (PlayerSetup does this automatically) so enemies
/// sharing the same rigged body prefab don't lose their heads too.</summary>
public class HeadHider : MonoBehaviour
{
    Transform head;
    Vector3 origScale = Vector3.one;
    bool active;

    void Start()
    {
        var anim = GetComponent<Animator>();
        if (anim == null || !anim.isHuman) { enabled = false; return; }
        head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) { enabled = false; return; }
        origScale = head.localScale;
    }

    /// <summary>Call once on whichever instance is actually the local player's body.</summary>
    public void Activate() { active = true; }

    // every frame, after animation/pose writes — some of them restore bone scale
    void LateUpdate()
    {
        if (!active || head == null) return;
        head.localScale = origScale * 0.001f;
    }

    // a zero-scale head bone wrecks ragdoll physics (degenerate collider + joint
    // frames), so whoever disables this on death gets the real head back
    void OnDisable()
    {
        if (head != null) head.localScale = origScale;
    }
}