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
    [SerializeField] private NetworkObject jammerDevicePrefab; //spawned when you press Q with a Signal Jammer in your kit. leave empty and it simply can't be deployed
    [SerializeField] private NetworkObject doorWedgePrefab;     //spawned when you kick one under a door. leave empty and wedges simply can't be placed
    [SerializeField] private int maxWedgesCarried = 3;
    [SerializeField] private float wedgePlaceRange = 2f;        //how close to a shut door you must be for G to wedge it instead of dropping loot

    [Networked] public int CrackingSafeId { get; private set; } //WHICH safe we're holding interact on (Safe.SafeId), or Safe.NoSafe. the safe reads this off every player to know someone's working on it - same one-source-of-truth trick as HidingSpotId
    [SerializeField] private float safeHoldToCrackTime = 0.3f;  //hold E longer than this at a safe and you start brute-forcing the dial; let go sooner and it counts as a tap, which opens the keypad instead
    private float safeInteractHoldTime;                         //how long E has been held at a safe this press
    [SerializeField] private GameObject playerVisuals; // parent of all mesh renderers; assign in inspector
    //PROPS THAT APPEAR IN YOUR HAND. Park them all as CHILDREN of the player model roughly where a hand would be,
    //leave every one DISABLED in the prefab, and UpdateHeldItemVisual switches on whichever matches what you're
    //holding. Any slot left empty just means that item shows nothing - the feature degrades one item at a time
    //rather than falling over.
    //ONE list, one row per thing you can hold. The row whose tool is None is ORDINARY LOOT, and it doubles as the
    //fallback for any tool nobody has modelled yet - so a crowbar with no model of its own still puts something in
    //your hand rather than nothing.
    [SerializeField] private HeldProp[] heldProps;

    [System.Serializable]
    public struct HeldProp
    {
        public ToolType tool;   //None = ordinary loot / fallback
        public GameObject prop; //a child of the player model, left DISABLED in the prefab
    }

    //WHICH item is in our hand, replicated so every client's copy of us holds the same thing. -1 means empty-handed.
    //CarriedCount alone was only ever enough to answer "is he carrying something"; a crowbar and a vase need to look
    //different in someone else's view, and SelectedSlot is deliberately local-only.
    [Networked] public int HeldKind { get; private set; }

    private void PublishHeldKind()
    {
        int slot = ResolveDropSlot();
        HeldKind = slot < 0 ? -1 : (int)inventory[slot].tool; //ToolType.None is 0, which is the ordinary-loot case
    }
    private bool wasHiding;

    [Networked] public bool IsFlashlightOn { get; private set; } //replicated so teammates see your beam, same idea as IsHiding driving playerVisuals
    [Networked] private float lookPitch { get; set; } //owner writes its up/down look angle here so remote clients can aim its flashlight beam vertically (their camera pitch isn't otherwise networked)
    [SerializeField] private Light flashlight; //spotlight child; assign in inspector
    //A SPRING, not a smooth-follow. The beam used to ease toward where you were looking, which never overshoots and
    //so reads as "smoothed" rather than "held". A real torch has weight - it swings past where you stopped and settles
    //back, and that settle is the whole tell. Stiffness is how hard it's pulled toward your aim, damping is how fast
    //the wobble dies: low damping = a loose wrist, high damping = a clamp.
    [SerializeField] private float flashlightSpringStiffness = 90f;
    [SerializeField] private float flashlightSpringDamping = 9f;
    [SerializeField] private float flashlightSwayAmount = 1.5f; //idle handheld tremor, in degrees - keeps the beam alive when you're still
    [SerializeField] private float flashlightSwayFrequency = 1.1f; //how fast that tremor drifts
    [SerializeField] private float flashlightWalkSwayMultiplier = 3f; //how much bigger the sway gets while walking - the bob that sells "handheld"
    //TRIPWIRE TANGLE. A hobble with a timer, not a hold - the bear trap is the one that stops you dead and needs a
    //teammate. Networked so everyone sees you stumbling, and so the timer survives the host being someone else.
    [SerializeField] private float tangledSpeedMultiplier = 0.45f; //slow enough to be frightening, fast enough to still make a run for it
    [Networked] public float TangledSecondsLeft { get; set; }
    public bool IsTangled => TangledSecondsLeft > 0f;

    //CAMERA FEEL - head bob, landing dip, breathing, strafe tilt, sprint FOV. See Player.CameraFeel.cs.
    //
    //Every one of these produces an OFFSET that is added to the crouch eye-height and the look pitch. Nothing here
    //writes the camera transform; ApplyCameraFeel sums the lot and writes it once. Two systems writing the same
    //transform field is how you get an effect that silently does nothing because the other one ran second.
    //MASTER SCALE for everything below - bob, landing dip, breathing, tilt, look lag, sprint FOV.
    //
    //0.1 was arrived at by dragging the F1 debug slider while walking, which is the only way this is tunable. It is
    //deliberately NOT a player setting: the feel is authored, the same for everyone, and not something to negotiate
    //in a menu. The individual numbers below stay at readable magnitudes rather than being pre-multiplied, so they
    //can still be reasoned about relative to each other.
    [SerializeField] private float cameraMotionScale = 0.1f;
    public float CameraMotionScale { get => cameraMotionScale; set => cameraMotionScale = Mathf.Clamp01(value); } //F1 panel only

    //Halved from the first pass - the original numbers were tuned blind and read as a shake rather than a walk.
    //Tune with the F1 slider while WALKING, not by guessing here.
    [SerializeField] private float headBobVerticalAmount = 0.04f;     //metres the view drops as weight lands
    [SerializeField] private float headBobHorizontalAmount = 0.025f;  //metres of side-to-side per step
    [SerializeField] private float headBobRollDegrees = 0.75f;        //the camera tilting as weight shifts foot to foot
    [SerializeField] private float headBobPitchDegrees = 0.45f;       //nod on impact. positional bob alone reads as a camera on rails
    [SerializeField] private float weakFootMultiplier = 0.9f;         //every other step is slightly lighter - subtle, or it reads as a limp
    [SerializeField] private float sprintBobMultiplier = 1.3f;        //sprinting should be visibly rougher, not just wider FOV

    //LOOK LAG - the view trails the mouse slightly on fast turns, then catches up. This is what sells "piloting a
    //body" rather than "being a camera". Deliberately kept small and clamped: it moves the VIEW, never where you're
    //actually aiming, so it can't make interacting with things feel broken.
    //Deliberately NOT a spring. A spring rings, and a view that rings after every mouse movement is an earthquake -
    //which is exactly what the first version did, because it clamped the position but let velocity keep building, so
    //it buzzed between the two clamps every frame. This is a plain trail-and-recover: it cannot oscillate.
    [SerializeField] private float lookLagAmount = 0.35f;             //fraction of this frame's turn the view lags behind by
    [SerializeField] private float lookLagMaxDegrees = 4f;            //hard clamp, so a fast 180 doesn't fling the view
    [SerializeField] private float lookLagRecoverSpeed = 9f;          //how fast it catches back up. higher = tighter

    [SerializeField] private float landingDipAmount = 0.22f;          //how far the view drops on the hardest landing
    [SerializeField] private float landingRollDegrees = 2.4f;         //and it twists as you absorb it - landing square on is a robot
    [SerializeField] private float landingDipFullSpeed = 14f;         //fall speed that earns the full dip; terminal is 20
    [SerializeField] private float landingDipStiffness = 130f;        //how hard the view is pulled back to level
    [SerializeField] private float landingDipDamping = 16f;           //higher = fewer bounces on the way back up
    [SerializeField] private float breathSwayAmount = 0.35f;          //degrees of idle drift while rested
    [SerializeField] private float breathSwayFrequency = 0.6f;        //how fast that drift wanders
    [SerializeField] private float exhaustedBreathMultiplier = 4.5f;  //how much heavier the breathing gets fully gassed
    [SerializeField] private float sprintFieldOfViewBoost = 8f;       //degrees of extra FOV while sprinting
    [SerializeField] private float fieldOfViewLerpSpeed = 6f;
    [SerializeField] private float strafeTiltAmount = 1.4f;           //degrees of roll at full sideways input
    [SerializeField] private float strafeTiltSpeed = 6f;

    private CinemachineVirtualCamera playerVirtualCamera; //FOV lives on the VCAM's lens, never on the Camera - see UpdateSprintFieldOfView
    private Vector3 cameraRestLocalPosition;  //the prefab's camera placement; bob is an offset from this, not a replacement
    private float cameraEyeHeight;            //eased crouch/stand height - the BASE the bob rides on
    private float baseFieldOfView;            //whatever the vcam shipped with, so sprint returns to the right number
    private float strideDistance;             //metres walked into the current step. THE clock for head bob - shared with PlayerFootsteps so the dip lands on the sound
    private Vector3 lastStridePosition;       //measured position change, NOT characterController.velocity - see UpdateStridePhase for why that lies
    private int strideStepIndex;              //which foot - drives the roll direction and the weak-foot asymmetry
    private float headBobSpeedFactor;         //smoothed 0-1 of how fast we're really moving
    private float landingDipOffset;           //current drop from a landing, in metres (negative = down)
    private float landingDipVelocity;
    private float landingRoll;                //the twist that comes with a landing, decays with the dip
    private float lookLagYaw;                 //visual-only trail behind the mouse
    private float lookLagPitch;
    private Vector2 lookDegreesTurnedThisFrame; //published by HandleLook - the ACTUAL degrees turned, not raw mouse pixels
    private float strafeTilt;                 //current roll in degrees, eased
    private bool isSprintingNow;              //published out of HandleMovement so the render frame can drive FOV
    private Vector2 lastMoveInput;            //likewise, for the strafe tilt

    private float flashlightAimPitch;      //where the beam actually points, as opposed to where you're looking
    private float flashlightAimYaw;
    private float flashlightPitchVelocity; //the spring's momentum - this is what produces the overshoot
    private float flashlightYawVelocity;
    private bool flashlightHeldLastTick; //rising-edge detect so one press = one toggle
    private Vector3 flashlightLastPosition; //to gauge how fast this player is moving, for the walk bob
    private bool hasRiddenVanForRunEnd; //one-shot so the run-end van teleport only fires once
    private bool hasRefilledToolsThisRun; //one-shot so a WedgeKit tops you up once per heist, not every tick
    private bool isUsingComputer; //local only - frozen at the van computer, camera focused on the screen, cursor freed
    private ComputerTerminal currentTerminal; //the terminal we're currently "in", so E can exit it
    private ComputerTerminal pendingTerminal; //terminal we've asked to use and are waiting on the networked lock for

    //EVERY state where a menu owns the cursor and the body should be standing still. ONE property because OnInput has
    //to ask this question, and the next menu anyone adds should only have to be listed here - the fence and the tool
    //shop were both added after the pause menu and neither inherited its input handling, which is exactly how you end
    //up walking away from a negotiation mid-sentence.
    //
    //Pause is deliberately NOT in here. It suppresses input completely (see GameBootstrap.OnInput), whereas these
    //three still need E to reach the body, because E is the only way back OUT of them.
    public bool MenuOwnsCursor => IsShopping || IsTalkingToKeeper || isUsingComputer || DebugPanel.IsOpen;

    //Separate question, separate property: is a KEY PRESS currently meant for a UI rather than for the world? The
    //safe keypad never touches the cursor - it's a keyboard-only overlay and you keep full mouselook - so it does not
    //belong in MenuOwnsCursor, but its digits absolutely should not also reach anything else reading the number row.
    public bool KeyboardIsCaptured => MenuOwnsCursor || isEnteringSafeCode || IsPaused;

    [SerializeField] private NetworkObject worldItemPrefab; //spawned when you drop - the generic pickup item, named on spawn
    [SerializeField] private int maxInventorySlots = 4;
    [SerializeField] private float pickupRange = 2f;
    [SerializeField] private float dropForwardOffset = 1f; //drop slightly in front so the item doesn't spawn inside you
    private readonly List<InventoryItem> inventory = new List<InventoryItem>(); //local only - carried loot (name + value), shown on this player's own HUD

    //HOW MANY of those we're carrying, replicated. The list itself stays local (nobody else needs the names), but the
    //COUNT has to cross the wire because other machines make decisions off it - RunManager checks it on the master
    //before selling you a tool, and on the master a remote player's list is permanently empty, so reading the list
    //there always said "bag's empty, go ahead". Anything asking about capacity from another machine reads this.
    [Networked] public int CarriedCount { get; private set; }
    public IReadOnlyList<InventoryItem> Inventory => inventory;
    //Tools take LOOT slots. Every one you bring is a vase you can't carry home, which is what turns a loadout into a
    //decision - and it's why the Duffel Bag reads as buying back the room your other tool cost. Clamped at 1 so a
    //full kit can never leave you unable to pick anything up at all.
    //No "- ToolsCarried" any more. Tools sit in the bag as real items, so they take a slot by BEING there - subtracting
    //them as well charged you twice for the same tool.
    public int MaxInventorySlots => Mathf.Max(1, maxInventorySlots + ToolInventoryBonus);
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
    private AudioSource voiceSpeakerSource;   //this player's runtime voice Speaker, cached so the voice-volume setting can be applied to it
    private AudioOcclusion voiceOcclusion;    //owns the low-pass on that Speaker - walls muffle it, and the taped mouth clamps it further through ExtraMuffleCutoff //the low-pass on this player's runtime voice Speaker(Clone) - found on first appearance, then toggled by IsLockedUp for the taped-mouth effect
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

            //camera-feel baselines, captured from whatever the prefab shipped with rather than hard-coded, so moving
            //the camera in the prefab doesn't quietly break the bob's rest position or send sprint FOV to a wrong number
            playerVirtualCamera = virtualCam;
            cameraRestLocalPosition = playerCamera.localPosition;
            lastStridePosition = transform.position; //seed it, or the first frame counts our whole spawn offset as one enormous step
            cameraEyeHeight = standCamHeight; //start stood up; HandleCrouchCamera eases this from here on
            baseFieldOfView = virtualCam.m_Lens.FieldOfView;
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
        UpdateHeldItemVisual(); //likewise - your crew needs to SEE you're carrying something, so it's driven by the networked CarriedCount

        if (!HasInputAuthority) return; //stop here if not our instance of player

        RememberVanPosition(); //keep noting where we're stood inside the van, so a scene change can put us back there
        UpdateKeeperProximity(); //same for the fence's desk
        UpdateShopProximity(); //walked away from the counter, or got dragged off it - close the shop rather than leaving it open on a frozen screen
        UpdatePause();     //Escape brings the menu up. it does NOT stop the game for anyone, including us
        UpdateSpectator(); //once we're out of the run, orbit a living teammate instead of our own frozen body

        //the menu being up, or us watching someone else, both mean our own look and reach are off. the SIMULATION
        //carries on regardless - our body is still stood there and can still be caught while we read the menu.
        //DebugPanel is in here for the same reason as the shop counters: its cursor is free, so without this the mouse
        //aimed at its buttons ALSO spun the camera, and the panel was unusable.
        if (IsPaused || spectatorActive || IsShopping || IsTalkingToKeeper || DebugPanel.IsOpen)
        {
            InteractPrompt = "";
            InteractAnchor = null;
            return;
        }

        UpdateDoorDrag();   //hold left mouse on a door, drawer or cupboard and push it open by hand
        UpdateDeployKey();  //Q sets down a Signal Jammer, read straight off the keyboard like the safe keypad so it needs no new binding
        UpdateLootWheel(); //hold MMB to pick which item G drops
        UpdateSafeKeypad(); //read typed digits while the safe keypad is up - local only until the 4th digit is sent
        UpdateComputerClaim(); //enter the computer once the networked lock is granted (or drop our request if someone else got it)
        UpdateInteractPrompt(); //what E would do from where we're standing - the HUD reads InteractPrompt. runs before the computer bail-out because it has to clear itself when we sit down
        if (isUsingComputer) return; //parked at the computer - don't let the mouse spin the body/look while the cursor's free
        HandleLook(); //our player only - updates xRotation, does NOT touch the camera transform
        HandleCrouchCamera(); //ease the crouch eye-height on the render frame so it's smooth at any FPS
        UpdateCameraFeel(); //bob, landing dip, breathing, tilt, sprint FOV - and the single write of the camera transform. MUST be last
    }

    private void UpdateVoiceMuffle() //Photon spawns a Speaker(Clone) under this player at runtime to play its voice; we find it and low-pass its audio while the player is locked up (taped mouth)
    {
        if (Object == null || !Object.IsValid)
        {
            return;
        }

        if (voiceOcclusion == null) //the speaker appears a moment after the player - keep looking until it's here. our OWN player has no speaker for its own voice, so this just stays null for us, which is fine
        {
            Speaker speaker = GetComponentInChildren<Speaker>();
            if (speaker != null)
            {
                AudioSource speakerSource = speaker.GetComponent<AudioSource>();
                if (speakerSource != null)
                {
                    //VOICE IS OCCLUDED LIKE EVERYTHING ELSE, and for this game that's the important one: a teammate
                    //shouting from the next room used to arrive as clear as one stood beside you, which quietly
                    //removed the reason to care where anybody is. AudioOcclusion owns the low-pass on this source;
                    //the taped-mouth effect goes through ExtraMuffleCutoff rather than fighting it for the filter.
                    voiceOcclusion = AudioOcclusion.Attach(speakerSource);
                    voiceSpeakerSource = speakerSource; //kept so the voice slider has something to turn down
                }
            }
        }

        if (voiceOcclusion != null)
        {
            voiceOcclusion.ExtraMuffleCutoff = IsLockedUp ? voiceMuffleCutoff : float.MaxValue; //taped mouth clamps it further; walls do the rest
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

        //a fresh run started - hand out anything a tool promises per-run (the WedgeKit's wedges). one-shot, so it
        //refills at the start of every heist rather than being a single purchase of two wedges that never comes back.
        if (HasStateAuthority && RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            bool runActive = RunManager.Instance.State == RunManager.RunState.InProgress;
            if (runActive && !hasRefilledToolsThisRun)
            {
                hasRefilledToolsThisRun = true;
                RefillToolConsumables();
            }
            else if (!runActive)
            {
                hasRefilledToolsThisRun = false; //re-arm for the next one
            }
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
                IsBearTrapped = false; //the only held-in-place state this list was missing. the ride fires whether or not a scene reloaded, so it can't lean on the scene-change safety net to have cleared it
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

        if (IsShopping || IsTalkingToKeeper) // stood at a counter - frozen with the cursor free, and E backs out
        {
            NoiseLevel = 0f;
            if (GetInput(out NetworkInputData shopInput))
            {
                bool exitPressed = shopInput.interactInput && !interactHeldLastTick; //rising edge, so the press that opened it doesn't immediately close it
                interactHeldLastTick = shopInput.interactInput;
                if (exitPressed)
                {
                    if (IsShopping) ExitShop();
                    else ExitKeeper();
                }
            }
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
            PublishHeldKind();     //and WHICH item is in our hand, so everyone else's copy of us holds the right prop
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

    //RpcSources.All, NOT StateAuthority. This is sent by the WEDGE's owner (the master), and in Shared Mode the state
    //authority of a Player object is that player themselves - so restricting the source to StateAuthority meant the
    //master could never send it, the wedge despawned, and nobody got one. Same shape as RPC_GrantPickup below.
    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] //the wedge's owner decided WE won it; this lands on our machine
    public void RPC_GrantWedge()
    {
        AddWedge();
    }

    //A WEDGE IS AN ITEM NOW, not a counter. It goes in the bag beside the loot and the tools, so it shows in your
    //slots, shows in your hand, can be scrolled to and can be dropped - none of which a bare int could do. Kept local
    //so a WedgeKit refilling doesn't have to route through an RPC to reach it.
    private void AddWedge()
    {
        if (!CanCarryAnotherWedge) return;
        inventory.Add(new InventoryItem(ToolType.DoorWedge));
        PublishCarriedCount(); //recomputes WedgesCarried from the bag, along with CarriedCount and ToolMask
    }

    //Two limits, and they mean different things. maxWedgesCarried stops you being a walking wedge dispenser; bag room
    //is the real cost, because every wedge you bring is a slot you can't fill with something worth money.
    public bool CanCarryAnotherWedge => WedgesCarried < maxWedgesCarried && inventory.Count < MaxInventorySlots;

    //Kick one under this door, on the side we're stood on. Returns whether it ACTUALLY went down - the caller needs
    //to know, because if this fails G has to carry on and do its other job rather than silently eating the press.
    public bool PlaceWedgeIn(Door door)
    {
        if (WedgesCarried <= 0 || door == null) return false;
        if (door.IsWedged) return false; //one is enough, and two would just fight over who owns the door

        if (doorWedgePrefab == null)
        {
            //loud, because this one is a SETUP mistake rather than a gameplay outcome, and it is otherwise completely
            //invisible - you press G at a door holding two wedges and simply nothing happens, forever.
            Debug.LogError("[Player] doorWedgePrefab is not assigned, so wedges can never be placed. Run Tools/Rollin' Robbers/Build Placeholder Prefabs.", this);
            return false;
        }

        //spend one out of the bag. removing the ITEM is what decrements WedgesCarried - the count is derived now
        for (int i = 0; i < inventory.Count; i++)
        {
            if (inventory[i].tool != ToolType.DoorWedge) continue;
            inventory.RemoveAt(i);
            PublishCarriedCount();
            break;
        }

        //remember WHICH SIDE we were on. that's the whole mechanic: only somebody stood on this side can pull it back
        //out, so wedging a door decides who ends up shut in with what.
        int side = door.SideOf(transform.position);
        Vector3 doorPosition = door.transform.position;
        //PLACE IT AT OUR OWN FEET, not by measuring out from the door. Deriving it from the door pivot kept burying it
        //INSIDE the leaf: the pivot is at the hinge, its height depends entirely on how each prefab was authored, and
        //once a door has swung toward you the leaf is occupying exactly the spot the offset points at.
        //
        //Our feet can't be inside the door - we're standing there. And it's where you'd actually kick a wedge.
        Vector3 wedgePosition = transform.position + transform.forward * 0.45f;

        //then drop it to whatever floor is really beneath, so it isn't hovering at the player's centre height
        Vector3 rayStart = wedgePosition + Vector3.up * 1f;
        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit floorHit, 3f, ~0, QueryTriggerInteraction.Ignore))
        {
            wedgePosition = floorHit.point + Vector3.up * 0.02f; //a hair above the surface so it isn't z-fighting with the floor
        }
        else
        {
            wedgePosition.y = transform.position.y - (characterController != null ? characterController.height * 0.5f : 1f); //no floor found: fall back to our own feet
        }

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
        return true;
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
    public void RPC_GrantPickup(NetworkString<_32> itemName, int value, int toolKind)
    {
        if (inventory.Count >= MaxInventorySlots) return; //bag filled while the request was in flight

        //toolKind rides along so a DROPPED TOOL is still a tool when someone picks it back up. Without it a crowbar
        //left on the floor came back as a worthless nameless trinket, which is a silent way to destroy 600 credits.
        ToolType tool = (ToolType)toolKind;
        inventory.Add(tool == ToolType.None
            ? new InventoryItem(itemName.ToString(), value)
            : new InventoryItem(tool));
        PublishCarriedCount();
    }


    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] // any caller; runs on the caught player's own machine
    public void RPC_GetCaught()
    {
        LoseCarriedLoot();                   // caught red-handed - you lose everything you were carrying
        LoseTools();                         // and the kit with it, per the locked tools-lost-on-catch decision
        IsEliminated = true;                 // out for the run - spectator handoff + visuals come later in Unity
        characterController.enabled = false; // freeze them in place, no more moving or colliding
    }

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] // runs on the dragged player's own machine, so we own the movement in Shared Mode
    public void RPC_GetDragged(GuardPatrol guard)
    {
        IsBearTrapped = false; //being hauled off overrides being pinned - the drag branch owns our position from here
        LoseTools();                         // the kit goes the moment he grabs you, same as the loot
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

    [Rpc(RpcSources.All, RpcTargets.InputAuthority)] //same routing as the bear trap: the victim's own machine owns their movement in Shared Mode, so the slow has to be applied there
    public void RPC_TangledInTripwire(float seconds)
    {
        if (IsEliminated || IsLockedUp || IsBearTrapped) return; //already stopped by something stronger - a slow on top of a hold is meaningless

        //REFRESHED, not stacked. Walking back through a second wire restarts the clock rather than adding to it, so a
        //cluster of wires can't quietly total up to a thirty-second immobilisation, which is the bear trap's job and
        //isn't fun even when the bear trap does it.
        TangledSecondsLeft = Mathf.Max(TangledSecondsLeft, seconds);
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
