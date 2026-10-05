using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>How the grenade is carried while it is out and not being thrown. Cycle live with the GrenadeStyle key (B).</summary>
public enum GrenadeHoldStyle
{
    Low = 0,      // carried down by the hip - the relaxed, out-of-the-way carry
    Chest = 1,    // up at chest height, close to the body
    Forward = 2   // arm held out in front, the way a pistol is held
}

/// <summary>A pose for the held grenade, relative to the camera. Position is the grenade's centre; euler rotates the hand
/// (0,0,0 = the hand oriented as it is when it holds a pistol straight out).</summary>
[System.Serializable]
public class GrenadePose
{
    [Tooltip("Where the grenade sits relative to the camera: x right, y up, z forward, metres.")]
    public Vector3 position;
    [Tooltip("Hand rotation in degrees (pitch, yaw, roll).")]
    public Vector3 euler;

    public GrenadePose() { }
    public GrenadePose(Vector3 position, Vector3 euler) { this.position = position; this.euler = euler; }
}

/// <summary>
/// A grenade you actually hold.
///
///   4            take a grenade out (the gun goes onto the body with the normal holster) / put it away
///   Left click   press: the arm raises and cocks back. Tap it and the throw follows straight on.
///                Keep holding: the grenade stays raised so you can aim (a landing arc is drawn), and
///                RELEASE to throw.
///   4 (raised)   cancel the throw and lower the grenade again (not once a cooking fuse is burning)
///   2 / 3        swap straight back to the gun while the grenade is just being carried: 2 draws the gun you had,
///                3 draws the other one (rifle <-> pistol), same as pressing 3 normally
///   B            cycle how the grenade is carried: Low / Chest / Forward (arm out like a gun)
///
/// How it is built (same ideas as the guns): the grenade is a small rig parented to the camera, and WeaponHandIK
/// puts the right hand on a grip transform on that rig, so the arm, hand and grenade all follow the pose I move
/// every frame. This script is the single owner of that rig's transform. The poses are plain Vector3s on the
/// component - edit them in the Inspector in Play Mode and the hand moves live.
///
/// The grenade itself is parented to the hand bone, and placed from the hand's own finger bones (wrist to knuckles,
/// plus the palm side), so it sits in the palm whatever the rig's bone axes are. If it ends up on the back of the
/// hand tick flipPalmSide; if the fingers curl backwards tick WeaponHandIK.invertRightCurl.
///
/// Fuse: by default it starts when the grenade leaves the hand, so you can hold and aim as long as you like.
/// cookFuseWhileHeld starts it at the click instead (the grenade is thrown for you when the fuse is nearly out).
///
/// SETUP: on the Player root, as before. grenadePrefab wants an Explosive with useTimer on.
/// heldGrenadeVisual is optional now - with it empty the grenade prefab itself is shown in the hand.
/// Re-running Build Animation System is NOT needed; the old "toss grenade" clip is no longer used.
/// </summary>
[DefaultExecutionOrder(-70)]
public class GrenadeController : MonoBehaviour
{
    [Header("Grenade")]
    public GameObject grenadePrefab;
    [Tooltip("Legacy. The old throw origin (a fixed point on the player root, which does NOT follow where you look). Only used if no camera can be found.")]
    public Transform throwPoint;
    [Tooltip("The first-person camera the grenade arm hangs off, so it follows looking up/down exactly like a gun does, and the throw goes where you look. Found automatically.")]
    public Transform viewPoint;
    public float throwForce = 15f;
    public float throwUpward = 5f;
    public int maxGrenades = 3;
    [Tooltip("Take the next grenade out straight away after a throw, if any are left.")]
    public bool stayEquippedAfterThrow = true;
    [Tooltip("How much of the player's own horizontal movement is added to the throw (0 = none, 1 = all of it).")]
    [Range(0f, 1f)] public float inheritMovement = 0.6f;

    [Header("Pickup")]
    [Tooltip("The pickup that appears when a grenade is dropped (the drop key while a grenade is in hand). A grenade pickup prefab, same idea as WeaponController.pickupPrefab.")]
    public GameObject pickupPrefab;

    [Header("Cooking")]
    [Tooltip("Start the fuse at the click rather than at release. The grenade is thrown automatically when the fuse is nearly out.")]
    public bool cookFuseWhileHeld = false;
    [Tooltip("Seconds of fuse left at which a cooked grenade is thrown for you.")]
    public float autoThrowMargin = 0.6f;

