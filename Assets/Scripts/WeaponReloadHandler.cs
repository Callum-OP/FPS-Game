using UnityEngine;
using System.Collections;

/// <summary>
/// Optional per-weapon add-on that makes reloads look like something is actually
/// happening instead of the hands staying glued to the gun for the full reload clip.
///
/// The left hand is moved procedurally through three stops - the normal grip, a grab
/// point (e.g. a hip mag pouch), and a reload point near the magwell - then back to the
/// grip. All of that movement is smoothly
/// interpolated over time rather than snapping between points. A mag prop rides along
/// on the hand (only while it's actually being carried) and drops the old mag on the
/// floor like the empty casings do. The weapon itself also moves to an authored "reload
/// pivot" position and back, so the right hand (IK'd to a grip that's a child of the
/// weapon) naturally follows.
///
/// Entirely optional: if this component isn't on a weapon, WeaponController's reload
/// falls back to exactly what it did before - just the animator trigger and a timed
/// ammo refill. That's the right call for weapons with no detachable mag (e.g. a
/// shotgun reloading shell by shell) - just leave usesMagazine unticked there.
///
/// EVERY point this needs is a real transform YOU place and can see in the Scene view -
/// nothing is created at runtime or tuned by typing numbers blind:
///  - reloadGrabPoint and magHandAttachPoint live on the PLAYER body (a hip pouch and a
///    spot on the hand are body-relative, not weapon-relative), so you place them once
///    in the Player model and register them by name on its CharacterAttachPoints
///    component ("ReloadGrab" and "MagHand"). PlayerSetup pushes them into whichever
///    weapon is equipped automatically - see SetAttachPoints() below. Add more named
///    points there any time for other features (pouches, holsters, etc) - this reload
///    system doesn't need to know about them.
///  - magSeatPoint and handReloadPoint stay on the weapon prefab since the magwell
///    position is different per gun. magSeatPoint is only used as the drop origin now -
///    the new mag rides on the hand for the rest of the reload and is then deleted,
///    while weaponMagVisual (if assigned) reappears on the gun at that same moment.
///
/// SETUP (per magazine-fed weapon prefab):
///  1. Add this component next to WeaponController.
///  2. Add an empty child transform at the magwell - this is only the drop origin for
///     the old mag now (assign to magSeatPoint).
///  3. Add a second empty child transform near the magwell for where the hand ends up
///     while carrying the new mag there. Assign to handReloadPoint.
///  4. Assign magazinePrefab - a small mag mesh. Give it a Rigidbody + Collider so the
///     dropped copy tumbles on the floor; the carried copy strips those off
///     automatically since they're purely cosmetic. Untick usesMagazine for weapons
///     that don't have one at all.
///  5. On the Player model, create "ReloadGrab" (near a hip pouch) and "MagHand" (a
///     child of the LeftHand bone) empty transforms, position/rotate them by eye in the
///     Scene view, and register both by name on the Player's CharacterAttachPoints
///     component. Do this once - every weapon picks them up automatically.
///  6. Leave weaponReloadPivot empty to skip the weapon-move entirely, or assign an
///     empty transform placed as a SIBLING of the weapon mesh (same parent as whatever
///     RightHandGrip's parent is) wherever you want the gun to move to during reload.
///  7. Tune the timing fields below (fractions of WeaponController.reloadTime) to match
///     however long your reload animation actually takes to look right.
/// </summary>
[RequireComponent(typeof(WeaponController))]
public class WeaponReloadHandler : MonoBehaviour
{
    [Header("Magazine")]
    [Tooltip("Untick for weapons with no detachable mag (e.g. a shotgun reloading shell by shell) - skips the drop/carry/seat visuals entirely even if a magazinePrefab is assigned.")]
    public bool usesMagazine = true;
    [Tooltip("Small mag mesh. Needs a Rigidbody + Collider for the dropped copy to fall like a casing.")]
    public GameObject magazinePrefab;
    [Tooltip("Where the empty mag drops from during reload - just the drop origin now, the new mag stays on the hand instead of being seated back into the weapon (most weapon meshes already model a mag in place, so seating a second copy there just looked like a stray duplicate).")]
    public Transform magSeatPoint;
    [Tooltip("Optional - assign this if the weapon's own mesh already has a mag modelled into it as a separate child object. It's hidden the instant the old mag drops (so you don't see the old, now-dropped mag still sitting in the gun) and shown again once the reload finishes. Since it's just SetActive on the real object rather than destroying/recreating anything, it always reappears in exactly the same place it always was - no repositioning needed. Leave empty if the weapon mesh doesn't have a separate mag piece.")]
    public GameObject weaponMagVisual;

