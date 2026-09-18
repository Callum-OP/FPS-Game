using UnityEngine;

/// <summary>
/// Calms down how much the torso swings, without touching the animation clips.
///
/// Every frame it low-passes the spine/chest/head bones' own local rotations: it keeps a
/// slowly-following average of each bone's rotation and blends the live pose back toward
/// that average by `dampen`. The pose still moves, just less - at dampen 0.7 the injured
/// walk's lurch drops to roughly a third of its authored amplitude while keeping its
/// timing and shape, which is what "only a little bit" looks like. Nothing is re-authored
/// and no reference pose has to be measured, so it works on any clip you add later.
///
/// Two separate amounts: `injuredDampen` applies while health is low (that's the pose
/// that's actually too big), `normalDampen` applies the rest of the time and defaults to
/// 0 so normal locomotion is untouched.
///
/// Runs in LateUpdate, after the Animator and after the hand IK pass, so it damps the
/// final pose. It only writes bones - never the camera or the weapon - so it can't
/// reintroduce the post-IK weapon jitter that the ownership rework removed.
///
/// SETUP: put it on the same object as the Animator (next to CharacterAnimationDriver),
/// on the player and/or the enemies. Auto-finds health on itself or a parent.
/// </summary>
[DefaultExecutionOrder(200)]
public class TorsoMotionDampener : MonoBehaviour
{
    [Header("Damping (0 = untouched, 1 = frozen to the running average)")]
    [Range(0f, 1f)] public float normalDampen = 0f;
    [Range(0f, 1f)] public float injuredDampen = 0.7f;
    [Tooltip("Extra multiplier for the head specifically - a head that's damped as hard as the spine can look stiff/disconnected.")]
    [Range(0f, 1f)] public float headDampenScale = 0.5f;

    [Header("Response")]
    [Tooltip("How fast the running average follows the live pose. Lower = the average is steadier, so more of the motion gets damped out.")]
    public float averageFollow = 3f;
    [Tooltip("How fast the damping amount crossfades when health crosses the injured threshold.")]
    public float blendSpeed = 2f;

    [Header("Injured Source")]
    [Tooltip("Health fraction at or below which injuredDampen is used. Match the InjuredHealthFraction on PlayerSetup / EnemyAI.")]
    [Range(0f, 1f)] public float injuredHealthFraction = 0.3f;
    public PlayerHealth playerHealth;
    public Health health;

    Animator anim;
    Transform[] bones;
    float[] boneScale;
    Quaternion[] averages;
    bool primed;
    float injuredBlend, targetInjured;

    void Start()
    {
        anim = GetComponent<Animator>();
        if (anim == null || !anim.isHuman) { enabled = false; return; }

        var list = new System.Collections.Generic.List<Transform>();
        var scales = new System.Collections.Generic.List<float>();
        void Add(HumanBodyBones b, float scale)
        {
            var t = anim.GetBoneTransform(b);
            if (t != null) { list.Add(t); scales.Add(scale); }
        }
        Add(HumanBodyBones.Spine, 1f);
        Add(HumanBodyBones.Chest, 1f);
        Add(HumanBodyBones.UpperChest, 1f);
        Add(HumanBodyBones.Neck, headDampenScale);
        Add(HumanBodyBones.Head, headDampenScale);

        if (list.Count == 0) { enabled = false; return; }
        bones = list.ToArray();
        boneScale = scales.ToArray();
        averages = new Quaternion[bones.Length];

        if (playerHealth == null) playerHealth = GetComponentInParent<PlayerHealth>();
        if (health == null) health = GetComponentInParent<Health>();
        if (playerHealth != null) playerHealth.onHealthChanged += OnHealthFraction;
        if (health != null) health.onDamaged.AddListener(OnHealthFraction);
    }

    void OnHealthFraction(float fraction) => targetInjured = fraction <= injuredHealthFraction ? 1f : 0f;

    void LateUpdate()
    {
        injuredBlend = Mathf.MoveTowards(injuredBlend, targetInjured, blendSpeed * Time.deltaTime);
        float dampen = Mathf.Lerp(normalDampen, injuredDampen, injuredBlend);

        float follow = 1f - Mathf.Exp(-Mathf.Max(0.01f, averageFollow) * Time.deltaTime);

        for (int i = 0; i < bones.Length; i++)
        {
            // Safeguard against destroyed or unassigned bone transforms (e.g. during ragdoll)
            if (bones[i] == null) continue;

            Quaternion live = bones[i].localRotation;
            if (!primed) averages[i] = live;
            averages[i] = Quaternion.Slerp(averages[i], live, follow);

            float amount = dampen * boneScale[i];
            if (amount > 0.001f)
                bones[i].localRotation = Quaternion.Slerp(live, averages[i], amount);
        }
        primed = true;
    }

    void OnDestroy()
    {
        if (playerHealth != null) playerHealth.onHealthChanged -= OnHealthFraction;
        if (health != null) health.onDamaged.RemoveListener(OnHealthFraction);
    }
}