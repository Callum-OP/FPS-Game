using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Several ways of HOLDING the same gun - where it sits on screen and how it is angled - switchable live.
///
/// The weapon's own WeaponADS hip pose and LowerWeapon lowered pose stay exactly as they are and are the style
/// called "Slanted Middle" (<see cref="slantedMiddleName"/>). Every entry in <see cref="styles"/> is another full hold: its own hip (held) position/rotation and,
/// optionally, its own lowered position/rotation. Aiming down sights is NEVER touched - the ADS pose, ADS FOV and the
/// aim speed come from WeaponADS exactly as before, so every style aims identically; only how the gun rests at the
/// hip and how it sits lowered changes.
///
/// ORDER: with <see cref="slantedMiddleLast"/> on (the default) the styles come first and Slanted Middle is last, so
/// the first style - "Straight Right" unless you add your own - is what everyone starts with (style number 0). Turn
/// it off to put Slanted Middle first again.
///
/// PLAYER: press the HoldStyle key (6, see PlayerInputMap) to cycle through the styles and back round.
/// The change eases over ~0.3s. The choice is remembered across weapons and between sessions (PlayerPrefs). A weapon
/// with fewer styles than the selected number just wraps round.
///
/// ENEMIES AND ALLIES: set EnemyWeapon.holdStyle on the character (same numbering as the cycle: 0 is the first
/// style). No key, same smooth blending, so each enemy type or ally can hold a gun its own way. For the AI, "lowered"
/// is the difference between the style's lowered pose and Slanted Middle's lowered pose, applied on top of the AI's own lowered pose (EnemyWeapon.loweredPositionOffset...), so
/// the AI's tuning is kept and only the style's change is added.
///
/// ADDING A STYLE: on the weapon prefab (this component sits next to WeaponADS - it is added automatically if
/// missing) open Styles, add an element, set its Hip Position / Hip Rotation (same meaning as WeaponADS's hipPosition /
/// hipRotation - tune them live in Play Mode), and tick Override Lowered to give it its own Lowered Position / Rotation
/// (same meaning as LowerWeapon's loweredPosition [Y is the height] / loweredRotation). Leave Override Lowered off and
/// it simply lowers like Slanted Middle.
///
/// With Styles left empty a single starter style, "Straight Right" (gun upright, a little to the right), is offered
/// so the system works out of the box. Add your own elements and the starter goes away.
/// </summary>
[DefaultExecutionOrder(-96)]
public class WeaponHoldStyles : MonoBehaviour
{
    [Serializable]
    public class Style
    {
        public string name = "New Style";

        [Header("Held (hip) - same meaning as WeaponADS.hipPosition / hipRotation")]
        public Vector3 hipPosition;
        public Vector3 hipRotation;

        [Header("Lowered - same meaning as LowerWeapon.loweredPosition (Y = height) / loweredRotation")]
        [Tooltip("Off = this style lowers exactly like Default. On = use the lowered pose below.")]
        public bool overrideLowered;
        public Vector3 loweredPosition = new Vector3(0f, -0.4f, 0.3f);
        public Vector3 loweredRotation = new Vector3(30f, 0f, 0f);
    }

    [Tooltip("Extra hold styles. The weapon's own WeaponADS/LowerWeapon values are always there too, as Slanted Middle, and are not listed here.")]
    public Style[] styles = new Style[0];

    [Tooltip("What the weapon's own pose (WeaponADS hip pose / LowerWeapon lowered pose) is called.")]
    public string slantedMiddleName = "Slanted Middle";
    [Tooltip("On: the extra styles come first and Slanted Middle last, so the first style (Straight Right by default) is the one everyone starts with. Off: Slanted Middle is first.")]
    public bool slantedMiddleLast = true;

    [Tooltip("While Styles is empty, offer one starter style - Straight Right - so there is something to switch to straight away.")]
    public bool offerStarterStyle = true;
    [Tooltip("Straight Right: how far right of Slanted Middle it sits (local X, metres). Smaller = nearer the middle; 0.09 is a touch left of the old 0.12. Try 0.06-0.10.")]
    public float straightRightOffsetX = 0.09f;

    [Tooltip("How fast a change of style eases in (per second; ~7 is about 0.3s).")]
    public float transitionSpeed = 7f;
    [Tooltip("Show the name of the style on screen for a moment after switching (player only). Has no effect while OnGUI is commented out.")]
    public bool showLabel = true;

