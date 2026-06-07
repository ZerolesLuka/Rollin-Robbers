using Fusion;
using UnityEngine;
using static Unity.Collections.Unicode;

public class PlayerMovement : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 7f;
    [SerializeField] private CharacterController characterController; //charcontroller
    private float verticalVelocity = Player.LocalPlayer.verticalVelocity; //gravity
    private void HandleMovement(Vector2 inputVector)
    {
        
        Vector3 moveDir = transform.right * inputVector.x + transform.forward * inputVector.y; //converts the 2d input vector to a 3d movement direction based on the player's orientation
        float moveDistance = moveSpeed * Runner.DeltaTime; //Sets for the raycast
        float playerRadius = .3f;//Set for the raycast
        float playerHeight = 2f; //Set for the raycast

        bool canMove = !Physics.CapsuleCast(transform.position, transform.position + Vector3.up * playerHeight, playerRadius, moveDir, moveDistance);


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


}
