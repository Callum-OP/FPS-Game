using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// One-click build for the whole player/enemy animation system.
///
/// What it does (menu: Tools/FPS Game/Build Animation System):
///  1. Finds every Mixamo .fbx clip this system needs under Assets/LocalAssets/Mixamo,
///     forces it to Humanoid rig + its own avatar, renames the clip and sets sane
///     loop/root-motion settings.
///  2. Builds Assets/Animations/UpperBodyMask.mask (arms/spine/head only, no legs).
///  3. Builds Assets/Animations/CharacterAnimator.controller with:
///       Base layer   - full body locomotion: Unarmed / Pistol / Rifle / RifleCrouch /
///                       Airborne / Death states, each a 2D directional blend tree
///                       driven by MoveX/MoveY (the character's local velocity in m/s,
///                       every clip placed at the speed it was authored at, so the feet
///                       match the ground), switched by the WeaponClass int.
///       UpperBody    - masked layer for Aim pose, Fire, Reload, Melee, only ever
///                       touches arms/spine/head so the legs never stop walking.
///
/// Run this from the Unity Editor (Tools/FPS Game/Build Animation System). It is
/// idempotent - re-running it after adding/renaming clips just rebuilds the assets.
///
/// After running:
///  - Drag Assets/Animations/CharacterAnimator.controller onto the player and enemy
///    Animator components (replacing Locomotion.controller).
///  - Add a CharacterAnimationDriver + CharacterLocomotion to the same object.
///  - Double check the "strafe" clip assumptions noted in STRAFE_ASSUMPTIONS below -
///    Mixamo doesn't label which of "strafe" / "strafe (2)" is left vs right, so this
///    script guesses (2) = right. Flip Left/Right in the printed log if it's mirrored.
/// </summary>
public static class AnimationSystemBuilder
{
    const string MixamoRoot = "Assets/LocalAssets/Mixamo";
    const string OutDir = "Assets/Animations";
    const string ControllerPath = OutDir + "/CharacterAnimator.controller";
    const string MaskPath = OutDir + "/UpperBodyMask.mask";
    const string LowerMaskPath = OutDir + "/LowerBodyMask.mask";

    // ---- clip descriptor -------------------------------------------------
    struct ClipDef
    {
        public string key;      // how we refer to it below
        public string pack;     // sub-folder under Mixamo/
        public string file;     // fbx file name (no extension)
        public bool loop;
        public bool rootMotion; // bake XZ root motion out (we always drive movement via code)
        public bool optional;   // missing file is logged quietly, not as a warning
        public bool extractRoot; // take the clip's whole-body travel (up, forward/sideways AND turning) OUT of the pose - code moves the character instead
        public ClipDef(string key, string pack, string file, bool loop, bool optional = false, bool extractRoot = false)
        { this.key = key; this.pack = pack; this.file = file; this.loop = loop; this.rootMotion = false; this.optional = optional; this.extractRoot = extractRoot; }
    }

