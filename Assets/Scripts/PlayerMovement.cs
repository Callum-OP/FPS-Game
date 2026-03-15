using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    // Movement settings
    [Header("Movement")]
    public float walkSpeed = 5f;
    public float runSpeed = 9f;
    public float jumpHeight = 1.5f;
    public float gravity = -19.62f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 0.2f;
    public Transform cameraTransform;

    [Header("Cant")]
    public WeaponCant weaponCant;
    public float cantSpeedMultiplier = 0.5f;  // 50% speed when canted

    private CharacterController controller;
    private Vector3 velocity;
    private float xRotation = 0f;

    // Input actions
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    private InputAction sprintAction;

    void Awake()
    {
        // Define all inputs
        moveAction   = new InputAction("Move",   binding: "<Keyboard>/w");
        lookAction   = new InputAction("Look",   binding: "<Mouse>/delta");
        jumpAction   = new InputAction("Jump",   binding: "<Keyboard>/space");
        sprintAction = new InputAction("Sprint", binding: "<Keyboard>/f");

        // WASD
        moveAction = new InputAction("Move");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/w")
            .With("Down",  "<Keyboard>/s")
            .With("Left",  "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        moveAction.Enable();
        lookAction.Enable();
        jumpAction.Enable();
        sprintAction.Enable();
    }

    void Start()
    {
        controller = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        HandleMouseLook();
        HandleMovement();
    }

    void HandleMouseLook()
    {
        Vector2 lookInput = lookAction.ReadValue<Vector2>();

        xRotation -= lookInput.y * mouseSensitivity;
        xRotation = Mathf.Clamp(xRotation, -85f, 85f);

        // Preserve camera lean angle
        float currentZ = cameraTransform.localEulerAngles.z;
        float currentY = cameraTransform.localEulerAngles.y;

        if (currentZ > 180f) currentZ -= 360f;
        if (currentY > 180f) currentY -= 360f;

        cameraTransform.localRotation = Quaternion.Euler(xRotation, currentY, currentZ);
        transform.Rotate(Vector3.up * lookInput.x * mouseSensitivity);
    }

    void HandleMovement()
    {
        bool isGrounded = controller.isGrounded;
        if (isGrounded && velocity.y < 0f) velocity.y = -2f;

        Vector2 moveInput = moveAction.ReadValue<Vector2>();
        bool isSprinting  = sprintAction.ReadValue<float>() > 0.5f;

        float speed = isSprinting ? runSpeed : walkSpeed;

        // Slow down when canted
        if (weaponCant != null && weaponCant.IsCanted())
            speed *= cantSpeedMultiplier;

        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        controller.Move(move * speed * Time.deltaTime);

        if (jumpAction.WasPressedThisFrame() && isGrounded)
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);
    }

    void OnDestroy()
    {
        moveAction.Disable();
        lookAction.Disable();
        jumpAction.Disable();
        sprintAction.Disable();
    }
}