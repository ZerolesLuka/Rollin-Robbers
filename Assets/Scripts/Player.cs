using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
//
public class Player : NetworkBehaviour
{
    public PlayerInputActions playerInputActions;
    public static Player LocalPlayer;
    public static readonly List<Player> ActivePlayers = new List<Player>(); //everyone currently in the session, AIs read this instead of scanning the scene once and going stale
    [Networked] public float NoiseLevel { get; private set; }
    [SerializeField] private float moveSpeed = 7f;
    [SerializeField] private Transform playerCamera; //simple camera ref
    [SerializeField] private float mouseSensitivity = 0.5f; //DOES NOT WORK
    [SerializeField] private CharacterController characterController; //charcontroller
    [SerializeField] private float standCamHeight = 0.559f; //set this to the camera's current local Y
    [SerializeField] private float crouchCamHeight = 0.1f;  //lower, tune to taste
    [SerializeField] private LayerMask ceilingMask; //set in inspector to everything EXCEPT the player
    [SerializeField] private float crouchSpeedMultiplier = 0.5f;
    [SerializeField] private float sprintSpeedMultiplier = 1.5f;
    [SerializeField] private float voiceNoiseScale = 16f; //higher = more sensitive guard
    [SerializeField] private float maxStamina = 3f;        //seconds of sprint you get
    [SerializeField] private float staminaRegenRate = 1f;  //stamina back per second when not sprinting
    [SerializeField] private float jumpHeight = 1.5f; //jump

    [SerializeField] private float fallGravityMultiplier = 2.2f;
    [SerializeField] private float lowJumpGravityMultiplier = 1.6f;
    public float staminaNormalized => stamina / maxStamina; //0..1 for the HUD to read

    private float stamina;                                 //current
    private bool exhausted;                                //hit empty -> must recover before sprinting again

    private bool isCrouching;
    private float gravity = 9.81f; //regular gravity lol
    public float verticalVelocity = 0f;
    private bool jumpHeldLastTick; //jump fires only on the rising edge (press), so holding space doesn't bunnyhop
    private float yRotation = 0f;
    private float xRotation = 0f;

    private float standingHeight = 2f;
    private float crouchingHeight = 1f;
    private float crouchSpeed = 10f; //how fast the player transitions between crouching and standing

    [Networked] public bool IsEliminated { get; private set; } //caught = out for the run, not respawned
    [Networked] public bool IsLockedUp { get; private set; } //stuffed in the closet - frozen, out of action but rescuable, freed ONLY by a teammate
    private bool isBeingDragged; //true while the guard is hauling us to the closet
    private GuardPatrol draggingGuard; //the guard currently dragging us, so we can trail behind him
    private readonly Queue<Vector3> dragTrail = new Queue<Vector3>(); //the guard's recent positions - a dragged player rides a point on this trail, following his REAL path (behind him, through doors, never clipping walls or sitting inside him)
    [SerializeField] private int dragTrailLag = 12; //how many ticks back on the guard's path the player trails (bigger = further behind)
    [SerializeField] private float rescueRange = 2.5f; //how close a free teammate must be to spring you from the closet
    [SerializeField] private float lootRange = 2f;    //how close you must be to pick up a lootable item
    [SerializeField] private float suffocateDuration = 45f; //taped mouth - seconds of air before you die if no teammate frees you
    private float suffocateTimer; //counts down while locked; hits 0 = you suffocate
    public float ScreenFade => IsEliminated ? 1f : ((IsLockedUp && suffocateDuration > 0f) ? Mathf.Clamp01(1f - suffocateTimer / suffocateDuration) : 0f); //0 = normal, ramps while suffocating, 1 = dead/blacked out. HUD reads this for the fullscreen fade
    [SerializeField] private AudioLowPassFilter voiceMuffle; //on the player's Photon Voice Speaker AudioSource - enabled while trapped so their LIVE voice comes through everyone muffled (taped mouth)
    private bool interactHeldLastTick; //interact fires on the rising edge (press), not while held

