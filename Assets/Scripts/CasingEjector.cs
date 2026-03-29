using UnityEngine;

public class CasingEjector : MonoBehaviour
{
    [Header("Settings")]
    public GameObject casingPrefab;
    public Transform ejectionPoint;
    public float ejectionForce = 3f;
    public float upwardForce = 1f;
    public float torqueForce = 4f;
    public float destroyDelay = 5f;

    public void Eject()
    {
        if (casingPrefab == null || ejectionPoint == null) return;

        // Instantiate the casing
        GameObject casing = Instantiate(casingPrefab, ejectionPoint.position, ejectionPoint.rotation);
        
        Rigidbody rb = casing.GetComponent<Rigidbody>();
        if (rb != null)
        {
                        // Randomise ejection force and upward force slightly
            float randomEjection = ejectionForce + Random.Range(-0.5f, 0.5f);
            float randomUpward   = upwardForce   + Random.Range(-0.3f, 0.3f);

            // Randomise direction slightly so casings don't all go same way
            // Place right of the ejection point and a bit up
            Vector3 randomDir = ejectionPoint.right
                + ejectionPoint.up * 0.3f
                + new Vector3(
                    Random.Range(-0.1f, 0.1f),
                    Random.Range(-0.1f, 0.1f),
                    Random.Range(-0.1f, 0.1f));

            // Calculate direction
            Vector3 forceDirection = (randomDir.normalized * randomEjection)
                + (ejectionPoint.up * randomUpward);

            // Apply physical force
            rb.AddForce(forceDirection, ForceMode.Impulse);

            // Add a random spin for realism
            Vector3 randomTorque = new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f));
            rb.AddTorque(randomTorque * torqueForce, ForceMode.Impulse);
        }

        // Clean up casing after a few seconds
        Destroy(casing, destroyDelay);
    }
}