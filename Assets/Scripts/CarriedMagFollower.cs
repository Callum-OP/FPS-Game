using UnityEngine;

/// <summary>
/// Keeps the mag/shell that's "in the hand" during a reload exactly where the mag hand
/// point is - every frame, in world space, after everything else has finished posing the
/// arm.
///
/// Why this exists instead of just parenting the prop to the mag hand point:
///  - Every mag prefab has its own pivot (some at the base, some at the top, some in the
///    middle) and its own child offsets, so "position 0" meant a different spot on the
///    mesh for each one, and no single offset could suit them all. This measures the
///    prop's actual visible mesh once and pins ITS CENTRE to the hand point instead.
///  - A parented prop also inherits whatever scale/rotation quirks the hand bone and the
///    hand-placed point carry, and it's positioned before the strict hand lock and the
///    other post-animation scripts have moved the arm. Driving the world pose in a late
///    LateUpdate (after WeaponHandIK's, order 300) means it always matches the hand you
///    actually see.
///
/// Added at runtime by WeaponReloadHandler.SpawnCarriedMag - nothing to set up.
/// </summary>
[DefaultExecutionOrder(400)]
public class CarriedMagFollower : MonoBehaviour
{
    Transform hand;
    Quaternion rotationOffset = Quaternion.identity;
    Vector3 nudge;
    Vector3 centerLocal; // centre of the visible meshes, in this object's local space

    public void Init(Transform handPoint, Quaternion rotOffset, Vector3 handSpaceNudge)
    {
        hand = handPoint;
        rotationOffset = rotOffset;
        nudge = handSpaceNudge;

        // The prop used to inherit the hand point's scale by being parented to it, and the
        // size you tuned it at includes that - keep the same size.
        transform.localScale = Vector3.Scale(transform.localScale, hand.lossyScale);

        // Measure the mesh centre with the prop in its final orientation.
        transform.rotation = hand.rotation * rotationOffset;
        transform.position = hand.position;

        bool any = false;
        Bounds bounds = new Bounds(transform.position, Vector3.zero);
        foreach (var r in GetComponentsInChildren<Renderer>(false))
        {
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }
        centerLocal = any ? transform.InverseTransformPoint(bounds.center) : Vector3.zero;

        Apply();
    }

    void LateUpdate() => Apply();

    void Apply()
    {
        if (hand == null) { Destroy(gameObject); return; }
        transform.rotation = hand.rotation * rotationOffset;
        Vector3 target = hand.TransformPoint(nudge);
        transform.position = target - transform.TransformVector(centerLocal);
    }
}