    // Every clip the animator system references. Edit this table to swap packs/clips.
    static readonly ClipDef[] Clips = new[]
    {
        // Unarmed - Locomotion Pack
        new ClipDef("un_idle",       "Locomotion Pack", "idle", true),
        new ClipDef("un_walk_fwd",   "Locomotion Pack", "walking", true),
        new ClipDef("un_run_fwd",    "Locomotion Pack", "running", true),
        new ClipDef("un_walk_left",  "Locomotion Pack", "left strafe walking", true),
        new ClipDef("un_walk_right", "Locomotion Pack", "right strafe walking", true),
        new ClipDef("un_run_left",   "Locomotion Pack", "left strafe", true),
        new ClipDef("un_run_right",  "Locomotion Pack", "right strafe", true),
        new ClipDef("un_jump",       "Locomotion Pack", "jump", false),

        // Pistol - Pistol_Handgun Locomotion Pack
        new ClipDef("pi_idle",       "Pistol_Handgun Locomotion Pack", "pistol idle", true),
        new ClipDef("pi_walk_fwd",   "Pistol_Handgun Locomotion Pack", "pistol walk", true),
        new ClipDef("pi_run_fwd",    "Pistol_Handgun Locomotion Pack", "pistol run", true),
        new ClipDef("pi_walk_back",  "Pistol_Handgun Locomotion Pack", "pistol walk backward", true),
        new ClipDef("pi_run_back",   "Pistol_Handgun Locomotion Pack", "pistol run backward", true),
        new ClipDef("pi_left",       "Pistol_Handgun Locomotion Pack", "pistol strafe", true),
        new ClipDef("pi_right",      "Pistol_Handgun Locomotion Pack", "pistol strafe (2)", true),
        new ClipDef("pi_jump",       "Pistol_Handgun Locomotion Pack", "pistol jump", false),

        // Rifle - Pro Rifle Pack
        new ClipDef("ri_idle",           "Pro Rifle Pack", "idle", true),
        new ClipDef("ri_idle_aim",       "Pro Rifle Pack", "idle aiming", true),
        new ClipDef("ri_walk_fwd",       "Pro Rifle Pack", "walk forward", true),
        new ClipDef("ri_walk_fwd_l",     "Pro Rifle Pack", "walk forward left", true),
        new ClipDef("ri_walk_fwd_r",     "Pro Rifle Pack", "walk forward right", true),
        new ClipDef("ri_walk_back",      "Pro Rifle Pack", "walk backward", true),
        new ClipDef("ri_walk_left",      "Pro Rifle Pack", "walk left", true),
        new ClipDef("ri_walk_right",     "Pro Rifle Pack", "walk right", true),
        new ClipDef("ri_run_fwd",        "Pro Rifle Pack", "run forward", true),
        new ClipDef("ri_run_fwd_l",      "Pro Rifle Pack", "run forward left", true),
        new ClipDef("ri_run_fwd_r",      "Pro Rifle Pack", "run forward right", true),
        new ClipDef("ri_run_back",       "Pro Rifle Pack", "run backward", true),
        new ClipDef("ri_run_back_l",     "Pro Rifle Pack", "run backward left", true),
        new ClipDef("ri_run_back_r",     "Pro Rifle Pack", "run backward right", true),
        new ClipDef("ri_run_left",       "Pro Rifle Pack", "run left", true),
        new ClipDef("ri_run_right",      "Pro Rifle Pack", "run right", true),
        new ClipDef("ri_jump_up",        "Pro Rifle Pack", "jump up", false),
        new ClipDef("ri_jump_loop",      "Pro Rifle Pack", "jump loop", true),
        new ClipDef("ri_jump_down",      "Pro Rifle Pack", "jump down", false),
        new ClipDef("ri_death_front",    "Pro Rifle Pack", "death from the front", false),
        // Shot-from-behind death. If your Pro Rifle Pack doesn't ship this file the
        // import just logs it as missing and the back-death state falls back to the
        // front clip - nothing else breaks.
        new ClipDef("ri_death_back",     "Pro Rifle Pack", "death from the back", false),
        new ClipDef("ri_death_right",    "Pro Rifle Pack", "death from right", false),
        new ClipDef("ri_death_head_f",   "Pro Rifle Pack", "death from front headshot", false),
        new ClipDef("ri_death_head_b",   "Pro Rifle Pack", "death from back headshot", false),
        new ClipDef("ri_death_crouch",   "Pro Rifle Pack", "death crouching headshot front", false),
        // Extra deaths from the loose clips at the top of the Mixamo folder and the Shooter Pack.
        // Fall directions and timings for all of these were MEASURED from the FBX data, see
        // CharacterAnimationDriver.DeathTable.
        new ClipDef("wk_death_moving",   "Shooter Pack", "walking to dying", false),
        new ClipDef("x_stumble_back",    "", "Stumble Backwards", false),
        new ClipDef("x_fall_over",       "", "Fall Over", false),
        new ClipDef("x_shot_front",      "", "Sweep Fall (As if shotgunned from front)", false),
        new ClipDef("x_shot_back",       "", "Fall Flat (As if shotgunned from back)", false),
        // Sprint set - the pack has proper sprint clips, so running flat out no longer
        // has to reuse the run cycle played faster.
        new ClipDef("ri_sprint_fwd",     "Pro Rifle Pack", "sprint forward", true),
        new ClipDef("ri_sprint_fwd_l",   "Pro Rifle Pack", "sprint forward left", true),
        new ClipDef("ri_sprint_fwd_r",   "Pro Rifle Pack", "sprint forward right", true),
        new ClipDef("ri_sprint_left",    "Pro Rifle Pack", "sprint left", true),
        new ClipDef("ri_sprint_right",   "Pro Rifle Pack", "sprint right", true),
        // Backward diagonals for the full 8-way rifle walk ring.
        new ClipDef("ri_walk_back_l",    "Pro Rifle Pack", "walk backward left", true),
        new ClipDef("ri_walk_back_r",    "Pro Rifle Pack", "walk backward right", true),

        // Turn-in-place (see BuildTurnLayer): a standing pivot, not a walk cycle, but marked
        // loop=true so holding a turn direction repeats the step instead of freezing on the
        // last frame - the same reason every locomotion clip above loops.
        new ClipDef("ri_turn_l",         "Pro Rifle Pack", "turn 90 left", true),
        new ClipDef("ri_turn_r",         "Pro Rifle Pack", "turn 90 right", true),

        // Rifle crouch - Pro Rifle Pack
        new ClipDef("rc_idle",       "Pro Rifle Pack", "idle crouching aiming", true),
        new ClipDef("rc_walk_fwd",   "Pro Rifle Pack", "walk crouching forward", true),
        new ClipDef("rc_walk_fwd_l", "Pro Rifle Pack", "walk crouching forward left", true),
        new ClipDef("rc_walk_fwd_r", "Pro Rifle Pack", "walk crouching forward right", true),
        new ClipDef("rc_walk_back",  "Pro Rifle Pack", "walk crouching backward", true),
        new ClipDef("rc_walk_left",  "Pro Rifle Pack", "walk crouching left", true),
        new ClipDef("rc_walk_right", "Pro Rifle Pack", "walk crouching right", true),
        new ClipDef("rc_idle_plain", "Pro Rifle Pack", "idle crouching", true),
        new ClipDef("rc_turn_l",     "Pro Rifle Pack", "crouching turn 90 left", true),
        new ClipDef("rc_turn_r",     "Pro Rifle Pack", "crouching turn 90 right", true),

        // Added: unarmed backwards (the Locomotion pack has none), crouch diagonals backwards,
        // and the sprint that was imported but never used.
        new ClipDef("un_walk_back",  "Pro Melee Axe Pack", "unarmed walk back", true),
        new ClipDef("un_run_back",   "Pro Melee Axe Pack", "unarmed run back", true),
        // Turn-in-place, unarmed. Also stands in for Pistol below - the Pistol_Handgun pack
        // doesn't ship a turn clip of its own, and this reads closer than the rifle turn does
        // for a one-handed weapon. Swap in a real pistol turn clip here if you source one.
        new ClipDef("un_turn_l",     "Pro Melee Axe Pack", "unarmed turn left 90", true),
        new ClipDef("un_turn_r",     "Pro Melee Axe Pack", "unarmed turn right 90", true),
        new ClipDef("rc_walk_back_l","Pro Rifle Pack", "walk crouching backward left", true),
        new ClipDef("rc_walk_back_r","Pro Rifle Pack", "walk crouching backward right", true),

        // Female variant of the unarmed set (Female Locomotion Pack). Selected per character with the Animation Variant
        // dropdown on CharacterAnimationDriver; everything is optional, so a missing pack just leaves the default set.
        // Speeds measured from the FBX hips travel (walk 1.61, run 3.62, strafe walk 1.87, strafe run 3.47 m/s) - the run
        // is ~14% slower than the default set's, which is why this has its own tuned blend tree. Backwards walk/run have
        // no female clip and stay shared. Turn clips are about 85 degrees.
        new ClipDef("f_idle",        "Female Locomotion Pack", "idle", true, optional: true),
        new ClipDef("f_walk_fwd",    "Female Locomotion Pack", "walking", true, optional: true),
        new ClipDef("f_run_fwd",     "Female Locomotion Pack", "running", true, optional: true),
        new ClipDef("f_walk_left",   "Female Locomotion Pack", "left strafe walk", true, optional: true),
        new ClipDef("f_walk_right",  "Female Locomotion Pack", "right strafe walk", true, optional: true),
        new ClipDef("f_run_left",    "Female Locomotion Pack", "left strafe", true, optional: true),
        new ClipDef("f_run_right",   "Female Locomotion Pack", "right strafe", true, optional: true),
        new ClipDef("f_turn_l",      "Female Locomotion Pack", "left turn", true, optional: true),
        new ClipDef("f_turn_r",      "Female Locomotion Pack", "right turn", true, optional: true),

        // Two-handed sword - Great Sword Pack: ONLY the three swings are used (as upper-body overlays, see
        // BuildUpperBodyLayer). The pack's own walk/run/strafe clips are deliberately not used: they are authored with the
        // body twisted ~30 degrees off the travel direction and read as walking diagonally / backwards, so a sword
        // carrier uses the unarmed locomotion for the legs and hips (same as the other melee props) and only the
        // torso and arms are sword animation. The swings are the in-place ones that start from the ready pose
        // (overhead chop, cross sweep, thrust).
        new ClipDef("sw_slash1",     "Great Sword Pack", "great sword slash", false, optional: true),
        new ClipDef("sw_slash2",     "Great Sword Pack", "great sword slash (3)", false, optional: true),
        new ClipDef("sw_slash3",     "Great Sword Pack", "great sword attack", false, optional: true),
        new ClipDef("sw_heavy",      "Great Sword Pack", "great sword high spin attack", false, optional: true),
        // Axe-style melee (Pro Melee Axe pack): the light swings reuse act_melee / act_melee_b / act_melee_d (defined
        // with the other melee clips below); the heavy is the 360 high swing.
        new ClipDef("ax_heavy",      "Pro Melee Axe Pack", "standing melee attack 360 high", false, optional: true),

        // Cover - Action Adventure Pack. Enemies (and allies) use these to drop into and
        // rise from cover instead of just crouching where they stand.
        new ClipDef("cov_enter",     "Action Adventure Pack", "stand to cover", false),
        new ClipDef("cov_exit",      "Action Adventure Pack", "cover to stand", false),
        new ClipDef("cov_sneak_l",   "Action Adventure Pack", "left cover sneak", true),
        new ClipDef("cov_sneak_r",   "Action Adventure Pack", "right cover sneak", true),

        // Pistol crouch - the Pistol pack has its own kneeling pose, which reads far
        // better under a handgun than the rifle crouch's two-handed arms did.
        new ClipDef("pi_kneel_idle", "Pistol_Handgun Locomotion Pack", "pistol kneeling idle", true),

        // Shared actions - Basic Shooter Pack (rifle-style, reused for both weapon classes
        // since the supplied packs don't include a dedicated pistol firing clip)
        new ClipDef("act_fire_rifle", "Basic Shooter Pack", "firing rifle", true),
        new ClipDef("act_reload",     "Basic Shooter Pack", "reloading", false),
        new ClipDef("act_grenade",    "Basic Shooter Pack", "toss grenade", false),
        new ClipDef("act_hit",        "Basic Shooter Pack", "hit reaction", false),

        // Melee - Pro Melee Axe Pack
        new ClipDef("act_melee",      "Pro Melee Axe Pack", "standing melee attack horizontal", false),
        new ClipDef("act_melee_b",    "Pro Melee Axe Pack", "standing melee attack backhand", false),
        new ClipDef("act_melee_d",    "Pro Melee Axe Pack", "standing melee attack downward", false),
        // --- Custom selection (Assets/LocalAssets/Mixamo/Custom Selection) -----------------------------
        // Shove: the attacker's clips. The punch is authored with the LEFT hand, so its state is mirrored.
        new ClipDef("shove_punch", "Custom Selection/Shove/Shover", "Standing Melee Punch", false, optional: true),
        new ClipDef("shove_kick",  "Custom Selection/Shove/Shover", "Standing Melee Kick",  false, optional: true),
        // Shove: the victim's reactions, named for the side the shove came FROM. Imported in-place like every
        // other clip (lockRootPositionXZ) - Health.PushRoutine moves the body, not the pose.
        new ClipDef("shoved_front", "Custom Selection/Shove/Shoved", "Standing React Large From Front", false, optional: true),
        new ClipDef("shoved_back",  "Custom Selection/Shove/Shoved", "Standing React Large From Back",  false, optional: true),
        new ClipDef("shoved_left",  "Custom Selection/Shove/Shoved", "Standing React Large From Left",  false, optional: true),
        new ClipDef("shoved_right", "Custom Selection/Shove/Shoved", "Standing React Large From Right", false, optional: true),
        // Vault / climb: imported in-place like every other clip - VaultClimb moves the root along a path
        // measured from the clip's own hip motion (see VaultClimb.VaultProfile).
        // extractRoot: the clips' own travel must NOT stay in the pose, because VaultClimb moves the root along the
        // real ledge by the same amount - baked in, the body went twice as far as the ledge: up (0.45m / 0.42m /
        // 2.14m of hip rise), FORWARD (0.49m / 1.66m / 2.21m of hip travel - this is what carried the model out in
        // front of the camera and gun) and, in Vault Over, round to face sideways (hips turn 145 degrees).
        new ClipDef("vault_step", "Custom Selection/Vault", "Step Up",    false, optional: true, extractRoot: true),
        new ClipDef("vault_over", "Custom Selection/Vault", "Vault Over", false, optional: true, extractRoot: true),
        new ClipDef("vault_wall", "Custom Selection/Vault", "Wall Climb", false, optional: true, extractRoot: true),
        // Stumble to the floor when shot while moving (death variants, baked like the other death clips).
        new ClipDef("stumble_front", "Custom Selection/StumbleToFloor", "Shot From Front", false, optional: true),
        new ClipDef("stumble_back",  "Custom Selection/StumbleToFloor", "Shot From Back",  false, optional: true),
        new ClipDef("stumble_left",  "Custom Selection/StumbleToFloor", "Shot From Left",  false, optional: true),
        new ClipDef("stumble_right", "Custom Selection/StumbleToFloor", "Shot From Right", false, optional: true),
        // Idle gestures. "Right Hand" ones only move the right arm (the left hand stays on the grip);
        // "Both Hands" ones are for pistol/unarmed only and free both hands.
        new ClipDef("gest_shoe",    "Custom Selection/IdleGestures/Right Hand",  "Check Shoe", false, optional: true),
        new ClipDef("gest_stretch", "Custom Selection/IdleGestures/Both Hands",  "Pistol Or Unarmed Arm Stretching", false, optional: true),

        // Basic idle gestures (the original set): played on the masked UpperBody layer by AIRelaxedIdle as soon as
        // a character has settled into the relaxed state, just to give the torso/head some varied movement.
        // Untouched from how they always were - the two full-body gestures above are the "special" ones.
        new ClipDef("gest_look_away",     "Gestures Pack Basic", "look away gesture", false, optional: true),
        new ClipDef("gest_weight_shift",  "Gestures Pack Basic", "weight shift", false, optional: true),
        new ClipDef("gest_relieved_sigh", "Gestures Pack Basic", "relieved sigh", false, optional: true),
        new ClipDef("gest_thoughtful",    "Gestures Pack Basic", "thoughtful head shake", false, optional: true),
        new ClipDef("gest_melee_look1",   "Pro Melee Axe Pack", "standing idle looking ver. 1", false, optional: true),
        new ClipDef("gest_melee_look2",   "Pro Melee Axe Pack", "standing idle looking ver. 2", false, optional: true),
        new ClipDef("gest_unarmed_look1", "Pro Melee Axe Pack", "unarmed idle looking ver. 1", false, optional: true),
        new ClipDef("gest_unarmed_look2", "Pro Melee Axe Pack", "unarmed idle looking ver. 2", false, optional: true),

        // Airborne fallback (any weapon) - Action Adventure Pack
        new ClipDef("airborne_idle",  "Action Adventure Pack", "falling idle", true),

        // Injured (low health) - Male Injured Pack. This pack only has forward/
        // backward locomotion (no left/right strafe clips like the other packs),
        // so the left/right blend points below reuse the forward clip - a
        // reasonable approximation for a temporary "hurt" overlay, but flag it
        // if a true strafing injured pose ever matters more than it does now.
        new ClipDef("inj_idle",      "Male Injured Pack", "injured idle", true),
        new ClipDef("inj_walk_fwd",  "Male Injured Pack", "injured walk", true),
        new ClipDef("inj_run_fwd",   "Male Injured Pack", "injured run", true),
        new ClipDef("inj_walk_back", "Male Injured Pack", "injured walk backwards", true),
        new ClipDef("inj_run_back",  "Male Injured Pack", "injured run backwards", true),
    };

    // NOTE: Mixamo gives no left/right label on ambiguous pairs. This script assumes
    // the un-suffixed clip is LEFT and the "(2)" clip is RIGHT. If characters strafe
    // backwards in-game, swap these two round in the Clips table above and re-run.
    const string StrafeAssumptions =
        "un_run_left/right -> Locomotion Pack 'left strafe'/'right strafe'; " +
        "pi_left/right -> Pistol pack 'pistol strafe'/'pistol strafe (2)'. Verify in the Animator preview.";

    static Dictionary<string, AnimationClip> _clipLookup;
    static HashSet<string> _optionalKeys;

    [MenuItem("Tools/FPS Game/Build Animation System")]
    public static void Build()
    {
        _clipLookup = new Dictionary<string, AnimationClip>();
        _optionalKeys = new HashSet<string>();
        Directory.CreateDirectory(OutDir);

        int imported = 0, missing = 0;
        foreach (var def in Clips)
        {
            if (def.optional) _optionalKeys.Add(def.key);
            var clip = ImportClip(def);
            if (clip == null) { if (!def.optional) missing++; continue; }
            _clipLookup[def.key] = clip;
            imported++;
        }

        AvatarMask upperBodyMask = BuildUpperBodyMask();
        AnimatorController controller = BuildController(upperBodyMask);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AutoAssignController(controller);
        AssetDatabase.SaveAssets();

        Debug.Log($"[AnimationSystemBuilder] Done. Imported {imported} clips, {missing} missing. " +
                  $"Controller: {ControllerPath}\nStrafe assumptions: {StrafeAssumptions}");
        if (missing > 0)
            Debug.LogWarning("[AnimationSystemBuilder] Some clips were missing - check the console above for exact file names expected under " + MixamoRoot);

        Selection.activeObject = controller;
    }

