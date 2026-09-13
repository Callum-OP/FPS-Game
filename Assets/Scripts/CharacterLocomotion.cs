using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drives a Humanoid Animator's locomotion from whatever is actually moving the
/// character — a NavMeshAgent (enemies) or a CharacterController (player). Falls
/// back to measuring transform movement if neither is present.
///
/// Animator setup (see LocomotionBuilder): a float "Speed" (0 idle, 0.5 walk,
/// 1 run) feeding a 1D blend tree, and a bool "Dead".
/// Put this on the rigged body that has the Animator.
/// </summary>
[RequireComponent(typeof(Animator))]
public class CharacterLocomotion : MonoBehaviour
{
    public Animator animator;
    public NavMeshAgent agent;
    public CharacterController controller;

    [Tooltip("Planar speed (m/s) that maps to a full run (Speed = 1).")]
    public float runSpeed = 4f;
    [Tooltip("Smoothing time for the Speed parameter.")]
    public float damping = 0.12f;

    Vector3 lastPos;
    float speedParam;
    static readonly int SpeedHash = Animator.StringToHash("Speed");
    static readonly int DeadHash = Animator.StringToHash("Dead");

    void Reset() { animator = GetComponent<Animator>(); }

    void Awake()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (agent == null) agent = GetComponentInParent<NavMeshAgent>();
        if (controller == null) controller = GetComponentInParent<CharacterController>();
        lastPos = transform.position;
    }

    void Update()
    {
        float planar;
        if (agent != null && agent.enabled && !agent.isStopped)
            planar = new Vector2(agent.velocity.x, agent.velocity.z).magnitude;
        else if (controller != null)
            planar = new Vector2(controller.velocity.x, controller.velocity.z).magnitude;
        else
        {
            Vector3 d = transform.position - lastPos; d.y = 0f;
            planar = d.magnitude / Mathf.Max(Time.deltaTime, 1e-4f);
            lastPos = transform.position;
        }

        float target = Mathf.Clamp01(planar / Mathf.Max(runSpeed, 0.01f));
        speedParam = Mathf.Lerp(speedParam, target, Time.deltaTime / Mathf.Max(damping, 0.01f));
        animator.SetFloat(SpeedHash, speedParam);
    }

    /// <summary>Call from your death logic (EnemyAI.OnDeath / PlayerHealth.onDeath) to play the death state.</summary>
    public void SetDead()
    {
        if (animator != null) animator.SetBool(DeadHash, true);
        enabled = false;
    }
}