    [Header("Held Grenade (visual)")]
    [Tooltip("Optional model for the grenade in the hand. Empty = the grenade prefab itself is shown (stripped to a prop).")]
    public GameObject heldGrenadeVisual;
    public float visualScale = 1f;
    [Tooltip("Extra rotation of the model in the hand, degrees - turn it if the pin/lever faces the wrong way.")]
    public Vector3 visualEuler = Vector3.zero;

    [Header("Hold Style")]
    public GrenadeHoldStyle holdStyle = GrenadeHoldStyle.Forward;
    [Tooltip("Carried by the hip.")]
    public GrenadePose lowPose = new GrenadePose(new Vector3(0.12f, -0.23f, 0.36f), new Vector3(0f, 0f, 20f));
    [Tooltip("Held up close to the chest.")]
    public GrenadePose chestPose = new GrenadePose(new Vector3(0.08f, -0.11f, 0.40f), new Vector3(-8f, 0f, 30f));
    [Tooltip("Arm out in front like a gun - the default: the same spot the pistol's right hand sits in its normal hip hold (hip pose -1.2/2.2/35, grip point about 0.05, -0.16, 0.33 from the camera).")]
    public GrenadePose forwardPose = new GrenadePose(new Vector3(0.06f, -0.16f, 0.38f), new Vector3(-1.2f, 2.2f, 35f));

    [Header("Throw Poses")]
    [Tooltip("Cocked back and held while the button is down (this is the aiming pose).")]
    public GrenadePose raisedPose = new GrenadePose(new Vector3(0.27f, 0.03f, 0.08f), new Vector3(-30f, 10f, 25f));
    [Tooltip("Where the hand is when the grenade leaves it. The throw arc preview starts here.")]
    public GrenadePose releasePose = new GrenadePose(new Vector3(0.13f, -0.05f, 0.50f), new Vector3(20f, -5f, -10f));
    [Tooltip("Where the hand ends up after the throw.")]
    public GrenadePose followThroughPose = new GrenadePose(new Vector3(0.05f, -0.40f, 0.42f), new Vector3(55f, -10f, -20f));
    [Tooltip("Offset from the carry pose the hand starts from when the grenade is drawn / goes back to when it is put away.")]
    public Vector3 drawOffset = new Vector3(0.12f, -0.40f, -0.10f);

    [Header("Hand Grip")]
    [Tooltip("Hand orientation at pose (0,0,0). Default is the pistol's own RightHandGrip rotation.")]
    public Vector3 gripEuler = new Vector3(-9.027f, -94.328f, -102.017f);
    [Tooltip("How far along wrist -> knuckles the grenade's centre sits (1 = at the knuckles).")]
    [Range(0f, 1.3f)] public float palmForward = 0.85f;
    [Tooltip("How far out of the palm, towards the fingers' curl side, metres (about the grenade's radius).")]
    public float palmLift = 0.035f;
    [Tooltip("Tick if the grenade sits on the BACK of the hand.")]
    public bool flipPalmSide = false;
    [Tooltip("Used only if the hand's finger bones can't be found. Hand-bone space, metres.")]
    public Vector3 fallbackPalmOffset = new Vector3(0.08f, 0.02f, 0f);
    [Tooltip("Close the right hand's fingers round the grenade (uses WeaponHandIK's finger override while it is out).")]
    public bool curlFingers = true;
    public FingerGripPose fingerCurl = new FingerGripPose { thumbCurl = 30f, indexCurl = 60f, middleCurl = 65f, ringCurl = 65f, pinkyCurl = 65f };

    [Header("Timing (seconds)")]
    public float equipTime = 0.40f;
    [Tooltip("Click to fully raised. A quick tap speeds this up so the throw doesn't lag the click.")]
    public float raiseTime = 0.28f;
    [Tooltip("How much faster the raise plays when the button has already been let go.")]
    public float quickRaiseSpeed = 1.8f;
    [Tooltip("Raised to release.")]
    public float throwTime = 0.26f;
    public float followThroughTime = 0.30f;
    public float recoverTime = 0.40f;
    public float putAwayTime = 0.30f;
    [Tooltip("How quickly the hand settles onto a held pose (and onto a new one when you edit it or change style).")]
    public float poseSharpness = 14f;

    [Header("Body")]
    [Tooltip("Torso twist (degrees, right) while the arm is cocked back. 0 = off. Needs TorsoPoseDriver.")]
    public float windUpTwist = 18f;
    [Tooltip("Torso twist at the moment of release (degrees, negative = left, through the throw).")]
    public float throwTwist = -8f;
    [Tooltip("Small right twist while the grenade is simply carried.")]
    public float carryTwist = 4f;

