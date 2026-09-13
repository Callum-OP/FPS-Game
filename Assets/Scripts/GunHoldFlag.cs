using UnityEngine;

/// <summary>Tells the shared body Animator whether this character carries a gun
/// (drives the UpperBody GunHold pose). Put next to the Animator.</summary>
public class GunHoldFlag : MonoBehaviour
{
    public bool hasGun = true;

    void Start()
    {
        var anim = GetComponent<Animator>();
        if (anim != null) anim.SetBool("HasGun", hasGun);
    }
}
