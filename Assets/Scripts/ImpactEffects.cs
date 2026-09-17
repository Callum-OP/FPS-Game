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

        var decal = Place(prefab, point, normal, decalSize);
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
        Vector3 upHint = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.99f
            ? Vector3.forward : Vector3.up;

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
}