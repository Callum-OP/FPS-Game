using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Finds somewhere to hide. Pure lookup - it holds no state about what the AI is doing,
/// so EnemyAI stays in charge of when to use it.
///
/// How a candidate is judged, which is the whole trick to cover that doesn't look silly:
///  1. Sample a ring of points on the NavMesh around the enemy (so it can actually walk
///     there).
///  2. Reject any point where the enemy's own chest has line of sight to the player -
///     standing in the open isn't cover.
///  3. Require a "peek" position - a step to either side of the candidate - that DOES
///     have line of sight. Cover you can never shoot from is a dead end, and this is
///     what lets the AI rise from cover, fire, and drop back.
///  4. Prefer points that are close, and that are roughly between the enemy and the
///     player rather than behind them.
///
/// SETUP: add next to EnemyAI. Set sightBlockers to the same layers EnemyAI uses.
/// </summary>
public class EnemyCover : MonoBehaviour
{
    [Header("Search")]
    [Tooltip("How far out to look for cover.")]
    public float searchRadius = 14f;
    [Tooltip("How many candidate points to sample per search. Higher finds better cover but costs more - this only runs when the AI actually asks for cover, not every frame.")]
    public int sampleCount = 24;
    [Tooltip("Don't bother with cover closer than this - shuffling half a metre looks like indecision.")]
    public float minDistance = 2.5f;

    [Header("Geometry")]
    [Tooltip("Height off the ground used for the line-of-sight tests - roughly where the enemy's chest sits while standing.")]
    public float eyeHeight = 1.5f;
    [Tooltip("Height used for the crouched-behind-cover test. A point only counts as cover if crouching there breaks line of sight.")]
    public float crouchedHeight = 0.9f;
    [Tooltip("How far to the side the enemy leans out to shoot. The peek position has to have a clear shot or the spot isn't used.")]
    public float peekOffset = 0.8f;
    public LayerMask sightBlockers;

    /// <summary>Best cover position found, or false if there isn't one worth moving to.
    /// peekPosition is where to step out to when firing.</summary>
    public bool FindCover(Vector3 playerPosition, out Vector3 coverPosition, out Vector3 peekPosition)
    {
        coverPosition = transform.position;
        peekPosition = transform.position;

        float bestScore = float.NegativeInfinity;
        bool found = false;

        for (int i = 0; i < sampleCount; i++)
        {
            Vector2 circle = Random.insideUnitCircle.normalized * Random.Range(minDistance, searchRadius);
            Vector3 probe = transform.position + new Vector3(circle.x, 0f, circle.y);

            if (!NavMesh.SamplePosition(probe, out NavMeshHit navHit, 2f, NavMesh.AllAreas)) continue;
            Vector3 candidate = navHit.position;

            if (Vector3.Distance(candidate, transform.position) < minDistance) continue;

            // Must actually be hidden while crouched there...
            if (HasLineOfSight(candidate + Vector3.up * crouchedHeight, playerPosition)) continue;

            // ...and there must be somewhere alongside it to shoot from.
            Vector3 toPlayer = (playerPosition - candidate);
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f) continue;
            Vector3 side = Vector3.Cross(Vector3.up, toPlayer.normalized);

            Vector3 peek = Vector3.zero;
            bool peekFound = false;
            for (int sign = -1; sign <= 1 && !peekFound; sign += 2)
            {
                Vector3 test = candidate + side * (peekOffset * sign);
                if (!NavMesh.SamplePosition(test, out NavMeshHit peekHit, 1f, NavMesh.AllAreas)) continue;
                if (!HasLineOfSight(peekHit.position + Vector3.up * eyeHeight, playerPosition)) continue;
                peek = peekHit.position;
                peekFound = true;
            }
            if (!peekFound) continue;

            // Closer is better, and facing the fight is better than running away from it.
            float distance = Vector3.Distance(candidate, transform.position);
            Vector3 toPlayerFromSelf = (playerPosition - transform.position).normalized;
            float forwardness = Vector3.Dot((candidate - transform.position).normalized, toPlayerFromSelf);
            float score = -distance * 0.5f + forwardness * 3f;

            if (score > bestScore)
            {
                bestScore = score;
                coverPosition = candidate;
                peekPosition = peek;
                found = true;
            }
        }

        return found;
    }

    /// <summary>True if the given world point can see the player's chest.</summary>
    public bool HasLineOfSight(Vector3 from, Vector3 playerPosition)
    {
        Vector3 target = playerPosition + Vector3.up * 1.2f;
        Vector3 dir = target - from;
        float dist = dir.magnitude;
        if (dist < 0.01f) return true;

        if (Physics.Raycast(from, dir / dist, out RaycastHit hit, dist, sightBlockers, QueryTriggerInteraction.Ignore))
            return hit.transform.CompareTag("Player");
        return true;
    }

    /// <summary>Whether the enemy is currently hidden where it stands (used to decide
    /// when it's safe to reload).</summary>
    public bool IsHiddenHere(Vector3 playerPosition) =>
        !HasLineOfSight(transform.position + Vector3.up * crouchedHeight, playerPosition);
}