    [Header("Hand Path")]
    [Tooltip("Where the left hand carries the mag to and seats it, near the magwell. Lives on the weapon prefab since the magwell differs per gun.")]
    public Transform handReloadPoint;
    [Tooltip("Optional override: assign directly to use a weapon-specific grab point instead of the player's shared \"ReloadGrab\" attach point pushed in by PlayerSetup. Leave empty in the normal case.")]
    public Transform reloadGrabPointOverride;
    [Tooltip("Optional override: assign directly to use a weapon-specific mag-hand point instead of the player's shared \"MagHand\" attach point pushed in by PlayerSetup. Leave empty in the normal case.")]
    public Transform magHandAttachPointOverride;

    [Header("Timing (fraction of WeaponController.reloadTime, 0-1)")]
    [Range(0f, 1f)] public float handGrabTime = 0.2f;    // hand arrives at the grab point, old mag drops, new mag appears in hand
    [Range(0f, 1f)] public float handReloadTime = 0.55f; // hand arrives at handReloadPoint
    [Range(0f, 1f)] public float handReturnTime = 0.95f; // hand arrives back at its normal grip

    [Header("Weapon Reload Move")]
    [Tooltip("Empty transform placed as a SIBLING of the weapon mesh (RightHandGrip's parent) wherever you want the weapon to move to during reload - its local position/rotation are read directly, so it must share the same parent to mean the same thing. The weapon eases there across the reload and back once done. Leave empty to skip.")]
    public Transform weaponReloadPivot;

    [Header("Carried Mag Offset")]
    [Tooltip("Local position offset applied to the carried mag prop relative to its attach point, to correct for the mag mesh's own pivot not being centred on the mag. Defaults to a sensible in-palm offset - tune further if it still looks off for a particular mag mesh.")]
    public Vector3 carriedMagOffsetPosition = new Vector3(0f, 0.096f, 0f);
    [Tooltip("Local rotation offset (Euler) applied to the carried mag prop relative to its attach point.")]
    public Vector3 carriedMagOffsetRotationEuler = new Vector3(-90f, 0f, -98.034f);

    [Header("Dropped Mag Physics")]
    public float dropForce = 1.5f;
    public float dropTorque = 3f;
    public float destroyDelay = 5f;

    WeaponHandIK handIK;
    Transform playerReloadGrabPoint; // pushed in by PlayerSetup.SetAttachPoints()
    Transform playerMagHandPoint;    // pushed in by PlayerSetup.SetAttachPoints()
    GameObject carriedMag;
    Transform handAnchor;       // runtime cursor the left hand IK actually targets; eased between waypoints each frame
    Transform movedWeaponPart;  // auto-resolved as rightHandGrip.parent; what weaponReloadPivot moves
    Coroutine running;

    Vector3 reloadMoveDeltaPos;
    Quaternion reloadMoveDeltaRot = Quaternion.identity;
    Vector3 appliedMoveDeltaPos;
    Quaternion appliedMoveDeltaRot = Quaternion.identity;
    float moveFrac;   // 0-1 through the current reload, drives the sine hump; read by LateUpdate
    bool moveActive;
    bool magVisualHidden;

    /// <summary>Wired up by PlayerSetup whenever this weapon becomes active.</summary>
    public void SetHandIK(WeaponHandIK ik) => handIK = ik;

    /// <summary>Wired up by PlayerSetup whenever this weapon becomes active, passing the
    /// player's own manually-placed "ReloadGrab"/"MagHand" CharacterAttachPoints. Either
    /// can be null (e.g. points not set up yet) - the reload just skips that part.</summary>
    public void SetAttachPoints(Transform reloadGrab, Transform magHand)
    {
        playerReloadGrabPoint = reloadGrab;
        playerMagHandPoint = magHand;
    }

    Transform EffectiveGrabPoint => reloadGrabPointOverride != null ? reloadGrabPointOverride : playerReloadGrabPoint;
    Transform EffectiveMagHandPoint => magHandAttachPointOverride != null ? magHandAttachPointOverride : playerMagHandPoint;

    /// <summary>Called by WeaponController at the start of its reload coroutine. reloadTime
    /// is passed in so all the timing fractions above scale with whatever this weapon uses.</summary>
    public void PlayReload(float reloadTime)
    {
        if (running != null)
            StopCoroutine(running);

        // Always clear out whatever mag prop the previous reload left riding on the
        // hand - whether that reload finished normally or got interrupted mid-flight -
        // so there's never more than one carried mag at a time. Also make sure the
        // weapon's real mag mesh isn't left hidden from a reload that got interrupted
        // before it had a chance to show it again.
        CleanupCarriedMag();
        ShowWeaponMagVisual();

        running = StartCoroutine(ReloadSequence(reloadTime));
    }

