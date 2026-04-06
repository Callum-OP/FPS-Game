using UnityEngine;
using System.Collections;

public class GunSlide : MonoBehaviour
{
    [Header("Slide Settings")]
    public float slideBackDistance = 0.03f;
    public float slideSpeed = 25f;
    public float slideReturnSpeed = 10f;

    [Header("Empty Slide Lock")]
    public bool lockOpenWhenEmpty = true; // Stays back when mag empty
    public WeaponController weaponController;

    private Vector3 originalPos;
    private Vector3 targetPos;
    private bool isSlideBack = false;
    private bool isSlideLocked = false;

    void Start()
    {
        originalPos = transform.localPosition;
        targetPos   = originalPos;

        // Hook into weapon events
        if (weaponController != null)
        {
            weaponController.onAmmoChanged += OnAmmoChanged;
        }
        else
        {
            // Auto find on parent if not assigned
            weaponController = GetComponentInParent<WeaponController>();
            if (weaponController != null)
                weaponController.onAmmoChanged += OnAmmoChanged;
        }
    }

    void Update()
    {
        transform.localPosition = Vector3.Lerp(
            transform.localPosition, targetPos,
            (isSlideBack ? slideSpeed : slideReturnSpeed) * Time.deltaTime);
    }

    // Called by WeaponController when firing
    public void OnFire()
    {
        if (isSlideLocked) return;
        StartCoroutine(SlideRoutine());
    }

    IEnumerator SlideRoutine()
    {
        isSlideBack = true;

        // Snap back
        targetPos = originalPos - new Vector3(0f, 0f, slideBackDistance);

        // Wait for slide to reach back position
        yield return new WaitForSeconds(0.04f);

        // Return forward unless locked
        if (!isSlideLocked)
        {
            isSlideBack = false;
            targetPos   = originalPos;
        }
    }

    void OnAmmoChanged(int current, int max)
    {
        if (!lockOpenWhenEmpty) return;

        if (current <= 0)
        {
            // Lock slide back on empty
            isSlideLocked = true;
            isSlideBack   = true;
            targetPos     = originalPos - new Vector3(0f, 0f, slideBackDistance);
        }
        else if (isSlideLocked)
        {
            // Release slide on reload
            isSlideLocked = false;
            isSlideBack   = false;
            targetPos     = originalPos;
        }
    }

    void OnDestroy()
    {
        if (weaponController != null)
            weaponController.onAmmoChanged -= OnAmmoChanged;
    }
}