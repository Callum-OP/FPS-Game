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

    private float speedMultiplier = 1f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 0.2f;
    public Transform cameraTransform;

    [Header("Cant")]
    public WeaponCant weaponCant;
    public float cantSpeedMultiplier = 0.5f;  // 50% speed when canted

    [Header("Crouch")]
    public CharacterAnimationDriver animationDriver; // auto-found in children if empty
    public float crouchSpeedMultiplier = 0.5f;
    [Tooltip("How far the camera drops (local Y, relative to its standing position) while crouched.")]
    public float crouchCameraDrop = 0.5f;
    [Tooltip("How much of the CharacterController's standing height is kept while crouched (0-1). Keeps the feet planted - the capsule shrinks from the top, not the middle.")]
    [Range(0.3f, 1f)] public float crouchHeightFraction = 0.6f;
    public float crouchTransitionSpeed = 8f;
    private bool isCrouching = false;
    private float standingCameraLocalY;
    private float standingControllerHeight;
    private Vector3 standingControllerCenter;

    private CharacterController controller;
    private Vector3 velocity;
    public Vector3 PlanarVelocity { get; private set; }
    public bool IsGrounded => controller != null && controller.isGrounded;
    private float xRotation = 0f;

    // Input actions
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    private InputAction sprintAction;
    private InputAction crouchAction;

    [Header("Footsteps")]
    public AudioClip[] footstepClips;
    public float footstepInterval = 0.4f;
    public float footstepSprintInterval = 0.25f;
    private float footstepTimer = 0f;

    void Awake()
    {
        // Define all inputs
        moveAction   = new InputAction("Move",   binding: "<Keyboard>/w");
        lookAction   = new InputAction("Look",   binding: "<Mouse>/delta");
        jumpAction   = new InputAction("Jump",   binding: "<Keyboard>/space");
        sprintAction = new InputAction("Sprint", binding: "<Keyboard>/f");
        crouchAction = new InputAction("Crouch", binding: "<Keyboard>/c");

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
        crouchAction.Enable();
    }

    void Start()
    {
        controller = GetComponent<CharacterController>();
        if (animationDriver == null) animationDriver = GetComponentInChildren<CharacterAnimationDriver>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // Captured rather than hardcoded, so this works with whatever height/eye
        // position was actually set up in the scene instead of guessing an absolute value.
        if (cameraTransform != null) standingCameraLocalY = cameraTransform.localPosition.y;
        if (controller != null)
        {
            standingControllerHeight = controller.height;
            standingControllerCenter = controller.center;
        }
    }

    void Update()
    {
        HandleMouseLook();
        HandleMovement();
        HandleCrouchHeight();
    }

    void HandleCrouchHeight()
    {
        if (cameraTransform != null)
        {
            float targetY = standingCameraLocalY - (isCrouching ? crouchCameraDrop : 0f);
            Vector3 pos = cameraTransform.localPosition;
            pos.y = Mathf.Lerp(pos.y, targetY, crouchTransitionSpeed * Time.deltaTime);
            cameraTransform.localPosition = pos;
        }

        if (controller != null)
        {
            float targetHeight = isCrouching ? standingControllerHeight * crouchHeightFraction : standingControllerHeight;
            controller.height = Mathf.Lerp(controller.height, targetHeight, crouchTransitionSpeed * Time.deltaTime);

            // Shrink from the top, not the middle - keeps the feet on the ground
            // instead of the whole capsule sinking/floating as height changes.
            float heightLost = standingControllerHeight - controller.height;
            controller.center = standingControllerCenter - new Vector3(0f, heightLost * 0.5f, 0f);
        }
    }

    public void SetSpeedMultiplier(float multiplier)
    {
        speedMultiplier = multiplier;
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

        if (crouchAction.WasPressedThisFrame())
        {
            isCrouching = !isCrouching;
            if (isCrouching) isSprinting = false;
            animationDriver?.SetCrouching(isCrouching);
        }

        float speed = isSprinting ? runSpeed : walkSpeed;

        // Apply all multipliers
        speed *= speedMultiplier;
        if (isCrouching) speed *= crouchSpeedMultiplier;

        // Slow down when canted
        if (weaponCant != null && weaponCant.IsCanted())
            speed *= cantSpeedMultiplier;

        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        Vector3 movement = move * speed;
        controller.Move(movement * Time.deltaTime);
        PlanarVelocity = movement;

        if (jumpAction.WasPressedThisFrame() && isGrounded)
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        HandleFootsteps(speed, isSprinting, isGrounded);

        void HandleFootsteps(float speed, bool isSprinting, bool isGrounded)
        {
            if (!isGrounded || moveInput.magnitude < 0.1f)
            {
                footstepTimer = 0f;
                return;
            }

            footstepTimer -= Time.deltaTime;
            if (footstepTimer <= 0f)
            {
                footstepTimer = isSprinting ? footstepSprintInterval : footstepInterval;

                if (footstepClips.Length > 0)
                    AudioManager.Instance?.Play(
                        footstepClips[Random.Range(0, footstepClips.Length)]);
            }
        }
    }

    void OnDestroy()
    {
        moveAction.Disable();
        lookAction.Disable();
        jumpAction.Disable();
        sprintAction.Disable();
        crouchAction.Disable();
    }
}