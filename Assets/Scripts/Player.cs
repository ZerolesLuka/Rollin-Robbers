using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// Player is split across partial files to keep this from being one 750-line monster:
//   Player.cs             - this file: all [Networked] state, lifecycle (Spawned/Despawned), the FixedUpdateNetwork
//                           state machine, the Update dispatch, and the caught/drag/rescue RPCs
//   Player.Movement.cs    - walking, crouching, sprinting, jumping, gravity, mouse look
//   Player.Interaction.cs - the E key: rescue, item pickup, loot, doors, van, computer, pawn shop, hiding spots
//   Player.Inventory.cs   - dropping carried items back into the world (G)
//   Player.Flashlight.cs  - the handheld flashlight beam + toggle (F)
//   Player.Teleport.cs    - scene-load spawning and the run-end van ride
//   Player.Computer.cs    - claiming and sitting at the van computer
// Everything Fusion weaves lives HERE so the weaver sees it in one place.
public partial class Player : NetworkBehaviour
{
    public PlayerInputActions playerInputActions;
    public static Player LocalPlayer;
    public Camera ViewCamera { get; private set; } //the rendering camera for this player - world-space UI needs it to raycast clicks
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

    [SerializeField] private float landNoiseAmount = 30f;      //how loud a hard landing is to the guard - way above walking (7) or sprinting (10.5), so a jump-land near him gives you away
    [SerializeField] private float landNoiseDecayRate = 60f;   //how fast that landing spike rings out, units per second
    [SerializeField] private float minLandingFallSpeed = 4f;   //must be dropping at least this fast to count as a landing - stepping off a small lip stays quiet
    private float landingNoise;                                //current landing-noise spike; decays each tick and folds into NoiseLevel
    private bool wasGroundedForLanding;                        //grounded state last tick, to catch the airborne -> grounded moment
    private PlayerFootsteps playerFootsteps;                   //cached so a landing can fire the thud through the footstep audio pipeline

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
    [Networked] public bool IsHiding { get; private set; } //inside a hiding spot - invisible to guards, can't move
    [SerializeField] private GameObject playerVisuals; // parent of all mesh renderers; assign in inspector
    private bool wasHiding;

    [Networked] public bool IsFlashlightOn { get; private set; } //replicated so teammates see your beam, same idea as IsHiding driving playerVisuals
    [Networked] private float lookPitch { get; set; } //owner writes its up/down look angle here so remote clients can aim its flashlight beam vertically (their camera pitch isn't otherwise networked)
    [SerializeField] private Light flashlight; //spotlight child; assign in inspector
    [SerializeField] private float flashlightFollowSpeed = 8f; //how fast the beam catches up to where you're looking - lower = more lag/sway off the camera
    [SerializeField] private float flashlightSwayAmount = 1.5f; //idle handheld tremor, in degrees - keeps the beam alive when you're still
    [SerializeField] private float flashlightSwayFrequency = 1.1f; //how fast that tremor drifts
    [SerializeField] private float flashlightWalkSwayMultiplier = 3f; //how much bigger the sway gets while walking - the bob that sells "handheld"
    private bool flashlightHeldLastTick; //rising-edge detect so one press = one toggle
    private Vector3 flashlightLastPosition; //to gauge how fast this player is moving, for the walk bob
    private bool hasRiddenVanForRunEnd; //one-shot so the run-end van teleport only fires once
    private bool isUsingComputer; //local only - frozen at the van computer, camera focused on the screen, cursor freed
    private ComputerTerminal currentTerminal; //the terminal we're currently "in", so E can exit it
    private ComputerTerminal pendingTerminal; //terminal we've asked to use and are waiting on the networked lock for

    [SerializeField] private NetworkObject worldItemPrefab; //spawned when you drop - the generic pickup item, named on spawn
    [SerializeField] private int maxInventorySlots = 4;
    [SerializeField] private float pickupRange = 2f;
    [SerializeField] private float dropForwardOffset = 1f; //drop slightly in front so the item doesn't spawn inside you
    private readonly List<InventoryItem> inventory = new List<InventoryItem>(); //local only - carried loot (name + value), shown on this player's own HUD
    public IReadOnlyList<InventoryItem> Inventory => inventory;
    public int MaxInventorySlots => maxInventorySlots;
    public int CarriedValue //total worth of what this player is holding - the HUD reads it, selling banks it
    {
        get
        {
            int total = 0;
            foreach (InventoryItem carried in inventory) total += carried.value;
            return total;
        }
    }
    private bool dropHeldLastTick; //rising-edge detect so one G press drops one item
    private bool hasPendingTeleport; //teleport requested from the scene-load coroutine, applied in FixedUpdateNetwork so the networked position updates and Fusion doesn't snap us back
    private Vector3 pendingTeleportPosition;
    private int teleportSettleTicks; //ticks to hold position with the CC disabled after a teleport, so the disable is processed before we re-enable (a same-frame off/on doesn't reset the CC's internal position)
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
        ViewCamera = mainCam; //exposed so world-space UI (the computer screen) can use it to raycast clicks
        CinemachineVirtualCamera virtualCam = GetComponentInChildren<CinemachineVirtualCamera>(); //cinemachine virtual
        stamina = maxStamina;
        playerFootsteps = GetComponentInChildren<PlayerFootsteps>(); //so a hard landing can fire a thud through the footstep audio

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