    [Header("Aim Preview")]
    public bool showTrajectory = true;
    public float trajectoryStep = 0.05f;
    public int trajectoryMaxPoints = 50;
    public Color trajectoryColor = new Color(1f, 0.85f, 0.3f, 0.8f);

    [Header("References")]
    public WeaponInventory inventory;
    public CharacterAnimationDriver animationDriver;
    public WeaponHandIK hands;

    // ---- runtime -----------------------------------------------------------------------------
    enum State { Off, Waiting, Drawing, Ready, Raising, Raised, Throwing, FollowThrough, Recovering, PuttingAway }

    State state = State.Off;
    float t;                              // 0-1 progress through the current timed state
    Vector3 pos, fromPos;                 // current grenade pose (camera space) and the pose a timed state started from
    Quaternion rot = Quaternion.identity, fromRot = Quaternion.identity;
    int currentGrenades;
    bool pressQueued, releaseQueued;
    bool cooking; float cookStart;
    float prefabFuse = 3f;
    float curlK = 1f, curlTarget = 1f;
    float twistNow;

    Transform rig, grip;
    Transform handBone;
    GameObject visual;
    float visualBaseScale = 1f;
    Vector3 palmFwd, palmNormal; bool havePalmBones;

    FingerGripPose savedPose;
    bool savedOverride, fingersSaved;

    LineRenderer line;
    Vector3[] linePoints;
    Collider[] ownColliders;
    CharacterController cc;
    PlayerHealth playerHealth;
    PlayerHUD hud;
    int shownCount = -1;                  // last grenade count written to the HUD (-1 = nothing shown)
    System.Action deathHandler;
    bool warned;

    InputAction equipAction, throwAction, styleAction, gunAction, switchGunAction;
    bool drawGunOnExit, switchGunOnExit;      // set by 2 / 3 while putting the grenade away
    static readonly RaycastHit[] hitBuffer = new RaycastHit[8];

    // The transform everything hangs off and is aimed from: the first-person camera (or the first-person eye while the
    // third-person camera is active, which is what the guns hang off too).
    Transform View => (ThirdPersonMode.Active && ThirdPersonMode.Eye != null) ? ThirdPersonMode.Eye : viewPoint;

    // ---- lifecycle ---------------------------------------------------------------------------
    void Awake()
    {
        equipAction = PlayerInputMap.Make("Grenade", PlayerInputMap.Grenade);
        throwAction = PlayerInputMap.Make("ThrowGrenade", PlayerInputMap.Fire);
        styleAction = PlayerInputMap.Make("GrenadeStyle", PlayerInputMap.GrenadeStyle);
        gunAction = PlayerInputMap.Make("GrenadeToGun", PlayerInputMap.LowerWeapon);
        switchGunAction = PlayerInputMap.Make("GrenadeSwitchGun", PlayerInputMap.ToggleWeapon);
        currentGrenades = maxGrenades;
    }

    void Start()
    {
        if (inventory == null) inventory = GetComponent<WeaponInventory>();
        if (animationDriver == null) animationDriver = GetComponentInChildren<CharacterAnimationDriver>();
        if (hands == null) hands = GetComponentInChildren<WeaponHandIK>(true);
        if (viewPoint == null)
        {
            var setup = GetComponent<PlayerSetup>();
            if (setup != null && setup.fpCamera != null) viewPoint = setup.fpCamera.transform;
            else if (Camera.main != null) viewPoint = Camera.main.transform;
            else viewPoint = throwPoint;
        }

        ownColliders = GetComponentsInChildren<Collider>(true);
        cc = GetComponent<CharacterController>();

        if (grenadePrefab != null)
        {
            var ex = grenadePrefab.GetComponent<Explosive>();
            if (ex != null) prefabFuse = ex.fuseTime;
        }

        playerHealth = GetComponentInChildren<PlayerHealth>(true);
        if (playerHealth != null) { deathHandler = () => Finish(false); playerHealth.onDeath += deathHandler; }
    }

    void Update()
    {
        float dt = Time.deltaTime;

        if (styleAction.WasPressedThisFrame()) CycleStyle();
        if (equipAction.WasPressedThisFrame()) HandleEquipKey();
        HandleGunKeys();

        if (state == State.Off || state == State.Waiting) return;
        if (View == null) return;

        bool pressed = throwAction.WasPressedThisFrame();
        bool held = throwAction.ReadValue<float>() > 0.5f;
        if (!held) pressQueued = false;
        else if (pressed && state != State.Ready) pressQueued = true;

        TickState(dt, pressed, held);
        TickFingers(dt);
        ApplyPose();
        UpdateTrajectory();
        UpdateTwist(dt);
        RefreshHud();
    }