    public override void Spawned()
    {
        DontDestroyOnLoad(gameObject); //survive scene loads - Fusion doesn't always preserve spawned objects automatically
        ActivePlayers.Add(this);
        characterController.enabled = false;
        characterController.enabled = true;
        Camera mainCam = GetComponentInChildren<Camera>(); //raw camera
        CinemachineVirtualCamera virtualCam = GetComponentInChildren<CinemachineVirtualCamera>(); //cinemachine virtual
        stamina = maxStamina;

        if (RunManager.Instance != null) RunManager.Instance.RegisterPlayer();

        if (HasInputAuthority) //if our player
        {
            LocalPlayer = this; //set the local player to this instance of the player script
            mainCam.enabled = true; //this our camera
            playerCamera = virtualCam.transform; //set the player camera to the virtual cam's transform, which is used for looking up and down
            playerInputActions = new PlayerInputActions(); //our input actions
            playerInputActions.Player.Enable(); //our input actions enabled
            Cursor.lockState = CursorLockMode.Locked; //our cursor locked
            SceneManager.activeSceneChanged += OnSceneChanged; //teleport to the new scene's spawn when Fusion loads a new scene
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
        if (voiceMuffle != null && Object != null && Object.IsValid)
        {
            voiceMuffle.enabled = IsLockedUp; //taped mouth: muffle this player's LIVE voice while trapped. Runs on ALL copies (before the authority check) so remote teammates hear the muffle, driven by the [Networked] IsLockedUp
        }

        if (!HasInputAuthority) return; //stop here if not our instance of player
        HandleLook(); //our player only
        HandleCrouchCamera(); //ease the crouch eye-height on the render frame so it's smooth at any FPS
    }
    private void LateUpdate()
    {
        if (!HasInputAuthority) return;
        transform.rotation = Quaternion.Euler(0f, yRotation, 0f);
    }

    public override void FixedUpdateNetwork()
    {
        if (IsEliminated) return; //eliminated players don't move, fall, or make noise anymore

        if (isBeingDragged) //the guard is hauling us to the closet - no control, trail behind him on his path
        {
            if (draggingGuard != null)
            {
                dragTrail.Enqueue(draggingGuard.transform.position); //remember where the guard is each tick
                while (dragTrail.Count > dragTrailLag)
                {
                    dragTrail.Dequeue(); //keep only the last few positions
                }
                transform.position = dragTrail.Peek(); //sit where the guard was a few ticks ago - behind him, ON his valid path (no clipping, not inside him)
            }
            NoiseLevel = 0f; //can't make useful noise while grabbed
            return;
        }

        if (IsLockedUp) //stuffed in the closet - frozen; a teammate must free you before your air runs out
        {
            if (HasStateAuthority)
            {
                NoiseLevel = 0f; //taped mouth - the guard hears nothing, so screaming for help is safe (voice still carries to teammates, just muffled - see voiceMuffle in Update)
                suffocateTimer -= Runner.DeltaTime;
                if (suffocateTimer <= 0f) //ran out of air, nobody came
                {
                    IsLockedUp = false;
                    IsEliminated = true;                 //dead for the run
                    characterController.enabled = false; //freeze the body like any other elimination
                    if (RunManager.Instance != null)
                    {
                        RunManager.Instance.RPC_ReportCaught(); //hop the death to the master so the alive-count drops
                    }
                }
            }
            return;
        }

        PlayerGravity();
        if (GetInput(out NetworkInputData networkInputData))
        {
            HandleMovement(networkInputData.movementInput, networkInputData.sprintInput, networkInputData.jumpInput); //hands off movement input from the network to handle movement, which is where inputVector uses it
            HandleCrouch(networkInputData.crouchInput); //hands off the crouch input data when the function is called
            HandleInteract(networkInputData.interactInput); //E to free a trapped teammate
        }
    }
    private void HandleMovement(Vector2 inputVector, bool sprinting, bool jumpInput)
    {
        Vector3 moveDir = transform.right * inputVector.x + transform.forward * inputVector.y; //move direction stays relative to where player is looking, so forward is always forward for the player, not the world

        bool isSprinting = sprinting && !isCrouching && !exhausted && stamina > 0f;
        if (isSprinting)
        {
            stamina -= Runner.DeltaTime; //sprinting burns stamina
            if (stamina <= 0f)
            {
                stamina = 0f;
                exhausted = true; //gassed out, locked until recovered
            }
        }
        else
        {
            stamina = Mathf.Min(maxStamina, stamina + staminaRegenRate * Runner.DeltaTime); //recover when not sprinting
            if (exhausted && stamina >= maxStamina * 0.3f)
            {
                exhausted = false; //recovered enough to sprint again
            }
        }

        float speed = moveSpeed;
        if (isCrouching)
        {
            speed = moveSpeed * crouchSpeedMultiplier; //crouch wins
        }
        else if (isSprinting)
        {
            speed = moveSpeed * sprintSpeedMultiplier;
        }

        float moveDistance = speed * Runner.DeltaTime;
        if (jumpInput && !jumpHeldLastTick && characterController.isGrounded && !isCrouching) //rising edge only: must release + repress to jump again (no bunnyhop from holding space)
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * 2f * gravity);
        }
        jumpHeldLastTick = jumpInput; //remember this tick's hold state so next tick can detect a fresh press
        characterController.Move(moveDir * moveDistance + Vector3.up * verticalVelocity * Runner.DeltaTime);

