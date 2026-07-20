using Fusion;
using UnityEngine;

public struct NetworkInputData : INetworkInput //A struct holds input of the player, it is sorta an array that can hold multiple types of info to my understanding
{
    //A INetworkInput struct retains values of the player and sends it to some sort of network
    public Vector2 movementInput;
    public bool crouchInput;
    public bool sprintInput;
    public bool jumpInput;
    public bool interactInput;
    public bool flashlightInput; //F held this tick - the player edge-detects it to toggle the flashlight on/off
    public bool dropInput;       //G held this tick - edge-detected to drop the most recent inventory item

}