    // While a grenade is in hand the ammo text shows grenades carried / max (same spot as gun ammo). Polled, so it stays
    // right however the count changes - throwing, pickups, anything else that adds grenades.
    void RefreshHud()
    {
        if (hud == null) { hud = FindFirstObjectByType<PlayerHUD>(); if (hud == null) return; }
        if (shownCount == currentGrenades) return;
        shownCount = currentGrenades;
        hud.ShowGrenades(currentGrenades, maxGrenades);
    }

    // ---- input -------------------------------------------------------------------------------
    void CycleStyle()
    {
        holdStyle = (GrenadeHoldStyle)(((int)holdStyle + 1) % 3);
        Debug.Log($"Grenade hold style: {holdStyle}");
    }

    // 2 / 3 while the grenade is only being carried: put it away and bring the gun up. (Not mid-throw: once the arm is
    // going back the throw is committed, and a cooking grenade has to go somewhere.)
    void HandleGunKeys()
    {
        bool two = gunAction.WasPressedThisFrame();
        bool three = switchGunAction.WasPressedThisFrame();
        if (!two && !three) return;
        if (state != State.Ready && state != State.Drawing && state != State.Recovering) return;

        drawGunOnExit = true;
        switchGunOnExit = three;
        BeginTimed(State.PuttingAway);
    }

    void HandleEquipKey()
    {
        switch (state)
        {
            case State.Off:
                if (currentGrenades <= 0) return;
                if (grenadePrefab == null || View == null)
                {
                    if (!warned) { warned = true; Debug.LogWarning("GrenadeController needs a grenade prefab and a throw point (the camera).", this); }
                    return;
                }
                if (inventory != null && inventory.IsHolsterBusy) return;
                StartCoroutine(TakeOutRoutine());
                break;

            case State.Ready:
                BeginTimed(State.PuttingAway);
                break;

            case State.Raising:
            case State.Raised:
                if (!cooking) BeginTimed(State.Recovering);   // cancel the throw - nothing was armed
                break;
        }
    }

    // ---- state machine -----------------------------------------------------------------------
    void BeginTimed(State next)
    {
        state = next;
        t = 0f;
        fromPos = pos;
        fromRot = rot;
    }

    void TickState(float dt, bool pressed, bool held)
    {
        switch (state)
        {
            case State.Drawing:
            {
                GrenadePose target = StylePose();
                if (Advance(dt, equipTime)) { state = State.Ready; }
                BlendFrom(target, Ease(t));
                break;
            }

            case State.Ready:
            {
                Damp(StylePose(), dt);
                if (pressed || (pressQueued && held))
                {
                    pressQueued = false;
                    releaseQueued = false;
                    cooking = cookFuseWhileHeld;
                    cookStart = Time.time;
                    BeginTimed(State.Raising);
                }
                break;
            }

            case State.Raising:
            {
                if (!held) releaseQueued = true;
                Advance(dt, raiseTime, releaseQueued ? quickRaiseSpeed : 1f);
                BlendFrom(raisedPose, Ease(t));
                if (t >= 1f)
                {
                    state = State.Raised;
                    if (releaseQueued) BeginThrow();
                }
                break;
            }

            case State.Raised:
            {
                // A little life while it is held, so the arm isn't a statue.
                Vector3 breathe = new Vector3(Mathf.Sin(Time.time * 1.3f) * 0.002f, Mathf.Sin(Time.time * 1.7f) * 0.003f, 0f);
                Damp(raisedPose, dt, breathe);
                bool fuseNearlyOut = cooking && CookRemaining() <= autoThrowMargin;
                if (!held || fuseNearlyOut) BeginThrow();
                break;
            }

            case State.Throwing:
            {
                Advance(dt, throwTime);
                BlendFrom(releasePose, t * t);          // accelerates into the release
                if (t >= 1f)
                {
                    Release();
                    BeginTimed(State.FollowThrough);
                }
                break;
            }

            case State.FollowThrough:
            {
                Advance(dt, followThroughTime);
                BlendFrom(followThroughPose, Ease(t));
                if (t >= 1f)
                {
                    if (stayEquippedAfterThrow && currentGrenades > 0) BeginTimed(State.Recovering);
                    else BeginTimed(State.PuttingAway);
                }
                break;
            }

            case State.Recovering:
            {
                Advance(dt, recoverTime);
                BlendFrom(StylePose(), Ease(t));
                // The next grenade comes up into the hand once the hand is most of the way back.
                if (t > 0.45f && visual != null && !visual.activeSelf && currentGrenades > 0)
                {
                    visual.SetActive(true);
                    curlTarget = 1f;
                }
                if (t >= 1f) { state = State.Ready; cooking = false; }
                break;
            }

            case State.PuttingAway:
            {
                Advance(dt, putAwayTime);
                BlendFrom(OffscreenPose(), Ease(t));
                if (t >= 1f) Finish(true);
                break;
            }
        }
    }