        if (playerVisuals != null && Object != null && Object.IsValid && IsHiding != wasHiding)
        {
            wasHiding = IsHiding;
            playerVisuals.SetActive(!IsHiding); // runs on all clients so other players see you vanish
        }

        UpdateFlashlight(); //runs on ALL clients so everyone sees this player's beam, driven by the networked IsFlashlightOn + lookPitch

        if (!HasInputAuthority) return; //stop here if not our instance of player
        UpdateComputerClaim(); //enter the computer once the networked lock is granted (or drop our request if someone else got it)
        if (isUsingComputer) return; //parked at the computer - don't let the mouse spin the body/look while the cursor's free
        HandleLook(); //our player only
        HandleCrouchCamera(); //ease the crouch eye-height on the render frame so it's smooth at any FPS
    }

    private void LateUpdate()
    {
        if (!HasInputAuthority) return;
        if (isUsingComputer) return; //hold still while parked at the computer
        transform.rotation = Quaternion.Euler(0f, yRotation, 0f);
    }

    public override void FixedUpdateNetwork()
    {
        if (hasPendingTeleport)
        {
            ApplyPendingTeleport(); //begins the teleport: CC off, position set, networked. CC is re-enabled a couple ticks later once the disable has processed
            return;
        }

        if (teleportSettleTicks > 0)
        {
            transform.position = pendingTeleportPosition; //hold firmly at the spawn while the CC is disabled so nothing drifts
            NoiseLevel = 0f; //a mid-teleport player isn't making footstep noise - clear the stale walking value so a freshly-spawned guard doesn't "hear" the footstep from before the transition
            teleportSettleTicks--;
            if (teleportSettleTicks == 0)
            {
                characterController.enabled = true; //re-enable now - the disable happened ticks ago, so the CC resets its internal position to the spawn cleanly
            }
            return; //no movement while settling
        }

        //a fresh run started (House button) - re-arm the one-shot so the NEXT run-over ride can fire
        if (hasRiddenVanForRunEnd && RunManager.Instance != null && RunManager.Instance.Object != null
            && RunManager.Instance.Object.IsValid && RunManager.Instance.State == RunManager.RunState.InProgress)
        {
            hasRiddenVanForRunEnd = false;
        }

        //run's over - ride to the van. done here in FUN (not a coroutine) so the state resets below actually stick as networked values, and so it fires whether or not a scene reload happened
        if (HasStateAuthority && !hasRiddenVanForRunEnd && RunManager.Instance != null && RunManager.Instance.Object != null
            && RunManager.Instance.Object.IsValid && RunManager.Instance.State != RunManager.RunState.InProgress)
        {
            VanSeat seat = FindMyVanSeat(); //null until the van's scene is actually loaded - indoor players wait for RunManager's reload to bring the seats in
            if (seat != null)
            {
                hasRiddenVanForRunEnd = true;
                IsEliminated = false;  //pull caught/dead players back in - no loot, just a clean slate (sticks because we're in FUN)
                IsLockedUp = false;
                isBeingDragged = false;
                draggingGuard = null;
                dragTrail.Clear();
                TeleportTo(seat.transform.position);
                return; //hand off to the teleport pipeline next tick
            }
        }

        if (IsEliminated) return; //eliminated players don't move, fall, or make noise anymore - stays true until the run ends and the van ride resets it (above)

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

        if (IsHiding) // locked inside a hiding spot - can't move, but can still press E to exit
        {
            NoiseLevel = 0f;
            if (GetInput(out NetworkInputData hidingInput))
                HandleInteract(hidingInput.interactInput);
            return;
        }

        if (isUsingComputer) // parked at the van computer - frozen, but E backs out
        {
            NoiseLevel = 0f;
            if (GetInput(out NetworkInputData computerInput))
            {
                bool exitPressed = computerInput.interactInput && !interactHeldLastTick; //rising edge so the enter-press doesn't instantly exit
                interactHeldLastTick = computerInput.interactInput;
                if (exitPressed && currentTerminal != null)
                {
                    currentTerminal.Exit();
                }
            }
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

        if (HasStateAuthority)
        {
            lookPitch = xRotation; //publish our up/down look angle so remote clients can aim our flashlight beam (their copy never runs HandleLook)
        }

        PlayerGravity();
        if (GetInput(out NetworkInputData networkInputData))
        {
            HandleMovement(networkInputData.movementInput, networkInputData.sprintInput, networkInputData.jumpInput); //hands off movement input from the network to handle movement, which is where inputVector uses it
            HandleCrouch(networkInputData.crouchInput); //hands off the crouch input data when the function is called
            HandleInteract(networkInputData.interactInput); //E to free a trapped teammate
            HandleFlashlight(networkInputData.flashlightInput); //F to toggle the flashlight
            HandleDrop(networkInputData.dropInput); //G to drop an item on the floor
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        ActivePlayers.Remove(this); //leave the list on disconnect so nobody iterates a destroyed player
        if (HasInputAuthority) SceneManager.activeSceneChanged -= OnSceneChanged;
    }

    public void SetHiding(bool hiding) => IsHiding = hiding;

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