    /// <summary>The player's chosen style number, shared by every weapon so it survives switching guns.</summary>
    public static int PlayerStyle
    {
        get { if (!loaded) { playerStyle = PlayerPrefs.GetInt(PrefKey, 0); loaded = true; } return playerStyle; }
        set { playerStyle = value; loaded = true; PlayerPrefs.SetInt(PrefKey, value); }
    }
    const string PrefKey = "WeaponHoldStyle2"; // new key: style numbers were reordered, so an old saved choice would land on the wrong style
    static int playerStyle;
    static bool loaded;

    bool playerControlled = true;
    InputAction cycleAction;
    int index;
    float labelUntil;
    string labelText = "";

    // Smoothed state. Each track advances once per frame however many callers ask.
    Vector3 hipPos, hipRot, lowPos, lowRot;
    bool hipInit, lowInit;
    int hipFrame = -1, lowFrame = -1;
    LowerWeapon lowerWeapon;

    // ---------------------------------------------------------------------------------------------
    // Selection
    // ---------------------------------------------------------------------------------------------
    bool UsesStarter => offerStarterStyle && (styles == null || styles.Length == 0);

    /// <summary>Number of styles including Default.</summary>
    public int Count => 1 + (styles != null ? styles.Length : 0) + (UsesStarter ? 1 : 0);

    /// <summary>Selected style number as listed/cycled (0 = first - Straight Right unless reordered).</summary>
    public int Index => index;

    // Listed number -> internal pose: 0 = the weapon's own pose (Slanted Middle), 1.. = the extra styles / starter.
    int PoseOf(int listed)
    {
        int n = Count;
        if (slantedMiddleLast && n > 1) return listed >= n - 1 ? 0 : listed + 1;
        return listed;
    }

    public string StyleName(int listed)
    {
        int pose = PoseOf(listed);
        if (pose <= 0) return string.IsNullOrEmpty(slantedMiddleName) ? "Slanted Middle" : slantedMiddleName;
        if (UsesStarter) return "Straight Right";
        int k = pose - 1;
        return styles != null && k < styles.Length && !string.IsNullOrEmpty(styles[k].name) ? styles[k].name : "Style " + pose;
    }

    /// <summary>Choose a style (wraps round). The pose eases there; pass snap to jump straight to it.</summary>
    public void SetIndex(int i, bool snap = false)
    {
        int n = Mathf.Max(1, Count);
        i = ((i % n) + n) % n;
        if (i == index && !snap) return;
        index = i;
        if (snap) { hipInit = false; lowInit = false; }
    }

    public void Cycle()
    {
        SetIndex(index + 1);
        if (playerControlled)
        {
            PlayerStyle = index;
            labelText = "Hold style: " + StyleName(index);
            labelUntil = Time.unscaledTime + 1.5f;
        }
    }

    /// <summary>Turns off the player's key (EnemyWeapon calls this - an AI's style comes from EnemyWeapon.holdStyle).</summary>
    public void SetPlayerControlled(bool on)
    {
        playerControlled = on;
        if (!on) { cycleAction?.Disable(); }
        else if (cycleAction != null && isActiveAndEnabled) cycleAction.Enable();
    }

    void Awake()
    {
        cycleAction = new InputAction("HoldStyle", binding: PlayerInputMap.HoldStyle);
        lowerWeapon = GetComponentInChildren<LowerWeapon>(true);
        if (lowerWeapon == null) lowerWeapon = GetComponentInParent<LowerWeapon>();
    }

    void OnEnable()
    {
        if (playerControlled)
        {
            cycleAction.Enable();
            SetIndex(PlayerStyle, snap: true); // arrive already in the chosen style (e.g. drawing the other gun)
        }
    }

    void OnDisable() => cycleAction?.Disable();
    void OnDestroy() => cycleAction?.Dispose();

    void Update()
    {
        if (!playerControlled) return;
        if (cycleAction.WasPressedThisFrame()) Cycle();
        else if (PlayerStyle != index) SetIndex(PlayerStyle); // another weapon changed it while this one was holstered
    }

    // On-screen style name: commented out for now to see how it looks without it. Uncomment to bring it back
    // (labelText / labelUntil are still set whenever the style changes).
    //void OnGUI()
    //{
    //    if (!showLabel || !playerControlled || Time.unscaledTime > labelUntil) return;
    //    var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
    //    style.normal.textColor = Color.white;
    //    var rect = new Rect(0f, Screen.height - 90f, Screen.width, 30f);
    //    GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), labelText, new GUIStyle(style) { normal = { textColor = Color.black } });
    //    GUI.Label(rect, labelText, style);
    //}