    bool Advance(float dt, float duration, float speed = 1f)
    {
        t = Mathf.Min(1f, t + dt * speed / Mathf.Max(0.01f, duration));
        return t >= 1f;
    }

    static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

    void BlendFrom(GrenadePose target, float k)
    {
        pos = Vector3.LerpUnclamped(fromPos, target.position, k);
        rot = Quaternion.SlerpUnclamped(fromRot, Quaternion.Euler(target.euler), k);
    }

    void Damp(GrenadePose target, float dt) { Damp(target, dt, Vector3.zero); }

    void Damp(GrenadePose target, float dt, Vector3 extra)
    {
        float k = 1f - Mathf.Exp(-poseSharpness * dt);
        pos = Vector3.Lerp(pos, target.position + extra, k);
        rot = Quaternion.Slerp(rot, Quaternion.Euler(target.euler), k);
    }

    GrenadePose StylePose()
    {
        switch (holdStyle)
        {
            case GrenadeHoldStyle.Chest: return chestPose;
            case GrenadeHoldStyle.Forward: return forwardPose;
            default: return lowPose;
        }
    }

    GrenadePose OffscreenPose()
    {
        GrenadePose s = StylePose();
        return new GrenadePose(s.position + drawOffset, s.euler);
    }

    float CookRemaining() => prefabFuse - (Time.time - cookStart);

    // ---- taking it out -----------------------------------------------------------------------
    IEnumerator TakeOutRoutine()
    {
        state = State.Waiting;

        // The gun goes onto the body with its normal holster animation; the grenade comes up once that is done.
        if (inventory != null) yield return inventory.BeginGrenadeRoutine();
        if (state != State.Waiting) yield break;      // died or was torn down meanwhile

        SetupHand();
        GrenadePose off = OffscreenPose();
        pos = off.position;
        rot = Quaternion.Euler(off.euler);
        curlK = 1f; curlTarget = 1f;
        pressQueued = false; releaseQueued = false; cooking = false;
        drawGunOnExit = false; switchGunOnExit = false;
        shownCount = -1;                      // force the HUD to switch to the grenade count
        BeginTimed(State.Drawing);
        ApplyPose();
    }

    void SetupHand()
    {
        Animator a = animationDriver != null ? animationDriver.BodyAnimator : null;
        if (a == null) a = GetComponentInChildren<Animator>();
        handBone = (a != null && a.isHuman) ? a.GetBoneTransform(HumanBodyBones.RightHand) : null;

        EnsureRig();
        ComputePalm(a);
        BuildVisual();

        if (hands != null)
        {
            hands.SetGripTargets(grip, null);
            if (curlFingers)
            {
                savedPose = new FingerGripPose
                {
                    thumbCurl = hands.rightGripPose.thumbCurl, indexCurl = hands.rightGripPose.indexCurl,
                    middleCurl = hands.rightGripPose.middleCurl, ringCurl = hands.rightGripPose.ringCurl,
                    pinkyCurl = hands.rightGripPose.pinkyCurl
                };
                savedOverride = hands.overrideRightFingers;
                fingersSaved = true;
                hands.overrideRightFingers = true;
                TickFingers(0f);
            }
        }
    }

    void EnsureRig()
    {
        if (rig == null)
        {
            rig = new GameObject("GrenadeHold").transform;
            grip = new GameObject("RightHandGrip").transform;
            grip.SetParent(rig, false);
        }
        if (rig.parent != View) rig.SetParent(View, false);
    }

