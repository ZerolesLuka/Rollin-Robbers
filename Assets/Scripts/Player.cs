using UnityEngine;
using UnityEngine.InputSystem;
using System;

public class Player : MonoBehaviour
{
    private PlayerInputActions playerInputActions;



    private void Awake()
    {
        playerInputActions = new PlayerInputActions(); //creates new inputaction
        playerInputActions.Player.Enable();
    }

    private void Update()
    {
        HandleMovement();
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

        Vector3 moveDir = new Vector3(inputVector.x, 0, inputVector.y);

        print(inputVector);

    }
}

