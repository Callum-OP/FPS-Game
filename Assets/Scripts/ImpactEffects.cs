using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bullet holes, blood, and scorch marks. One of these in the scene; everything else
/// calls the static helpers, so nothing has to hold a reference to it.
///
/// Decals are pooled with a hard cap: past maxDecals the oldest is recycled rather than a
/// new one spawned. A shooter left running will otherwise accumulate thousands of little
/// quads and quietly eat the frame budget.
///
/// SETUP:
///  - Drop this on an empty GameObject in the scene (call it "ImpactEffects").
///  - Fill wallDecals with the Easy Weapons bullet hole prefabs
///    (Easy Weapons/Prefabs/Bullet Hole Decals/bullet_hole1..13). Several is better -
///    one repeated hole texture is very visible.
///  - fleshDecal: reuse one of the same bullet hole prefabs and tint it with
///    bloodColor below. There's no wound decal in the pack, and a dark red hole reads
///    perfectly well at the distances you actually see bodies from.
///  - scorchDecal: another bullet hole prefab works, scaled up by the explosion.
/// Wounds on characters can be turned off entirely with showCharacterWounds.
/// </summary>
public class ImpactEffects : MonoBehaviour
{
    public static ImpactEffects Instance { get; private set; }

    [Header("Decal Prefabs")]
    [Tooltip("Bullet holes for walls/scenery. Easy Weapons' bullet hole prefabs drop straight in.")]
    public GameObject[] wallDecals;
    [Tooltip("Used on characters. Tinted with bloodColor.")]
    public GameObject fleshDecal;
    [Tooltip("Left on the ground and nearby surfaces by explosions.")]
    public GameObject scorchDecal;

    [Header("Toggles")]
    [Tooltip("Bullet holes on walls and scenery.")]
    public bool showWallHoles = true;
    [Tooltip("Wounds on enemies and the player. Off leaves bodies clean.")]
    public bool showCharacterWounds = true;
    public Color bloodColor = new Color(0.45f, 0.02f, 0.02f, 1f);

    [Header("Appearance")]
    public float decalSize = 0.12f;
    public float woundSize = 0.08f;
    [Tooltip("How far off the surface the decal sits. Too small and it z-fights, too large and it floats.")]
    public float surfaceOffset = 0.012f;
    [Tooltip("Seconds before a decal fades out. 0 keeps it until it's recycled.")]
    public float lifetime = 45f;

    [Header("Surface Conforming")]
    [Tooltip("Build wall decals as a small fan mesh that samples the surface at several points around the hit and follows it, instead of a single flat floating plane. This is what actually makes them read as 'projected onto' a corner or an uneven surface rather than hovering over it - a real projector/decal-shader system would do better still, but this needs no render-pipeline features and works with the existing prefabs' materials.")]
    public bool conformToSurface = true;
    [Tooltip("How many points around the rim are sampled - higher wraps corners more smoothly but costs more per decal.")]
    [Range(4, 16)] public int conformSegments = 10;
    [Tooltip("How far past the decal's own radius to search for the real surface at each sample point - covers a corner or ledge just past the edge of the hole. Too large starts picking up unrelated geometry.")]
    public float conformSearchMargin = 0.15f;
    [Tooltip("What counts as surface to conform to.")]
    public LayerMask conformSurfaceMask = ~0;

    [Header("Budget")]
    [Tooltip("Hard cap on decals in the world. The oldest is recycled past this.")]
    public int maxDecals = 250;

    readonly Queue<GameObject> live = new Queue<GameObject>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// <summary>Called by Projectile on every impact. hitTransform decides whether this is
    /// scenery or a body.</summary>
    public static void SpawnImpact(Vector3 point, Vector3 normal, Transform hitTransform)
    {
        if (Instance == null) return;

        bool isCharacter = hitTransform != null
            && (hitTransform.GetComponentInParent<Health>() != null
                || hitTransform.GetComponentInParent<PlayerHealth>() != null);

        if (isCharacter) Instance.SpawnWound(point, normal, hitTransform);
        else Instance.SpawnWallHole(point, normal, hitTransform);
    }

    void SpawnWallHole(Vector3 point, Vector3 normal, Transform surface)
    {
        if (!showWallHoles || wallDecals == null || wallDecals.Length == 0) return;
        var prefab = wallDecals[Random.Range(0, wallDecals.Length)];
        if (prefab == null) return;

        // Conforming needs a static-ish surface to sample around; a moving/animated one
        // (a ragdoll bone, say) would need re-sampling every frame to stay glued to it,
        // which wounds don't do either - so this only applies to wall/scenery holes,
        // which is also exactly where the "floating flat plane" look is most obvious.
        var decal = conformToSurface
            ? PlaceConforming(prefab, point, normal)
            : Place(prefab, point, normal, decalSize);

        // Parented to whatever was hit, so holes in a moving object travel with it.
        if (decal != null && surface != null) decal.transform.SetParent(surface, true);
    }

