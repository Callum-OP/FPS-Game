using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Grenades lying on the floor. Stand in the trigger and press the pickup key (1, same as weapons) to add them to the
/// grenade count (GrenadeController, capped at its maxGrenades). If you can only carry some of them the rest stay put.
///
/// The pickup prefab needs: this script, a Rigidbody, a solid BoxCollider (so it lies on the ground) and a BoxCollider
/// with Is Trigger on (the pickup zone) - GrenadePickup.prefab is set up that way. The model is made at run time from
/// visualPrefab (the frag grenade) with its physics and fuse stripped, so the pickup always matches the grenade.
/// </summary>
public class GrenadePickup : MonoBehaviour
{
    [Tooltip("How many grenades this pickup gives.")]
    public int amount = 1;
    [Tooltip("The grenade model to show (the frag grenade prefab). Its Explosive, Rigidbody and colliders are removed on the copy.")]
    public GameObject visualPrefab;
    [Tooltip("Seconds after it appears before it can be picked up (stops a just-dropped pickup being grabbed by the same key press).")]
    public float pickupDelay = 0.4f;
    [Tooltip("Slow spin so it reads as a pickup. Degrees per second, 0 = off.")]
    public float spinSpeed = 0f;

    float spawnTime;
    bool playerInRange;
    PlayerSetup playerSetup;
    GrenadeController grenades;
    InputAction pickupAction;

    void Awake()
    {
        pickupAction = new InputAction("GrenadePickup", binding: PlayerInputMap.Pickup);
        pickupAction.Enable();
        spawnTime = Time.unscaledTime;
        BuildVisual();
    }

    void BuildVisual()
    {
        if (visualPrefab == null) return;
        GameObject go = Instantiate(visualPrefab, transform);
        go.name = "Visual";
        go.transform.localPosition = Vector3.zero;

        // Just a model: nothing in it may tick, collide or explode. (Explosive only starts its fuse in Start, which a
        // disabled behaviour never reaches.)
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; Destroy(rb); }
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) { c.enabled = false; Destroy(c); }
        foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        foreach (var au in go.GetComponentsInChildren<AudioSource>(true)) au.enabled = false;
    }

    void Update()
    {
        if (spinSpeed != 0f) transform.Rotate(0f, spinSpeed * Time.deltaTime, 0f, Space.World);

        if (Time.unscaledTime - spawnTime < pickupDelay) return;
        if (!playerInRange || grenades == null || !grenades.CanCarryMore) return;

        // Same courtesy the weapon pickups give: a pickup under your feet wins the key over swapping with an ally.
        WeaponPickup.AnyPlayerInRangeThisFrame = true;

        if (pickupAction.WasPressedThisFrame()) Pickup();
    }

    void Pickup()
    {
        int taken = grenades.AddGrenades(amount);
        if (taken <= 0) return;
        amount -= taken;
        if (amount <= 0) Destroy(gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        playerSetup = other.GetComponent<PlayerSetup>();
        grenades = playerSetup != null ? playerSetup.GetComponent<GrenadeController>() : other.GetComponent<GrenadeController>();
        playerInRange = grenades != null;
    }

    void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        playerInRange = false;
        playerSetup = null;
        grenades = null;
    }

    void OnDestroy()
    {
        if (pickupAction != null) pickupAction.Disable();
    }
}