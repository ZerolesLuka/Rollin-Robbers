using System.Collections.Generic;
using UnityEngine;

// A hinged door. Players open it with E; the guard and dog shove it open when they walk into one. It's NOT a
// NetworkObject - every change goes through RunManager.RPC_SetDoorOpen and each client swings its OWN copy, so
// scene-placed doors sidestep Fusion's scene-object enrolment problem the loot ran into. That RPC identifies a
// door by its POSITION, so there's nothing to number or wire per door - drop this on a door and it works.
// Doors start closed on scene load.
//
// The door swings around its OWN pivot, so the pivot MUST sit at the hinge edge. Synty doors already do; if a
// door's pivot is at its centre it'll spin in place instead of swinging - fix that by parenting the door mesh
// under an empty placed at the hinge and putting this script on the empty. The collider rides on the door, so
// opening it clears the doorway for free. Put the door on the guard's obstacle layer (Enviorment) if you want a
// CLOSED door to also block his line of sight - that's the stealth payoff.
public class Door : MonoBehaviour
{
    public static readonly List<Door> AllDoors = new List<Door>();

    [SerializeField] public float interactRange = 2f;
    [SerializeField] private float openAngle = 90f;   // how far it swings, degrees around local up
    [SerializeField] private float swingSpeed = 300f; // degrees per second - how fast it opens and closes

    [Header("Per-door character - rolled from the door's position, so it's identical on every client")]
    [SerializeField] private float angleVariation = 8f;    // +/- degrees off openAngle, so doors don't all rest at exactly 90
    [SerializeField] private float speedVariation = 0.25f; // +/- fraction of swingSpeed - some doors are heavy, some light

    [Header("Settle wobble once it finishes opening")]
    [SerializeField] private float minSwayAngle = 1.5f;     // degrees of overshoot wobble
    [SerializeField] private float maxSwayAngle = 4f;
    [SerializeField] private float minSwayFrequency = 0.8f; // wobbles per second
    [SerializeField] private float maxSwayFrequency = 1.6f;
    [SerializeField] private float swayDamping = 1.8f;      // higher = settles quicker

    [Header("Sound - drop in as many clips as you like, one is picked at random")]
    [SerializeField] private AudioClip[] openClips;   // creaks / handle turns. leave empty for a silent door
    [SerializeField] private AudioClip[] closeClips;  // thuds / latch clicks. leave empty and it reuses the open clips
    [SerializeField, Range(0f, 1f)] private float doorVolume = 0.7f;
    [SerializeField] private float minPitch = 0.92f;  // every play is pitched slightly differently, so the same clip
    [SerializeField] private float maxPitch = 1.08f;  // never sounds copy-pasted when you work through a house of doors
    [SerializeField] private float soundMaxDistance = 18f; // past this you can't hear it at all

    private Quaternion closedRotation;
    private bool isOpen;
    private AudioSource doorAudio;

    private float currentAngle;      // degrees from closed. driven toward the target each frame
    private float myOpenAngle;       // this door's own open angle, after variation
    private float mySwingSpeed;      // this door's own swing speed, after variation
    private float mySwayAngle;       // how far this door wobbles when it settles
    private float mySwayFrequency;
    private float swayTimer = -1f;   // -1 = not wobbling; counts up while the settle plays out