    void SpawnWound(Vector3 point, Vector3 normal, Transform bone)
    {
        if (!showCharacterWounds || fleshDecal == null) return;

        var decal = Place(fleshDecal, point, normal, woundSize);
        if (decal == null) return;

        // Stuck to the bone it hit, so it moves with the body and the ragdoll.
        decal.transform.SetParent(bone, true);

        foreach (var r in decal.GetComponentsInChildren<Renderer>())
        {
            var mat = r.material; // instance, not shared - don't tint every decal in the scene
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", bloodColor);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", bloodColor);
        }
    }

    /// <summary>Scorch marks around an explosion - a few rays out from the centre, marking
    /// whatever they hit.</summary>
    public static void SpawnScorch(Vector3 centre, float radius)
    {
        var inst = Instance;
        if (inst == null || inst.scorchDecal == null) return;

        for (int i = 0; i < 6; i++)
        {
            Vector3 dir = i == 0 ? Vector3.down : Random.onUnitSphere;
            if (!Physics.Raycast(centre, dir, out RaycastHit hit, radius * 0.6f, ~0, QueryTriggerInteraction.Ignore))
                continue;
            if (hit.collider.GetComponentInParent<Health>() != null) continue;

            inst.Place(inst.scorchDecal, hit.point, hit.normal, inst.decalSize * Random.Range(4f, 8f));
        }
    }

    GameObject Place(GameObject prefab, Vector3 point, Vector3 normal, float size)
    {
        // Recycle before spawning, so the cap is a cap rather than a target.
        while (live.Count >= Mathf.Max(1, maxDecals))
        {
            var old = live.Dequeue();
            if (old != null) Destroy(old);
        }

        // Quaternion.LookRotation degenerates when the forward direction (-normal) is
        // parallel to the up hint - which is exactly a floor or ceiling hit if the hint
        // is always Vector3.up, producing an undefined/garbage rotation there. Falling
        // back to a different hint whenever the two are nearly parallel fixes decals on
        // floors and ceilings; ordinary walls still use Vector3.up as before.
        Vector3 upHint = UpHintFor(normal);

        // This mesh's local axes need an extra 90-degree twist on top of a plain
        // LookRotation to sit flush and upright rather than sideways - confirmed
        // against the actual BulletHole prefab. If it ever looks like it's facing into
        // the wall instead of out from it, swap this to Quaternion.Euler(0f, 0f, 90f)
        // instead (roll around the decal's own outward axis rather than tip around its
        // side axis) - one of the two is right for this mesh and it's a one-line swap.
        Quaternion rot = Quaternion.LookRotation(-normal, upHint) * Quaternion.Euler(90f, 0f, 0f);

        var decal = Instantiate(prefab, point + normal * surfaceOffset, rot);
        // Random spin is still around the decal's own forward/normal axis (Z), which
        // the fixed 90-degree correction above doesn't change - so hole textures still
        // get per-decal variety without reintroducing the sideways problem.
        decal.transform.Rotate(Vector3.forward, Random.Range(0f, 360f), Space.Self);
        decal.transform.localScale = Vector3.one * size;

        // The Easy Weapons hole prefabs carry their own timed destroyer/pool scripts;
        // this owns decal lifetime instead, so the budget above is the only thing
        // deciding how many exist.
        foreach (var mb in decal.GetComponentsInChildren<MonoBehaviour>())
            mb.enabled = false;

        // Some of the flat quad decals in that pack ship with a Collider on them (for
        // their own click/pool logic). Left on, a decal spawned at the exact point a
        // bullet just hit becomes a second solid surface appearing mid-collision, which
        // is what was making bullets "ricochet"/keep travelling instead of stopping - a
        // fast Rigidbody's collision response for this step can still be resolving
        // against the new collider before Destroy(gameObject) on the bullet actually
        // takes effect at end of frame. A decal has no business being solid, so this
        // strips every collider on it unconditionally rather than trying to guess which
        // prefabs need it.
        foreach (var col in decal.GetComponentsInChildren<Collider>())
            col.enabled = false;

        live.Enqueue(decal);
        if (lifetime > 0f) Destroy(decal, lifetime);
        return decal;
    }

    // Robust up-hint for LookRotation, shared by Place and PlaceConforming - see the
    // comment in Place for why this is needed (LookRotation degenerates on floors/ceilings).
    static Vector3 UpHintFor(Vector3 normal) =>
        Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;

    /// <summary>Builds the wall decal as a small fan mesh instead of a single flat quad:
    /// a centre vertex at the impact point plus a ring of rim vertices, each independently
    /// raycast back onto the surface a little past the decal's own radius. On an ordinary
    /// flat wall every rim sample lands on the same plane and this looks identical to a
    /// flat quad; on a corner, a doorframe edge, or a ledge, whichever rim samples cross
    /// onto the other surface follow it there instead of continuing to float across empty
    /// space - which is what actually reads as "projected onto the object" rather than a
    /// plane hovering in front of it. Reuses the source prefab's material so the existing
    /// hole textures/wallDecals array need no changes.</summary>
    GameObject PlaceConforming(GameObject prefab, Vector3 point, Vector3 normal)
    {
        while (live.Count >= Mathf.Max(1, maxDecals))
        {
            var old = live.Dequeue();
            if (old != null) Destroy(old);
        }

        var sourceRenderer = prefab.GetComponentInChildren<Renderer>();
        Material material = sourceRenderer != null ? sourceRenderer.sharedMaterial : null;
        if (material == null) return Place(prefab, point, normal, decalSize); // nothing to build a mesh with - fall back

        Vector3 upHint = UpHintFor(normal);
        Vector3 tangent = Vector3.Cross(upHint, normal).normalized;
        if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.Cross(Vector3.right, normal).normalized;
        Vector3 bitangent = Vector3.Cross(normal, tangent);

        float radius = decalSize * 0.5f;
        float rotationOffset = Random.Range(0f, Mathf.PI * 2f); // per-decal variety, same idea as the old random spin

        var vertices = new Vector3[conformSegments + 1];
        var normals = new Vector3[conformSegments + 1];
        var uvs = new Vector2[conformSegments + 1];
        // Double-sided (both winding orders) rather than betting on getting the winding
        // direction exactly right for every possible surface orientation - the small
        // amount of extra overdraw on a handful of small decals is a much better trade
        // than a decal that's invisible from the wrong side.
        var triangles = new int[conformSegments * 6];

        Vector3 centerWorld = point + normal * surfaceOffset;
        uvs[0] = new Vector2(0.5f, 0.5f);

        for (int i = 0; i < conformSegments; i++)
        {
            float angle = rotationOffset + (i / (float)conformSegments) * Mathf.PI * 2f;
            Vector3 dir = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
            Vector3 flatRim = point + dir * radius;

            // Search from a little in front of the flat rim point, back along -normal,
            // far enough to catch a surface just past the decal's own edge (a corner
            // bending away) without reaching so far it picks up unrelated geometry.
            Vector3 searchStart = flatRim + normal * (radius + conformSearchMargin);
            Vector3 rimWorld;
            Vector3 rimNormal;
            if (Physics.Raycast(searchStart, -normal, out RaycastHit hit,
                    radius + conformSearchMargin * 2f, conformSurfaceMask, QueryTriggerInteraction.Ignore))
            {
                rimWorld = hit.point + hit.normal * surfaceOffset;
                rimNormal = hit.normal;
            }
            else
            {
                // No surface found nearby (an edge of the geometry, or a gap) - lie flat
                // in the original plane rather than guessing.
                rimWorld = flatRim + normal * surfaceOffset;
                rimNormal = normal;
            }

            vertices[i + 1] = rimWorld;
            normals[i + 1] = rimNormal;
            uvs[i + 1] = new Vector2(0.5f + 0.5f * Mathf.Cos(angle), 0.5f + 0.5f * Mathf.Sin(angle));

            int next = (i + 1) % conformSegments;
            triangles[i * 6 + 0] = 0;
            triangles[i * 6 + 1] = i + 1;
            triangles[i * 6 + 2] = next + 1;
            // Reverse winding for the back face - see the comment above the array.
            triangles[i * 6 + 3] = 0;
            triangles[i * 6 + 4] = next + 1;
            triangles[i * 6 + 5] = i + 1;
        }

        var go = new GameObject("BulletHoleDecal");
        go.transform.position = centerWorld;
        go.transform.rotation = Quaternion.LookRotation(-normal, upHint);

        // Vertices were built in world space above (raycasting needs world space); convert
        // to the new GameObject's local space now that its transform is set.
        vertices[0] = Vector3.zero;
        normals[0] = go.transform.InverseTransformDirection(normal);
        for (int i = 1; i < vertices.Length; i++)
        {
            Vector3 worldPos = vertices[i];
            vertices[i] = go.transform.InverseTransformPoint(worldPos);
            normals[i] = go.transform.InverseTransformDirection(normals[i]);
        }

        var mesh = new Mesh { name = "BulletHoleDecalMesh" };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        live.Enqueue(go);
        if (lifetime > 0f) Destroy(go, lifetime);
        return go;
    }
}