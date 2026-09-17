using UnityEngine.InputSystem;

/// <summary>
/// Every key the player presses, in one place. Change a key here and it changes
/// everywhere - previously every script hardcoded its own InputAction path, which is
/// how pickup ended up on H and drop on G with no single place to check for clashes.
///
/// Current layout (left hand stays on WASD, everything else clusters around 1-4):
///   1  pick up                 2  lower gun (fast walk = the sprint replacement)
///   3  toggle rifle/pistol     4  grenade (hold, then left click to throw)
///   R  reload                  F  drop
///   Ctrl crouch                Shift aim                    Space jump
///   Q/E cant                   V melee                      Left click fire
///
/// There is no dedicated sprint any more - lowering the gun (2) already gives a speed
/// boost via LowerWeapon.fastWalkMultiplier, which does the job a sprint key would have
/// and is what "sprint" now means in this game.
/// </summary>
public static class PlayerInputMap
{
    // Movement
    public const string Jump        = "<Keyboard>/space";
    public const string Crouch      = "<Keyboard>/leftCtrl";

    // Weapons
    public const string Pickup       = "<Keyboard>/1";
    public const string LowerWeapon  = "<Keyboard>/2";
    public const string ToggleWeapon = "<Keyboard>/3";
    public const string Grenade      = "<Keyboard>/4";
    public const string Reload       = "<Keyboard>/r";
    public const string Drop         = "<Keyboard>/f";
    public const string Melee        = "<Keyboard>/v";
    public const string CantLeft     = "<Keyboard>/q";
    public const string CantRight    = "<Keyboard>/e";
    public const string Fire         = "<Mouse>/leftButton";
    public const string Aim          = "<Keyboard>/leftShift";

    public static InputAction Make(string name, string binding)
    {
        var action = new InputAction(name, binding: binding);
        action.Enable();
        return action;
    }
}