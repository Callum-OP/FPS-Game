using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player shove: right mouse button. A full-body move (the base-layer ShoveLight / ShoveHeavy states built by
/// AnimationSystemBuilder) that staggers, damages and pushes back whatever is in front of you.
///
///  - TAP  = light shove (a punch, right hand). Longer reach and a wide cone, modest push. Takes about five
///           hits to kill something at full health.
///  - HOLD = heavy shove (a kick). Shorter reach and a narrow cone (it needs aiming), but it knocks back much
///           further and hits harder - about three hits at full health. It fires as soon as the hold time is
///           reached, without waiting for the button to be released.
///
/// The target plays a stagger clip for the side the shove came from (see Health.PushBack).
/// Works with any weapon held or bare-handed, like PlayerMelee.
/// </summary>
public class PlayerShove : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("Seconds the button must be held to turn the tap into the heavy shove.")]
    public float holdTime = 0.28f;

    [Header("Light shove (tap)")]
    public float lightRange = 2f;
    [Tooltip("Half-angle (degrees) around where you are looking that a target can be in.")]
    public float lightAngle = 70f;
    [Tooltip("Damage as a fraction of the target's max health. 0.2 = five hits from full health.")]
    [Range(0f, 1f)] public float lightDamageFraction = 0.2f;
    public float lightPushDistance = 2.5f;
    public float lightStagger = 0.9f;
    public float lightCooldown = 0.8f;

    [Header("Heavy shove (hold)")]
    public float heavyRange = 1.4f;
    public float heavyAngle = 30f;
    [Tooltip("Damage as a fraction of the target's max health. 0.4 = three hits from full health (the third finishes it).")]
    [Range(0f, 1f)] public float heavyDamageFraction = 0.4f;
    public float heavyPushDistance = 4.5f;
    public float heavyStagger = 1.2f;
    public float heavyCooldown = 1.3f;

    [Header("References")]
    public CharacterAnimationDriver animationDriver; // auto-found in children if empty

    InputAction shoveAction;
    Camera cam;
    float cooldownTimer;
    bool holding;
    float heldFor;

    void Awake()
    {
        shoveAction = new InputAction("Shove", binding: PlayerInputMap.Shove);
        shoveAction.Enable();
        cam = GetComponentInChildren<Camera>();
    }

    void Update()
    {
        cooldownTimer -= Time.deltaTime;

        if (!holding)
        {
            if (cooldownTimer <= 0f && shoveAction.WasPressedThisFrame()) { holding = true; heldFor = 0f; }
            return;
        }

        heldFor += Time.deltaTime;
        if (heldFor >= holdTime) { Fire(true); holding = false; }          // held long enough: heavy, straight away
        else if (!shoveAction.IsPressed()) { Fire(false); holding = false; } // released early: light
    }

    void Fire(bool heavy)
    {
        cooldownTimer = heavy ? heavyCooldown : lightCooldown;
        if (animationDriver == null) animationDriver = GetComponentInChildren<CharacterAnimationDriver>();
        animationDriver?.PlayShove(heavy);

        // The hit lands when the limb actually connects, not on the button press - same idea as PlayerMelee's strike delay.
        StartCoroutine(PushAfter(CharacterAnimationDriver.ShoveStrikeDelayFor(heavy), heavy));
    }

    IEnumerator PushAfter(float delay, bool heavy)
    {
        yield return new WaitForSeconds(delay);

        float range = heavy ? heavyRange : lightRange;
        float maxAngle = heavy ? heavyAngle : lightAngle;

        Transform eye = ThirdPersonMode.Active && ThirdPersonMode.Eye != null ? ThirdPersonMode.Eye : (cam != null ? cam.transform : null);
        Vector3 origin = eye != null ? eye.position : transform.position + Vector3.up * 1.5f;
        Vector3 dir = eye != null ? eye.forward : transform.forward;
        Vector3 flatDir = Vector3.ProjectOnPlane(dir, Vector3.up);
        if (flatDir.sqrMagnitude < 0.0001f) flatDir = transform.forward;
        flatDir.Normalize();

        var hit = new HashSet<Health>();
        foreach (var col in Physics.OverlapSphere(origin + dir * range * 0.6f, range * 0.7f))
        {
            if (col.transform.IsChildOf(transform)) continue;
            var h = col.GetComponentInParent<Health>();
            if (h == null || hit.Contains(h)) continue;

            Vector3 away = h.transform.position - transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = flatDir;
            if (Vector3.Angle(flatDir, away) > maxAngle) continue;   // outside the cone (the heavy shove has to be aimed)
            hit.Add(h);

            float damage = h.maxHealth * (heavy ? heavyDamageFraction : lightDamageFraction);
            // Push first, then damage: a shove that kills should still register as a shove (the ragdoll takes over).
            h.PushBack(away.normalized, heavy ? heavyPushDistance : lightPushDistance, heavy ? heavyStagger : lightStagger);
            if (damage > 0f) h.TakeDamage(damage);
        }
    }

    void OnDestroy() => shoveAction.Disable();
}