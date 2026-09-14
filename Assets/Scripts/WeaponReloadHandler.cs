using UnityEngine;
using System.Collections;

/// <summary>
/// Optional per-weapon add-on that makes reloads look like something is actually
/// happening instead of the hands staying glued to the gun for the full reload clip.
///
/// Rather than relying on a baked left-hand-only Mixamo animation (none of the packs
/// have one that lines up with an arbitrary weapon's magwell), this drives the left
/// hand procedurally through WeaponHandIK's reload-override hook: it reaches for a
/// magazine point, a mag prop pops out and falls to the floor like the empty casings
/// do, a fresh mag prop appears "in hand", gets seated back into the weapon, and the
/// hand returns to its normal grip. The right hand gets a small lift offset for the
/// same window so it doesn't look frozen mid-reload while the left hand is off the gun.
///
/// Entirely optional: if this component isn't on a weapon (or magazinePrefab /
/// handReloadPoint are left empty) WeaponController's reload falls back to exactly
/// what it did before - just the animator trigger and a timed ammo refill. That's the
/// right call for the shotgun, which doesn't use a detachable mag.
///
/// SETUP (per magazine-fed weapon prefab):
///  1. Add this component next to WeaponController.
///  2. Add an empty child transform at the magwell - where the mag physically sits when
///     seated in the gun. Assign to magSeatPoint. The old mag also drops from here.
///  3. Add an empty child transform roughly where the left hand should reach to while
///     carrying a fresh mag (e.g. down and slightly back, near a hip/chest mag pouch
///     position translated into weapon-local space, or just below the magwell if you
///     don't need it to look like it's coming from a pouch). Orient it so its rotation
///     roughly matches LeftHandGrip's. Assign to handReloadPoint.
///  4. Assign magazinePrefab - a small mag mesh. Give it a Rigidbody + Collider so the
///     dropped copy tumbles on the floor; the carried/seated copies strip those off
///     automatically since they're purely cosmetic.
///  5. Tune the timing fields below (fractions of WeaponController.reloadTime) to match
///     however long your reload animation actually takes to look right.
/// </summary>
[RequireComponent(typeof(WeaponController))]
public class WeaponReloadHandler : MonoBehaviour
{
    [Header("Magazine Prop")]
    [Tooltip("Small mag mesh. Needs a Rigidbody + Collider for the dropped copy to fall like a casing. Leave empty to disable the mag-drop/pickup visuals entirely (e.g. shotgun).")]
    public GameObject magazinePrefab;
    [Tooltip("Where the mag sits once seated in the weapon - also where the empty one drops from.")]
    public Transform magSeatPoint;
    [Tooltip("Where the left hand reaches to while carrying the fresh mag before it's seated. Leave empty to skip the hand-repositioning entirely.")]
    public Transform handReloadPoint;

    [Header("Timing (fraction of WeaponController.reloadTime, 0-1)")]
    [Range(0f, 1f)] public float handMoveOutTime = 0.05f;  // left hand starts leaving the foregrip
    [Range(0f, 1f)] public float magDropTime = 0.15f;      // old mag pops out and falls
    [Range(0f, 1f)] public float magSeatTime = 0.75f;      // new mag clicks in
    [Range(0f, 1f)] public float handReturnTime = 0.9f;    // left hand returns to the foregrip

    [Header("Right Hand")]
    [Tooltip("Local offset (weapon space) the right hand lifts by while the left hand is off the gun, so the grip doesn't look frozen mid-reload.")]
    public Vector3 rightHandLiftOffset = new Vector3(0f, 0.03f, 0f);

    [Header("Dropped Mag Physics")]
    public float dropForce = 1.5f;
    public float dropTorque = 3f;
    public float destroyDelay = 5f;

    WeaponHandIK handIK;
    Transform rightOverrideAnchor;
    GameObject carriedMag;
    Coroutine running;

    void Awake()
    {
        // Small hidden marker that tracks the normal right-hand grip plus our lift
        // offset every frame, so it stays correct through sway/recoil/camera pitch.
        rightOverrideAnchor = new GameObject("ReloadRightHandAnchor").transform;
        rightOverrideAnchor.SetParent(transform, false);
    }

