using UnityEngine;

/// <summary>Tells the shared body Animator whether this character carries a gun
/// (drives the UpperBody GunHold pose). Put next to the Animator.</summary>
public class GunHoldFlag : MonoBehaviour
{
    public bool hasGun = true;

    void Start()
    {
        // Superseded by CharacterAnimationDriver's WeaponClass parameter - stand down if present.
        if (GetComponent<CharacterAnimationDriver>() != null) { enabled = false; return; }

        var anim = GetComponent<Animator>();
        if (anim != null) anim.SetBool("HasGun", hasGun);
    }
}