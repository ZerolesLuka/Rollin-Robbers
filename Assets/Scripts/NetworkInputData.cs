using Fusion;
using UnityEngine;

public struct NetworkInputData : INetworkInput //A struct holds input of the player, it is sorta an array that can hold multiple types of info to my understanding
{
    //A INetworkInput struct retains values of the player and sends it to some sort of network
    public Vector2 movementInput;
    public Vector2 lookInput;
    public bool crouchInput;
    public bool sprintInput;
    public bool jumpInput;
    public bool interactInput;

}
