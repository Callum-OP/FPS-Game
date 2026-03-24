using UnityEngine;

public class CasingEjector : MonoBehaviour
{
    [Header("Settings")]
    public GameObject casingPrefab;
    public Transform ejectionPoint;
    public float ejectionForce = 5f;
    public float upwardForce = 2f;
    public float torqueForce = 10f;
    public float destroyDelay = 5f;

    public void Eject()
    {
        if (casingPrefab == null || ejectionPoint == null) return;

        // Instantiate the casing
        GameObject casing = Instantiate(casingPrefab, ejectionPoint.position, ejectionPoint.rotation);
        
        Rigidbody rb = casing.GetComponent<Rigidbody>();
        if (rb != null)
        {
            // Calculate direction (Right of the ejection point and a bit of up)
            Vector3 forceDirection = (ejectionPoint.right * ejectionForce) + (ejectionPoint.up * upwardForce);
            
            // Apply physical forces
            rb.AddForce(forceDirection, ForceMode.Impulse);
            
            // Add a random spin for realism
            Vector3 randomTorque = new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f));
            rb.AddTorque(randomTorque * torqueForce, ForceMode.Impulse);
        }

        // Clean up casing after a few seconds
        Destroy(casing, destroyDelay);
    }
}