    IEnumerator ReloadSequence(float reloadTime)
    {
        WeaponController weapon = GetComponent<WeaponController>();

        if (movedWeaponPart == null)
            movedWeaponPart = weapon != null && weapon.rightHandGrip != null ? weapon.rightHandGrip.parent : null;

        Transform grabPoint = EffectiveGrabPoint;
        bool canReposition = handIK != null && (grabPoint != null || handReloadPoint != null);

        if (canReposition && handAnchor == null)
            handAnchor = new GameObject("ReloadHandAnchor (runtime)").transform;

        // Snapshot where the left grip actually is right now so the hand's path starts
        // and ends exactly there, however the weapon happens to be held at the moment.
        Vector3 gripStartPos = weapon != null && weapon.leftHandGrip != null ? weapon.leftHandGrip.position : transform.position;
        Quaternion gripStartRot = weapon != null && weapon.leftHandGrip != null ? weapon.leftHandGrip.rotation : transform.rotation;

        if (canReposition)
        {
            handAnchor.SetPositionAndRotation(gripStartPos, gripStartRot);
            handIK.SetReloadOverride(null, handAnchor);
        }

        if (movedWeaponPart != null && weaponReloadPivot != null)
        {
            // Deltas computed once from the snapshot rather than absolute targets, and
            // applied additively in LateUpdate (undo-old/add-new) - this is what lets it
            // safely compose with WeaponHandIK's own additive pull-back on this same
            // pivot regardless of which component's LateUpdate happens to run first.
            reloadMoveDeltaPos = weaponReloadPivot.localPosition - movedWeaponPart.localPosition;
            reloadMoveDeltaRot = weaponReloadPivot.localRotation * Quaternion.Inverse(movedWeaponPart.localRotation);
            moveActive = true;
        }
        else
        {
            moveActive = false;
        }
        moveFrac = 0f;

        bool droppedMag = false;
        float t = 0f;

        while (t < reloadTime)
        {
            t += Time.deltaTime;
            float frac = reloadTime > 0f ? Mathf.Clamp01(t / reloadTime) : 1f;
            moveFrac = frac; // read by LateUpdate()

            if (canReposition)
                UpdateHandAnchor(frac, gripStartPos, gripStartRot, grabPoint);

            if (usesMagazine && frac >= handGrabTime && !droppedMag)
            {
                DropOldMag();
                SpawnCarriedMag();
                HideWeaponMagVisual();
                droppedMag = true;
            }

            yield return null;
        }

        if (canReposition)
            handIK.SetReloadOverride(null, null);

        moveActive = false; // LateUpdate eases the additive offset back to zero next frame, then stops touching it

        // The carried mag prop was only ever a stand-in for "there's now a mag in the
        // gun" - once the reload is actually done it gets deleted rather than left
        // parented to the hand, otherwise it just sits there stuck to the hand forever
        // (same stray-object problem the old seat-into-weapon step had). The weapon's
        // own built-in mag mesh (if assigned) reappears at the same moment, right where
        // it always was, so there's still a mag visible - just on the gun, not the hand.
        CleanupCarriedMag();
        ShowWeaponMagVisual();

        running = null;
    }

    void LateUpdate()
    {
        if (movedWeaponPart == null) return;

        // Purely additive: undo whatever this component added last frame, then add this
        // frame's amount. This composes safely with WeaponHandIK's own additive
        // pull-back on the same pivot, regardless of execution order between the two.
        movedWeaponPart.localPosition -= appliedMoveDeltaPos;
        movedWeaponPart.localRotation = Quaternion.Inverse(appliedMoveDeltaRot) * movedWeaponPart.localRotation;

        float mul = moveActive ? Mathf.Sin(moveFrac * Mathf.PI) : 0f;
        appliedMoveDeltaPos = reloadMoveDeltaPos * mul;
        appliedMoveDeltaRot = Quaternion.Slerp(Quaternion.identity, reloadMoveDeltaRot, mul);

        movedWeaponPart.localPosition += appliedMoveDeltaPos;
        movedWeaponPart.localRotation = appliedMoveDeltaRot * movedWeaponPart.localRotation;
    }

