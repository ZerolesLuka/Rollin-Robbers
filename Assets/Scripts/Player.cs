using UnityEngine;
using UnityEngine.InputSystem;
using System;

public class Player : MonoBehaviour
{
    private PlayerInputActions playerInputActions;
    [SerializeField] private float moveSpeed = 7f;
    [SerializeField] private Transform playerCamera;
    [SerializeField] private float mouseSensitivity = 0.5f;
    [SerializeField] private CharacterController characterController;
    private float xRotation = 0f;

    private void Awake()
    {
        playerInputActions = new PlayerInputActions(); //creates new inputaction
        playerInputActions.Player.Enable();
        Cursor.lockState = CursorLockMode.Locked;
    }

    private void Update()
    {
        HandleMovement();
        HandleLook();
    }

    public Vector2 GetMovementVectorNormalized() //ensure same speed 
    {
        Vector2 inputVector = playerInputActions.Player.Move.ReadValue<Vector2>();// Handle movement logic here using movementInput
        inputVector = inputVector.normalized; // Normalize the movement vector to ensure consistent speed in all directions

        return inputVector;

    }

    private void HandleMovement()
    {
        Vector2 inputVector = GetMovementVectorNormalized(); //sets the input vector to the normalized movement vector

        Vector3 moveDir = transform.right * inputVector.x + transform.forward * inputVector.y; //converts the 2d input vector to a 3d movement direction based on the player's orientation
        float moveDistance = moveSpeed * Time.deltaTime; //Sets for the raycast
        float playerRadius = .3f;//Set for the raycast
        float playerHeight = 2f; //Set for the raycast

        bool canMove = !Physics.CapsuleCast(transform.position, transform.position + Vector3.up * playerHeight, playerRadius, moveDir, moveDistance );

        if (canMove)
        {
            transform.position += moveDir * moveDistance; //position += direction * speed * time
        }

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
                }
            }
        }

        

    }
    private void HandleLook()
    {
        Vector2 lookInput = playerInputActions.Player.Look.ReadValue<Vector2>();

        // Rotate player body left/right
        transform.Rotate(Vector3.up * lookInput.x * mouseSensitivity);

        // Rotate camera up/down, clamped
        xRotation -= lookInput.y * mouseSensitivity;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);
        playerCamera.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
    }
}

