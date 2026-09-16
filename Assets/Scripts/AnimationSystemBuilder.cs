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
///                       driven by MoveX/MoveY, switched by the WeaponClass int.
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

    // ---- clip descriptor -------------------------------------------------
    struct ClipDef
    {
        public string key;      // how we refer to it below
        public string pack;     // sub-folder under Mixamo/
        public string file;     // fbx file name (no extension)
        public bool loop;
        public bool rootMotion; // bake XZ root motion out (we always drive movement via code)
        public ClipDef(string key, string pack, string file, bool loop)
        { this.key = key; this.pack = pack; this.file = file; this.loop = loop; this.rootMotion = false; }
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
        new ClipDef("pi_walk_fwd",   "Pistol_Handgun Locomotion Pack", "pistol walk arc", true),
        new ClipDef("pi_run_fwd",    "Pistol_Handgun Locomotion Pack", "pistol run arc", true),
        new ClipDef("pi_walk_back",  "Pistol_Handgun Locomotion Pack", "pistol walk backward arc", true),
        new ClipDef("pi_run_back",   "Pistol_Handgun Locomotion Pack", "pistol run backward arc", true),
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

        // Rifle crouch - Pro Rifle Pack
        new ClipDef("rc_idle",       "Pro Rifle Pack", "idle crouching aiming", true),
        new ClipDef("rc_walk_fwd",   "Pro Rifle Pack", "walk crouching forward", true),
        new ClipDef("rc_walk_fwd_l", "Pro Rifle Pack", "walk crouching forward left", true),
        new ClipDef("rc_walk_fwd_r", "Pro Rifle Pack", "walk crouching forward right", true),
        new ClipDef("rc_walk_back",  "Pro Rifle Pack", "walk crouching backward", true),
        new ClipDef("rc_walk_left",  "Pro Rifle Pack", "walk crouching left", true),
        new ClipDef("rc_walk_right", "Pro Rifle Pack", "walk crouching right", true),

        // Shared actions - Basic Shooter Pack (rifle-style, reused for both weapon classes
        // since the supplied packs don't include a dedicated pistol firing clip)
        new ClipDef("act_fire_rifle", "Basic Shooter Pack", "firing rifle", true),
        new ClipDef("act_reload",     "Basic Shooter Pack", "reloading", false),
        new ClipDef("act_grenade",    "Basic Shooter Pack", "toss grenade", false),
        new ClipDef("act_hit",        "Basic Shooter Pack", "hit reaction", false),

        // Melee - Pro Melee Axe Pack
        new ClipDef("act_melee",      "Pro Melee Axe Pack", "standing melee attack horizontal", false),

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

    [MenuItem("Tools/FPS Game/Build Animation System")]
    public static void Build()
    {
        _clipLookup = new Dictionary<string, AnimationClip>();
        Directory.CreateDirectory(OutDir);

        int imported = 0, missing = 0;
        foreach (var def in Clips)
        {
            var clip = ImportClip(def);
            if (clip == null) { missing++; continue; }
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
        string fbxPath = $"{MixamoRoot}/{def.pack}/{def.file}.fbx";
        var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogWarning($"[AnimationSystemBuilder] Missing FBX: {fbxPath}");
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
        clipSettings.lockRootRotation = true;
        clipSettings.keepOriginalOrientation = false;
        clipSettings.lockRootHeightY = true;
        clipSettings.keepOriginalPositionY = !def.rootMotion;
        clipSettings.lockRootPositionXZ = true;
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
        if (c == null) Debug.LogWarning($"[AnimationSystemBuilder] Clip '{key}' unavailable - a blend tree node will be empty.");
        return c;
    }

    // ---- avatar mask --------------------------------------------------
    static AvatarMask BuildUpperBodyMask()
    {
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath) ?? new AvatarMask();
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
        {
            var part = (AvatarMaskBodyPart)i;
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
        controller.AddParameter("WeaponClass", AnimatorControllerParameterType.Int); // 0 Unarmed 1 Pistol 2 Rifle
        controller.AddParameter("IsCrouching", AnimatorControllerParameterType.Bool);
        controller.AddParameter("IsGrounded", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Aiming", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Fire", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Reload", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Melee", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Dead", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Injured", AnimatorControllerParameterType.Bool);
        // Which death clip to play - set by CharacterAnimationDriver.SetDead(dead, fromBack).
        controller.AddParameter("DeathFromBack", AnimatorControllerParameterType.Bool);

        BuildBaseLayer(controller);
        BuildUpperBodyLayer(controller, upperBodyMask);

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

    // -- base (full body) layer --
    static void BuildBaseLayer(AnimatorController controller)
    {
        var sm = controller.layers[0].stateMachine;

        BlendTree unarmed = Directional2D("Unarmed", new (float x, float y, string key)[]
        {
            (0,0,"un_idle"), (0,1,"un_walk_fwd"), (0,2,"un_run_fwd"),
            (-1,1,"un_walk_left"), (1,1,"un_walk_right"),
            (-1,2,"un_run_left"), (1,2,"un_run_right"),
        });

        BlendTree pistol = Directional2D("Pistol", new (float x, float y, string key)[]
        {
            (0,0,"pi_idle"), (0,1,"pi_walk_fwd"), (0,2,"pi_run_fwd"),
            (0,-1,"pi_walk_back"), (0,-2,"pi_run_back"),
            (-1,1,"pi_left"), (1,1,"pi_right"),
            (-1,2,"pi_left"), (1,2,"pi_right"),
        });

        BlendTree rifle = Directional2D("Rifle", new (float x, float y, string key)[]
        {
            (0,0,"ri_idle"),
            (0,1,"ri_walk_fwd"), (-0.5f,1,"ri_walk_fwd_l"), (0.5f,1,"ri_walk_fwd_r"),
            (-1,0.5f,"ri_walk_left"), (1,0.5f,"ri_walk_right"),
            (0,-1,"ri_walk_back"),
            (0,2,"ri_run_fwd"), (-0.5f,2,"ri_run_fwd_l"), (0.5f,2,"ri_run_fwd_r"),
            (-1,1.2f,"ri_run_left"), (1,1.2f,"ri_run_right"),
            (0,-2,"ri_run_back"), (-0.5f,-2,"ri_run_back_l"), (0.5f,-2,"ri_run_back_r"),
        });

        // Used by EVERY weapon class now, not just the rifle. Only the arms differ
        // between a rifle and a pistol pose, and the arms are IK'd onto whichever gun is
        // actually held (plus, for the pistol, overridden by the masked UpperBody pose
        // added in BuildUpperBodyLayer) - so the legs from the rifle crouch clip read
        // fine underneath a pistol or empty hands. That's the "lower half of one pose,
        // upper half of another" combination.
        BlendTree rifleCrouch = Directional2D("RifleCrouch", new (float x, float y, string key)[]
        {
            (0,0,"rc_idle"),
            (0,1,"rc_walk_fwd"), (-0.5f,1,"rc_walk_fwd_l"), (0.5f,1,"rc_walk_fwd_r"),
            (-1,0.5f,"rc_walk_left"), (1,0.5f,"rc_walk_right"),
            (0,-1,"rc_walk_back"),
        });

        // Male Injured Pack has no strafe clips, so left/right just reuse forward -
        // a fine approximation for a temporary low-health overlay.
        BlendTree injured = Directional2D("Injured", new (float x, float y, string key)[]
        {
            (0,0,"inj_idle"),
            (0,1,"inj_walk_fwd"), (-1,1,"inj_walk_fwd"), (1,1,"inj_walk_fwd"),
            (0,2,"inj_run_fwd"), (-1,2,"inj_run_fwd"), (1,2,"inj_run_fwd"),
            (0,-1,"inj_walk_back"), (0,-2,"inj_run_back"),
        });

        AssetDatabase.AddObjectToAsset(unarmed, controller);
        AssetDatabase.AddObjectToAsset(pistol, controller);
        AssetDatabase.AddObjectToAsset(rifle, controller);
        AssetDatabase.AddObjectToAsset(rifleCrouch, controller);
        AssetDatabase.AddObjectToAsset(injured, controller);

        AnimatorState sUnarmed = AddMotionState(sm, "Unarmed", unarmed, new Vector3(0, 300, 0));
        AnimatorState sPistol  = AddMotionState(sm, "Pistol", pistol, new Vector3(220, 300, 0));
        AnimatorState sRifle   = AddMotionState(sm, "Rifle", rifle, new Vector3(440, 300, 0));
        AnimatorState sCrouch  = AddMotionState(sm, "RifleCrouch", rifleCrouch, new Vector3(440, 460, 0));
        AnimatorState sAir     = AddMotionState(sm, "Airborne", C("airborne_idle"), new Vector3(220, 460, 0));
        AnimatorState sInjured = AddMotionState(sm, "Injured", injured, new Vector3(660, 300, 0));
        AnimatorState sDead     = AddMotionState(sm, "Death", C("ri_death_front"), new Vector3(220, 620, 0));
        AnimatorState sDeadBack = AddMotionState(sm, "DeathBack", C("ri_death_back") != null ? C("ri_death_back") : C("ri_death_front"), new Vector3(440, 620, 0));
        sm.defaultState = sUnarmed;

        var weaponStates = new[] { sUnarmed, sPistol, sRifle };
        for (int i = 0; i < weaponStates.Length; i++)
        for (int j = 0; j < weaponStates.Length; j++)
        {
            if (i == j) continue;
            var t = weaponStates[i].AddTransition(weaponStates[j]);
            t.hasExitTime = false; t.duration = 0.15f;
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

        // Airborne in/out
        foreach (var s in new[] { sUnarmed, sPistol, sRifle, sCrouch })
            AddInstantTransition(s, sAir, AnimatorConditionMode.IfNot, 0, "IsGrounded");
        AddInstantTransition(sAir, sUnarmed, AnimatorConditionMode.If, 0, "IsGrounded", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 0, "WeaponClass"));
        AddInstantTransition(sAir, sPistol, AnimatorConditionMode.If, 0, "IsGrounded", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 1, "WeaponClass"));
        AddInstantTransition(sAir, sRifle, AnimatorConditionMode.If, 0, "IsGrounded", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 2, "WeaponClass"));

        // Death from anywhere - added before the Injured wiring below so it's
        // evaluated first: Unity checks a state's transitions in the order
        // they were added, and Dead must win if both are true simultaneously.
        foreach (var s in new[] { sUnarmed, sPistol, sRifle, sCrouch, sAir, sInjured })
        {
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
        var groundStates = new[] { sUnarmed, sPistol, sRifle, sCrouch };
        foreach (var s in groundStates)
            AddInstantTransition(s, sInjured, AnimatorConditionMode.If, 0, "Injured");
        AddInstantTransition(sInjured, sUnarmed, AnimatorConditionMode.IfNot, 0, "Injured", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 0, "WeaponClass"));
        AddInstantTransition(sInjured, sPistol, AnimatorConditionMode.IfNot, 0, "Injured", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 1, "WeaponClass"));
        AddInstantTransition(sInjured, sRifle, AnimatorConditionMode.IfNot, 0, "Injured", extra: (t) => t.AddCondition(AnimatorConditionMode.Equals, 2, "WeaponClass"));
    }

    static void AddInstantTransition(AnimatorState from, AnimatorState to, AnimatorConditionMode mode, float threshold, string param, System.Action<AnimatorStateTransition> extra = null)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = false;
        t.duration = 0.15f;
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
        AnimatorState melee  = AddMotionState(sm, "UB_Melee", C("act_melee"), new Vector3(0, 140, 0));
        // Pistol upper body, masked over whatever the legs are doing. This is what makes
        // the shared (rifle-authored) crouch tree usable for a handgun: the legs crouch,
        // this replaces the arms/spine with the pistol pack's own ready stance.
        AnimatorState pistolPose = AddMotionState(sm, "UB_PistolPose", C("pi_idle"), new Vector3(440, 0, 0));
        sm.defaultState = idle;

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
            tm.hasExitTime = false; tm.duration = 0.05f;
            tm.AddCondition(AnimatorConditionMode.If, 0, "Melee");
        }

        // Fire is a short loud loop while the trigger stays fresh; return to aim shortly after.
        var backFromFire = fire.AddTransition(aim);
        backFromFire.hasExitTime = true; backFromFire.exitTime = 0.12f; backFromFire.duration = 0.05f;
        backFromFire.hasFixedDuration = true;

        var backFromReload = reload.AddTransition(idle);
        backFromReload.hasExitTime = true; backFromReload.exitTime = 0.95f; backFromReload.duration = 0.1f;

        var backFromMelee = melee.AddTransition(idle);
        backFromMelee.hasExitTime = true; backFromMelee.exitTime = 0.9f; backFromMelee.duration = 0.15f;
    }

    // ---- helpers ----------------------------------------------------------
    static AnimatorState AddMotionState(AnimatorStateMachine sm, string name, Motion motion, Vector3 pos)
    {
        var s = sm.AddState(name, pos);
        s.motion = motion;
        s.writeDefaultValues = true;
        return s;
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