    // Where the palm is, in the hand bone's own frame, taken from the finger bones so it doesn't depend on which way
    // this rig's bone axes happen to point.
    void ComputePalm(Animator a)
    {
        havePalmBones = false;
        if (a == null || !a.isHuman || handBone == null) return;

        Transform mid = a.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        Transform idx = a.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        Transform lit = a.GetBoneTransform(HumanBodyBones.RightLittleProximal);
        if (mid == null || idx == null || lit == null) return;

        Vector3 fwd = handBone.InverseTransformDirection(mid.position - handBone.position);
        Vector3 across = handBone.InverseTransformDirection(idx.position - lit.position);
        Vector3 n = Vector3.Cross(fwd, across);
        if (n.sqrMagnitude < 1e-8f) return;
        n.Normalize();

        // Which side is the palm? Fingers rest slightly curled TOWARDS the palm, so the middle finger's tip joint
        // sits on the palm side of the line through its first two joints. If the hand is too straight to tell,
        // fall back on the usual T-pose assumption (palms face down).
        float side = 0f;
        Transform mi = a.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate);
        Transform md = a.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
        if (mi != null && md != null)
        {
            Vector3 d = (mi.position - mid.position).normalized;
            Vector3 toTip = md.position - mid.position;
            Vector3 dev = toTip - d * Vector3.Dot(toTip, d);
            if (dev.magnitude > 0.004f) side = Vector3.Dot(handBone.InverseTransformDirection(dev), n);
        }
        if (Mathf.Abs(side) < 1e-5f) side = Vector3.Dot(n, handBone.InverseTransformDirection(Vector3.down));
        if (side < 0f) n = -n;

