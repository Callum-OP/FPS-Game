using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Player melee attack: V key or middle mouse. Plays the masked upper-body
/// swing (Melee trigger on CharacterAnimationDriver), damages and briefly stuns
/// enemies in front of the camera.</summary>
public class PlayerMelee : MonoBehaviour
{
    [Header("Attack")]
    public float range = 2.4f;
    public float damage = 40f;
    public float cooldown = 0.8f;
    public float stunDuration = 1.2f;

    [Header("References")]
    public CharacterAnimationDriver animationDriver; // auto-found in children if empty
    public UpperBodyPose legacyBodyPose;              // optional fallback for older rigs

    InputAction meleeAction;
    Camera cam;
    float timer;

    void Awake()
    {
        meleeAction = new InputAction("Melee", binding: "<Keyboard>/v");
        meleeAction.AddBinding("<Mouse>/middleButton");
        meleeAction.Enable();
        cam = GetComponentInChildren<Camera>();
    }

    void Update()
    {
        timer -= Time.deltaTime;
        if (timer > 0f || !meleeAction.WasPressedThisFrame()) return;
        timer = cooldown;

        if (animationDriver == null) animationDriver = GetComponentInChildren<CharacterAnimationDriver>();
        if (animationDriver != null)
            animationDriver.PlayMelee();
        else
        {
            if (legacyBodyPose == null) legacyBodyPose = GetComponentInChildren<UpperBodyPose>();
            legacyBodyPose?.TriggerMelee();
        }

        Vector3 origin = cam != null ? cam.transform.position : transform.position + Vector3.up * 1.5f;
        Vector3 dir = cam != null ? cam.transform.forward : transform.forward;

        var hit = new HashSet<Health>();
        foreach (var col in Physics.OverlapSphere(origin + dir * range * 0.6f, range * 0.7f))
        {
            if (col.transform.IsChildOf(transform)) continue;
            var h = col.GetComponentInParent<Health>();
            if (h == null || hit.Contains(h)) continue;
            hit.Add(h);
            h.TakeDamage(damage);
            var ai = h.GetComponent<EnemyAI>();
            if (ai != null) ai.Stun(stunDuration);
        }
    }

    void OnDestroy() { meleeAction.Disable(); }
}