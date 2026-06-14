using Cinemachine;
using Fusion;
using System.Runtime.CompilerServices;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;

public class Player : NetworkBehaviour
{
    public PlayerInputActions playerInputActions;
    public static Player LocalPlayer;
    [SerializeField] private float moveSpeed = 7f;
    [SerializeField] private Transform playerCamera; //simple camera ref
    [SerializeField] private float mouseSensitivity = 0.5f; //DOES NOT WORK
    [SerializeField] private CharacterController characterController; //charcontroller
    [SerializeField] private float standCamHeight = 0.559f; //set this to the camera's current local Y
    [SerializeField] private float crouchCamHeight = 0.1f;  //lower, tune to taste
    [SerializeField] private LayerMask ceilingMask; //set in inspector to everything EXCEPT the player

    private float gravity = 9.81f; //regular gravity lol
    public float verticalVelocity = 0f;
    private float yRotation = 0f;
    private float xRotation = 0f;

    private float standingHeight = 2f;
    private float crouchingHeight = 1f;
    private float crouchSpeed = 10f; //how fast the player transitions between crouching and standing
    public override void Spawned()
    {
        Camera mainCam = GetComponentInChildren<Camera>(); //raw camera
        CinemachineVirtualCamera virtualCam = GetComponentInChildren<CinemachineVirtualCamera>(); //cinemachine virtual

        if (HasInputAuthority) //if our player
        {
            LocalPlayer = this; //set the local player to this instance of the player script
            mainCam.enabled = true; //this our camera
            playerCamera = virtualCam.transform; //set the player camera to the virtual cam's transform, which is used for looking up and down
            playerInputActions = new PlayerInputActions(); //our input actions
            playerInputActions.Player.Enable(); //our input actions enabled
            Cursor.lockState = CursorLockMode.Locked; //our cursor locked
        }
        else
        {
            mainCam.enabled = false;
            virtualCam.enabled = false;
            foreach (var listener in GetComponentsInChildren<AudioListener>())
            {
                listener.enabled = false; //turn off other ears if not our player
            }
        }
    }
    private void Update()
    {
        if (!HasInputAuthority) return; //stop here if not our instance of player
        HandleLook(); //our player only
    }
    private void LateUpdate()
    {
        if (!HasInputAuthority) return;
        transform.rotation = Quaternion.Euler(0f, yRotation, 0f);
    }

    public override void FixedUpdateNetwork()
    {
        PlayerGravity();
        if (GetInput(out NetworkInputData networkInputData))
        {
            HandleMovement(networkInputData.movementInput); //hands off movement input from the network to handle movement, which is where inputVector uses it
            HandleCrouch(networkInputData.crouchInput); //hands off the crouch input data when the function is called
        }
    }
    private void HandleMovement(Vector2 inputVector)
    {
        Vector3 moveDir = transform.right * inputVector.x + transform.forward * inputVector.y; //move direction stays relative to where player is looking, so forward is always forward for the player, not the world
        float moveDistance = moveSpeed * Runner.DeltaTime; //how far we move per frame
        characterController.Move(moveDir * moveDistance + Vector3.up * verticalVelocity * Runner.DeltaTime); //
    }
    private bool BlockedAbove()
    {
        float radius = characterController.radius; //radius of our capsule
        //top hemisphere center of the capsule AS IT IS RIGHT NOW, in world space
        Vector3 capsuleTop = transform.position + characterController.center + Vector3.up * (characterController.height / 2f - radius); 
        //how much more we'd grow to reach full standing
        float distance = standingHeight - characterController.height + 0.05f; //add a small buffer so we dont have to be perfectly flush with the ceiling to crouch
        return Physics.SphereCast(capsuleTop, radius, Vector3.up, out _, distance, ceilingMask);
    }
    private void HandleCrouch(bool crouching)
    {
        //pick the height we want based on if the crouch key is held
        bool staysCrouched = crouching || BlockedAbove(); //if crouch held OR no room to stand, stay down
        float targetHeight = staysCrouched ? crouchingHeight : standingHeight;
        float targetCamY = staysCrouched ? crouchCamHeight : standCamHeight; //question mark acts as a tiny if/else, if crouching is true, target height is crouching height, if crouching is false, target height is standing height

        //ease the controller height toward the target so it doesnt snap instantly
        characterController.height = Mathf.Lerp(characterController.height, targetHeight, crouchSpeed * Runner.DeltaTime); //height transitions smoothly to the target height based on crouchSpeed

        //as the capsule shrinks, drop the center by half the shrink so feet stay planted instead of floating up
        characterController.center = new Vector3(0f, (characterController.height - standingHeight) / 2f, 0f);

        //ease the camera down to crouch eye level and back up to match the body
        Vector3 camPos = playerCamera.localPosition;
        camPos.y = Mathf.Lerp(camPos.y, targetCamY, crouchSpeed * Runner.DeltaTime); //camera y transitions smoothly to the target cam height based on crouchSpeed
        playerCamera.localPosition = camPos;
    }
    private void HandleLook()
    {
        Vector2 lookInput = playerInputActions.Player.Look.ReadValue<Vector2>();

        // Vertical camera pitch
        xRotation -= lookInput.y * mouseSensitivity;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f); //clamp to prevent flipping over
        playerCamera.localRotation = Quaternion.Euler(xRotation, 0f, 0f);

        // Horizontal player body 
        yRotation += lookInput.x * mouseSensitivity;
        transform.rotation = Quaternion.Euler(0f, yRotation, 0f);
    }
    private void PlayerGravity()
    {
        if (characterController.isGrounded && verticalVelocity < 0) //if player is on the ground and velocity is negative, reset velocity to a small negative value
        {
            verticalVelocity = -2f; //small negative keeps player grounded
        }

        verticalVelocity -= gravity * Runner.DeltaTime; ; // velocity grows more negative each frame
        verticalVelocity = Mathf.Max(verticalVelocity, -20f); // terminal velocity
    }
}

