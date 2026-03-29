using UnityEngine;
using UnityEngine.InputSystem;

public class WeaponPickup : MonoBehaviour
{
    [Header("Which held weapon this pickup gives the player")]
    public GameObject heldWeaponPrefab;

    private bool playerInRange = false;
    private PlayerSetup playerSetup;
    private InputAction pickupAction;

    void Awake()
    {
        pickupAction = new InputAction("Pickup", binding: "<Keyboard>/h");
        pickupAction.Enable();
    }

    void Update()
    {
        if (playerInRange && pickupAction.WasPressedThisFrame())
            Pickup();
    }

    void Pickup()
    {
        if (playerSetup == null) return;

        // Drop current weapon first if holding one
        WeaponDrop drop = playerSetup.GetComponent<WeaponDrop>();
        drop?.Drop();

        // Find or instantiate the held version under camera
        GameObject held = playerSetup.fpCamera.transform
            .Find(heldWeaponPrefab.name)?.gameObject;

        if (held == null)
            held = Instantiate(heldWeaponPrefab,
                playerSetup.fpCamera.transform);

        held.transform.localPosition = new Vector3(0f, -0.1f, 0.5f);
        held.transform.localRotation = Quaternion.identity;
        held.SetActive(true);

        WeaponController weapon = held.GetComponent<WeaponController>();
        playerSetup.WireWeapon(weapon);

        // Destroy the world object
        Destroy(gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        playerInRange = true;
        playerSetup   = other.GetComponent<PlayerSetup>();
    }

    void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        playerInRange = false;
        playerSetup   = null;
    }

    void OnDestroy() => pickupAction.Disable();
}