using UnityEngine;

/// <summary>
/// Drives upper-body combat poses straight through the humanoid muscle rig
/// (HumanPoseHandler) after the Animator evaluates each frame:
///  - hasGun: two-handed weapon hold layered over locomotion
///  - TriggerMelee(): 0.6s arm swing with body twist
/// Runtime-only and independent of animation clip import quirks.
/// </summary>
[RequireComponent(typeof(Animator))]
public class UpperBodyPose : MonoBehaviour
{
    public bool hasGun = true;
    [Range(0f, 1f)] public float weight = 1f;
    [Tooltip("Raises both arms further forward/up — set higher on the player so arms show in first person.")]
    public float armLift = 0f;

    Animator anim;
    HumanPoseHandler handler;
    HumanPose pose;
    float meleeT = -1f;

    static readonly (string muscle, float value)[] HoldPose =
    {
        ("Right Shoulder Down-Up", 0.15f), ("Right Arm Down-Up", -0.3f), ("Right Arm Front-Back", 0.8f),
        ("Right Arm Twist In-Out", 0.35f), ("Right Forearm Stretch", -0.75f), ("Right Forearm Twist In-Out", 0.25f),
        ("Right Hand Down-Up", -0.15f),
        ("Left Shoulder Down-Up", 0.15f), ("Left Arm Down-Up", -0.25f), ("Left Arm Front-Back", 0.9f),
        ("Left Arm Twist In-Out", -0.35f), ("Left Forearm Stretch", -0.85f), ("Left Forearm Twist In-Out", -0.4f),
        ("Left Hand Down-Up", -0.1f),
    };
    int[] holdIdx;
    float[] holdLift; // per-muscle armLift multiplier (Front-Back 1, Down-Up 0.6, others 0)

    // melee swing keyframes: time, right arm front-back, arm down-up, forearm stretch, spine twist
    static readonly float[][] SwingKeys =
    {
        new[] { 0f,    0.3f, -0.4f, -0.4f,  0f },
        new[] { 0.15f, -0.8f, 0.5f, -0.9f, -0.35f },
        new[] { 0.32f, 1f,    0.1f,  0.5f,  0.45f },
        new[] { 0.6f,  0.3f, -0.4f, -0.4f,  0f },
    };
    int iArmFB, iArmDU, iStretch, iSpine, iChest;

    void Start()
    {
        anim = GetComponent<Animator>();
        if (anim == null || !anim.isHuman || anim.avatar == null) { enabled = false; return; }
        handler = new HumanPoseHandler(anim.avatar, transform);

        holdIdx = new int[HoldPose.Length];
        holdLift = new float[HoldPose.Length];
        for (int i = 0; i < HoldPose.Length; i++)
        {
            holdIdx[i] = MuscleIndex(HoldPose[i].muscle);
            if (HoldPose[i].muscle.Contains("Arm Front-Back")) holdLift[i] = 1f;
            else if (HoldPose[i].muscle.Contains("Arm Down-Up")) holdLift[i] = 0.6f;
        }
        iArmFB   = MuscleIndex("Right Arm Front-Back");
        iArmDU   = MuscleIndex("Right Arm Down-Up");
        iStretch = MuscleIndex("Right Forearm Stretch");
        iSpine   = MuscleIndex("Spine Twist Left-Right");
        iChest   = MuscleIndex("Chest Twist Left-Right");
    }

    static int MuscleIndex(string name) => System.Array.IndexOf(HumanTrait.MuscleName, name);

    public void TriggerMelee() { meleeT = 0f; }

    void LateUpdate()
    {
        if (handler == null || weight <= 0f) return;
        if (anim == null || !anim.enabled) return; // ragdolled/dead — never fight the physics
        bool swinging = meleeT >= 0f;
        if (!hasGun && !swinging) return;

        handler.GetHumanPose(ref pose);

        if (hasGun)
            for (int i = 0; i < HoldPose.Length; i++)
                if (holdIdx[i] >= 0)
                    pose.muscles[holdIdx[i]] = Mathf.Lerp(pose.muscles[holdIdx[i]],
                        HoldPose[i].value + armLift * holdLift[i], weight);

        if (swinging)
        {
            Sample(meleeT, out float fb, out float du, out float st, out float tw);
            if (iArmFB >= 0)   pose.muscles[iArmFB] = fb;
            if (iArmDU >= 0)   pose.muscles[iArmDU] = du;
            if (iStretch >= 0) pose.muscles[iStretch] = st;
            if (iSpine >= 0)   pose.muscles[iSpine] = tw;
            if (iChest >= 0)   pose.muscles[iChest] = tw * 0.7f;
            meleeT += Time.deltaTime;
            if (meleeT > SwingKeys[SwingKeys.Length - 1][0]) meleeT = -1f;
        }

        handler.SetHumanPose(ref pose);
    }

    static void SampleInto(float[][] k, float t, int col, out float v)
    {
        int seg = 0;
        while (seg < k.Length - 2 && t > k[seg + 1][0]) seg++;
        float u = Mathf.InverseLerp(k[seg][0], k[seg + 1][0], t);
        v = Mathf.Lerp(k[seg][col], k[seg + 1][col], Mathf.SmoothStep(0f, 1f, u));
    }

    void Sample(float t, out float fb, out float du, out float st, out float tw)
    {
        SampleInto(SwingKeys, t, 1, out fb);
        SampleInto(SwingKeys, t, 2, out du);
        SampleInto(SwingKeys, t, 3, out st);
        SampleInto(SwingKeys, t, 4, out tw);
    }

    void OnDestroy() { handler?.Dispose(); }
}
