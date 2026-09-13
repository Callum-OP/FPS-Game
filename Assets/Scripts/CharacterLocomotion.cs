using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drives the Humanoid Animator's locomotion from whatever is actually moving the
/// character - a NavMeshAgent (enemies) or a CharacterController (player). Falls
/// back to measuring transform movement if neither is present.
///
/// Feeds CharacterAnimationDriver's MoveX/MoveY (local-space, -1..1 strafe / -2..2
/// forward-back-ish where run clips live) so the per-weapon 2D directional blend
/// trees built by AnimationSystemBuilder pick the right clip. Put this on the rigged
/// body that has the Animator, same as before.
/// </summary>
[RequireComponent(typeof(Animator))]
public class CharacterLocomotion : MonoBehaviour
{
    public Animator animator;
    public NavMeshAgent agent;
    public CharacterController controller;
    public CharacterAnimationDriver animationDriver;

    [Tooltip("Planar speed (m/s) that maps to a full walk (blend value 1).")]
    public float walkSpeed = 2.5f;
    [Tooltip("Planar speed (m/s) that maps to a full run (blend value 2).")]
    public float runSpeed = 5.5f;
    [Tooltip("Smoothing time for the Move parameters.")]
    public float damping = 0.12f;
    [Tooltip("How far below the feet to ray-check for ground (player only - NavMeshAgent enemies are always \"grounded\").")]
    public float groundCheckDistance = 0.3f;
    [Tooltip("Planar speed (m/s) below which we snap straight to idle instead of blending, so CharacterController/NavMeshAgent velocity noise can't keep the walk cycle alive while stationary.")]
    public float idleDeadzone = 0.15f;
    [Tooltip("Log raw velocity/move values to the Console twice a second - use this to see exactly what's reaching the Animator.")]
    public bool debugLog = false;

    Vector3 lastPos;
    Vector2 moveParam;
    static readonly int DeadHash = Animator.StringToHash("Dead");

    void Reset() { animator = GetComponent<Animator>(); }

    void Awake()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (agent == null) agent = GetComponentInParent<NavMeshAgent>();
        if (controller == null) controller = GetComponentInParent<CharacterController>();
        if (animationDriver == null) animationDriver = GetComponent<CharacterAnimationDriver>();
        lastPos = transform.position;
    }

    void Update()
    {
        Vector3 worldVelocity;
        bool grounded = true;

        if (agent != null && agent.enabled && !agent.isStopped)
        {
            worldVelocity = agent.velocity;
        }
        else if (controller != null)
        {
            worldVelocity = controller.velocity;
            grounded = controller.isGrounded;
        }
        else
        {
            Vector3 d = transform.position - lastPos; d.y = 0f;
            worldVelocity = d / Mathf.Max(Time.deltaTime, 1e-4f);
            lastPos = transform.position;
        }

        // World velocity -> this transform's local space so strafing left/right and
        // walking backward map onto the correct blend-tree axes regardless of facing.
        Vector3 local = transform.InverseTransformDirection(new Vector3(worldVelocity.x, 0f, worldVelocity.z));
        float lateral = local.x;
        float forward = local.z;
        float planar = new Vector2(worldVelocity.x, worldVelocity.z).magnitude;

        Vector2 target;
        if (planar < idleDeadzone)
        {
            target = Vector2.zero; // snap to true idle - residual CharacterController/NavMeshAgent
                                    // velocity noise should never keep the walk cycle alive
        }
        else
        {
            float speedTier = forward >= 0f
                ? Mathf.InverseLerp(0f, Mathf.Max(runSpeed, 0.01f), Mathf.Abs(forward)) * 2f
                : -Mathf.InverseLerp(0f, Mathf.Max(runSpeed, 0.01f), Mathf.Abs(forward)) * 2f;
            float lateralTier = Mathf.InverseLerp(0f, Mathf.Max(runSpeed, 0.01f), Mathf.Abs(lateral)) * Mathf.Sign(lateral);
            target = new Vector2(Mathf.Clamp(lateralTier, -2f, 2f), Mathf.Clamp(speedTier, -2f, 2f));
        }

        moveParam = Vector2.Lerp(moveParam, target, Time.deltaTime / Mathf.Max(damping, 0.01f));
        if (target == Vector2.zero && moveParam.magnitude < 0.03f) moveParam = Vector2.zero; // kill the last sliver

        if (animationDriver != null)
        {
            animationDriver.SetMove(moveParam.x, moveParam.y);
            animationDriver.SetGrounded(grounded);
        }

        if (debugLog && Time.frameCount % 30 == 0)
            Debug.Log($"[CharacterLocomotion] worldVel={worldVelocity} local(x={lateral:F2},z={forward:F2}) move={moveParam} grounded={grounded} controller={(controller != null)} agent={(agent != null)}");
    }

    /// <summary>Call from your death logic (EnemyAI.OnDeath / PlayerHealth.onDeath) to play the death state.</summary>
    public void SetDead()
    {
        if (animationDriver != null) animationDriver.SetDead(true);
        else if (animator != null) animator.SetBool(DeadHash, true);
        enabled = false;
    }
}