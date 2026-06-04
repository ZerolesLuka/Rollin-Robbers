using Cinemachine;
using Fusion;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;

public class Player : NetworkBehaviour
{
    private PlayerInputActions playerInputActions;
    [SerializeField] private float moveSpeed = 7f;
    [SerializeField] private Transform playerCamera; //simple camera ref
    [SerializeField] private float mouseSensitivity = 0.5f; //DOES NOT WORK
    [SerializeField] private CharacterController characterController; //charcontroller

    [Networked] public Vector3 NetworkedPosition { get; set; } //networked property to sync player position across the network, automatically updated by fusion and can be accessed by all clients
    private float xRotation = 0f;
    private float gravity = 9.81f;
    private float verticalVelocity = 0f;
    public override void Spawned()
    {
        Camera mainCam = GetComponentInChildren<Camera>(); //raw camera
        CinemachineVirtualCamera virtualCam = GetComponentInChildren<CinemachineVirtualCamera>(); //cinemachine virtual

        if (HasInputAuthority) //if our player
        {
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
        if (!HasInputAuthority) return;
        HandleMovement();
        PlayerGravity();
        HandleLook();
    }


    /*public override void FixedUpdateNetwork()
    {
        // sync logic goes here later
    }*/
    public Vector2 GetMovementVectorNormalized() //ensure same speed 
    {
        Vector2 inputVector = playerInputActions.Player.Move.ReadValue<Vector2>();// Handle movement logic here using movementInput
        inputVector = inputVector.normalized; // Normalize the movement vector to ensure consistent speed in all directions

        return inputVector; //gives the var input vector back to the caller
    }

    private void HandleMovement()
    {
        Vector2 inputVector = GetMovementVectorNormalized(); //sets the input vector to the normalized movement vector

        Vector3 moveDir = transform.right * inputVector.x + transform.forward * inputVector.y; //converts the 2d input vector to a 3d movement direction based on the player's orientation
        float moveDistance = moveSpeed * Runner.DeltaTime; //Sets for the raycast
        float playerRadius = .3f;//Set for the raycast
        float playerHeight = 2f; //Set for the raycast

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
    private void HandleLook()
    {
        Vector2 lookInput = playerInputActions.Player.Look.ReadValue<Vector2>();

        // Rotate player body left/right
        transform.Rotate(Vector3.up * lookInput.x * mouseSensitivity); // Rotate the player around the y-axis based on horizontal mouse movement

        // Rotate camera up/down, clamped
        xRotation -= lookInput.y * mouseSensitivity;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);
        playerCamera.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
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