    private void Awake()
    {
        closedRotation = transform.localRotation; // wherever it's placed in the scene = the closed pose

        //roll this door's personality from its POSITION rather than a plain Random. doors aren't networked, so if
        //each client rolled independently the same door would rest at a different angle on every screen. hashing the
        //position means every machine derives identical numbers with zero syncing - and a given door always behaves
        //like itself, which is how a real door with a real weight and real hinges actually works.
        //round the coordinates to centimetres FIRST, then multiply. multiplying the float by a huge prime and only
        //then converting overflows int (this house sits at x=-71, so -71 * 73856093 is about -5.2 billion vs an int
        //ceiling of 2.1 billion) - every door clamps to the same value and they all come out identical. int overflow
        //on the multiply below is fine: C# wraps it deterministically, so every machine still lands on the same seed.
        int positionSeed = (Mathf.RoundToInt(transform.position.x * 100f) * 73856093)
                         ^ (Mathf.RoundToInt(transform.position.y * 100f) * 83492791)
                         ^ (Mathf.RoundToInt(transform.position.z * 100f) * 19349663);
        System.Random doorRandom = new System.Random(positionSeed & 0x7FFFFFFF); //force non-negative: System.Random(int.MinValue) throws on some runtimes

        myOpenAngle = openAngle + RollBetween(doorRandom, -angleVariation, angleVariation);
        mySwingSpeed = swingSpeed * RollBetween(doorRandom, 1f - speedVariation, 1f + speedVariation);
        mySwayAngle = RollBetween(doorRandom, minSwayAngle, maxSwayAngle);
        mySwayFrequency = RollBetween(doorRandom, minSwayFrequency, maxSwayFrequency);

        //built in code so a door needs nothing wired in the inspector beyond the clips themselves. 3D on purpose:
        //a door swinging across the house should be faint, one behind you should make you jump.
        doorAudio = gameObject.AddComponent<AudioSource>();
        doorAudio.playOnAwake = false;
        doorAudio.loop = false;
        doorAudio.spatialBlend = 1f;
        doorAudio.rolloffMode = AudioRolloffMode.Linear;
        doorAudio.minDistance = 1.5f;
        doorAudio.maxDistance = soundMaxDistance;
    }

    private void OnEnable()
    {
        AllDoors.Add(this);
    }

    private void OnDisable()
    {
        AllDoors.Remove(this);
    }

    private static float RollBetween(System.Random source, float min, float max)
    {
        return min + (float)source.NextDouble() * (max - min);
    }

    private void Update()
    {
        //ease toward the current state every frame - a clean swing, not a snap. the collider is part of the door,
        //so as it rotates open the doorway physically clears; rotate closed and it blocks again.
        float targetAngle = isOpen ? myOpenAngle : 0f;
        float angleBeforeThisFrame = currentAngle;
        currentAngle = Mathf.MoveTowards(currentAngle, targetAngle, mySwingSpeed * Time.deltaTime);

        //the exact frame it finishes swinging OPEN, kick off the settle wobble. closing doesn't wobble - a shut
        //door is stopped dead by its own frame. the "moved this frame" half stops it re-triggering while it rests.
        bool justFinishedOpening = isOpen && angleBeforeThisFrame != currentAngle && Mathf.Approximately(currentAngle, targetAngle);
        if (justFinishedOpening)
        {
            swayTimer = 0f;
        }

        float swayOffset = 0f;
        if (swayTimer >= 0f)
        {
            swayTimer += Time.deltaTime;
            float decay = Mathf.Exp(-swayDamping * swayTimer); //exponential decay = it wobbles hard, then noticeably less, then stops
            if (decay < 0.01f)
            {
                swayTimer = -1f; //settled - switch the whole calculation off rather than drift forever at invisible amplitudes
            }
            else
            {
                swayOffset = mySwayAngle * decay * Mathf.Sin(swayTimer * mySwayFrequency * Mathf.PI * 2f);
            }
        }

        transform.localRotation = closedRotation * Quaternion.Euler(0f, currentAngle + swayOffset, 0f);
    }

    public bool IsOpen => isOpen;

    public void SetOpen(bool open) //run on EVERY client by RunManager.RPC_SetDoorOpen, so all copies swing together.
    {
        if (isOpen == open)
        {
            return; //already in that state - bail before the sound, so the guard re-asserting "open" every tick can't machine-gun the creak
        }

        isOpen = open; //explicit state, not a toggle - so two players pressing E on the same tick can't flip it twice and cancel out
        PlayDoorSound(open);
    }

    private void PlayDoorSound(bool opening)
    {
        //fall back to the open clips if no close clips were assigned, so one set of sounds is enough to get started
        AudioClip[] clips = (!opening && closeClips != null && closeClips.Length > 0) ? closeClips : openClips;
        if (clips == null || clips.Length == 0 || doorAudio == null)
        {
            return;
        }

        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null)
        {
            return; //an empty slot in the array
        }

        doorAudio.pitch = Random.Range(minPitch, maxPitch); //random pitch per play - the cheapest way to stop repetition fatigue
        doorAudio.PlayOneShot(clip, doorVolume);
    }

    public static Door FindClosedDoorNear(Vector3 position, float range) //used by the AIs to spot a shut door in their way
    {
        foreach (Door door in AllDoors)
        {
            if (door.isOpen)
            {
                continue;
            }
            if (Vector3.Distance(door.transform.position, position) <= range)
            {
                return door;
            }
        }
        return null;
    }
}
