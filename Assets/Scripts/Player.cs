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

    private void UpdateComputerClaim()
    {
        if (pendingTerminal == null || isUsingComputer) return;
        if (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid) return;

        if (RunManager.Instance.ComputerUser == Object.InputAuthority) //the lock is ours - sit down
        {
            ComputerTerminal terminal = pendingTerminal;
            pendingTerminal = null;
            terminal.Enter();
        }
        else if (!RunManager.Instance.IsComputerFree) //someone else grabbed it first - give up our request
        {
            pendingTerminal = null;
        }
    }

    private void UpdateFlashlight()
    {
        if (flashlight == null || Object == null || !Object.IsValid) return;

        flashlight.enabled = IsFlashlightOn; //on every client, so you see teammates' beams too
        if (!IsFlashlightOn)
        {
            flashlightLastPosition = transform.position; //keep this current while off so we don't get a huge fake "speed" spike the frame it turns on
            return;
        }

        //how fast are we actually moving this frame - drives a bigger bob while walking
        float speed = Time.deltaTime > 0f ? (transform.position - flashlightLastPosition).magnitude / Time.deltaTime : 0f;
        flashlightLastPosition = transform.position;
        float walkFactor = Mathf.Clamp01(speed / moveSpeed); //0 standing still, ~1 at full walk speed

        //handheld tremor: two Perlin channels drifting independently so it wanders naturally instead of looping. bigger while walking (the bob), always a little alive while still
        float swayScale = flashlightSwayAmount * (1f + walkFactor * flashlightWalkSwayMultiplier);
        float swayPitch = (Mathf.PerlinNoise(Time.time * flashlightSwayFrequency, 0f) - 0.5f) * 2f * swayScale;
        float swayYaw = (Mathf.PerlinNoise(0f, Time.time * flashlightSwayFrequency) - 0.5f) * 2f * swayScale;

        //aim where the player looks (networked yaw + pitch), plus the handheld sway, eased in so the beam trails the camera instead of snapping
        Quaternion aimRotation = Quaternion.Euler(lookPitch + swayPitch, transform.eulerAngles.y + swayYaw, 0f);
        flashlight.transform.rotation = Quaternion.Slerp(flashlight.transform.rotation, aimRotation, flashlightFollowSpeed * Time.deltaTime);
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

    private void HandleDrop(bool dropPressed)
    {
        bool pressed = dropPressed && !dropHeldLastTick; //rising edge only - one drop per press
        dropHeldLastTick = dropPressed;
        if (!pressed || inventory.Count == 0 || worldItemPrefab == null) return;

        InventoryItem dropped = inventory[inventory.Count - 1]; //drop the most recently picked up (the one you're "holding")
        inventory.RemoveAt(inventory.Count - 1);

        Vector3 dropPosition = transform.position + transform.forward * dropForwardOffset + Vector3.up; //spawn it a bit ahead and up so it falls to the floor
        Runner.Spawn(worldItemPrefab, dropPosition, UnityEngine.Random.rotation, Object.InputAuthority, //random tilt so it tumbles and lands on a face, not balanced on a point
            (runner, spawnedObject) =>
            {
                WorldItem item = spawnedObject.GetComponent<WorldItem>();
                if (item != null) //carry the name AND value back onto the dropped item so it's worth the same when re-picked
                {
                    item.ItemName = dropped.name;
                    item.Value = dropped.value;
                }
            });
    }

    private void HandleFlashlight(bool flashlightPressed)
    {
        bool pressed = flashlightPressed && !flashlightHeldLastTick; //rising edge only - one toggle per press
        flashlightHeldLastTick = flashlightPressed;
        if (pressed)
        {
            IsFlashlightOn = !IsFlashlightOn; //networked, so every client's copy of us updates the beam in Update
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

        //world item pickup: into the inventory (separate from the loot-value system). doesn't need RunManager, so it runs before that check
        if (inventory.Count < maxInventorySlots)
        {
            foreach (WorldItem item in WorldItem.AllItems)
            {
                if (item.pendingRemoval) continue; //already grabbed locally, waiting on the despawn
                if (Vector3.Distance(transform.position, item.transform.position) <= pickupRange)
                {
                    inventory.Add(new InventoryItem(item.ItemName.ToString(), item.Value));
                    if (RunManager.Instance != null) RunManager.Instance.RPC_ReportLootTaken(item.Value); //the house is now missing this - feeds the guard's suspicion
                    item.pendingRemoval = true; //so we don't re-grab it before it despawns
                    item.RPC_PickUp();
                    return; //picked up, done for this press
                }
            }
        }

        //loot pickup: only runs if no rescue or item pickup happened
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
                RunManager.Instance.RPC_LoadScene(door.targetSceneBuildIndex, door.spawnPointId);
                return;
            }
        }

        //getaway van: start it from the driver's seat and the run ends successfully for everyone
        foreach (Van van in Van.AllVans)
        {
            Transform seat = van.driverSeat != null ? van.driverSeat : van.transform;
            if (Vector3.Distance(transform.position, seat.position) <= van.interactRange)
            {
                RunManager.Instance.RPC_StartGetaway();
                return;
            }
        }

        //van computer: press E to sit down at it - only if nobody else is on it (networked lock). we don't enter here; we claim it and enter once granted (see UpdateComputerClaim)
        foreach (ComputerTerminal terminal in ComputerTerminal.AllTerminals)
        {
            if (Vector3.Distance(transform.position, terminal.transform.position) <= terminal.interactRange)
            {
                if (RunManager.Instance.IsComputerFree)
                {
                    pendingTerminal = terminal;
                    RunManager.Instance.RPC_ClaimComputer(Object.InputAuthority);
                }
                return;
            }
        }

       //pawn shop counter: sell the team's haul for money
        foreach (SellCounter counter in SellCounter.AllCounters)
        {
            if (Vector3.Distance(transform.position, counter.transform.position) <= counter.interactRange)
            {
                if (inventory.Count > 0)
                {
                    RunManager.Instance.RPC_SellItems(CarriedValue); //bank the worth of my haul into the shared money
                    inventory.Clear();                                //handed it all over
                }
                return;
            }
        }

        foreach (HidingSpot hidingSpot in HidingSpot.AllHidingSpots)
        {
            if(Vector3.Distance(transform.position, hidingSpot.transform.position) <= hidingSpot.interactRange)
            {
                if(!hidingSpot.isOccupied)
                {
                    hidingSpot.OnSpotEnter();
                    hidingSpot.isOccupied = true;
                }
                else if(hidingSpot.isOccupied && hidingSpot.isHiding)
                {
                    hidingSpot.OnSpotExit();
                }
                return;
            }
        }
    }

    public void TeleportTo(Vector3 position) //called after a scene load to reposition the local player
    {
        if (!HasInputAuthority) return; //only move our own player; Fusion syncs the position to everyone else
        pendingTeleportPosition = position;
        hasPendingTeleport = true; //applied in FixedUpdateNetwork - see the block at the top of it
    }

    private void ApplyPendingTeleport()
    {
        hasPendingTeleport = false;
        verticalVelocity = 0f; //reset fall speed so the player doesn't phase through the floor on arrival
        NoiseLevel = 0f; //clear any stale walking noise from before the transition so a freshly-spawned guard doesn't wake to a footstep that already happened
        characterController.enabled = false; //stays off for teleportSettleTicks so the disable is processed before the re-enable
        transform.position = pendingTeleportPosition;

        NetworkTransform networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform != null)
        {
            networkTransform.Teleport(pendingTeleportPosition); //update the networked position + clear interpolation so Fusion doesn't lerp us back to the old spot
        }

        teleportSettleTicks = 2;
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
        //if the run already ended, the van ride is handled in FixedUpdateNetwork - don't also do a normal door-spawn teleport here
        bool runEnded = RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid
            && RunManager.Instance.State != RunManager.RunState.InProgress;
        if (runEnded)
        {
            yield break;
        }

        SpawnPoint spawnPoint = null;
        float timeout = 5f;
        while (spawnPoint == null && timeout > 0f)
        {
            //re-read the id every frame - the networked value may not have replicated on the frame the scene loaded
            int targetId = (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
                ? RunManager.Instance.EntrySpawnPointId
                : 0;

            Scene activeScene = SceneManager.GetActiveScene();
            foreach (SpawnPoint candidate in SpawnPoint.All)
            {
                //ignore spawn points from the scene we just left - they linger in the static list for a frame or two and hold stale coordinates
                if (candidate.gameObject.scene != activeScene) continue;
                if (candidate.spawnId == targetId)
                {
                    spawnPoint = candidate;
                    break;
                }
            }
            timeout -= Time.deltaTime;
            yield return null;
        }

        //fall back to any spawn point IN THIS SCENE rather than leaving the player floating in the void at their old coordinates
        if (spawnPoint == null)
        {
            Scene activeScene = SceneManager.GetActiveScene();
            foreach (SpawnPoint candidate in SpawnPoint.All)
            {
                if (candidate.gameObject.scene != activeScene) continue;
                spawnPoint = candidate;
                Debug.LogWarning($"[Player] Matching SpawnPoint not found in time - falling back to '{spawnPoint.name}'.");
                break;
            }
        }

        if (spawnPoint != null)
        {
            TeleportTo(spawnPoint.transform.position);
        }
        else
        {
            Debug.LogWarning($"[Player] No SpawnPoints exist in scene '{SceneManager.GetActiveScene().name}'.");
        }
    }

    private VanSeat FindMyVanSeat() //my assigned seat if it exists in the loaded scene, else any seat, else null (van scene not loaded yet)
    {
        Scene activeScene = SceneManager.GetActiveScene();
        int seatIndex = Object.InputAuthority.PlayerId % 4;

        VanSeat fallback = null;
        foreach (VanSeat candidate in VanSeat.AllSeats)
        {
            if (candidate.gameObject.scene != activeScene) continue; //ignore seats lingering from a scene we just left
            if (candidate.seatIndex == seatIndex) return candidate; //my exact seat
            fallback = candidate; //at least land in the van somewhere
        }
        return fallback;
    }

    public void SetHiding(bool hiding) => IsHiding = hiding;

    public void EnterComputer(ComputerTerminal terminal)
    {
        isUsingComputer = true;
        currentTerminal = terminal;
        Cursor.lockState = CursorLockMode.None; //free the cursor for the on-screen buttons (coming next)
        Cursor.visible = true;
    }

    public void ExitComputer()
    {
        isUsingComputer = false;
        currentTerminal = null;
        Cursor.lockState = CursorLockMode.Locked; //back to mouselook
        Cursor.visible = false;
        if (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            RunManager.Instance.RPC_ReleaseComputer(Object.InputAuthority); //free the lock for the next player
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
}//

