using System.Collections;
using UnityEngine;

/// <summary>
/// Cinematic death camera. On player death it detaches the camera, stops player
/// control, and slowly orbits + zooms out around the spot the player fell (or
/// their ragdoll, if one is assigned). Hooks PlayerHealth.onDeath automatically.
///
/// Setup: add this to the Player. Assign the FPS camera (or it uses Camera.main),
/// and drag the movement/look components you want frozen into `disableOnDeath`.
/// If you add a ragdoll, set `orbitTarget` to its root and the camera will circle it.
/// </summary>
[RequireComponent(typeof(PlayerHealth))]
public class DeathCam : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Camera to take over on death. Defaults to Camera.main.")]
    public Camera cam;
    [Tooltip("Optional ragdoll/body to orbit. Defaults to where the player died.")]
    public Transform orbitTarget;
    [Tooltip("Components switched off on death so control stops (movement, mouse-look, weapon, etc.).")]
    public Behaviour[] disableOnDeath;

    [Header("Orbit")]
    public float startRadius = 2.5f;
    public float endRadius = 7f;
    public float startHeight = 1.8f;
    public float endHeight = 4.5f;
    [Tooltip("Degrees per second the camera circles the body.")]
    public float orbitSpeed = 25f;
    [Tooltip("Seconds to pull out from start to end radius/height.")]
    public float zoomOutTime = 3.5f;
    [Tooltip("Look slightly above the ground so the body is framed.")]
    public float lookHeight = 0.8f;

    PlayerHealth health;
    bool triggered;

    void Awake()
    {
        health = GetComponent<PlayerHealth>();
        if (cam == null) cam = Camera.main;
    }

    void OnEnable()  { if (health != null) health.onDeath += OnDeath; }
    void OnDisable() { if (health != null) health.onDeath -= OnDeath; }

    void OnDeath()
    {
        if (triggered) return;
        triggered = true;

        Vector3 deathPos = (orbitTarget != null) ? orbitTarget.position : transform.position;

        // Freeze player control
        if (disableOnDeath != null)
            foreach (var b in disableOnDeath)
                if (b != null) b.enabled = false;

        // Release the cursor for any death/respawn UI
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (cam != null)
        {
            cam.transform.SetParent(null, true);
            StartCoroutine(Orbit(deathPos));
        }
    }

    IEnumerator Orbit(Vector3 center)
    {
        float angle = cam.transform.eulerAngles.y;
        float t = 0f;
        while (true)
        {
            t += Time.deltaTime;
            float k = (zoomOutTime > 0f) ? Mathf.Clamp01(t / zoomOutTime) : 1f;
            float s = Mathf.SmoothStep(0f, 1f, k);
            float radius = Mathf.Lerp(startRadius, endRadius, s);
            float height = Mathf.Lerp(startHeight, endHeight, s);

            angle += orbitSpeed * Time.deltaTime;
            float rad = angle * Mathf.Deg2Rad;

            // Track the ragdoll if it slides/settles after death
            Vector3 c = (orbitTarget != null) ? orbitTarget.position : center;
            cam.transform.position = c + new Vector3(Mathf.Sin(rad) * radius, height, Mathf.Cos(rad) * radius);
            cam.transform.LookAt(c + Vector3.up * lookHeight);
            yield return null;
        }
    }
}
