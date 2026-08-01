using Cinemachine;
using Fusion;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Photon.Voice.Unity;

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
    //sensitivity is NOT a field here any more. it belongs to the machine, not to a spawned player object - it has to
    //survive scene loads and be there next launch, which a prefab field isn't. see GameSettings.
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
    [SerializeField] private float crackNoiseAmount = 14f;     //how loud cracking a safe is to the guard - above walking (7) and sprinting (10.5), so working a safe near him draws him in. that's the risk: you're pinned AND loud
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
    [Networked] public bool IsBearTrapped { get; private set; } //jaws round your ankle - a TEAMMATE has to lever it open, same as the closet. you're LOUD the whole time you wait
    [SerializeField] private float bearTrapNoiseAmount = 26f;   //thrashing while stuck is nearly as loud as a hard landing (30) - that's the real damage, not the delay
    [SerializeField] private float bearTrapSelfEscapeSeconds = 30f; //LAST-RESORT failsafe, not the intended way out: a teammate should free you long before this. set it to 0 to make it teammate-only forever, but read the warning in RPC_CaughtInBearTrap first
    private float bearTrapTimer;                                //counts the failsafe down on our own machine
    [Networked] public int HidingSpotId { get; private set; } //WHICH hiding spot we're inside (HidingSpot.spotId), or HidingSpot.NoSpot. replicated so every client can tell an occupied spot from a free one - a local bool let two players share one spot
    [Networked] public NetworkString<_16> DisplayName { get; private set; } //who this is, for nameplates. written once by the owner in Spawned; the source lives in PlayerIdentity so Steam can take over later

    [Networked] public int WedgesCarried { get; private set; } //door wedges in your pockets. networked so teammates' prompts and the HUD can see what you're holding
    [SerializeField] private NetworkObject doorWedgePrefab;     //spawned when you kick one under a door. leave empty and wedges simply can't be placed
    [SerializeField] private int maxWedgesCarried = 3;
    [SerializeField] private float wedgePlaceRange = 2f;        //how close to a shut door you must be for G to wedge it instead of dropping loot

    [Networked] public int CrackingSafeId { get; private set; } //WHICH safe we're holding interact on (Safe.SafeId), or Safe.NoSafe. the safe reads this off every player to know someone's working on it - same one-source-of-truth trick as HidingSpotId
    [SerializeField] private float safeHoldToCrackTime = 0.3f;  //hold E longer than this at a safe and you start brute-forcing the dial; let go sooner and it counts as a tap, which opens the keypad instead
    private float safeInteractHoldTime;                         //how long E has been held at a safe this press
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
    [SerializeField] private float suffocateDuration = 45f; //taped mouth - seconds of air before you die if no teammate frees you
    private float suffocateTimer; //counts down while locked; hits 0 = you suffocate
    public float ScreenFade => IsEliminated ? 1f : ((IsLockedUp && suffocateDuration > 0f) ? Mathf.Clamp01(1f - suffocateTimer / suffocateDuration) : 0f); //0 = normal, ramps while suffocating, 1 = dead/blacked out. HUD reads this for the fullscreen fade

    public bool IsBeingDragged => isBeingDragged; //the HUD says so, because otherwise being hauled across the house with no control reads as the game hanging
    public float AirSecondsLeft => IsLockedUp ? Mathf.Max(0f, suffocateTimer) : 0f; //shown while jailed. the clock IS the threat there, so it belongs on screen
    private AudioSource voiceSpeakerSource; //this player's runtime voice Speaker, cached so the voice-volume setting can be applied to it
    private AudioLowPassFilter voiceMuffleFilter; //the low-pass on this player's runtime voice Speaker(Clone) - found on first appearance, then toggled by IsLockedUp for the taped-mouth effect
    [SerializeField] private float voiceMuffleCutoff = 1000f; //how muffled a trapped player's voice sounds to teammates - lower = more muffled
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
            DisplayName = PlayerIdentity.ResolveName(Object.InputAuthority.PlayerId); //only the owner names itself; it replicates to everyone else's nameplate
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
        UpdateVoiceMuffle(); //taped mouth: low-pass this player's runtime voice Speaker while locked up. Runs on ALL clients so every teammate hears the muffle, driven by the [Networked] IsLockedUp

        //hiding has to switch off the BODY as well as the mesh. it used to only hide the visuals, which left a live
        //CharacterController standing in the closet - teammates walked into an invisible person blocking the doorway.
        //runs on every client, because it's the remote copy of you that everyone else actually collides with.
        if (Object != null && Object.IsValid && IsHiding != wasHiding)
        {
            wasHiding = IsHiding;
            if (playerVisuals != null)
            {
                playerVisuals.SetActive(!IsHiding); // runs on all clients so other players see you vanish
            }
            if (characterController != null)
            {
                //climbing OUT only gives the body back if nothing else is holding it - being ripped from a closet by
                //the guard clears IsHiding on the same tick he starts dragging you, and re-enabling here would undo
                //the freeze he just applied.
                characterController.enabled = !IsHiding && !IsLockedUp && !IsEliminated && !isBeingDragged;
            }
        }

        UpdateFlashlight(); //runs on ALL clients so everyone sees this player's beam, driven by the networked IsFlashlightOn + lookPitch

        if (!HasInputAuthority) return; //stop here if not our instance of player

        UpdatePause();     //Escape brings the menu up. it does NOT stop the game for anyone, including us
        UpdateSpectator(); //once we're out of the run, orbit a living teammate instead of our own frozen body

        //the menu being up, or us watching someone else, both mean our own look and reach are off. the SIMULATION
        //carries on regardless - our body is still stood there and can still be caught while we read the menu.
        if (IsPaused || spectatorActive)
        {
            InteractPrompt = "";
            InteractAnchor = null;
            return;
        }

        UpdateLootWheel(); //hold MMB to pick which item G drops
        UpdateSafeKeypad(); //read typed digits while the safe keypad is up - local only until the 4th digit is sent
        UpdateComputerClaim(); //enter the computer once the networked lock is granted (or drop our request if someone else got it)
        UpdateInteractPrompt(); //what E would do from where we're standing - the HUD reads InteractPrompt. runs before the computer bail-out because it has to clear itself when we sit down
        if (isUsingComputer) return; //parked at the computer - don't let the mouse spin the body/look while the cursor's free
        HandleLook(); //our player only
        HandleCrouchCamera(); //ease the crouch eye-height on the render frame so it's smooth at any FPS
    }

    private void UpdateVoiceMuffle() //Photon spawns a Speaker(Clone) under this player at runtime to play its voice; we find it and low-pass its audio while the player is locked up (taped mouth)
    {
        if (Object == null || !Object.IsValid)
        {
            return;
        }

        if (voiceMuffleFilter == null) //the speaker appears a moment after the player - keep looking until it's here. our OWN player has no speaker for its own voice, so this just stays null for us, which is fine
        {
            Speaker speaker = GetComponentInChildren<Speaker>();
            if (speaker != null)
            {
                AudioSource speakerSource = speaker.GetComponent<AudioSource>();
                if (speakerSource != null)
                {
                    voiceMuffleFilter = speakerSource.GetComponent<AudioLowPassFilter>();
                    if (voiceMuffleFilter == null)
                    {
                        voiceMuffleFilter = speakerSource.gameObject.AddComponent<AudioLowPassFilter>(); //add it ourselves so nothing needs wiring in the inspector
                    }
                    voiceMuffleFilter.cutoffFrequency = voiceMuffleCutoff;
                    voiceMuffleFilter.enabled = false;
                    voiceSpeakerSource = speakerSource; //kept so the voice slider has something to turn down
                }
            }
        }

        if (voiceMuffleFilter != null)
        {
            voiceMuffleFilter.enabled = IsLockedUp;
        }

        //teammates' voices get their own volume. worth a separate slider in a proximity-voice game more than most:
        //one friend with a hot mic buries the footsteps and creaks you're straining to hear, and pulling the master
        //down to fix that turns the guard down with it. done here because this is where the Speaker gets found.
        if (voiceSpeakerSource != null)
        {
            voiceSpeakerSource.volume = GameSettings.VoiceVolume;
        }
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
                IsEliminated = false;  //pull caught/dead players back in for the next run (their loot was already stripped at capture; a clean getaway keeps its haul). sticks because we're in FUN
                IsLockedUp = false;
                //someone still tucked in a closet when the run ended used to ride to the van STILL hiding, and that
                //is a hard softlock: IsHiding freezes movement and hides playerVisuals, and the only way out is
                //pressing E while standing next to a hiding spot - which doesn't exist in the van scene. Invisible,
                //immobile, forever. clearing the spot id too, or that closet reads as occupied all next run.
                SetHiding(false, HidingSpot.NoSpot);
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
            //the drag normally ends when he reaches the closet (RPC_GetLockedUp). but he can VANISH mid-haul -
            //every scene change despawns him (an exit door, the van computer) - and then nothing would ever clear
            //this: frozen, input ignored, and not IsLockedUp so teammates can't even rescue us. so the drag owns
            //its own exit condition: no guard, no drag.
            if (draggingGuard == null || draggingGuard.Object == null || !draggingGuard.Object.IsValid)
            {
                ReleaseFromDrag();
                return; //free again next tick
            }

            dragTrail.Enqueue(draggingGuard.transform.position); //remember where the guard is each tick
            while (dragTrail.Count > dragTrailLag)
            {
                dragTrail.Dequeue(); //keep only the last few positions
            }
            transform.position = dragTrail.Peek(); //sit where the guard was a few ticks ago - behind him, ON his valid path (no clipping, not inside him)
            NoiseLevel = 0f; //can't make useful noise while grabbed
            return;
        }

        if (IsBearTrapped) //pinned until a teammate levers it open. no control, and thrashing broadcasts exactly where you are
        {
            if (HasStateAuthority)
            {
                NoiseLevel = bearTrapNoiseAmount; //the point of the trap: it doesn't just hold you, it TELLS him

                //the failsafe, NOT the mechanic. a teammate pressing E is the way out; this only exists so a solo or
                //last-alive player can't be frozen forever with nobody left to come and get them.
                if (bearTrapSelfEscapeSeconds > 0f)
                {
                    bearTrapTimer -= Runner.DeltaTime;
                    if (bearTrapTimer <= 0f)
                    {
                        IsBearTrapped = false; //finally worked your foot loose on your own
                    }
                }
            }
            return;
        }

        if (IsHiding) // locked inside a hiding spot - can't move, but can still press E to exit
        {
            //a closet door isn't soundproof. movement noise is gone (you're not moving), but your VOICE still
            //carries out - so chattering on the mic while the guard walks past is what gives you away. staying
            //quiet in there is a real choice, not just a wait.
            NoiseLevel = (MicLoudnessProbe.Instance != null) ? MicLoudnessProbe.Instance.VoiceLoudness * voiceNoiseScale : 0f;
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
                        RunManager.Instance.RPC_ReportCaught(Object.InputAuthority); //hop the death to the master so the alive-count drops (for THIS player)
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
            HandleInteract(networkInputData.interactInput); //E to free a trapped teammate (tap)
            HandleCracking(networkInputData.interactInput); //E HELD next to a safe cracks it (hold) - separate from the tap above
            HandleFlashlight(networkInputData.flashlightInput); //F to toggle the flashlight
            HandleDrop(networkInputData.dropInput); //G to drop an item on the floor
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        ActivePlayers.Remove(this); //leave the list on disconnect so nobody iterates a destroyed player
        if (HasInputAuthority) SceneManager.activeSceneChanged -= OnSceneChanged;
    }

    public void SetHiding(bool hiding, int spotId) //spotId identifies WHICH spot, so other clients can see it as taken. HidingSpot.NoSpot when climbing out
    {
        IsHiding = hiding;
        HidingSpotId = hiding ? spotId : HidingSpot.NoSpot;
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.InputAuthority)] //the wedge's owner decided WE won it; this lands on our machine
    public void RPC_GrantWedge()
    {
        if (WedgesCarried < maxWedgesCarried) WedgesCarried++;
    }

    public bool CanCarryAnotherWedge => WedgesCarried < maxWedgesCarried;

    public void PlaceWedgeIn(Door door) //kick one under this door, on the side we're stood on
    {
        if (WedgesCarried <= 0 || doorWedgePrefab == null || door == null) return;
        if (door.IsWedged) return; //one is enough, and two would just fight over who owns the door

        WedgesCarried--;

        //remember WHICH SIDE we were on. that's the whole mechanic: only somebody stood on this side can pull it back
        //out, so wedging a door decides who ends up shut in with what.
        int side = door.SideOf(transform.position);
        Vector3 doorPosition = door.transform.position;
        Vector3 wedgePosition = doorPosition + door.ThroughDoorway * (0.35f * side); //sat at the foot of the door, on our side of it

        //PlayerRef.None, not us: a wedge owned by the player who placed it would go with them when they disconnect,
        //silently un-jamming a door someone was counting on. the master holds it, like the traps and the loot.
        Runner.Spawn(doorWedgePrefab, wedgePosition, Quaternion.identity, PlayerRef.None, (runner, spawnedObject) =>
        {
            DoorWedge wedge = spawnedObject.GetComponent<DoorWedge>();
            if (wedge != null)
            {
                wedge.IsPlaced = true;
                wedge.DoorPosition = doorPosition; //doors aren't networked, so the wedge refers to its door by where it is
                wedge.WedgedSide = side;
                wedge.SpawnPoint = wedgePosition;  //networked-position safeguard - a deferred spawn drops the position argument
                wedge.UseSpawnPoint = true;
            }
        });
    }

    public void SetCrackingSafe(int safeId) //publish which safe we're holding on (or Safe.NoSafe). the safe reads this to advance its meter
    {
        CrackingSafeId = safeId;
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] //the guard yanks the door open. has to be an RPC: in Shared Mode only WE own our own networked state, so he can't clear IsHiding for us directly
    public void RPC_PulledFromHiding()
    {
        SetHiding(false, HidingSpot.NoSpot);
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] //sent by the item's owner to the ONE player who won it - see WorldItem.RPC_RequestPickUp
    public void RPC_GrantPickup(NetworkString<_32> itemName, int value)
    {
        if (inventory.Count >= maxInventorySlots) return; //bag filled while the request was in flight
        inventory.Add(new InventoryItem(itemName.ToString(), value));
    }


    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] // any caller; runs on the caught player's own machine
    public void RPC_GetCaught()
    {
        LoseCarriedLoot();                   // caught red-handed - you lose everything you were carrying
        IsEliminated = true;                 // out for the run - spectator handoff + visuals come later in Unity
        characterController.enabled = false; // freeze them in place, no more moving or colliding
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] // runs on the dragged player's own machine, so we own the movement in Shared Mode
    public void RPC_GetDragged(GuardPatrol guard)
    {
        IsBearTrapped = false; //being hauled off overrides being pinned - the drag branch owns our position from here
        LoseCarriedLoot();                   // hauled off to the closet - the haul spills the moment he grabs you (jail's mercy is staying in the run, not keeping the loot). flip this line off if jail should let a rescued player keep their loot
        draggingGuard = guard;
        isBeingDragged = true;
        dragTrail.Clear();                   // fresh trail for this drag
        verticalVelocity = 0f;               // don't bank fall velocity while pinned
        characterController.enabled = false; // off so we can be positioned along the guard's path without the CC fighting it
    }

    private void ReleaseFromDrag() //the haul ended without us reaching the closet (the guard despawned) - hand control back instead of leaving us a statue
    {
        isBeingDragged = false;
        draggingGuard = null;
        dragTrail.Clear();
        verticalVelocity = 0f;              // don't drop with banked fall speed from however long the drag lasted
        characterController.enabled = true; // RPC_GetDragged turned this off to position us along his path - we own our body again
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] // runs on the dragged player's own machine
    public void RPC_GetLockedUp(Vector3 closetPosition)
    {
        IsBearTrapped = false; //he's carried us off; whatever had our ankle doesn't any more. leaving it set would run
                               //the bear-trap branch ahead of the closet one, leaking noise 26 out of a locked wardrobe
                               //and pausing the suffocation clock that's supposed to be the whole threat
        isBeingDragged = false;
        draggingGuard = null;
        characterController.enabled = false; // toggle the CC so it accepts the teleport
        transform.position = closetPosition;
        characterController.enabled = true;
        IsLockedUp = true;
        suffocateTimer = suffocateDuration; //start the air clock the moment the closet closes
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] //fired by the trap; runs on the victim's own machine, which owns their movement in Shared Mode
    public void RPC_CaughtInBearTrap()
    {
        //WARNING before you set bearTrapSelfEscapeSeconds to 0: a teammate is the intended way out, but if you're the
        //last one moving there IS nobody. the guard usually arrives and resolves it (the trap called him), but if he
        //can't path to you, a teammate-only trap freezes that player for the rest of the run with no way to quit to
        //the van. the timer is the only thing standing between this trap and that softlock.
        if (IsEliminated || IsLockedUp) return; //already out of the run - nothing left to catch
        IsBearTrapped = true;
        bearTrapTimer = bearTrapSelfEscapeSeconds;
        verticalVelocity = 0f; //don't bank fall speed while pinned in place
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] //any caller (the rescuer); runs on the freed player's own machine
    public void RPC_Rescue()
    {
        if (IsBearTrapped)
        {
            IsBearTrapped = false; //jaws levered open by a friend - the intended way out
            return;
        }
        if (!IsLockedUp)
        {
            return; //nothing to free
        }
        IsLockedUp = false; //sprung by a friend - the only way out of the closet
    }
}
