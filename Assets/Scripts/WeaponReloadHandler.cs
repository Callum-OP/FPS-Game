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
/// do, a fresh mag prop appears in the hand, gets seated back into the weapon, and the
/// hand returns to its normal grip. Instead of also faking a right-hand lift (which
/// looked disconnected from the gun), the weapon itself lifts and tilts up slightly for
/// the same window - the right hand just naturally follows since it's IK'd to a grip
/// point that's a child of the weapon.
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
///  5. Leave weaponLiftPivot empty to auto-use the RightHandGrip's parent (the weapon
///     mesh's own pivot, above WeaponCant/WeaponRecoil/LowerWeapon in the rig) - only
///     assign it explicitly if your weapon's hierarchy differs.
///  6. Tune magHandOffsetPosition/Rotation in Play Mode so the carried mag prop actually
///     lines up with the hand mesh - it's parented straight to the LeftHand bone now, so
///     this offset is the only thing positioning it (see note on that field).
///  7. Tune the timing fields below (fractions of WeaponController.reloadTime) to match
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
    [Tooltip("Local position offset from the LeftHand bone the carried mag prop sits at. The mag is parented directly to the hand bone (not handReloadPoint) so it visibly moves with the hand mesh - tune this in Play Mode until it sits in the palm correctly.")]
    public Vector3 magHandOffsetPosition = Vector3.zero;
    [Tooltip("Local rotation offset (Euler) from the LeftHand bone for the carried mag prop.")]
    public Vector3 magHandOffsetRotation = Vector3.zero;

    [Header("Timing (fraction of WeaponController.reloadTime, 0-1)")]
    [Range(0f, 1f)] public float handMoveOutTime = 0.05f;  // left hand starts leaving the foregrip
    [Range(0f, 1f)] public float magDropTime = 0.15f;      // old mag pops out and falls
    [Range(0f, 1f)] public float magSeatTime = 0.75f;      // new mag clicks in
    [Range(0f, 1f)] public float handReturnTime = 0.9f;    // left hand returns to the foregrip

    [Header("Weapon Lift (replaces a faked right-hand lift)")]
    [Tooltip("Transform to nudge up/rotate during the reload instead of moving the right hand. Leave empty to auto-use RightHandGrip's parent transform.")]
    public Transform weaponLiftPivot;
    [Tooltip("Peak local position offset applied to weaponLiftPivot, eased in and back out across the whole reload (smoothly peaks at the midpoint - no separate timing needed).")]
    public Vector3 weaponLiftPosition = new Vector3(0f, 0.02f, 0f);
    [Tooltip("Peak local rotation offset (Euler) applied to weaponLiftPivot. Negative X tilts the muzzle up on this rig's convention (matches WeaponRecoil's kick-up direction).")]
    public Vector3 weaponLiftRotation = new Vector3(-6f, 0f, 0f);

    [Header("Dropped Mag Physics")]
    public float dropForce = 1.5f;
    public float dropTorque = 3f;
    public float destroyDelay = 5f;

    WeaponHandIK handIK;
    GameObject carriedMag;
    Coroutine running;

    // What we last added to weaponLiftPivot, so each frame can cleanly undo it before
    // adding the new amount - keeps this additive on top of WeaponCant/LowerWeapon/etc
    // instead of fighting them for the transform.
    Vector3 appliedLiftPos = Vector3.zero;
    Quaternion appliedLiftRot = Quaternion.identity;

    /// <summary>Wired up by PlayerSetup whenever this weapon becomes active.</summary>
    public void SetHandIK(WeaponHandIK ik) => handIK = ik;

    /// <summary>Called by WeaponController at the start of its reload coroutine. reloadTime
    /// is passed in so all the timing fractions above scale with whatever this weapon uses.</summary>
    public void PlayReload(float reloadTime)
    {
        if (running != null)
        {
            StopCoroutine(running);
            RemoveAppliedLift();
        }
        running = StartCoroutine(ReloadSequence(reloadTime));
    }

    Transform ResolveLiftPivot(WeaponController weapon)
    {
        if (weaponLiftPivot != null) return weaponLiftPivot;
        // RightHandGrip's parent is the weapon mesh's own pivot (sits above
        // WeaponCant/WeaponRecoil/LowerWeapon in the rig) - a sensible default so this
        // works without extra Inspector setup on top of the grip transforms you already have.
        if (weapon != null && weapon.rightHandGrip != null) return weapon.rightHandGrip.parent;
        return null;
    }

    IEnumerator ReloadSequence(float reloadTime)
    {
        WeaponController weapon = GetComponent<WeaponController>();
        Transform liftPivot = ResolveLiftPivot(weapon);
        bool canReposition = handIK != null && handReloadPoint != null;

        float t = 0f;
        bool dropped = false, seated = false, returned = false;

        while (t < reloadTime)
        {
            t += Time.deltaTime;
            float frac = reloadTime > 0f ? Mathf.Clamp01(t / reloadTime) : 1f;

            if (canReposition && frac >= handMoveOutTime && !returned)
                handIK.SetReloadOverride(null, handReloadPoint);

            if (liftPivot != null)
            {
                // Smooth 0 -> 1 -> 0 hump across the whole reload - peaks at the midpoint,
                // eases back to nothing by the time it's done. No extra timing fields needed.
                float liftMul = Mathf.Sin(frac * Mathf.PI);
                ApplyLift(liftPivot, weaponLiftPosition * liftMul, Quaternion.Euler(weaponLiftRotation * liftMul));
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

        if (liftPivot != null)
            RemoveAppliedLift(liftPivot);

        CleanupCarriedMag();
        running = null;
    }

    // Undoes whatever lift was applied last frame, then applies the new amount - keeps
    // this purely additive on top of the pivot's normal pose instead of overwriting it.
    void ApplyLift(Transform pivot, Vector3 newPos, Quaternion newRot)
    {
        pivot.localPosition -= appliedLiftPos;
        pivot.localRotation = Quaternion.Inverse(appliedLiftRot) * pivot.localRotation;

        appliedLiftPos = newPos;
        appliedLiftRot = newRot;

        pivot.localPosition += appliedLiftPos;
        pivot.localRotation = appliedLiftRot * pivot.localRotation;
    }

    void RemoveAppliedLift(Transform pivot = null)
    {
        if (pivot == null) pivot = ResolveLiftPivot(GetComponent<WeaponController>());
        if (pivot != null)
        {
            pivot.localPosition -= appliedLiftPos;
            pivot.localRotation = Quaternion.Inverse(appliedLiftRot) * pivot.localRotation;
        }
        appliedLiftPos = Vector3.zero;
        appliedLiftRot = Quaternion.identity;
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
        if (magazinePrefab == null) return;

        // Parent straight to the actual LeftHand bone (not handReloadPoint, which is only
        // an IK aim target in weapon space) so the prop visibly rides along with the hand
        // mesh even if the IK doesn't land pixel-perfect on handReloadPoint.
        Transform handBone = handIK != null ? handIK.GetHandBone(isRight: false) : null;
        Transform parent = handBone != null ? handBone : handReloadPoint;
        if (parent == null) return;

        carriedMag = Instantiate(magazinePrefab, parent);
        carriedMag.transform.localPosition = magHandOffsetPosition;
        carriedMag.transform.localRotation = Quaternion.Euler(magHandOffsetRotation);

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