    // ---------------------------------------------------------------------------------------------
    // Poses
    // ---------------------------------------------------------------------------------------------
    static Vector3 LerpEuler(Vector3 a, Vector3 b, float k) =>
        new Vector3(Mathf.LerpAngle(a.x, b.x, k), Mathf.LerpAngle(a.y, b.y, k), Mathf.LerpAngle(a.z, b.z, k));

    float StepK() => 1f - Mathf.Exp(-Mathf.Max(0.01f, transitionSpeed) * Time.deltaTime);

    void HipTarget(Vector3 defPos, Vector3 defRot, out Vector3 pos, out Vector3 rot)
    {
        pos = defPos; rot = defRot;
        int pose = PoseOf(index);
        if (pose <= 0) return;
        if (UsesStarter)
        {
            pos = defPos + new Vector3(straightRightOffsetX, 0f, 0f);
            rot = new Vector3(defRot.x, defRot.y, 0f); // upright instead of rolled over
            return;
        }
        int k = pose - 1;
        if (styles == null || k >= styles.Length) return;
        pos = styles[k].hipPosition; rot = styles[k].hipRotation;
    }

    void LoweredTarget(Vector3 defPos, Vector3 defRot, out Vector3 pos, out Vector3 rot)
    {
        pos = defPos; rot = defRot;
        int pose = PoseOf(index);
        if (pose <= 0 || UsesStarter) return;
        int k = pose - 1;
        if (styles == null || k >= styles.Length || !styles[k].overrideLowered) return;
        pos = styles[k].loweredPosition; rot = styles[k].loweredRotation;
    }

    /// <summary>The held (hip) pose to use right now, eased between styles. defPos/defRot are the weapon's own
    /// WeaponADS hipPosition/hipRotation (what Default means).</summary>
    public void GetHip(Vector3 defPos, Vector3 defRot, out Vector3 pos, out Vector3 rot)
    {
        HipTarget(defPos, defRot, out Vector3 tp, out Vector3 tr);
        if (!hipInit) { hipPos = tp; hipRot = tr; hipInit = true; hipFrame = Time.frameCount; }
        else if (hipFrame != Time.frameCount)
        {
            hipFrame = Time.frameCount;
            float k = StepK();
            hipPos = Vector3.Lerp(hipPos, tp, k);
            hipRot = LerpEuler(hipRot, tr, k);
        }
        pos = hipPos; rot = hipRot;
    }

    /// <summary>The lowered pose to use right now, eased between styles. defPos/defRot are LowerWeapon's own
    /// lowered values (position Y = height).</summary>
    public void GetLowered(Vector3 defPos, Vector3 defRot, out Vector3 pos, out Vector3 rot)
    {
        LoweredTarget(defPos, defRot, out Vector3 tp, out Vector3 tr);
        if (!lowInit) { lowPos = tp; lowRot = tr; lowInit = true; lowFrame = Time.frameCount; }
        else if (lowFrame != Time.frameCount)
        {
            lowFrame = Time.frameCount;
            float k = StepK();
            lowPos = Vector3.Lerp(lowPos, tp, k);
            lowRot = LerpEuler(lowRot, tr, k);
        }
        pos = lowPos; rot = lowRot;
    }

    /// <summary>For AI: how far the style's lowered pose differs from Default's (position, euler degrees), eased.
    /// Zero for Default and for styles that do not override the lowered pose.</summary>
    public void GetLoweredDelta(out Vector3 posDelta, out Vector3 rotDelta)
    {
        posDelta = Vector3.zero; rotDelta = Vector3.zero;
        if (lowerWeapon == null)
        {
            lowerWeapon = GetComponentInChildren<LowerWeapon>(true);
            if (lowerWeapon == null) lowerWeapon = GetComponentInParent<LowerWeapon>();
            if (lowerWeapon == null) return;
        }
        Vector3 defPos = new Vector3(lowerWeapon.loweredPosition.x, lowerWeapon.loweredHeight, lowerWeapon.loweredPosition.z);
        Vector3 defRot = lowerWeapon.loweredRotation;
        GetLowered(defPos, defRot, out Vector3 p, out Vector3 r);
        posDelta = p - defPos;
        rotDelta = new Vector3(Mathf.DeltaAngle(defRot.x, r.x), Mathf.DeltaAngle(defRot.y, r.y), Mathf.DeltaAngle(defRot.z, r.z));
    }
}