    /// <summary>Wired up by PlayerSetup whenever this weapon becomes active.</summary>
    public void SetHandIK(WeaponHandIK ik) => handIK = ik;

    /// <summary>Called by WeaponController at the start of its reload coroutine. reloadTime
    /// is passed in so all the timing fractions above scale with whatever this weapon uses.</summary>
    public void PlayReload(float reloadTime)
    {
        if (running != null) StopCoroutine(running);
        running = StartCoroutine(ReloadSequence(reloadTime));
    }

    IEnumerator ReloadSequence(float reloadTime)
    {
        WeaponController weapon = GetComponent<WeaponController>();
        Transform baseRight = weapon != null ? weapon.rightHandGrip : null;
        bool canReposition = handIK != null && handReloadPoint != null;

        float t = 0f;
        bool dropped = false, seated = false, returned = false;

        while (t < reloadTime)
        {
            t += Time.deltaTime;
            float frac = reloadTime > 0f ? t / reloadTime : 1f;

            if (canReposition && frac >= handMoveOutTime && !returned)
            {
                if (baseRight != null)
                {
                    rightOverrideAnchor.rotation = baseRight.rotation;
                    rightOverrideAnchor.position = baseRight.position + baseRight.TransformDirection(rightHandLiftOffset);
                }

                handIK.SetReloadOverride(rightOverrideAnchor, handReloadPoint);
            }

            if (frac >= magDropTime && !dropped)
            {
                DropOldMag();
                SpawnCarriedMag();
                dropped = true;
            }

            if (frac >= magSeatTime && !seated)
            {
                SeatCarriedMag();
                seated = true;
            }

            if (canReposition && frac >= handReturnTime && !returned)
            {
                handIK.SetReloadOverride(null, null);
                returned = true;
            }

            yield return null;
        }

        if (canReposition && !returned)
            handIK.SetReloadOverride(null, null);

        CleanupCarriedMag();
        running = null;
    }

    void DropOldMag()
    {
        if (magazinePrefab == null || magSeatPoint == null) return;

        GameObject dropped = Instantiate(magazinePrefab, magSeatPoint.position, magSeatPoint.rotation);
        Rigidbody rb = dropped.GetComponent<Rigidbody>();
        if (rb != null)
        {
            Vector3 dir = -magSeatPoint.up + new Vector3(Random.Range(-0.2f, 0.2f), 0f, Random.Range(-0.2f, 0.2f));
            rb.AddForce(dir.normalized * dropForce, ForceMode.Impulse);
            rb.AddTorque(Random.insideUnitSphere * dropTorque, ForceMode.Impulse);
        }
        Destroy(dropped, destroyDelay);
    }

    void SpawnCarriedMag()
    {
        if (magazinePrefab == null || handReloadPoint == null) return;

        carriedMag = Instantiate(magazinePrefab, handReloadPoint.position, handReloadPoint.rotation, handReloadPoint);
        // Purely cosmetic while it's "in hand" - strip physics so it doesn't fall or collide.
        Rigidbody rb = carriedMag.GetComponent<Rigidbody>();
        if (rb != null) Destroy(rb);
        Collider col = carriedMag.GetComponent<Collider>();
        if (col != null) Destroy(col);
    }

    void SeatCarriedMag()
    {
        if (carriedMag == null || magSeatPoint == null)
        {
            CleanupCarriedMag();
            return;
        }

        // Clear out whatever mag prop was left seated from the previous reload.
        for (int i = magSeatPoint.childCount - 1; i >= 0; i--)
            Destroy(magSeatPoint.GetChild(i).gameObject);

        carriedMag.transform.SetParent(magSeatPoint, false);
        carriedMag.transform.localPosition = Vector3.zero;
        carriedMag.transform.localRotation = Quaternion.identity;
        carriedMag = null;
    }

    void CleanupCarriedMag()
    {
        if (carriedMag != null) Destroy(carriedMag);
        carriedMag = null;
    }
}