    // Eases handAnchor through grip -> grabPoint -> handReloadPoint -> grip, skipping
    // any waypoint left unassigned. Interpolated every frame (not snapped between
    // targets), so the hand actually travels there instead of teleporting.
    void UpdateHandAnchor(float frac, Vector3 gripPos, Quaternion gripRot, Transform grabPoint)
    {
        Vector3 p0 = gripPos; Quaternion r0 = gripRot;
        Vector3 p1 = gripPos; Quaternion r1 = gripRot;
        Vector3? grabP = grabPoint != null ? grabPoint.position : (Vector3?)null;
        Quaternion grabR = grabPoint != null ? grabPoint.rotation : Quaternion.identity;
        Vector3? reloadP = handReloadPoint != null ? handReloadPoint.position : (Vector3?)null;
        Quaternion reloadR = handReloadPoint != null ? handReloadPoint.rotation : Quaternion.identity;

        if (grabP.HasValue && frac <= handGrabTime)
        {
            LerpAnchor(0f, p0, r0, handGrabTime, grabP.Value, grabR, frac);
            return;
        }
        if (reloadP.HasValue && frac <= handReloadTime)
        {
            float segStart = grabP.HasValue ? handGrabTime : 0f;
            Vector3 segStartPos = grabP.HasValue ? grabP.Value : p0;
            Quaternion segStartRot = grabP.HasValue ? grabR : r0;
            LerpAnchor(segStart, segStartPos, segStartRot, handReloadTime, reloadP.Value, reloadR, frac);
            return;
        }
        // Final leg back to the grip - starts from whichever real waypoint was last used.
        float returnSegStart = reloadP.HasValue ? handReloadTime : (grabP.HasValue ? handGrabTime : 0f);
        Vector3 returnStartPos = reloadP.HasValue ? reloadP.Value : (grabP.HasValue ? grabP.Value : p0);
        Quaternion returnStartRot = reloadP.HasValue ? reloadR : (grabP.HasValue ? grabR : r0);
        LerpAnchor(returnSegStart, returnStartPos, returnStartRot, handReturnTime, p1, r1, frac);
    }

    void LerpAnchor(float tStart, Vector3 posStart, Quaternion rotStart, float tEnd, Vector3 posEnd, Quaternion rotEnd, float frac)
    {
        float segLen = Mathf.Max(0.0001f, tEnd - tStart);
        float segT = Mathf.Clamp01((frac - tStart) / segLen);
        segT = segT * segT * (3f - 2f * segT); // smoothstep - eases in/out of each leg instead of moving at a constant rate
        handAnchor.SetPositionAndRotation(Vector3.Lerp(posStart, posEnd, segT), Quaternion.Slerp(rotStart, rotEnd, segT));
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

        // Parented to the player's own manually-placed "MagHand" point (or this weapon's
        // override) so it's exactly where you put it and visible in the Scene view -
        // nothing is created or guessed at runtime. Falls back to the raw hand bone only
        // if no attach point has been set up at all yet.
        Transform parent = EffectiveMagHandPoint;
        if (parent == null) parent = handIK != null ? handIK.GetHandBone(isRight: false) : null;
        if (parent == null) return;

        carriedMag = Instantiate(magazinePrefab, parent);
        carriedMag.transform.localPosition = carriedMagOffsetPosition;
        carriedMag.transform.localRotation = Quaternion.Euler(carriedMagOffsetRotationEuler);

        // Purely cosmetic while it's "in hand" - strip physics so it doesn't fall or collide.
        Rigidbody rb = carriedMag.GetComponent<Rigidbody>();
        if (rb != null) Destroy(rb);
        Collider col = carriedMag.GetComponent<Collider>();
        if (col != null) Destroy(col);
    }

    void CleanupCarriedMag()
    {
        if (carriedMag != null) Destroy(carriedMag);
        carriedMag = null;
    }

    void HideWeaponMagVisual()
    {
        if (weaponMagVisual == null) return;
        weaponMagVisual.SetActive(false);
        magVisualHidden = true;
    }

    void ShowWeaponMagVisual()
    {
        if (weaponMagVisual == null) return;
        // Just re-enabling the same object it always was - never moved, so it reappears
        // in exactly the same place with no repositioning needed.
        weaponMagVisual.SetActive(true);
        magVisualHidden = false;
    }

    void OnDisable()
    {
        // Weapon got swapped away or holstered mid-reload - don't leave its real mag
        // mesh hidden for next time it's equipped.
        if (magVisualHidden) ShowWeaponMagVisual();
    }

    void OnDestroy()
    {
        if (handAnchor != null) Destroy(handAnchor.gameObject);
        // Don't leave the weapon's real mag mesh permanently hidden if this component
        // (or the weapon it's on) gets destroyed mid-reload.
        if (magVisualHidden && weaponMagVisual != null) weaponMagVisual.SetActive(true);
    }
}