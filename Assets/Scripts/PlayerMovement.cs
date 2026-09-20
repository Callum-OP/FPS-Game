using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Also the SINGLE OWNER of the camera's local position/rotation.
///
/// This is the fix for the jitter when looking up while walking. Previously
/// PlayerMovement (pitch + crouch drop), CameraLean (tilt/yaw/side shift) and
/// CameraRecoil (recoil kick) all wrote cameraTransform.localRotation/localPosition
/// outright in their own Update(), in whatever order Unity happened to run them, and
/// each one read the transform back as its own smoothing input - so every frame they
/// partially erased and re-derived each other's contribution. That feedback shows up as
/// the camera (and therefore the camera-parented weapon and the IK'd hands chasing it)
/// shaking and jumping, worst exactly when several of them are active at once: looking
/// up + walking + leaning.
///
/// Now the other two only push their contribution in (SetCameraLean / SetCameraRotationOffset)
/// and this script composes all of them once, from its own internal state, so nothing
/// ever reads back a transform another script wrote. Execution order is pinned so the
/// providers run first each frame.
/// </summary>
[DefaultExecutionOrder(-150)]
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
    [Tooltip("How far up the camera can pitch, in degrees.")]
    public float lookUpLimit = 80f;
    [Tooltip("How far down the camera can pitch, in degrees.")]
    public float lookDownLimit = 65f;

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
    private float currentCrouchDrop = 0f;
    private Vector3 standingCameraLocalPos;
    private float standingControllerHeight;
    private Vector3 standingControllerCenter;

    // --- camera contribution channels (pushed in by other scripts, applied here) ---
    private Quaternion cameraRotationOffset = Quaternion.identity; // CameraRecoil
    private float leanTiltZ, leanYawY, leanShiftX;                 // CameraLean
    private Vector3 cameraPositionOffset;                          // CameraBodyFollow

    private CharacterController controller;
    private Vector3 velocity;
    public Vector3 PlanarVelocity { get; private set; }
    public bool IsGrounded => controller != null && controller.isGrounded;
    public bool IsCrouching => isCrouching;
    public float CameraPitch => xRotation;
    /// <summary>Set by ThirdPersonMode while the camera is being orbited, so the mouse moves the camera and not the aim.</summary>
    public bool LookDetached { get; set; }
    private float xRotation = 0f;

    // Input actions
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    // No dedicated sprint InputAction any more - see the "fast" bool derived from the
    // speed multiplier in HandleMovement below.
    private InputAction crouchAction;

    [Header("Footsteps")]
    public AudioClip[] footstepClips;
    public float footstepInterval = 0.4f;
    public float footstepSprintInterval = 0.25f;
    private float footstepTimer = 0f;

    void Awake()
    {
        // Define all inputs
        // All bindings live in PlayerInputMap now - see that file for the full layout.
        lookAction   = new InputAction("Look", binding: "<Mouse>/delta");
        jumpAction   = new InputAction("Jump",   binding: PlayerInputMap.Jump);
        crouchAction = new InputAction("Crouch", binding: PlayerInputMap.Crouch);

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
        if (cameraTransform != null) standingCameraLocalPos = cameraTransform.localPosition;
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
        ApplyCameraTransform();
    }

    // ------------------------------------------------------------------
    // Camera contribution API - other scripts push, they never write the transform.
    // ------------------------------------------------------------------

    /// <summary>Extra local-space rotation composed on top of pitch/lean (CameraRecoil).</summary>
    public void SetCameraRotationOffset(Quaternion offset) => cameraRotationOffset = offset;

    /// <summary>Lean contribution (CameraLean): roll, yaw and sideways shift.</summary>
    public void SetCameraLean(float tiltZ, float yawY, float shiftX)
    {
        leanTiltZ = tiltZ;
        leanYawY = yawY;
        leanShiftX = shiftX;
    }

    /// <summary>Extra local-space camera position offset (CameraBodyFollow).</summary>
    public void SetCameraPositionOffset(Vector3 offset) => cameraPositionOffset = offset;

    void ApplyCameraTransform()
    {
        if (cameraTransform == null) return;

        // Composed from state every frame - never read back off the transform, so no
        // script can feed its own (or anyone else's) previous output back into itself.
        cameraTransform.localRotation =
            Quaternion.Euler(xRotation, leanYawY, leanTiltZ) * cameraRotationOffset;

        cameraTransform.localPosition = standingCameraLocalPos
            + new Vector3(leanShiftX, -currentCrouchDrop, 0f)
            + cameraPositionOffset;
    }

    void HandleCrouchHeight()
    {
        float targetDrop = isCrouching ? crouchCameraDrop : 0f;
        currentCrouchDrop = Mathf.Lerp(currentCrouchDrop, targetDrop, crouchTransitionSpeed * Time.deltaTime);

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
        Vector2 lookInput = LookDetached ? Vector2.zero : lookAction.ReadValue<Vector2>();

        xRotation -= lookInput.y * mouseSensitivity;
        xRotation = Mathf.Clamp(xRotation, -lookUpLimit, lookDownLimit);

        transform.Rotate(Vector3.up * lookInput.x * mouseSensitivity);
        // The camera transform itself is written once, in ApplyCameraTransform().
    }

    void HandleMovement()
    {
        bool isGrounded = controller.isGrounded;
        if (isGrounded && velocity.y < 0f) velocity.y = -2f;

        Vector2 moveInput = moveAction.ReadValue<Vector2>();
        if (crouchAction.WasPressedThisFrame())
        {
            isCrouching = !isCrouching;
            animationDriver?.SetCrouching(isCrouching);
        }

        // No sprint key any more - lowering the gun (LowerWeapon, key 2) is what makes
        // the player move fast, purely via SetSpeedMultiplier below. This used to ALSO
        // jump the base speed from walkSpeed up to runSpeed on top of that multiplier -
        // 5 * 1.5 became 9 * 1.5 = 13.5, a double boost - which is why lowering the gun
        // ended up faster than sprinting ever was. Base speed now always starts from
        // walkSpeed; the multiplier alone (fastWalkMultiplier on LowerWeapon, still
        // 1.5x by default) is the entire "sprint replacement".
        bool isFast = speedMultiplier > 1.01f;

        float speed = walkSpeed;

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

        HandleFootsteps(speed, isFast, isGrounded);

        void HandleFootsteps(float speed, bool isFast, bool isGrounded)
        {
            if (!isGrounded || moveInput.magnitude < 0.1f)
            {
                footstepTimer = 0f;
                return;
            }

            footstepTimer -= Time.deltaTime;
            if (footstepTimer <= 0f)
            {
                footstepTimer = isFast ? footstepSprintInterval : footstepInterval;

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
        crouchAction.Disable();
    }
}