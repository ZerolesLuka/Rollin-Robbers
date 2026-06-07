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
            mainCam.enabled = false; //any other player's camera is disabled for us
            virtualCam.enabled = false;// any other player's camera is disabled for us
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
            HandleCrouch(networkInputData.crouchInput); //hands off crouch input from the network to handle crouching
        }
    }

    private void HandleMovement(Vector2 inputVector)
    { 

        Vector3 moveDir = transform.right * inputVector.x + transform.forward * inputVector.y; //converts the 2d input vector to a 3d movement direction based on the player's orientation
        float moveDistance = moveSpeed * Runner.DeltaTime; //Sets for the raycast
        float playerRadius = .3f;//Set for the raycast
        float playerHeight = characterController.height; //Set for the raycast

        bool canMove = !Physics.CapsuleCast(transform.position, transform.position + Vector3.up * playerHeight, playerRadius, moveDir, moveDistance );


        if (!canMove)
        {
            Vector3 moveDirX = new Vector3(moveDir.x, 0, 0).normalized; //new vector for the x dir
            canMove = moveDir.x < -.5f || moveDir.x > +.5f && !Physics.CapsuleCast(transform.position, transform.position + Vector3.up * playerHeight, playerRadius, moveDirX, moveDistance); //tryna move in x dir
            if (canMove)
            {
                moveDir = moveDirX; //sets the move dir to the x dir if can move in x dir
            }
            else
            {
                Vector3 moveDirZ = new Vector3(0, 0, moveDir.z).normalized; //new vector for the z dir
                canMove = moveDir.z < -.5f || moveDir.z > +.5f && !Physics.CapsuleCast(transform.position, transform.position + Vector3.up * playerHeight, playerRadius, moveDirZ, moveDistance);//tryna move in z dir
                if (canMove)
                {
                    moveDir = moveDirZ; //sets the move dir to the z dir if can move in z dir
                }
                else
                {
                    //cant move anywhere bc y axis not applicable
                    moveDir = Vector3.zero;
                }
            }
        }

        characterController.Move(moveDir * moveDistance + Vector3.up * verticalVelocity * Runner.DeltaTime);

    }
    private void HandleCrouch(bool crouching)
    {
        //pick the height we want based on if the crouch key is held
        float targetHeight = crouching ? crouchingHeight : standingHeight; //question mark acts as a tiny if/else, if crouching is true, target height is crouching height, if crouching is false, target height is standing height

        //ease the controller height toward the target so it doesnt snap instantly
        characterController.height = Mathf.Lerp(characterController.height, targetHeight, crouchSpeed * Runner.DeltaTime); //height transitions smoothly to the target height based on crouchSpeed

        //as the capsule shrinks, drop the center by half the shrink so feet stay planted instead of floating up
        characterController.center = new Vector3(0f, (characterController.height - standingHeight) / 2f, 0f);

        //ease the camera down to crouch eye level and back up to match the body
        float targetCamY = crouching ? crouchCamHeight : standCamHeight; //question mark acts as a tiny if/else, if crouching is true, target height is crouching height, if crouching is false, target height is standing height
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