        //noise comes AFTER speed is finalized
        float movementNoise = (inputVector.magnitude > 0.1f) ? speed : 0f; //moving = your speed, still = 0
        float voiceNoise = (MicLoudnessProbe.Instance != null) ? MicLoudnessProbe.Instance.VoiceLoudness * voiceNoiseScale : 0f;
        NoiseLevel = Mathf.Max(movementNoise, voiceNoise); //loudest of moving vs talking, set ONCE
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
        isCrouching = staysCrouched;
        float targetHeight = staysCrouched ? crouchingHeight : standingHeight;

        //ease the controller height toward the target so it doesnt snap instantly (collider stays on the 32Hz tick - fine for physics/networked body)
        characterController.height = Mathf.Lerp(characterController.height, targetHeight, crouchSpeed * Runner.DeltaTime); //height transitions smoothly to the target height based on crouchSpeed

        //as the capsule shrinks, drop the center by half the shrink so feet stay planted instead of floating up
        characterController.center = new Vector3(0f, (characterController.height - standingHeight) / 2f, 0f);
        //camera eye-height is eased in Update (render frame) instead - see HandleCrouchCamera - so it doesn't step at the 32Hz tick
    }

    private void HandleCrouchCamera() //eases the crouch eye-height on the RENDER frame (local only) so it's smooth at any FPS, not stepped at the 32Hz network tick
    {
        float targetCamY = isCrouching ? crouchCamHeight : standCamHeight;
        Vector3 camPos = playerCamera.localPosition;
        camPos.y = Mathf.Lerp(camPos.y, targetCamY, crouchSpeed * Time.deltaTime); //same easing, but on Time.deltaTime so it matches the render rate
        playerCamera.localPosition = camPos;
    }

    private void HandleInteract(bool interacting)
    {
        bool pressed = interacting && !interactHeldLastTick; //rising edge only - one action per press
        interactHeldLastTick = interacting;
        if (!pressed) return;

        //rescue takes priority: free the nearest trapped teammate. you can NEVER free yourself (locked player returns before this runs, and we skip self below)
        foreach (Player other in ActivePlayers)
        {
            if (other == this) continue;
            if (!other.IsLockedUp) continue;
            if (Vector3.Distance(transform.position, other.transform.position) <= rescueRange)
            {
                other.RPC_Rescue();
                return; //rescued, done for this press
            }
        }

        //loot pickup: only runs if no rescue happened
        if (RunManager.Instance == null) return;
        foreach (Lootable lootable in Lootable.AllLootables)
        {
            if (lootable.IsLooted) continue;
            if (Vector3.Distance(transform.position, lootable.transform.position) <= lootRange)
            {
                RunManager.Instance.RPC_ClaimLoot(lootable.lootId, lootable.value);
                return; //looted, done for this press
            }
        }

        //exit door: only runs if no rescue or loot happened
        foreach (ExitDoor door in ExitDoor.AllDoors)
        {
            if (Vector3.Distance(transform.position, door.transform.position) <= door.interactRange)
            {
                RunManager.Instance.RPC_LoadScene(door.targetSceneBuildIndex);
                return;
            }
        }
    }

    public void TeleportTo(Vector3 position) //called by GameBootstrap after a scene load to reposition the local player
    {
        if (!HasInputAuthority) return; //only move our own player; Fusion syncs the position to everyone else
        verticalVelocity = 0f; //reset fall speed so the player doesn't phase through the floor on arrival
        characterController.enabled = false;
        transform.position = position;
        characterController.enabled = true;
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
        if (characterController.isGrounded && verticalVelocity < 0f) //planted on the ground
        {
            verticalVelocity = -2f; //small downward stick keeps isGrounded reliable on steps/slopes, and stops the fall speed building to terminal velocity while just standing
            return;
        }

        float gravityMultiplier = 1f;

        if (verticalVelocity < 0f)
        {
            gravityMultiplier = fallGravityMultiplier;
        }
        else if (verticalVelocity > 0f)
        {
            gravityMultiplier = lowJumpGravityMultiplier;
        }

        verticalVelocity -= gravity * gravityMultiplier * Runner.DeltaTime;
        verticalVelocity = Mathf.Max(verticalVelocity, -20f); // terminal velocity
    }
    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        ActivePlayers.Remove(this); //leave the list on disconnect so nobody iterates a destroyed player
        if (HasInputAuthority) SceneManager.activeSceneChanged -= OnSceneChanged;
    }

    private void OnSceneChanged(Scene previous, Scene next)
    {
        Cursor.lockState = CursorLockMode.Locked;
        StartCoroutine(TeleportAfterLoad());
    }

    private System.Collections.IEnumerator TeleportAfterLoad()
    {
        //scene objects aren't always ready on the frame activeSceneChanged fires - poll until the spawn point exists
        GameObject spawnPoint = null;
        float timeout = 3f;
        while (spawnPoint == null && timeout > 0f)
        {
            spawnPoint = GameObject.Find("PlayerSpawn");
            timeout -= Time.deltaTime;
            yield return null;
        }
        if (spawnPoint != null)
        {
            TeleportTo(spawnPoint.transform.position);
        }
        else
        {
            Debug.LogWarning($"[Player] PlayerSpawn not found. Active scene: '{SceneManager.GetActiveScene().name}'. Root objects:");
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                Debug.LogWarning($"  '{root.name}' (active: {root.activeInHierarchy})");
            }
        }
    }
    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] // any caller; runs on the caught player's own machine
    public void RPC_GetCaught()
    {
        IsEliminated = true;                 // out for the run - spectator handoff + visuals come later in Unity
        characterController.enabled = false; // freeze them in place, no more moving or colliding
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] // runs on the dragged player's own machine, so we own the movement in Shared Mode
    public void RPC_GetDragged(GuardPatrol guard)
    {
        draggingGuard = guard;
        isBeingDragged = true;
        dragTrail.Clear();                   // fresh trail for this drag
        verticalVelocity = 0f;               // don't bank fall velocity while pinned
        characterController.enabled = false; // off so we can be positioned along the guard's path without the CC fighting it
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] // runs on the dragged player's own machine
    public void RPC_GetLockedUp(Vector3 closetPosition)
    {
        isBeingDragged = false;
        draggingGuard = null;
        characterController.enabled = false; // toggle the CC so it accepts the teleport 
        transform.position = closetPosition;
        characterController.enabled = true;
        IsLockedUp = true;
        suffocateTimer = suffocateDuration; //start the air clock the moment the closet closes
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] //any caller (the rescuer); runs on the freed player's own machine
    public void RPC_Rescue()
    {
        if (!IsLockedUp)
        {
            return; //nothing to free
        }
        IsLockedUp = false; //sprung by a friend - the only way out of the closet
    }
}

