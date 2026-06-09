using UnityEngine;
using Fusion;

public class PlayerFootsteps : NetworkBehaviour
{
    [SerializeField] private AudioSource audioSource; // Reference to the AudioSource component
    [SerializeField] private AudioClip[] footstepClips; // Array of footstep audio clips
    [SerializeField] private CharacterController characterController;
    [SerializeField] private float strideLength = 2f;   //distance walked per step

    private Vector3 lastPosition; // To track the player's last position
    private float stepAccumulator; // Accumulates distance walked to determine when to play the next footstep sound

    public override void Spawned()
    {
        
    }
    public override void FixedUpdateNetwork()
    {
        
    }
    private void PlayFootstep()
    {

    }
}
