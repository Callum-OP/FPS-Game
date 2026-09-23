using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Experimental third-person camera (press 5 to toggle) for watching how the animations
/// look while the player performs them.
///
/// How it works without touching the first-person rig: the weapon normally hangs off the
/// camera, so moving the camera behind the player would drag the gun with it. Instead this
/// creates a "WeaponEyeMount" transform that copies exactly the pose the first-person
/// camera would have (PlayerMovement still composes it - pitch, lean, recoil, crouch), moves
/// the weapon onto that mount, and only then pulls the real camera back. Everything that
/// aims (hands, weapon, ADS, recoil, melee, grenades) keeps working from the eye.
///
///   5            toggle third person
///   mouse        aims as normal; camera sits high behind the shoulder
///   hold Alt     orbit the camera 360 degrees around the player without changing aim
///                (release and it eases back behind you)
///   scroll       zoom in/out
///
/// The real head is restored while active. No setup: it adds itself to the Player at load.
/// </summary>
[DefaultExecutionOrder(-100)] // after PlayerMovement (-150) composes the camera pose, before WeaponADS (-80)
public class ThirdPersonMode : MonoBehaviour
{
    public static bool Active { get; private set; }
    /// <summary>The first-person eye pose while active (weapon parent, melee/grenade origin).</summary>
    public static Transform Eye { get; private set; }
    /// <summary>How far along the camera aim ray to start the crosshair raycast (skips the player).</summary>
    public static float AimRayStartOffset { get; private set; }

    [Header("Camera")]
    public float distance = 3.2f;
    public float minDistance = 1.2f;
    public float maxDistance = 8f;
    [Tooltip("How far above the eye the camera sits.")]
    public float extraHeight = 0.7f;
    [Tooltip("Sideways shoulder offset (metres, + = right).")]
    public float shoulderOffset = 0.3f;
    [Tooltip("Camera stops this far short of walls.")]
    public float collisionRadius = 0.25f;
    public float orbitSensitivity = 0.2f;
    public float orbitBlendTime = 0.25f;
    public float zoomSpeed = 0.5f;

    PlayerMovement pm;
    PlayerSetup setup;
    WeaponInventory inventory;
    HeadHider head;
    Transform cam;
    InputAction toggle, orbit, look, scroll;
    GrenadeController[] grenades;
    Transform[] grenadeThrowOriginals;