    // Wipes an existing controller asset back to a blank single-layer, zero-parameter
    // state so BuildController can repopulate it from scratch without touching its
    // GUID (see comment above BuildController).
    static void ClearController(AnimatorController controller)
    {
        string path = AssetDatabase.GetAssetPath(controller);
        foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (obj != null && obj != controller)
                Object.DestroyImmediate(obj, true);
        }

        controller.parameters = new AnimatorControllerParameter[0];

        var freshSM = new AnimatorStateMachine { name = "Base Layer", hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(freshSM, controller);
        controller.layers = new[]
        {
            new AnimatorControllerLayer { name = "Base Layer", defaultWeight = 1f, stateMachine = freshSM }
        };
    }

    // ---- auto-assign to Player/Enemy ------------------------------------
    // Finds every prefab with a humanoid Animator (Player, Enemy, EnemyMelee, etc.)
    // and points its Animator at the freshly-built controller, so you never have to
    // manually re-drag CharacterAnimator.controller onto them after running this tool.
    static void AutoAssignController(AnimatorController controller)
    {
        int assigned = 0;

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            bool changed = false;
            foreach (var animator in prefab.GetComponentsInChildren<Animator>(true))
            {
                if (animator.avatar == null || !animator.avatar.isHuman) continue;
                if (animator.runtimeAnimatorController == controller) continue;
                animator.runtimeAnimatorController = controller;
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(prefab);
                PrefabUtility.SavePrefabAsset(prefab);
                assigned++;
            }
        }

        // Also cover Player/Enemy instances placed directly in open scenes (not
        // prefab instances) so a rebuild doesn't leave those stale either.
        foreach (var animator in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
        {
            if (animator.avatar == null || !animator.avatar.isHuman) continue;
            if (animator.runtimeAnimatorController == controller) continue;
            animator.runtimeAnimatorController = controller;
            EditorUtility.SetDirty(animator);
            assigned++;
        }

        if (assigned > 0)
            Debug.Log($"[AnimationSystemBuilder] Auto-assigned CharacterAnimator.controller to {assigned} humanoid Animator(s)/prefab(s).");
    }

    // ---- clip import ------------------------------------------------------
    static AnimationClip ImportClip(ClipDef def)
    {
        string fbxPath = string.IsNullOrEmpty(def.pack)
            ? $"{MixamoRoot}/{def.file}.fbx"
            : $"{MixamoRoot}/{def.pack}/{def.file}.fbx";
        var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
        if (importer == null)
        {
            if (def.optional) Debug.Log($"[AnimationSystemBuilder] Optional clip not present, skipped: {fbxPath}");
            else Debug.LogWarning($"[AnimationSystemBuilder] Missing FBX: {fbxPath}");
            return null;
        }

        bool changed = false;
        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            changed = true;
        }

        var srcClips = importer.clipAnimations != null && importer.clipAnimations.Length > 0
            ? importer.clipAnimations
            : importer.defaultClipAnimations;

        if (srcClips == null || srcClips.Length == 0)
        {
            Debug.LogWarning($"[AnimationSystemBuilder] No clip found inside {fbxPath}");
            return null;
        }

        string wantedName = def.key;
        var clipSettings = srcClips[0];
        clipSettings.name = wantedName;
        clipSettings.loopTime = def.loop;
        clipSettings.loopPose = def.loop;
        clipSettings.lockRootRotation = !def.extractRoot; // false = body turning extracted, so the model keeps facing where the camera does
        clipSettings.keepOriginalOrientation = false;
        clipSettings.lockRootHeightY = !def.extractRoot; // false = vertical travel extracted (vault clips - see ClipDef.extractRoot)
        // "Original" is right for every pack except the Male Injured Pack, which is
        // authored with a hunched, lowered pelvis that doesn't match the standing pose
        // every other pack assumes.
        //
        // IMPORTANT, and the actual cause of "every character is halfway under the
        // map": keepOriginalPositionY = false does NOT mean "Feet" on its own - it
        // means "Center of Mass" unless heightFromFeet is ALSO set true. An earlier
        // version of this file set keepOriginalPositionY false (for one pack, then
        // briefly for every pack) without ever touching heightFromFeet, so every clip
        // that had it was baked against center-of-mass height instead - roughly
        // mid-torso on a standing human - which drops the whole skeleton about half a
        // body height below the CharacterController/NavMeshAgent that never moved.
        // That's shared across the player, enemies and allies because all three
        // animate off this one AnimatorController. Both flags below have to be set
        // together to actually get Feet-based baking.
        bool useFeetBasis = def.pack == "Male Injured Pack";
        clipSettings.keepOriginalPositionY = !useFeetBasis;
        clipSettings.heightFromFeet = useFeetBasis;
        clipSettings.lockRootPositionXZ = !def.extractRoot; // false = forward/sideways travel extracted (vault clips)
        clipSettings.keepOriginalPositionXZ = def.rootMotion;

        importer.clipAnimations = new[] { clipSettings };
        changed = true;

        if (changed)
            importer.SaveAndReimport();

        var assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        foreach (var a in assets)
            if (a is AnimationClip c && c.name == wantedName)
                return c;

        // Reimport didn't take (name collision) - fall back to first non-preview clip.
        foreach (var a in assets)
            if (a is AnimationClip c && !c.name.Contains("__preview__"))
                return c;

