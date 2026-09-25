using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player shove: right mouse button. A full-body kick (see AnimationSystemBuilder's "Shove"
/// state - unlike PlayerMelee's swings this isn't masked to the arms, because a kick needs the
/// legs) that staggers and pushes back anything in front of you, rather than trying to kill it -
/// crowd control, not a second melee weapon. Works with any weapon held or bare-handed, exactly
/// like PlayerMelee.
///
/// Right mouse used to be SlowMotion's key; that moved to PlayerInputMap.SlowMo (C) to make room
/// for this.
/// </summary>
public class PlayerShove : MonoBehaviour
{
    [Header("Shove")]
    public float range = 2f;
    [Tooltip("Light, mostly to sell the impact - this is a push, not an attack. Set to 0 for a pure knockback with no damage.")]
    public float damage = 5f;
    public float pushDistance = 2.5f;
    public float staggerDuration = 0.7f;
    public float cooldown = 1f;

    [Header("References")]
    public CharacterAnimationDriver animationDriver; // auto-found in children if empty

    InputAction shoveAction;
    Camera cam;
    float timer;

    void Awake()
    {
        shoveAction = new InputAction("Shove", binding: PlayerInputMap.Shove);
        shoveAction.Enable();
        cam = GetComponentInChildren<Camera>();
    }

    void Update()
    {
        timer -= Time.deltaTime;
        if (timer > 0f || !shoveAction.WasPressedThisFrame()) return;
        timer = cooldown;

        if (animationDriver == null) animationDriver = GetComponentInChildren<CharacterAnimationDriver>();
        animationDriver?.PlayShove();

        // The push lands when the kick actually connects, not on the button press - same idea as
        // PlayerMelee's strike delay.
        StartCoroutine(PushAfter(CharacterAnimationDriver.ShoveStrikeDelay));
    }

    IEnumerator PushAfter(float delay)
    {
        yield return new WaitForSeconds(delay);

        Transform eye = ThirdPersonMode.Active && ThirdPersonMode.Eye != null ? ThirdPersonMode.Eye : (cam != null ? cam.transform : null);
        Vector3 origin = eye != null ? eye.position : transform.position + Vector3.up * 1.5f;
        Vector3 dir = eye != null ? eye.forward : transform.forward;

        var hit = new HashSet<Health>();
        foreach (var col in Physics.OverlapSphere(origin + dir * range * 0.6f, range * 0.7f))
        {
            if (col.transform.IsChildOf(transform)) continue;
            var h = col.GetComponentInParent<Health>();
            if (h == null || hit.Contains(h)) continue;
            hit.Add(h);

            if (damage > 0f) h.TakeDamage(damage);

            Vector3 away = h.transform.position - transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = dir;
            h.PushBack(away.normalized, pushDistance, staggerDuration);
        }
    }

    void OnDestroy() => shoveAction.Disable();
}