    float orbitBlend, orbitVel, targetBlend;
    float orbitYaw, orbitPitch;
    bool orbiting;
    static readonly RaycastHit[] hits = new RaycastHit[8];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        // AfterSceneLoad only fires for the FIRST scene. Restarting reloads the scene, which
        // made a fresh Player with no ThirdPersonMode - so key 5 died after death + restart.
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        Attach();
    }

    static void OnSceneLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) => Attach();

    static void Attach()
    {
        var p = FindFirstObjectByType<PlayerMovement>();
        if (p != null && p.GetComponent<ThirdPersonMode>() == null) p.gameObject.AddComponent<ThirdPersonMode>();
    }

    void Awake()
    {
        Active = false; Eye = null;
        toggle = PlayerInputMap.Make("ThirdPerson", PlayerInputMap.ThirdPerson);
        orbit = PlayerInputMap.Make("OrbitCamera", PlayerInputMap.OrbitCamera);
        look = PlayerInputMap.Make("OrbitLook", "<Mouse>/delta");
        scroll = PlayerInputMap.Make("OrbitZoom", "<Mouse>/scroll/y");
    }

    void Start()
    {
        pm = GetComponent<PlayerMovement>();
        setup = GetComponent<PlayerSetup>();
        inventory = GetComponent<WeaponInventory>();
        head = GetComponentInChildren<HeadHider>();
        cam = setup != null && setup.fpCamera != null ? setup.fpCamera.transform : (pm != null ? pm.cameraTransform : null);
        if (pm == null || cam == null) { enabled = false; return; }
        orbitSensitivity = pm.mouseSensitivity;

        var health = GetComponent<PlayerHealth>();
        if (health != null) health.onDeath += OnDeath;
    }

    void OnDeath() { if (Active) Exit(); enabled = false; }

    void Update()
    {
        if (toggle.WasPressedThisFrame())
        {
            if (Active) Exit(); else Enter();
        }
        if (!Active) return;

        // Eye mount = the pose PlayerMovement just gave the first-person camera.
        Eye.localPosition = cam.localPosition;
        Eye.localRotation = cam.localRotation;

        distance = Mathf.Clamp(distance - scroll.ReadValue<float>() * 0.01f * zoomSpeed, minDistance, maxDistance);

        Vector3 eyePos = Eye.position;
        Vector3 aimFwd = Eye.forward;
        Vector3 center = eyePos + Vector3.down * 0.35f;

        // Aim pose: high behind the shoulder, looking where the player is aiming.
        Vector3 aimPos = eyePos - aimFwd * distance + Vector3.up * extraHeight + pm.transform.right * shoulderOffset;
        Quaternion aimRot = Quaternion.LookRotation(eyePos + aimFwd * 30f - aimPos, Vector3.up);

        // Orbit (hold Alt): the mouse moves the camera, not the aim.
        bool alt = orbit.IsPressed();
        if (alt && !orbiting)
        {
            Vector3 d = aimPos - center;
            orbitYaw = Mathf.Atan2(-d.x, -d.z) * Mathf.Rad2Deg;
            orbitPitch = Mathf.Asin(Mathf.Clamp(d.y / Mathf.Max(0.01f, d.magnitude), -1f, 1f)) * Mathf.Rad2Deg;
        }
        orbiting = alt;
        pm.LookDetached = alt;
        targetBlend = alt ? 1f : 0f;
        orbitBlend = Mathf.SmoothDamp(orbitBlend, targetBlend, ref orbitVel, orbitBlendTime);

        if (alt)
        {
            Vector2 delta = look.ReadValue<Vector2>();
            orbitYaw += delta.x * orbitSensitivity;
            orbitPitch = Mathf.Clamp(orbitPitch + delta.y * orbitSensitivity, -85f, 85f);
        }

        Vector3 pos = aimPos; Quaternion rot = aimRot; Vector3 pivot = eyePos;
        if (orbitBlend > 0.001f)
        {
            Vector3 orbitPos = center + Quaternion.Euler(orbitPitch, orbitYaw, 0f) * Vector3.back * distance;
            Quaternion orbitRot = Quaternion.LookRotation(center - orbitPos, Vector3.up);
            pos = Vector3.Lerp(aimPos, orbitPos, orbitBlend);
            rot = Quaternion.Slerp(aimRot, orbitRot, orbitBlend);
            pivot = Vector3.Lerp(eyePos, center, orbitBlend);
        }

        pos = ClipToWorld(pivot, pos);
        cam.SetPositionAndRotation(pos, rot);
        AimRayStartOffset = Vector3.Distance(pos, eyePos) + 0.3f;
    }

    Vector3 ClipToWorld(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float len = dir.magnitude;
        if (len < 0.01f) return to;
        dir /= len;
        int n = Physics.SphereCastNonAlloc(from, collisionRadius, dir, hits, len, ~0, QueryTriggerInteraction.Ignore);
        float nearest = len;
        for (int i = 0; i < n; i++)
        {
            if (hits[i].collider.transform.IsChildOf(transform)) continue;
            if (hits[i].distance < nearest) nearest = hits[i].distance;
        }
        return from + dir * Mathf.Max(0.05f, nearest - 0.05f);
    }

    void Enter()
    {
        if (Eye == null)
        {
            var go = new GameObject("WeaponEyeMount");
            go.transform.SetParent(cam.parent != null ? cam.parent : transform, false);
            Eye = go.transform;
        }
        Eye.localPosition = cam.localPosition;
        Eye.localRotation = cam.localRotation;
        Active = true;

        inventory?.ReparentActiveWeapon();

        grenades = GetComponentsInChildren<GrenadeController>(true);
        grenadeThrowOriginals = new Transform[grenades.Length];
        for (int i = 0; i < grenades.Length; i++) { grenadeThrowOriginals[i] = grenades[i].throwPoint; grenades[i].throwPoint = Eye; }

        head?.SetHeadVisible(true);
    }

    void Exit()
    {
        Active = false;
        orbiting = false; orbitBlend = 0f; orbitVel = 0f;
        if (pm != null) pm.LookDetached = false;
        inventory?.ReparentActiveWeapon();
        if (grenades != null)
            for (int i = 0; i < grenades.Length; i++) if (grenades[i] != null) grenades[i].throwPoint = grenadeThrowOriginals[i];
        head?.SetHeadVisible(false);
    }

    void OnDisable() { if (Active) Exit(); }

    void OnDestroy()
    {
        toggle?.Disable(); orbit?.Disable(); look?.Disable(); scroll?.Disable();
        if (Eye != null) Destroy(Eye.gameObject);
        Active = false; Eye = null;
    }
}