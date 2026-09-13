using UnityEngine;

/// <summary>First-person body helper: collapses the head bone so the player's own
/// head/mask never blocks the camera, while arms and torso stay visible.</summary>
public class HeadHider : MonoBehaviour
{
    Transform head;
    Vector3 origScale = Vector3.one;

    void Start()
    {
        var anim = GetComponent<Animator>();
        if (anim == null || !anim.isHuman) { enabled = false; return; }
        head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) { enabled = false; return; }
        origScale = head.localScale;
    }

    // every frame, after animation/pose writes — some of them restore bone scale
    void LateUpdate()
    {
        head.localScale = origScale * 0.001f;
    }

    // a zero-scale head bone wrecks ragdoll physics (degenerate collider + joint
    // frames), so whoever disables this on death gets the real head back
    void OnDisable()
    {
        if (head != null) head.localScale = origScale;
    }
}
