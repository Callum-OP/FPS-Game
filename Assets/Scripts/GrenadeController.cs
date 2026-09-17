using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Grenades you actually hold. Press 4 to take one out - the weapon goes onto its
/// holster on your back/hip - then left click to throw it. Press 4 again to put it away
/// without throwing.
///
/// The Basic Shooter Pack's "toss grenade" clip drives the throw on the masked UpperBody
/// layer, and the grenade leaves the hand partway through it (releaseAtFraction) rather
/// than on the button press, so it looks thrown rather than spawned. No pack has a
/// pin-pull clip, so the pin is folded into the wind-up: the fuse starts when the throw
/// animation does, which is what "pulled the pin, then threw it" amounts to in practice.
/// Hold the button and the fuse keeps burning - cookedThrow lets you cook one, or kill
/// yourself doing it.
///
/// SETUP: on the Player root. grenadePrefab wants an Explosive with useTimer on.
/// </summary>
public class GrenadeController : MonoBehaviour
{
    [Header("Grenade")]
    public GameObject grenadePrefab;
    public Transform throwPoint;
    public float throwForce = 15f;
    public float throwUpward = 5f;
    public int maxGrenades = 3;

    [Header("Holding")]
    [Tooltip("Optional grenade mesh shown in the hand while it's out. The Easy Weapons Grenade prefab works - it gets stripped to a prop.")]
    public GameObject heldGrenadeVisual;
    [Tooltip("Where the held grenade sits, relative to the camera.")]
    public Vector3 heldOffset = new Vector3(0.18f, -0.18f, 0.35f);
    [Tooltip("How far through the throw animation the grenade leaves the hand.")]
    [Range(0f, 1f)] public float releaseAtFraction = 0.45f;
    [Tooltip("Length of the throw animation - the release is timed against this.")]
    public float throwAnimationLength = 1.1f;
    [Tooltip("Let the fuse run while the throw button is held, so grenades can be cooked.")]
    public bool cookedThrow = true;
    [Tooltip("Keep one in hand after throwing, if any are left.")]
    public bool stayEquippedAfterThrow = true;

    [Header("References")]
    public WeaponInventory inventory;
    public CharacterAnimationDriver animationDriver;

    int currentGrenades;
    bool holding;
    bool throwing;
    GameObject heldVisual;
    InputAction equipAction, throwAction;

    void Awake()
    {
        equipAction = PlayerInputMap.Make("Grenade", PlayerInputMap.Grenade);
        throwAction = PlayerInputMap.Make("ThrowGrenade", PlayerInputMap.Fire);
        currentGrenades = maxGrenades;
    }

    void Start()
    {
        if (inventory == null) inventory = GetComponent<WeaponInventory>();
        if (animationDriver == null) animationDriver = GetComponentInChildren<CharacterAnimationDriver>();
        if (throwPoint == null && Camera.main != null) throwPoint = Camera.main.transform;
    }

    void Update()
    {
        if (throwing) return;

        if (equipAction.WasPressedThisFrame())
        {
            if (holding) PutAway();
            else if (currentGrenades > 0) TakeOut();
        }

        if (holding && throwAction.WasPressedThisFrame())
            StartCoroutine(ThrowRoutine());
    }

    void TakeOut()
    {
        holding = true;

        // Both hands are needed, so the gun goes onto the body rather than just vanishing.
        inventory?.HolsterAll();

        if (heldGrenadeVisual != null && throwPoint != null)
        {
            heldVisual = Instantiate(heldGrenadeVisual, throwPoint);
            heldVisual.transform.localPosition = heldOffset;
            heldVisual.transform.localRotation = Quaternion.identity;
            StripProp(heldVisual);
        }
    }

    void PutAway()
    {
        holding = false;
        if (heldVisual != null) Destroy(heldVisual);
        inventory?.RestoreActive();
    }

    IEnumerator ThrowRoutine()
    {
        if (grenadePrefab == null || throwPoint == null || currentGrenades <= 0) yield break;

        throwing = true;
        currentGrenades--;
        animationDriver?.PlayGrenade();

        float release = throwAnimationLength * releaseAtFraction;
        float elapsed = 0f;

        // Wind-up. As far as the fuse is concerned the pin is already out, so holding the
        // button here cooks the grenade.
        while (elapsed < release
               || (cookedThrow && throwAction.ReadValue<float>() > 0.5f && elapsed < throwAnimationLength))
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (heldVisual != null) Destroy(heldVisual);

        GameObject grenade = Instantiate(grenadePrefab,
            throwPoint.position + throwPoint.forward * 0.4f, throwPoint.rotation);

        Rigidbody rb = grenade.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = throwPoint.forward * throwForce + Vector3.up * throwUpward;
            rb.AddTorque(Random.insideUnitSphere * 5f, ForceMode.Impulse);
        }

        // Time spent cooking comes off the fuse.
        var explosive = grenade.GetComponent<Explosive>();
        if (explosive != null && cookedThrow)
            explosive.fuseTime = Mathf.Max(0.35f, explosive.fuseTime - elapsed);

        // Let the rest of the throw play out before the gun comes back up.
        yield return new WaitForSeconds(Mathf.Max(0f, throwAnimationLength - elapsed));

        throwing = false;
        holding = false;

        if (stayEquippedAfterThrow && currentGrenades > 0) TakeOut();
        else inventory?.RestoreActive();
    }

    // It's a prop in the hand: no physics, no explosive, no colliders.
    void StripProp(GameObject prop)
    {
        foreach (var mb in prop.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
        foreach (var c in prop.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        foreach (var r in prop.GetComponentsInChildren<Rigidbody>(true)) r.isKinematic = true;
    }

    public int GetCurrentGrenades() => currentGrenades;
    public int GetMaxGrenades()     => maxGrenades;
    public bool IsHoldingGrenade()  => holding;

    void OnDestroy()
    {
        equipAction.Disable();
        throwAction.Disable();
    }
}