        Debug.LogWarning($"[AnimationSystemBuilder] Could not resolve clip '{wantedName}' in {fbxPath}");
        return null;
    }

    static AnimationClip C(string key)
    {
        _clipLookup.TryGetValue(key, out var c);
        if (c == null && !key.EndsWith("crouch_b") && !_optionalKeys.Contains(key)) Debug.LogWarning($"[AnimationSystemBuilder] Clip '{key}' unavailable - a blend tree node will be empty.");
        return c;
    }

    // ---- avatar mask --------------------------------------------------
    static AvatarMask BuildUpperBodyMask()
    {
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath) ?? new AvatarMask();
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
        {
            var part = (AvatarMaskBodyPart)i;
            // NOTE: AvatarMaskBodyPart.Root already excludes root motion (the Hips
            // bone's own translate/rotate curves), so the Hips bone's position was never
            // actually driven by this masked layer - Body only bends the spine/chest
            // shape above it. Excluding Body here was tried as a fix for the
            // reload-while-walking gun jump and did NOT resolve it, so it's reverted -
            // see WeaponHandIK for the actual cause.
            bool active = part != AvatarMaskBodyPart.LeftLeg &&
                          part != AvatarMaskBodyPart.RightLeg &&
                          part != AvatarMaskBodyPart.LeftFootIK &&
                          part != AvatarMaskBodyPart.RightFootIK &&
                          part != AvatarMaskBodyPart.Root;
            mask.SetHumanoidBodyPartActive(part, active);
        }

        if (AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath) == null)
            AssetDatabase.CreateAsset(mask, MaskPath);
        else
            EditorUtility.SetDirty(mask);

        return mask;
    }

    // The exact inverse of BuildUpperBodyMask: only legs/feet move, nothing else. Used by the
    // turn-in-place layer (BuildTurnLayer) so it can only ever affect footwork - it runs
    // alongside WeaponHandIK and TorsoPoseDriver (which own the arms/spine/head) and must
    // never fight either of those for the same bones, the single most common bug class in
    // this project's animation system (see AnatomicalConstraints.cs for the same principle
    // applied to the runtime joint-limit pass).
    static AvatarMask BuildLowerBodyMask()
    {
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(LowerMaskPath) ?? new AvatarMask();
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
        {
            var part = (AvatarMaskBodyPart)i;
            bool active = part == AvatarMaskBodyPart.LeftLeg ||
                          part == AvatarMaskBodyPart.RightLeg ||
                          part == AvatarMaskBodyPart.LeftFootIK ||
                          part == AvatarMaskBodyPart.RightFootIK ||
                          part == AvatarMaskBodyPart.Root;
            mask.SetHumanoidBodyPartActive(part, active);
        }

        if (AssetDatabase.LoadAssetAtPath<AvatarMask>(LowerMaskPath) == null)
            AssetDatabase.CreateAsset(mask, LowerMaskPath);
        else
            EditorUtility.SetDirty(mask);

        return mask;
    }

    // ---- controller -----------------------------------------------------
    static AnimatorController BuildController(AvatarMask upperBodyMask)
    {
        // Rebuild IN PLACE instead of delete+recreate. Deleting the asset and
        // recreating it at the same path gives it a brand new GUID, which silently
        // breaks the Animator.runtimeAnimatorController reference on Player/Enemy
        // prefabs every single time this tool runs - that's why they kept needing
        // to be re-dragged in. Reusing the existing asset object keeps its GUID
        // stable, so prefab references survive a rebuild.
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller != null)
            ClearController(controller);
        else
            controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

        controller.AddParameter("MoveX", AnimatorControllerParameterType.Float);
        controller.AddParameter("MoveY", AnimatorControllerParameterType.Float);
        controller.AddParameter("WeaponClass", AnimatorControllerParameterType.Int); // 0 Unarmed 1 Pistol 2 Rifle 3 Sword
        controller.AddParameter("IsCrouching", AnimatorControllerParameterType.Bool);
        controller.AddParameter("IsGrounded", AnimatorControllerParameterType.Bool);
        // Real ground contact. IsGrounded is switched on early (landing prediction) to start the landing
        // clip; Land only returns to walking once this is true, so it can't finish mid-air.
        controller.AddParameter(new AnimatorControllerParameter { name = "TouchingGround", type = AnimatorControllerParameterType.Bool, defaultBool = true });
        controller.AddParameter("Aiming", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Fire", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Reload", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Melee", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Grenade", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Hit", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Dead", AnimatorControllerParameterType.Bool);
        // Marker: the trees below are laid out in m/s (MoveX = right, MoveY = forward), not the old
        // 0-3 "tier" values. CharacterAnimationDriver looks for this to choose what to feed them,
        // so a controller that hasn't been rebuilt yet keeps working with the old feed.
        controller.AddParameter(new AnimatorControllerParameter { name = "MoveInMetres", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
        // Which melee swing to play (0 horizontal, 1 backhand, 2 downward). Set before the Melee trigger.
        controller.AddParameter("MeleeIndex", AnimatorControllerParameterType.Int);
        controller.AddParameter("Shove", AnimatorControllerParameterType.Trigger);
        // Which shove to play: false = the light punch shove, true = the heavy kick shove. Set before the Shove trigger.
        controller.AddParameter("ShoveHeavy", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Injured", AnimatorControllerParameterType.Bool);
        // Which death clip to play - set by CharacterAnimationDriver.SetDead(dead, fromBack).
        controller.AddParameter("DeathFromBack", AnimatorControllerParameterType.Bool);
        // Signed turn rate in deg/s, fed by TurnInPlace.cs (negative = turning left). Only the
        // SIGN really matters to the blend tree below; TurnInPlace also drives this layer's
        // weight directly at runtime, so the exact magnitude scale here isn't load-bearing.
        controller.AddParameter("TurnSpeed", AnimatorControllerParameterType.Float);
        // 0 = default animation set, 1 = female set (CharacterAnimationDriver.animationVariant). Blends the unarmed trees.
        controller.AddParameter("AnimVariant", AnimatorControllerParameterType.Float);
        // Melee swings (MeleeWeapon): Swing + SwingIndex pick a state from MeleeSwings; SwingSpeed (default 1) multiplies a
        // heavy swing's playback speed, so 0 holds it at the top of the wind-up.
        controller.AddParameter("Swing", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("SwingIndex", AnimatorControllerParameterType.Int);
        controller.AddParameter(new AnimatorControllerParameter { name = "SwingSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });

        BuildBaseLayer(controller);
        BuildUpperBodyLayer(controller, upperBodyMask);
        var lowerMask = BuildLowerBodyMask();
        BuildFemaleLegsLayer(controller, lowerMask);   // before the turn layer, so turning in place still wins
        BuildTurnLayer(controller, lowerMask);

        // Turn on the IK pass so OnAnimatorIK() actually gets called - WeaponHandIK
        // uses it to snap the hand bones onto the weapon's grip points every frame,
        // after the animator has posed the arms. Without this the hands never move
        // and the "hand doesn't match the weapon" mismatch stays.
        var baseLayer = controller.layers[0];
        baseLayer.iKPass = true;
        var layers = controller.layers;
        layers[0] = baseLayer;
        controller.layers = layers;

        return controller;
    }

    // The ground speed everything walks at (PlayerMovement.walkSpeed / Enemy.walkSpeed / FriendlyAI.walkSpeed all default
    // to this). Each tree gets a point here, using the walk clip slowed or sped to fit, so a normal walk is a pure walk
    // cycle instead of a blend with idle or run. Keep in step with those if you change their walk speed.
    const float PlayerWalk = 1.9f;

    // -- base (full body) layer --
    static void BuildBaseLayer(AnimatorController controller)
    {
        var sm = controller.layers[0].stateMachine;

        // All trees are FreeformDirectional2D: Simple Directional only looks at the ANGLE of the
        // input and can't hold several clips in one direction (walk + run straight ahead).
        //
        // Every tree is laid out in VELOCITY space: MoveX = the character's local velocity to the
        // right in m/s, MoveY = forward in m/s (CharacterLocomotion feeds exactly that), and each clip
        // sits at the speed it was AUTHORED at. Those speeds were measured from the FBX root motion:
        //   rifle 8-way pack   walk 1.86   run 4.64   sprint 6.96   (diagonals are the same speed on the ring)
        //   pistol pack        walk 2.57   run 4.77   back 1.41 / 3.32   strafes 2.16 (L) / 2.43 (R)
        //   locomotion pack    walk 1.60   run 4.20   strafe walk 1.66 / run 4.34
        //   crouch walk        1.97 (diagonals 1.39 per axis)      injured   walk 1.23  run 2.37
        // The old layout used a 0-3 "tier" (walk 1 / run 2 / sprint 3) that assumed every walk clip was
        // half the speed of the run clip, so a 2.5 m/s AI walking on a 1.86 m/s clip skated by a third.
        // With the clips at their real speeds the tree blends walk -> run -> sprint for whatever speed the
        // character is actually doing and the feet stay on the ground.

        // Rifle: the full 8-way set (walk/run/sprint ring). Real diagonals and real strafes, where the old
        // shared tree faked diagonals by blending forward with a single-speed strafe.
        const float RW = 1.86f, RR = 4.64f, RS = 6.96f;
        const float RWd = 1.315f, RRd = 3.28f, RSd = 4.92f; // per-axis on the diagonals (ring / 1.414)
        BlendTree rifle = Locomotion2D("Rifle", new (float x, float y, string key, float ts)[]
        {
            (0, 0, "ri_idle", 1f),
            (0, RW, "ri_walk_fwd", 1f), (-RWd, RWd, "ri_walk_fwd_l", 1f), (RWd, RWd, "ri_walk_fwd_r", 1f),
            (-RW, 0, "ri_walk_left", 1f), (RW, 0, "ri_walk_right", 1f),
            (0, -RW, "ri_walk_back", 1f), (-RWd, -RWd, "ri_walk_back_l", 1f), (RWd, -RWd, "ri_walk_back_r", 1f),
            (0, RR, "ri_run_fwd", 1f), (-RRd, RRd, "ri_run_fwd_l", 1f), (RRd, RRd, "ri_run_fwd_r", 1f),
            (-RR, 0, "ri_run_left", 1f), (RR, 0, "ri_run_right", 1f),
            (0, -RR, "ri_run_back", 1f), (-RRd, -RRd, "ri_run_back_l", 1f), (RRd, -RRd, "ri_run_back_r", 1f),
            (0, RS, "ri_sprint_fwd", 1f), (-RSd, RSd, "ri_sprint_fwd_l", 1f), (RSd, RSd, "ri_sprint_fwd_r", 1f),
            (-RS, 0, "ri_sprint_left", 1f), (RS, 0, "ri_sprint_right", 1f),
        });

        // Pistol: keeps the handgun pack's own legs and stance (the standing pistol pose comes from these
        // clips - nothing masks the arms over them), now at their measured speeds. The strafe clips exist
        // at one speed only, so they are repeated further out with a matching time scale instead of
        // sliding at run speed.
        BlendTree pistol = Locomotion2D("Pistol", new (float x, float y, string key, float ts)[]
        {
            (0, 0, "pi_idle", 1f),
            (0, PlayerWalk, "pi_walk_fwd", PlayerWalk / 2.57f), (0, 2.57f, "pi_walk_fwd", 1f), (0, 4.77f, "pi_run_fwd", 1f), (0, 6.96f, "ri_sprint_fwd", 1f),
            (0, -1.41f, "pi_walk_back", 1f), (0, -3.32f, "pi_run_back", 1f),
            (-PlayerWalk, 0, "pi_left", PlayerWalk / 2.16f), (-4.6f, 0, "pi_left", 4.6f / 2.16f),
            (PlayerWalk, 0, "pi_right", PlayerWalk / 2.43f), (4.6f, 0, "pi_right", 4.6f / 2.43f),
        });

        // Unarmed: Locomotion Pack (backwards from the Melee pack, which is all there is).
        BlendTree unarmed = Locomotion2D("Unarmed", new (float x, float y, string key, float ts)[]
        {
            (0, 0, "un_idle", 1f),
            (0, 1.60f, "un_walk_fwd", 1f), (0, PlayerWalk, "un_walk_fwd", PlayerWalk / 1.60f), (0, 4.20f, "un_run_fwd", 1f),
            (0, -0.86f, "un_walk_back", 1f), (0, -2.03f, "un_run_back", 1f),
            (-1.66f, 0, "un_walk_left", 1f), (-4.34f, 0, "un_run_left", 1f),
            (1.66f, 0, "un_walk_right", 1f), (4.34f, 0, "un_run_right", 1f),
        });

        // Crouch (rifle pack walk-crouch set, 8-way).
        const float CW = 1.97f, CWd = 1.39f;
        const float CH = PlayerWalk * 0.5f, CHd = CH * 0.7071f;   // crouch walking speed (ring and per-axis diagonal)
        BlendTree rifleCrouch = Locomotion2D("RifleCrouch", new (float x, float y, string key, float ts)[]
        {
            (0, 0, "rc_idle", 1f),
            (0, CW, "rc_walk_fwd", 1f), (-CWd, CWd, "rc_walk_fwd_l", 1f), (CWd, CWd, "rc_walk_fwd_r", 1f),
            (-CW, 0, "rc_walk_left", 1f), (CW, 0, "rc_walk_right", 1f),
            (0, -CW, "rc_walk_back", 1f), (-CWd, -CWd, "rc_walk_back_l", 1f), (CWd, -CWd, "rc_walk_back_r", 1f),
            // The player crouch-walks at PlayerWalk * 0.5 (PlayerMovement.crouchSpeedMultiplier) - about half the clips'
            // 1.97 m/s - so without these the tree sat half way between idle and walk. Same clips, slowed to fit.
            (0, CH, "rc_walk_fwd", CH / CW), (-CHd, CHd, "rc_walk_fwd_l", CH / CW), (CHd, CHd, "rc_walk_fwd_r", CH / CW),
            (-CH, 0, "rc_walk_left", CH / CW), (CH, 0, "rc_walk_right", CH / CW),
            (0, -CH, "rc_walk_back", CH / CW), (-CHd, -CHd, "rc_walk_back_l", CH / CW), (CHd, -CHd, "rc_walk_back_r", CH / CW),
        });

        // Male Injured Pack has no strafe clips - sideways reuses forward.
        BlendTree injured = Locomotion2D("Injured", new (float x, float y, string key, float ts)[]
        {
            (0, 0, "inj_idle", 1f),
            (0, 1.23f, "inj_walk_fwd", 1f), (0, 2.37f, "inj_run_fwd", 1f),
            (0, -0.84f, "inj_walk_back", 1f), (0, -1.71f, "inj_run_back", 1f),
            (-1.23f, 0, "inj_walk_fwd", 1f), (1.23f, 0, "inj_walk_fwd", 1f),
        });

        // Female set: same shape, its own clips and measured speeds. The outer points repeat the run clips at the default
        // set's top speeds with a matching time scale, so a sprint doesn't skate on the slower female run.
        BlendTree unarmedFemale = Locomotion2D("UnarmedFemale", FemaleLocomotion());
        Motion unarmedMotion = VariantMotion("UnarmedVariants", unarmed, unarmedFemale, controller);

        AssetDatabase.AddObjectToAsset(unarmed, controller);
        AssetDatabase.AddObjectToAsset(pistol, controller);
        AssetDatabase.AddObjectToAsset(rifle, controller);
        AssetDatabase.AddObjectToAsset(rifleCrouch, controller);
        AssetDatabase.AddObjectToAsset(injured, controller);

        AnimatorState sUnarmed = AddMotionState(sm, "Unarmed", unarmedMotion, new Vector3(0, 300, 0));
        AnimatorState sPistol  = AddMotionState(sm, "Pistol", pistol, new Vector3(220, 300, 0));
        AnimatorState sRifle   = AddMotionState(sm, "Rifle", rifle, new Vector3(440, 300, 0));
        AnimatorState sCrouch  = AddMotionState(sm, "RifleCrouch", rifleCrouch, new Vector3(440, 460, 0));
        // The sword stance's legs/hips ARE the unarmed locomotion (including the female variant); the sword-specific
        // part is the upper-body overlay (swings) and the weapon IK. The state exists so WeaponClass 3 has a home.
        AnimatorState sSword   = AddMotionState(sm, "Sword", unarmedMotion, new Vector3(0, 380, 0));
        // Jump chain: take-off -> loop -> landing (these clips were imported but never used;
        // it was one falling-idle pose for every jump and fall).
        AnimatorState sAir     = AddMotionState(sm, "JumpUp", C("ri_jump_up") != null ? C("ri_jump_up") : C("airborne_idle"), new Vector3(220, 460, 0));
        AnimatorState sAirLoop = AddMotionState(sm, "Airborne", C("ri_jump_loop") != null ? C("ri_jump_loop") : C("airborne_idle"), new Vector3(220, 540, 0));
        AnimatorState sLand    = AddMotionState(sm, "Land", C("ri_jump_down"), new Vector3(0, 540, 0));
        // ---- full-body one-shot actions (base layer, so the legs and torso are driven too) ----------------
        // The masked UpperBody layer is faded out while any of these plays (CharacterAnimationDriver does it),
        // so the arms come from these clips; WeaponHandIK then decides per hand whether the clip or the weapon
        // grip owns it (SetAnimationFollow) and blends between the two.
        //
        // Shove: the light shove is the punch (authored left-handed, so the state is mirrored to punch with the
        // right hand); the heavy shove is the kick. Entered by the Shove trigger from any weapon pose.
        AnimatorState sShoveLight = C("shove_punch") != null ? AddMotionState(sm, "ShoveLight", C("shove_punch"), new Vector3(660, 540, 0)) : null;
        if (sShoveLight != null) { sShoveLight.mirror = true; sShoveLight.speed = CharacterAnimationDriver.ShoveLightSpeed; }
        AnimatorState sShoveHeavy = C("shove_kick") != null ? AddMotionState(sm, "ShoveHeavy", C("shove_kick"), new Vector3(660, 620, 0)) : null;
        if (sShoveHeavy != null) sShoveHeavy.speed = CharacterAnimationDriver.ShoveHeavySpeed;
        var shoveStates = new List<AnimatorState>();
        if (sShoveLight != null) shoveStates.Add(sShoveLight);
        if (sShoveHeavy != null) shoveStates.Add(sShoveHeavy);

        // Shoved: the victim's stagger, one clip per side the shove came from. Entered by script (CrossFade),
        // never by a transition, so any character in any pose can be shoved.
        var oneShotStates = new List<AnimatorState>();   // every state below exits back to the weapon pose
        float osY = 700f;
        foreach (var (stateName, key) in new[] { ("Shoved_Front", "shoved_front"), ("Shoved_Back", "shoved_back"),
                                                 ("Shoved_Left", "shoved_left"), ("Shoved_Right", "shoved_right") })
        {
            if (C(key) == null) continue;
            var st = AddMotionState(sm, stateName, C(key), new Vector3(880, osY, 0));
            st.speed = CharacterAnimationDriver.ShovedSpeed;
            oneShotStates.Add(st);
            osY += 60f;
        }
        // Vault / climb / step up: entered by script (VaultClimb), exit is timed so the crossfade back to the
        // weapon pose ends exactly on the last frame of the clip (VaultClimb relies on that timing).
        var vaultStates = new List<(AnimatorState state, float length)>();
        foreach (var (stateName, key) in new[] { ("Vault_StepUp", "vault_step"), ("Vault_Over", "vault_over"), ("Vault_Wall", "vault_wall") })
        {
            if (C(key) == null) continue;
            var st = AddMotionState(sm, stateName, C(key), new Vector3(1100, osY, 0));
            vaultStates.Add((st, C(key).length));
            osY += 60f;
        }
        // Idle gestures: full-body, entered by script (AIRelaxedIdle), same exit pattern.
        var gestureStates = new List<AnimatorState>();
        foreach (var (stateName, key) in new[] { ("Gesture_CheckShoe", "gest_shoe"), ("Gesture_ArmStretch", "gest_stretch") })
        {
            if (C(key) == null) continue;
            var st = AddMotionState(sm, stateName, C(key), new Vector3(1320, osY, 0));
            gestureStates.Add(st);
            osY += 60f;
        }
        AnimatorState sDeadCrouch = AddMotionState(sm, "DeathCrouch", C("ri_death_crouch") != null ? C("ri_death_crouch") : C("ri_death_front"), new Vector3(660, 620, 0));
        AnimatorState sInjured = AddMotionState(sm, "Injured", injured, new Vector3(660, 300, 0));
        AnimatorState sDead     = AddMotionState(sm, "Death", C("ri_death_front"), new Vector3(220, 620, 0));
        AnimatorState sDeadBack = AddMotionState(sm, "DeathBack", C("ri_death_back") != null ? C("ri_death_back") : C("ri_death_front"), new Vector3(440, 620, 0));
        sm.defaultState = sUnarmed;

        // ---- death variants ----
        // No transitions lead INTO these: Ragdoll picks one (from the direction of the killing shot, headshot,
        // stance, speed and how hard it hit - see CharacterAnimationDriver.TryChooseDeath) and crossfades to
        // it by name. The names must match that table. "M" states play the same clip mirrored, which doubles
        // the variety for free. Any variant whose clip isn't in the folder is simply not created and never chosen.
        float dy = 700f;
        AddDeathVariant(sm, "Death_FrontM",      C("ri_death_front"),  true,  new Vector3(0,    dy, 0));
        AddDeathVariant(sm, "Death_BackM",       C("ri_death_back"),   true,  new Vector3(220,  dy, 0));
        AddDeathVariant(sm, "Death_HeadFront",   C("ri_death_head_f"), false, new Vector3(440,  dy, 0));
        AddDeathVariant(sm, "Death_HeadFrontM",  C("ri_death_head_f"), true,  new Vector3(660,  dy, 0));
        AddDeathVariant(sm, "Death_HeadBack",    C("ri_death_head_b"), false, new Vector3(0,    dy + 80, 0));
        AddDeathVariant(sm, "Death_HeadBackM",   C("ri_death_head_b"), true,  new Vector3(220,  dy + 80, 0));
        AddDeathVariant(sm, "Death_Right",       C("ri_death_right"),  false, new Vector3(440,  dy + 80, 0));
        AddDeathVariant(sm, "Death_Left",        C("ri_death_right"),  true,  new Vector3(660,  dy + 80, 0));
        AddDeathVariant(sm, "Death_Moving",      C("wk_death_moving"), false, new Vector3(0,    dy + 160, 0));
        AddDeathVariant(sm, "Death_MovingM",     C("wk_death_moving"), true,  new Vector3(220,  dy + 160, 0));
        AddDeathVariant(sm, "Death_Stumble",     C("x_stumble_back"),  false, new Vector3(440,  dy + 160, 0));
        AddDeathVariant(sm, "Death_StumbleM",    C("x_stumble_back"),  true,  new Vector3(660,  dy + 160, 0));
        AddDeathVariant(sm, "Death_FallOver",    C("x_fall_over"),     false, new Vector3(0,    dy + 240, 0));
        AddDeathVariant(sm, "Death_FallOverM",   C("x_fall_over"),     true,  new Vector3(220,  dy + 240, 0));
        AddDeathVariant(sm, "Death_ShotFront",   C("x_shot_front"),    false, new Vector3(440,  dy + 240, 0));
        AddDeathVariant(sm, "Death_ShotFrontM",  C("x_shot_front"),    true,  new Vector3(660,  dy + 240, 0));
        AddDeathVariant(sm, "Death_ShotBack",    C("x_shot_back"),     false, new Vector3(0,    dy + 320, 0));
        AddDeathVariant(sm, "Death_ShotBackM",   C("x_shot_back"),     true,  new Vector3(220,  dy + 320, 0));
        // Stumble to the floor - the four directional clips picked by CharacterAnimationDriver.TryChooseStumble when a
        // moving body is shot (chance-based, see Ragdoll). Named for the side the shot came from.
        AddDeathVariant(sm, "Death_StumbleFront", C("stumble_front"),  false, new Vector3(440,  dy + 320, 0));
        AddDeathVariant(sm, "Death_StumbleBack",  C("stumble_back"),   false, new Vector3(660,  dy + 320, 0));
        AddDeathVariant(sm, "Death_StumbleLeft",  C("stumble_left"),   false, new Vector3(0,    dy + 400, 0));
        AddDeathVariant(sm, "Death_StumbleRight", C("stumble_right"),  false, new Vector3(220,  dy + 400, 0));

        var weaponStates = new[] { sUnarmed, sPistol, sRifle, sSword };
        for (int i = 0; i < weaponStates.Length; i++)
        for (int j = 0; j < weaponStates.Length; j++)
        {
            if (i == j) continue;
            var t = weaponStates[i].AddTransition(weaponStates[j]);
            t.hasExitTime = false; t.duration = 0.2f;
            t.AddCondition(AnimatorConditionMode.Equals, j, "WeaponClass");
        }

        // Rifle & Pistol <-> crouch. Both reuse the same crouch pose - hands are
        // already IK-attached to whichever weapon's own grip transform, so the
        // rifle pack's crouch clip reads fine held as a pistol too, no separate
        // pistol crouch animation needed.
        AddInstantTransition(sRifle, sCrouch, AnimatorConditionMode.If, 1, "IsCrouching");
        AddInstantTransition(sPistol, sCrouch, AnimatorConditionMode.If, 1, "IsCrouching");
        AddInstantTransition(sCrouch, sRifle, AnimatorConditionMode.IfNot, 0, "IsCrouching", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 2, "WeaponClass"));
        AddInstantTransition(sCrouch, sPistol, AnimatorConditionMode.IfNot, 0, "IsCrouching", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 1, "WeaponClass"));
        // Unarmed crouches too now - same legs, empty hands. Worth seeing how it reads
        // before deciding whether an unarmed-specific crouch pack is worth sourcing.
        AddInstantTransition(sUnarmed, sCrouch, AnimatorConditionMode.If, 1, "IsCrouching");
        AddInstantTransition(sCrouch, sUnarmed, AnimatorConditionMode.IfNot, 0, "IsCrouching", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 0, "WeaponClass"));
        // The sword crouches on the same pose too (hands stay on the sword's grips, like the pistol).
        AddInstantTransition(sSword, sCrouch, AnimatorConditionMode.If, 1, "IsCrouching");
        AddInstantTransition(sCrouch, sSword, AnimatorConditionMode.IfNot, 0, "IsCrouching", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 3, "WeaponClass"));

        // Airborne in/out
        foreach (var s in new[] { sUnarmed, sPistol, sRifle, sSword, sCrouch })
            AddInstantTransition(s, sAir, AnimatorConditionMode.IfNot, 0, "IsGrounded");
        var toLoop = sAir.AddTransition(sAirLoop);
        toLoop.hasExitTime = true; toLoop.exitTime = 0.85f; toLoop.duration = 0.15f;
        AddInstantTransition(sAir, sLand, AnimatorConditionMode.If, 0, "IsGrounded");
        AddInstantTransition(sAirLoop, sLand, AnimatorConditionMode.If, 0, "IsGrounded");
        AddInstantTransition(sLand, sAir, AnimatorConditionMode.IfNot, 0, "IsGrounded");

        // Shove: from any grounded weapon pose (not while airborne, crouched or injured), plays out, then returns to
        // whichever pose matches the current weapon. Light (punch) and heavy (kick) are separate states chosen by
        // the ShoveHeavy bool, each wired in from and out to every weapon pose.
        var weaponPoseStates = new[] { sUnarmed, sPistol, sRifle, sSword };
        foreach (var (shoveState, heavy) in new[] { (sShoveLight, false), (sShoveHeavy, true) })
        {
            if (shoveState == null) continue;
            foreach (var src in weaponPoseStates)
            {
                var into = src.AddTransition(shoveState);
                into.hasExitTime = false; into.duration = 0.1f;
                into.AddCondition(AnimatorConditionMode.If, 0, "Shove");
                into.AddCondition(heavy ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, "ShoveHeavy");
            }
            AddExitToWeaponPose(shoveState, weaponPoseStates, 0.78f, 0.22f, false);
            AddInstantTransition(shoveState, sAir, AnimatorConditionMode.IfNot, 0, "IsGrounded");
        }
        foreach (var st in oneShotStates)
        {
            AddExitToWeaponPose(st, weaponPoseStates, 0.72f, 0.28f, false);
            AddInstantTransition(st, sAir, AnimatorConditionMode.IfNot, 0, "IsGrounded");
        }
        // Vaults and gestures: the crossfade out is FIXED-length and timed to finish on the clip's last frame.
        foreach (var (st, len) in vaultStates)
            AddExitToWeaponPose(st, weaponPoseStates, Mathf.Clamp01(1f - CharacterAnimationDriver.OneShotExitFade / Mathf.Max(0.1f, len / Mathf.Max(0.01f, st.speed))), CharacterAnimationDriver.OneShotExitFade, true);
        foreach (var st in gestureStates)
        {
            float len = st.motion != null ? st.motion.averageDuration : 3f;
            AddExitToWeaponPose(st, weaponPoseStates, Mathf.Clamp01(1f - CharacterAnimationDriver.OneShotExitFade / Mathf.Max(0.1f, len / Mathf.Max(0.01f, st.speed))), CharacterAnimationDriver.OneShotExitFade, true);
        }

        // Landing plays out, then returns to whichever pose matches the weapon.
        for (int wc = 0; wc < 4; wc++)
        {
            var back = sLand.AddTransition(wc == 0 ? sUnarmed : (wc == 1 ? sPistol : (wc == 2 ? sRifle : sSword)));
            back.hasExitTime = true; back.exitTime = 0.8f; back.duration = 0.15f;
            back.AddCondition(AnimatorConditionMode.Equals, wc, "WeaponClass");
            back.AddCondition(AnimatorConditionMode.If, 0, "TouchingGround");
        }

        // Death from anywhere - added before the Injured wiring below so it's
        // evaluated first: Unity checks a state's transitions in the order
        // they were added, and Dead must win if both are true simultaneously.
        var deathSources = new[] { sUnarmed, sPistol, sRifle, sSword, sCrouch, sAir, sAirLoop, sLand, sInjured }
            .Concat(shoveStates).Concat(oneShotStates).Concat(vaultStates.Select(v => v.state)).Concat(gestureStates).ToArray();
        foreach (var s in deathSources)
        {
            if (s == sCrouch)
            {
                var tc = s.AddTransition(sDeadCrouch);
                tc.hasExitTime = false; tc.duration = 0.1f;
                tc.AddCondition(AnimatorConditionMode.If, 0, "Dead");
            }
            // Front death first, then back - Unity evaluates a state's transitions in
            // the order they were added, and both carry the same "Dead" condition, so
            // the back one is distinguished by DeathFromBack being true.
            var tb = s.AddTransition(sDeadBack);
            tb.hasExitTime = false; tb.duration = 0.1f;
            tb.AddCondition(AnimatorConditionMode.If, 0, "Dead");
            tb.AddCondition(AnimatorConditionMode.If, 0, "DeathFromBack");

            var t = s.AddTransition(sDead);
            t.hasExitTime = false; t.duration = 0.1f;
            t.AddCondition(AnimatorConditionMode.If, 0, "Dead");
        }

        // Injured (low health) overrides normal ground movement, and returns to
        // whichever weapon pose is currently active once health recovers.
        var groundStates = new[] { sUnarmed, sPistol, sRifle, sSword, sCrouch };
        foreach (var s in groundStates)
            AddInstantTransition(s, sInjured, AnimatorConditionMode.If, 0, "Injured");
        AddInstantTransition(sInjured, sUnarmed, AnimatorConditionMode.IfNot, 0, "Injured", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 0, "WeaponClass"));
        AddInstantTransition(sInjured, sPistol, AnimatorConditionMode.IfNot, 0, "Injured", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 1, "WeaponClass"));
        AddInstantTransition(sInjured, sRifle, AnimatorConditionMode.IfNot, 0, "Injured", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 2, "WeaponClass"));
        AddInstantTransition(sInjured, sSword, AnimatorConditionMode.IfNot, 0, "Injured", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 3, "WeaponClass"));
    }

    // Exit from a one-shot base-layer action back to whichever weapon pose is active (one transition per weapon
    // class, told apart by the WeaponClass condition). fixedDuration = crossfade length in seconds (true) or as a
    // fraction of the clip (false).
    static void AddExitToWeaponPose(AnimatorState from, AnimatorState[] weaponPoses, float exitTime, float duration, bool fixedDuration)
    {
        for (int wc = 0; wc < weaponPoses.Length; wc++)
        {
            var back = from.AddTransition(weaponPoses[wc]);
            back.hasExitTime = true; back.exitTime = exitTime;
            back.hasFixedDuration = fixedDuration; back.duration = duration;
            back.AddCondition(AnimatorConditionMode.Equals, wc, "WeaponClass");
        }
    }

    // A death state that is only ever entered by CrossFade. Skipped when its clip wasn't imported.
    static void AddDeathVariant(AnimatorStateMachine sm, string name, AnimationClip clip, bool mirror, Vector3 pos)
    {
        if (clip == null) return;
        var s = AddMotionState(sm, name, clip, pos);
        s.mirror = mirror;
    }

    static void AddInstantTransition(AnimatorState from, AnimatorState to, AnimatorConditionMode mode, float threshold, string param, System.Action<AnimatorStateTransition> extra = null)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = false;
        // Crouch and injured changes are whole-body pose swaps, so they get a longer, softer blend than
        // the quick triggers on the upper-body layer.
        t.duration = (param == "IsCrouching" || param == "Injured") ? 0.25f : 0.15f;
        t.AddCondition(mode, threshold, param);
        extra?.Invoke(t);
    }

    // -- upper body (masked) layer --
    static void BuildUpperBodyLayer(AnimatorController controller, AvatarMask mask)
    {
        var layer = new AnimatorControllerLayer
        {
            name = "UpperBody",
            defaultWeight = 1f,
            avatarMask = mask,
            blendingMode = AnimatorLayerBlendingMode.Override,
            // This layer overrides arm/spine/head bones AFTER the base layer every
            // frame, which was silently overwriting WeaponHandIK's hand placement the
            // instant it was applied - that's why hands tracked rotation loosely but
            // never actually reached the grip and never responded to camera pitch.
            // Enabling the IK pass here too makes this (the layer that actually owns
            // the arms) apply the IK snap last, so it sticks.
            iKPass = true,
            stateMachine = new AnimatorStateMachine { name = "UpperBody", hideFlags = HideFlags.HideInHierarchy }
        };
        AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
        controller.AddLayer(layer);
        var sm = layer.stateMachine;

        AnimatorState idle   = AddMotionState(sm, "UB_Idle", null, new Vector3(0, 0, 0));
        AnimatorState aim    = AddMotionState(sm, "UB_Aim", C("ri_idle_aim"), new Vector3(220, 0, 0));
        AnimatorState fire   = AddMotionState(sm, "UB_Fire", C("act_fire_rifle"), new Vector3(220, 140, 0));
        AnimatorState reload = AddMotionState(sm, "UB_Reload", C("act_reload"), new Vector3(0, 280, 0));
        // Three swings picked by MeleeIndex. Measured from the clips: the strike (peak hand speed) lands
        // ~0.85-1.0 s into each, and the arm has only really settled again ~2 s in - far too slow for a
        // melee that used to deal its damage the instant the key was pressed. So each plays at 1.8x from
        // a point already part-way into the wind-up (transition offset), which puts the strike at ~0.36 s
        // (CharacterAnimationDriver.MeleeStrikeDelay - PlayerMelee and Enemy delay their damage by it) and
        // frees the layer again by ~0.85 s, so firing isn't locked out for two seconds after a swing.
        AnimatorState melee  = AddMotionState(sm, "UB_Melee", C("act_melee"), new Vector3(0, 140, 0));
        AnimatorState melee2 = C("act_melee_b") != null ? AddMotionState(sm, "UB_Melee2", C("act_melee_b"), new Vector3(-220, 140, 0)) : null;
        AnimatorState melee3 = C("act_melee_d") != null ? AddMotionState(sm, "UB_Melee3", C("act_melee_d"), new Vector3(-220, 220, 0)) : null;
        // Melee swings (MeleeWeapon, Sword and Axe styles, light combo + heavy): the masked upper body only, so the legs
        // keep walking underneath. Every state is reachable from idle AND from every other swing (see the transitions
        // below) so one swing can cut straight into the next. Timing, speeds and start offsets: MeleeSwings in the driver.
        var swingStates = new AnimatorState[MeleeSwings.All.Length];
        for (int i = 0; i < MeleeSwings.All.Length; i++)
        {
            var d = MeleeSwings.All[i];
            var swingClip = C(d.clipKey);
            if (swingClip == null) continue;
            var st = AddMotionState(sm, d.state, swingClip, new Vector3(-440 - 220 * (i / MeleeSwings.PerStyle), 140 + 80 * (i % MeleeSwings.PerStyle), 0));
            st.speed = d.speed;
            if (d.heavy) { st.speedParameterActive = true; st.speedParameter = "SwingSpeed"; }
            swingStates[i] = st;
        }
        melee.speed = 1.8f;
        if (melee2 != null) melee2.speed = 1.8f;
        if (melee3 != null) melee3.speed = 1.8f;
        AnimatorState hit = AddMotionState(sm, "UB_Hit", C("act_hit"), new Vector3(440, 140, 0));
        AnimatorState grenade = AddMotionState(sm, "UB_Grenade", C("act_grenade"), new Vector3(220, 280, 0));
        // Pistol upper body, masked over whatever the legs are doing. This is what makes
        // the shared (rifle-authored) crouch tree usable for a handgun: the legs crouch,
        // this replaces the arms/spine with the pistol pack's own ready stance.
        // Pistol kneeling idle while crouched (the handgun pack's own pose), plain pistol
        // idle otherwise - both are upper-body only here, so the legs keep doing whatever
        // the base layer says.
        AnimatorState pistolPose = AddMotionState(sm, "UB_PistolPose",
            C("pi_kneel_idle") != null ? C("pi_kneel_idle") : C("pi_idle"), new Vector3(440, 0, 0));
        sm.defaultState = idle;

        // Basic idle gestures - AIRelaxedIdle discovers which of these exist via Animator.HasState and
        // CrossFadeInFixedTime's straight to whichever it picks, then back to UB_Idle when done. No transitions
        // needed in the graph at all, same as how melee variants are driven entirely from script.
        if (C("gest_look_away") != null)     AddMotionState(sm, "UB_GestureLookAway",     C("gest_look_away"),     new Vector3(660, 280, 0));
        if (C("gest_weight_shift") != null)  AddMotionState(sm, "UB_GestureWeightShift",  C("gest_weight_shift"),  new Vector3(660, 360, 0));
        if (C("gest_relieved_sigh") != null) AddMotionState(sm, "UB_GestureSigh",         C("gest_relieved_sigh"), new Vector3(660, 440, 0));
        if (C("gest_thoughtful") != null)    AddMotionState(sm, "UB_GestureThoughtful",   C("gest_thoughtful"),    new Vector3(660, 520, 0));
        if (C("gest_melee_look1") != null)   AddMotionState(sm, "UB_GestureMeleeLook1",   C("gest_melee_look1"),   new Vector3(880, 280, 0));
        if (C("gest_melee_look2") != null)   AddMotionState(sm, "UB_GestureMeleeLook2",   C("gest_melee_look2"),   new Vector3(880, 360, 0));
        if (C("gest_unarmed_look1") != null) AddMotionState(sm, "UB_GestureUnarmedLook1", C("gest_unarmed_look1"), new Vector3(880, 440, 0));
        if (C("gest_unarmed_look2") != null) AddMotionState(sm, "UB_GestureUnarmedLook2", C("gest_unarmed_look2"), new Vector3(880, 520, 0));

        AddInstantTransition(idle, pistolPose, AnimatorConditionMode.If, 0, "IsCrouching",
            extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 1, "WeaponClass"));
        AddInstantTransition(pistolPose, idle, AnimatorConditionMode.IfNot, 0, "IsCrouching");
        AddInstantTransition(pistolPose, idle, AnimatorConditionMode.NotEqual, 1, "WeaponClass");

        // Only Rifle raises into the masked Aim pose. The Pistol_Handgun pack has no
        // separate "resting" pose - pi_idle is already a raised, ready-to-fire stance -
        // so forcing the Rifle pack's two-handed "ri_idle_aim" clip on a pistol-holder
        // here was overwriting a perfectly good pose with a mismatched one (no rifle in
        // hand to justify the two-handed grip, hence the arms looking like they're
        // bracing/resting an invisible weapon off to the side instead of firing).
        AddInstantTransition(idle, aim, AnimatorConditionMode.If, 0, "Aiming",
            extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 2, "WeaponClass"));
        AddInstantTransition(aim, idle, AnimatorConditionMode.IfNot, 0, "Aiming");

        foreach (var s in new[] { idle, aim, pistolPose })
        {
            var tf = s.AddTransition(fire);
            tf.hasExitTime = false; tf.duration = 0.05f;
            tf.AddCondition(AnimatorConditionMode.If, 0, "Fire");

            var tr = s.AddTransition(reload);
            tr.hasExitTime = false; tr.duration = 0.1f;
            tr.AddCondition(AnimatorConditionMode.If, 0, "Reload");

            var tm = s.AddTransition(melee);
            tm.hasExitTime = false; tm.duration = 0.05f; tm.offset = 0.12f;
            tm.AddCondition(AnimatorConditionMode.If, 0, "Melee");
            tm.AddCondition(AnimatorConditionMode.Equals, 0, "MeleeIndex");
            if (melee2 != null)
            {
                var tm2 = s.AddTransition(melee2);
                tm2.hasExitTime = false; tm2.duration = 0.05f; tm2.offset = 0.10f;
                tm2.AddCondition(AnimatorConditionMode.If, 0, "Melee");
                tm2.AddCondition(AnimatorConditionMode.Equals, 1, "MeleeIndex");
            }
            if (melee3 != null)
            {
                var tm3 = s.AddTransition(melee3);
                tm3.hasExitTime = false; tm3.duration = 0.05f; tm3.offset = 0.10f;
                tm3.AddCondition(AnimatorConditionMode.If, 0, "Melee");
                tm3.AddCondition(AnimatorConditionMode.Equals, 2, "MeleeIndex");
            }

            var th = s.AddTransition(hit);
            th.hasExitTime = false; th.duration = 0.05f;
            th.AddCondition(AnimatorConditionMode.If, 0, "Hit");

            var tg = s.AddTransition(grenade);
            tg.hasExitTime = false; tg.duration = 0.1f;
            tg.AddCondition(AnimatorConditionMode.If, 0, "Grenade");
        }

        // Fire is a short loud loop while the trigger stays fresh; return to aim shortly after.
        var backFromFire = fire.AddTransition(aim);
        backFromFire.hasExitTime = true; backFromFire.exitTime = 0.12f; backFromFire.duration = 0.05f;
        backFromFire.hasFixedDuration = true;

        // A flinch is short and never blocks shooting or reloading.
        var backFromHit = hit.AddTransition(idle);
        backFromHit.hasExitTime = true; backFromHit.exitTime = 0.55f; backFromHit.duration = 0.15f;
        var hitToFire = hit.AddTransition(fire);
        hitToFire.hasExitTime = false; hitToFire.duration = 0.05f; hitToFire.AddCondition(AnimatorConditionMode.If, 0, "Fire");
        var hitToReload = hit.AddTransition(reload);
        hitToReload.hasExitTime = false; hitToReload.duration = 0.1f; hitToReload.AddCondition(AnimatorConditionMode.If, 0, "Reload");

        var backFromReload = reload.AddTransition(idle);
        backFromReload.hasExitTime = true; backFromReload.exitTime = 0.95f; backFromReload.duration = 0.1f;

        var backFromGrenade = grenade.AddTransition(idle);
        backFromGrenade.hasExitTime = true; backFromGrenade.exitTime = 0.95f; backFromGrenade.duration = 0.15f;

        for (int i = 0; i < swingStates.Length; i++)
        {
            if (swingStates[i] == null) continue;
            var d = MeleeSwings.All[i];
            var backFromSwing = swingStates[i].AddTransition(idle);
            backFromSwing.hasExitTime = true; backFromSwing.exitTime = d.exit / d.length;
            backFromSwing.duration = 0.2f; backFromSwing.hasFixedDuration = true;
        }

        // Into a swing: from idle (and the other upper-body poses), and from every other swing. The offset skips the dead
        // time at the start of the clip; the fixed fade is the blend-in.
        var swingSources = new List<AnimatorState> { idle, aim, pistolPose };
        swingSources.AddRange(swingStates.Where(x => x != null));
        foreach (var src in swingSources)
        for (int i = 0; i < swingStates.Length; i++)
        {
            if (swingStates[i] == null) continue;
            var d = MeleeSwings.All[i];
            var ts = src.AddTransition(swingStates[i]);
            ts.hasExitTime = false;
            ts.hasFixedDuration = true; ts.duration = d.fade;
            ts.offset = d.offset / d.length;
            ts.canTransitionToSelf = true;
            ts.AddCondition(AnimatorConditionMode.If, 0, "Swing");
            ts.AddCondition(AnimatorConditionMode.Equals, i, "SwingIndex");
        }

        foreach (var m in new[] { melee, melee2, melee3 })
        {
            if (m == null) continue;
            var backFromMelee = m.AddTransition(idle);
            backFromMelee.hasExitTime = true; backFromMelee.exitTime = 0.72f; backFromMelee.duration = 0.15f;
        }
    }

    // -- turn-in-place (masked to legs only) layer --
    //
    // Real footwork for turning on the spot (a pivot step), instead of the whole body just
    // silently rotating under a locomotion tree that has no idea a turn is happening. This
    // layer never decides WHETHER or how fast to turn - Enemy.cs/FriendlyAI.cs/PlayerMovement
    // keep doing exactly what they already do to actually rotate the transform. TurnInPlace.cs
    // (runtime) just WATCHES that rotation, feeds its rate into TurnSpeed, and fades this
    // layer's weight up while idle-turning and down the instant real movement starts (the
    // directional locomotion trees already cover turning while walking/strafing - this layer
    // is only for turning from a standstill). Layer weight starts at 0 for exactly that reason:
    // until TurnInPlace.cs raises it, this layer is fully transparent and the base layer's own
    // legs show through untouched.
    // Female gait for characters carrying a PISTOL or RIFLE. The Female Locomotion Pack only has unarmed clips, so there is no
    // female armed stance; instead this layer plays the female locomotion on the LEGS ONLY (lower-body mask) over the armed
    // base layer, whose spine, arms and weapon pose stay as they are. Its weight is 0 and is driven by
    // CharacterAnimationDriver (Animation Variant = Female, pistol/rifle class, standing, grounded, no full-body action).
    static void BuildFemaleLegsLayer(AnimatorController controller, AvatarMask lowerBodyMask)
    {
        if (C("f_walk_fwd") == null) return;        // Female Locomotion Pack not imported: no layer, the dropdown just has less to switch

        var layer = new AnimatorControllerLayer
        {
            name = "FemaleLegs",
            defaultWeight = 0f,
            avatarMask = lowerBodyMask,
            blendingMode = AnimatorLayerBlendingMode.Override,
            stateMachine = new AnimatorStateMachine { name = "FemaleLegs", hideFlags = HideFlags.HideInHierarchy }
        };
        AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
        controller.AddLayer(layer);

        BlendTree tree = Locomotion2D("FemaleLegs", FemaleLocomotion());
        AssetDatabase.AddObjectToAsset(tree, controller);
        var st = AddMotionState(layer.stateMachine, "FemaleLegs", tree, new Vector3(0, 0, 0));
        layer.stateMachine.defaultState = st;
    }

    static void BuildTurnLayer(AnimatorController controller, AvatarMask lowerBodyMask)
    {
        var layer = new AnimatorControllerLayer
        {
            name = "TurnInPlace",
            defaultWeight = 0f,
            avatarMask = lowerBodyMask,
            blendingMode = AnimatorLayerBlendingMode.Override,
            stateMachine = new AnimatorStateMachine { name = "TurnInPlace", hideFlags = HideFlags.HideInHierarchy }
        };
        AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
        controller.AddLayer(layer);
        var sm = layer.stateMachine;

        // Each is a 3-point Simple1D blend on TurnSpeed: full left clip at -90, the matching
        // idle pose (reused, not a real "hold" - see Turn1D) at 0, full right clip at +90. The
        // idle midpoint keeps the pose sane at low |TurnSpeed| while this layer's weight is
        // still ramping in/out, rather than a raw 50/50 blend of the two turn clips.
        BlendTree unarmedTree = Turn1D("TL_Unarmed", "un_turn_l", "un_idle", "un_turn_r");
        BlendTree pistolTree  = Turn1D("TL_Pistol", "un_turn_l", "pi_idle", "un_turn_r"); // no pistol-specific turn clip yet, see the un_turn_l/r ClipDefs
        BlendTree rifleTree   = Turn1D("TL_Rifle", "ri_turn_l", "ri_idle", "ri_turn_r");
        BlendTree crouchTree  = Turn1D("TL_Crouch", "rc_turn_l", "rc_idle", "rc_turn_r");
        BlendTree unarmedTreeFemale = Turn1D("TL_UnarmedFemale", "f_turn_l", "f_idle", "f_turn_r");
        Motion unarmedTurnMotion = VariantMotion("TL_UnarmedVariants", unarmedTree, unarmedTreeFemale, controller);
        AssetDatabase.AddObjectToAsset(unarmedTree, controller);
        AssetDatabase.AddObjectToAsset(pistolTree, controller);
        AssetDatabase.AddObjectToAsset(rifleTree, controller);
        AssetDatabase.AddObjectToAsset(crouchTree, controller);

        AnimatorState tUnarmed = AddMotionState(sm, "TL_Unarmed", unarmedTurnMotion, new Vector3(0, 0, 0));
        AnimatorState tPistol  = AddMotionState(sm, "TL_Pistol", pistolTree, new Vector3(220, 0, 0));
        AnimatorState tRifle   = AddMotionState(sm, "TL_Rifle", rifleTree, new Vector3(440, 0, 0));
        AnimatorState tCrouch  = AddMotionState(sm, "TL_Crouch", crouchTree, new Vector3(440, 140, 0));
        AnimatorState tSword   = AddMotionState(sm, "TL_Sword", unarmedTurnMotion, new Vector3(220, 140, 0));
        sm.defaultState = tUnarmed;

        // Same weapon-swap wiring as the base layer (see BuildBaseLayer) - kept identical on
        // purpose so this layer never disagrees with the base layer about which weapon's legs
        // should be showing.
        var weaponStates = new[] { tUnarmed, tPistol, tRifle, tSword };
        for (int i = 0; i < weaponStates.Length; i++)
        for (int j = 0; j < weaponStates.Length; j++)
        {
            if (i == j) continue;
            var t = weaponStates[i].AddTransition(weaponStates[j]);
            t.hasExitTime = false; t.duration = 0.2f;
            t.AddCondition(AnimatorConditionMode.Equals, j, "WeaponClass");
        }

        AddInstantTransition(tRifle, tCrouch, AnimatorConditionMode.If, 1, "IsCrouching");
        AddInstantTransition(tPistol, tCrouch, AnimatorConditionMode.If, 1, "IsCrouching");
        AddInstantTransition(tUnarmed, tCrouch, AnimatorConditionMode.If, 1, "IsCrouching");
        AddInstantTransition(tSword, tCrouch, AnimatorConditionMode.If, 1, "IsCrouching");
        AddInstantTransition(tCrouch, tRifle, AnimatorConditionMode.IfNot, 0, "IsCrouching", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 2, "WeaponClass"));
        AddInstantTransition(tCrouch, tPistol, AnimatorConditionMode.IfNot, 0, "IsCrouching", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 1, "WeaponClass"));
        AddInstantTransition(tCrouch, tUnarmed, AnimatorConditionMode.IfNot, 0, "IsCrouching", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 0, "WeaponClass"));
        AddInstantTransition(tCrouch, tSword, AnimatorConditionMode.IfNot, 0, "IsCrouching", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 3, "WeaponClass"));
    }

    // ---- helpers ----------------------------------------------------------
    static AnimatorState AddMotionState(AnimatorStateMachine sm, string name, Motion motion, Vector3 pos)
    {
        var s = sm.AddState(name, pos);
        s.motion = motion;
        s.writeDefaultValues = true;
        return s;
    }

    // Where in each locomotion clip's loop the RIGHT foot passes the left one while moving ahead in the direction the
    // clip travels (0-1 of the cycle). Measured from the FBX skeletons (Python forward-kinematics over the Mixamo files:
    // right-foot-minus-left-foot offset along the travel direction, first rising zero crossing). Every locomotion clip
    // is exactly one step cycle long, but the packs were authored separately, so their cycles start at different foot
    // phases: the handgun strafes are ~0.25-0.35 of a cycle out from its forward walk, the sword strafes up to half a
    // cycle, the rifle sides ~0.13. A blend tree plays all of its clips from the same normalized time, so blending
    // forward into a diagonal or a strafe mixed two clips with their legs in different parts of the step - the
    // bouncy, mushy legs. Locomotion2D now gives every child a cycleOffset that lines each clip's step up with the
    // tree's forward walk.
    static readonly Dictionary<string, float> FootPhase = new Dictionary<string, float>
    {
            { "f_run_fwd", 0.531f }, { "f_run_left", 0.554f }, { "f_run_right", 0.547f }, { "f_walk_fwd", 0.533f },
            { "f_walk_left", 0.590f }, { "f_walk_right", 0.549f }, { "pi_left", 0.841f }, { "pi_right", 0.244f },
            { "pi_run_back", 0.486f }, { "pi_run_fwd", 0.458f }, { "pi_walk_back", 0.455f }, { "pi_walk_fwd", 0.494f },
            { "rc_walk_back", 0.421f }, { "rc_walk_back_l", 0.427f }, { "rc_walk_back_r", 0.421f }, { "rc_walk_fwd", 0.568f },
            { "rc_walk_fwd_l", 0.580f }, { "rc_walk_fwd_r", 0.513f }, { "rc_walk_left", 0.483f }, { "rc_walk_right", 0.498f },
            { "ri_run_back", 0.527f }, { "ri_run_back_l", 0.526f }, { "ri_run_back_r", 0.450f }, { "ri_run_fwd", 0.584f },
            { "ri_run_fwd_l", 0.570f }, { "ri_run_fwd_r", 0.520f }, { "ri_run_left", 0.575f }, { "ri_run_right", 0.470f },
            { "ri_sprint_fwd", 0.532f }, { "ri_sprint_fwd_l", 0.504f }, { "ri_sprint_fwd_r", 0.493f },
            { "ri_sprint_left", 0.550f }, { "ri_sprint_right", 0.466f }, { "ri_walk_back", 0.461f },
            { "ri_walk_back_l", 0.449f }, { "ri_walk_back_r", 0.438f }, { "ri_walk_fwd", 0.552f }, { "ri_walk_fwd_l", 0.561f },
            { "ri_walk_fwd_r", 0.545f }, { "ri_walk_left", 0.583f }, { "ri_walk_right", 0.424f }, { "sw_run_back", 0.429f },
            { "sw_run_fwd", 0.478f }, { "sw_run_left", 0.989f }, { "sw_run_right", 0.005f }, { "sw_walk_back", 0.447f },
            { "sw_walk_fwd", 0.531f }, { "sw_walk_left", 0.273f }, { "sw_walk_right", 0.754f }, { "un_run_back", 0.546f },
            { "un_run_fwd", 0.504f }, { "un_run_left", 0.520f }, { "un_run_right", 0.569f }, { "un_walk_back", 0.535f },
            { "un_walk_fwd", 0.510f }, { "un_walk_left", 0.536f }, { "un_walk_right", 0.481f },
    };

    // Blend tree in velocity space: each point is (right m/s, forward m/s, clip key, child time scale).
    static BlendTree Locomotion2D(string name, (float x, float y, string key, float ts)[] points)
    {
        var tree = new BlendTree { name = name, blendType = BlendTreeType.FreeformDirectional2D };
        tree.blendParameter = "MoveX";
        tree.blendParameterY = "MoveY";
        tree.hideFlags = HideFlags.HideInHierarchy;
        var found = new List<(AnimationClip clip, Vector2 pos, float ts, string key)>();
        foreach (var p in points)
        {
            var clip = C(p.key);
            if (clip == null) continue;
            found.Add((clip, new Vector2(p.x, p.y), p.ts, p.key));
        }

        // Reference step phase: the slowest straight-ahead walk in the tree that has a measurement.
        float refPhase = -1f, refSpeed = float.MaxValue;
        foreach (var f in found)
            if (Mathf.Abs(f.pos.x) < 0.001f && f.pos.y > 0.1f && f.pos.y < refSpeed && FootPhase.ContainsKey(f.key))
            { refSpeed = f.pos.y; refPhase = FootPhase[f.key]; }

        foreach (var f in found) tree.AddChild(f.clip, f.pos);
        var children = tree.children;
        for (int k = 0; k < children.Length && k < found.Count; k++)
        {
            children[k].timeScale = found[k].ts;
            if (refPhase >= 0f && FootPhase.TryGetValue(found[k].key, out float phase))
                children[k].cycleOffset = Mathf.Repeat(phase - refPhase, 1f);
        }
        tree.children = children;
        return tree;
    }

    // The female unarmed locomotion points (right m/s, forward m/s, clip key, time scale): used by the unarmed tree AND by the
    // FemaleLegs overlay layer that gives pistol/rifle carriers the female gait. Same shape as the default set, measured speeds;
    // the outer points repeat the run clips at the default set's top speeds with a matching time scale so a sprint doesn't skate.
    static (float x, float y, string key, float ts)[] FemaleLocomotion()
    {
        return new (float x, float y, string key, float ts)[]
        {
            (0, 0, "f_idle", 1f),
            (0, 1.61f, "f_walk_fwd", 1f), (0, PlayerWalk, "f_walk_fwd", PlayerWalk / 1.61f), (0, 3.62f, "f_run_fwd", 1f), (0, 4.20f, "f_run_fwd", 4.20f / 3.62f),
            (0, -0.86f, "un_walk_back", 1f), (0, -2.03f, "un_run_back", 1f),
            (-1.87f, 0, "f_walk_left", 1f), (-3.47f, 0, "f_run_left", 1f), (-4.34f, 0, "f_run_left", 4.34f / 3.47f),
            (1.87f, 0, "f_walk_right", 1f), (3.47f, 0, "f_run_right", 1f), (4.34f, 0, "f_run_right", 4.34f / 3.47f),
        };
    }

    // Default / female pair on the AnimVariant parameter: 0 plays `defaultMotion`, 1 plays `femaleMotion`. The tree the
    // parameter isn't pointing at has zero weight, so it costs nothing. With no female clips found (pack not imported)
    // the default is returned untouched and the variant dropdown simply does nothing.
    static Motion VariantMotion(string name, BlendTree defaultMotion, BlendTree femaleMotion, AnimatorController controller)
    {
        if (femaleMotion == null || femaleMotion.children.Length == 0) return defaultMotion;

        AssetDatabase.AddObjectToAsset(femaleMotion, controller);
        var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D };
        tree.blendParameter = "AnimVariant";
        tree.useAutomaticThresholds = false;
        tree.hideFlags = HideFlags.HideInHierarchy;
        tree.AddChild(defaultMotion, 0f);
        tree.AddChild(femaleMotion, 1f);
        AssetDatabase.AddObjectToAsset(tree, controller);
        return tree;
    }

    // 3-point Simple1D blend for the turn-in-place layer: left clip at -90, idle at 0, right
    // clip at +90. Threshold values only need the right SIGN to pick a side - TurnInPlace.cs
    // owns the actual deg/s -> weight mapping via this layer's runtime weight, not this tree.
    static BlendTree Turn1D(string name, string leftKey, string idleKey, string rightKey)
    {
        var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D };
        tree.blendParameter = "TurnSpeed";
        tree.useAutomaticThresholds = false;
        tree.hideFlags = HideFlags.HideInHierarchy;

        var left = C(leftKey);
        var idle = C(idleKey);
        var right = C(rightKey);
        if (left != null) tree.AddChild(left, -90f);
        if (idle != null) tree.AddChild(idle, 0f);
        if (right != null) tree.AddChild(right, 90f);
        return tree;
    }

    static BlendTree Freeform2D(string name, (float x, float y, string key)[] points)
    {
        var tree = new BlendTree { name = name, blendType = BlendTreeType.FreeformDirectional2D };
        tree.blendParameter = "MoveX";
        tree.blendParameterY = "MoveY";
        tree.hideFlags = HideFlags.HideInHierarchy;
        foreach (var p in points)
        {
            var clip = C(p.key);
            if (clip == null) continue;
            tree.AddChild(clip, new Vector2(p.x, p.y));
        }
        return tree;
    }

    static BlendTree Directional2D(string name, (float x, float y, string key)[] points)
    {
        var tree = new BlendTree { name = name, blendType = BlendTreeType.SimpleDirectional2D };
        tree.blendParameter = "MoveX";
        tree.blendParameterY = "MoveY";
        tree.hideFlags = HideFlags.HideInHierarchy;
        foreach (var p in points)
        {
            var clip = C(p.key);
            if (clip == null) continue;
            tree.AddChild(clip, new Vector2(p.x, p.y));
        }
        return tree;
    }
}