        palmFwd = fwd;
        palmNormal = n;
        havePalmBones = true;
    }

    Vector3 PalmVec()
    {
        if (!havePalmBones) return fallbackPalmOffset;
        return palmFwd * palmForward + palmNormal * (flipPalmSide ? -palmLift : palmLift);
    }

    void BuildVisual()
    {
        if (visual != null) Destroy(visual);

        GameObject src = heldGrenadeVisual != null ? heldGrenadeVisual : grenadePrefab;
        GameObject go = src != null ? Instantiate(src) : null;
        if (go == null) go = MakeFallbackGrenade();

        StripProp(go);
        visualBaseScale = go.transform.lossyScale.x;
        go.transform.SetParent(handBone != null ? handBone : rig, false);
        visual = go;
        PlaceVisual();
    }

    void PlaceVisual()
    {
        if (visual == null) return;
        Transform parent = visual.transform.parent;
        if (parent == null) return;

        Vector3 s = parent.lossyScale;
        float inv = 1f / Mathf.Max(0.0001f, s.x);
        visual.transform.localScale = Vector3.one * (visualBaseScale * visualScale * inv);
        visual.transform.localRotation = Quaternion.Euler(visualEuler);
        visual.transform.localPosition = (handBone != null ? PalmVec() * inv : Vector3.zero);
    }

    GameObject MakeFallbackGrenade()
    {
        var root = new GameObject("HeldGrenade");
        var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = new Vector3(0.065f, 0.08f, 0.065f);
        var lever = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        lever.transform.SetParent(root.transform, false);
        lever.transform.localPosition = new Vector3(0f, 0.045f, 0f);
        lever.transform.localScale = new Vector3(0.025f, 0.01f, 0.025f);
        foreach (var r in root.GetComponentsInChildren<Renderer>()) r.material.color = new Color(0.25f, 0.3f, 0.2f);
        return root;
    }

    // It's a prop in the hand: no physics, no explosive, no colliders, no effects.
    static void StripProp(GameObject prop)
    {
        foreach (var mb in prop.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
        foreach (var c in prop.GetComponentsInChildren<Collider>(true)) { c.enabled = false; Destroy(c); }
        foreach (var rb in prop.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; Destroy(rb); }
        foreach (var ps in prop.GetComponentsInChildren<ParticleSystem>(true)) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        foreach (var au in prop.GetComponentsInChildren<AudioSource>(true)) au.enabled = false;
    }

    // ---- per-frame outputs -------------------------------------------------------------------
    void ApplyPose()
    {
        if (rig == null || View == null) return;
        if (rig.parent != View) rig.SetParent(View, false);

        rig.localPosition = pos;
        rig.localRotation = rot;

        // The hand bone's origin is the wrist; put it where it has to be for the grenade (the palm point) to land on `pos`.
        Quaternion g = Quaternion.Euler(gripEuler);
        grip.localRotation = g;
        grip.localPosition = -(g * PalmVec());

        PlaceVisual();
    }

    void TickFingers(float dt)
    {
        curlK = Mathf.MoveTowards(curlK, curlTarget, 6f * dt);
        if (hands == null || !curlFingers || !fingersSaved) return;
        var p = hands.rightGripPose;
        p.thumbCurl = fingerCurl.thumbCurl * curlK;
        p.indexCurl = fingerCurl.indexCurl * curlK;
        p.middleCurl = fingerCurl.middleCurl * curlK;
        p.ringCurl = fingerCurl.ringCurl * curlK;
        p.pinkyCurl = fingerCurl.pinkyCurl * curlK;
    }

    void UpdateTwist(float dt)
    {
        var torso = TorsoPoseDriver.Instance;
        if (torso == null) return;

        float target;
        switch (state)
        {
            case State.Raising: target = Mathf.Lerp(carryTwist, windUpTwist, Ease(t)); break;
            case State.Raised: target = windUpTwist; break;
            case State.Throwing: target = Mathf.Lerp(windUpTwist, throwTwist, Ease(t)); break;
            case State.FollowThrough: target = Mathf.Lerp(throwTwist, 0f, Ease(t)); break;
            case State.Drawing:
            case State.Ready:
            case State.Recovering: target = carryTwist; break;
            default: target = 0f; break;
        }
        twistNow = target;
        torso.SetExternalTwist(twistNow);
    }

    // ---- the throw ---------------------------------------------------------------------------
    void BeginThrow()
    {
        BeginTimed(State.Throwing);
    }

    Vector3 ThrowVelocity()
    {
        Vector3 v = View.forward * throwForce + Vector3.up * throwUpward;
        if (cc != null && inheritMovement > 0f)
        {
            Vector3 pv = cc.velocity; pv.y = 0f;
            v += pv * inheritMovement;
        }
        return v;
    }

    void Release()
    {
        if (grenadePrefab == null) return;

        currentGrenades = Mathf.Max(0, currentGrenades - 1);

        Vector3 eye = View.position;
        Vector3 start = visual != null ? visual.transform.position : View.TransformPoint(releasePose.position);
        // Hand behind a wall or railing: leave the grenade on this side of it.
        if (FirstHit(eye, start, out Vector3 wallPoint)) start = wallPoint - (start - eye).normalized * 0.12f;

        GameObject grenade = Instantiate(grenadePrefab, start, View.rotation);

        Rigidbody rb = grenade.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = ThrowVelocity();
            rb.AddTorque(Random.insideUnitSphere * 5f, ForceMode.Impulse);
        }

        // Time spent cooking comes off the fuse.
        var explosive = grenade.GetComponent<Explosive>();
        if (explosive != null && cooking)
            explosive.fuseTime = Mathf.Max(0.35f, explosive.fuseTime - (Time.time - cookStart));
        cooking = false;

        // It starts at the hand, which is inside the player's own capsule - don't collide with ourselves for a moment.
        StartCoroutine(IgnoreOwnCollidersBriefly(grenade, 0.4f));

        if (visual != null) visual.SetActive(false);
        curlTarget = 0.15f;                 // hand opens as it lets go
    }

    IEnumerator IgnoreOwnCollidersBriefly(GameObject grenade, float seconds)
    {
        var theirs = grenade.GetComponentsInChildren<Collider>();
        SetIgnore(theirs, true);
        yield return new WaitForSeconds(seconds);
        if (grenade != null) SetIgnore(theirs, false);
    }

    void SetIgnore(Collider[] theirs, bool ignore)
    {
        if (ownColliders == null) return;
        foreach (var a in theirs)
        {
            if (a == null) continue;
            foreach (var b in ownColliders)
                if (b != null && b.enabled && b.gameObject.activeInHierarchy) Physics.IgnoreCollision(a, b, ignore);
        }
    }

    // First thing between a and b that isn't part of the player.
    bool FirstHit(Vector3 a, Vector3 b, out Vector3 point)
    {
        point = b;
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 1e-4f) return false;
        int n = Physics.RaycastNonAlloc(a, d / len, hitBuffer, len, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; bool found = false;
        for (int i = 0; i < n; i++)
        {
            var h = hitBuffer[i];
            if (h.collider == null || h.collider.transform.IsChildOf(transform)) continue;
            if (h.distance < best) { best = h.distance; point = h.point; found = true; }
        }
        return found;
    }

    // ---- aim preview -------------------------------------------------------------------------
    void UpdateTrajectory()
    {
        bool show = showTrajectory && (state == State.Raised || (state == State.Raising && t > 0.6f));
        if (!show)
        {
            if (line != null && line.enabled) line.enabled = false;
            return;
        }

        if (line == null && !BuildLine()) { showTrajectory = false; return; }

        Vector3 p = View.TransformPoint(releasePose.position);
        Vector3 v = ThrowVelocity();
        Vector3 g = Physics.gravity;
        float dt = Mathf.Max(0.01f, trajectoryStep);
        int max = Mathf.Clamp(trajectoryMaxPoints, 2, linePoints.Length);

        int count = 0;
        linePoints[count++] = p;
        while (count < max)
        {
            Vector3 next = p + v * dt + 0.5f * g * dt * dt;
            v += g * dt;
            if (FirstHit(p, next, out Vector3 hit)) { linePoints[count++] = hit; break; }
            linePoints[count++] = next;
            p = next;
        }

        line.enabled = true;
        line.positionCount = count;
        line.SetPositions(linePoints);
    }

    bool BuildLine()
    {
        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) return false;

        var go = new GameObject("GrenadeTrajectory");
        go.transform.SetParent(transform, false);
        line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.widthMultiplier = 0.025f;
        line.numCapVertices = 4;
        line.material = new Material(sh);
        line.startColor = trajectoryColor;
        line.endColor = new Color(trajectoryColor.r, trajectoryColor.g, trajectoryColor.b, 0.15f);
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.enabled = false;
        linePoints = new Vector3[Mathf.Max(2, trajectoryMaxPoints)];
        return true;
    }

    // ---- put away / abort --------------------------------------------------------------------
    // resumeWeapon = true: normal put away (gun comes back). false: the player died, nothing is drawn.
    void Finish(bool resumeWeapon)
    {
        if (state == State.Off) return;
        state = State.Off;
        pressQueued = false; releaseQueued = false; cooking = false;

        if (hands != null) hands.SetGripTargets(null, null);
        if (hands != null && fingersSaved)
        {
            var p = hands.rightGripPose;
            p.thumbCurl = savedPose.thumbCurl; p.indexCurl = savedPose.indexCurl; p.middleCurl = savedPose.middleCurl;
            p.ringCurl = savedPose.ringCurl; p.pinkyCurl = savedPose.pinkyCurl;
            hands.overrideRightFingers = savedOverride;
        }
        fingersSaved = false;

        if (visual != null) { Destroy(visual); visual = null; }
        if (line != null) line.enabled = false;
        if (TorsoPoseDriver.Instance != null) TorsoPoseDriver.Instance.SetExternalTwist(0f);
        if (hud != null && shownCount >= 0) hud.RestoreAmmo();   // before the gun is drawn - the gun then writes its own ammo
        shownCount = -1;
        bool drawGun = drawGunOnExit, switchGun = switchGunOnExit;
        drawGunOnExit = false; switchGunOnExit = false;

        if (inventory != null)
        {
            if (resumeWeapon) inventory.EndGrenade(drawGun, switchGun);
            else inventory.AbortGrenade();
        }
    }

    // ---- public API (unchanged) --------------------------------------------------------------
    public int GetCurrentGrenades() => currentGrenades;

    /// <summary>Drops one grenade as a pickup (WeaponDrop calls this when the drop key is pressed with a grenade in hand and no weapon out).
    /// Only while the grenade is simply being carried - not mid-throw. False if nothing was dropped.</summary>
    public bool DropHeldGrenade()
    {
        if (state != State.Ready || currentGrenades <= 0) return false;
        if (pickupPrefab == null)
        {
            Debug.LogWarning("GrenadeController has no Pickup Prefab set, so a grenade can't be dropped.", this);
            return false;
        }

        Transform v = View != null ? View : transform;
        Vector3 pos = visual != null ? visual.transform.position : v.position + v.forward * 0.5f;
        GameObject world = Instantiate(pickupPrefab, pos, Quaternion.identity);
        Rigidbody rb = world.GetComponent<Rigidbody>();
        if (rb != null) rb.linearVelocity = v.forward * 2f + Vector3.up;

        currentGrenades--;
        if (currentGrenades <= 0) BeginTimed(State.PuttingAway);   // empty hand: put it away
        return true;
    }
    /// <summary>True while there is room for more grenades (what a GrenadePickup checks).</summary>
    public bool CanCarryMore => currentGrenades < maxGrenades;
    /// <summary>Adds grenades up to maxGrenades; returns how many were actually taken.</summary>
    public int AddGrenades(int amount)
    {
        int add = Mathf.Clamp(amount, 0, maxGrenades - currentGrenades);
        currentGrenades += add;
        return add;
    }
    public int GetMaxGrenades() => maxGrenades;
    /// <summary>True from the moment a grenade is taken out until it is put away - what the weapon inventory checks.</summary>
    public bool IsHoldingGrenade() => state != State.Off;
    /// <summary>True while the arm is raised (or going up) with the button held - handy for a crosshair/HUD change.</summary>
    public bool IsAimingGrenade() => state == State.Raising || state == State.Raised;

    void OnDisable()
    {
        if (state != State.Off) Finish(false);
    }

    void OnDestroy()
    {
        if (playerHealth != null && deathHandler != null) playerHealth.onDeath -= deathHandler;
        equipAction?.Disable();
        throwAction?.Disable();
        styleAction?.Disable();
        gunAction?.Disable();
        switchGunAction?.Disable();
        if (rig != null) Destroy(rig.gameObject);
    }
}