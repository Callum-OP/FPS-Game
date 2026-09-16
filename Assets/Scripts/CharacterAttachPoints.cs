using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class NamedAttachPoint
{
    public string pointName;
    public Transform point;
}

/// <summary>
/// A single place to register the empty transforms you manually place on the character
/// model - hip pouches, hand-carry spots, wherever - so any script can look one up by
/// name instead of every feature needing its own bespoke Inspector wiring. Add this
/// once to the Player prefab (and Enemy, if something ever needs it there too).
///
/// SETUP:
///  1. In the model's hierarchy, create an empty child GameObject wherever you want a
///     point (e.g. under the LeftHand bone for something carried in-hand, or near the
///     hip bones for a pouch). Position and rotate it BY EYE in the Scene view - you'll
///     actually see it as a gizmo and can nudge it around like any other object.
///  2. Add an entry to the Points list below, give it a name (e.g. "ReloadGrab",
///     "MagHand"), and drag the transform you just placed into the Point field.
///  3. Anything that wants that spot calls GetComponentInChildren&lt;CharacterAttachPoints&gt;()
///     .Get("ThatName") - see WeaponReloadHandler/PlayerSetup for the reload example.
///
/// This is intentionally just a flat, hand-authored list rather than anything clever -
/// you're always in full control of exactly where each point sits and can add as many
/// as you like for future features (pouches, holsters, etc) without touching code.
/// </summary>
public class CharacterAttachPoints : MonoBehaviour
{
    public List<NamedAttachPoint> points = new List<NamedAttachPoint>();

    public Transform Get(string name)
    {
        foreach (var p in points)
        {
            if (p.pointName == name)
            {
                if (p.point == null)
                    Debug.LogWarning($"CharacterAttachPoints on {gameObject.name}: entry '{name}' has no Transform assigned.", this);
                return p.point;
            }
        }
        return null;
    }
}