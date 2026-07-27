using UnityEngine;

// A thing that swings open on a hinge: house doors, safe doors, cupboards. Pure presentation - it owns the rotation,
// the settle wobble and the sound, and nothing else. WHO is allowed to open it, and how that syncs across the
// network, is the caller's problem (Door routes through RunManager.RPC_SetDoorOpen; Safe opens itself once its
// crack meter fills). Put this on the object that should rotate, with its pivot at the hinge edge.
//
// Every hinge rolls its own personality (open angle, swing speed, wobble) from its POSITION rather than a plain
// Random. These aren't networked, so independent rolls would leave the same door resting at a different angle on
// every screen. Hashing the position means every machine derives identical numbers with zero syncing - and a given
// hinge always behaves like itself, which is how a real door with a real weight actually works.
public class SwingingHinge : MonoBehaviour
{
    [SerializeField] private float openAngle = 90f;   // how far it swings, degrees around local up. negative swings the other way
    [SerializeField] private float swingSpeed = 300f; // degrees per second

    [Header("Per-hinge character - rolled from its position, so it's identical on every client")]
    [SerializeField] private float angleVariation = 8f;    // +/- degrees off openAngle, so they don't all rest at exactly 90
    [SerializeField] private float speedVariation = 0.25f; // +/- fraction of swingSpeed - some are heavy, some light

    [Header("Settle wobble once it finishes opening")]
    [SerializeField] private float minSwayAngle = 1.5f;
    [SerializeField] private float maxSwayAngle = 4f;
    [SerializeField] private float minSwayFrequency = 0.8f;
    [SerializeField] private float maxSwayFrequency = 1.6f;
    [SerializeField] private float swayDamping = 1.8f;     // higher = settles quicker

    [Header("Sound - drop in as many clips as you like, one is picked at random")]
    [SerializeField] private AudioClip[] openClips;
    [SerializeField] private AudioClip[] closeClips;  // leave empty and it reuses the open clips
    [SerializeField, Range(0f, 1f)] private float volume = 0.7f;
    [SerializeField] private float minPitch = 0.92f;  // every play is pitched slightly differently, so the same clip
    [SerializeField] private float maxPitch = 1.08f;  // never sounds copy-pasted across a house full of doors
    [SerializeField] private float soundMaxDistance = 18f;

    private Quaternion closedRotation;
    private bool isOpen;
    private AudioSource hingeAudio;

    private float currentAngle;      // degrees from closed, driven toward the target each frame
    private float myOpenAngle;
    private float mySwingSpeed;
    private float mySwayAngle;
    private float mySwayFrequency;
    private float swayTimer = -1f;   // -1 = not wobbling; counts up while the settle plays out

    public bool IsOpen => isOpen;

    private void Awake()
    {
        closedRotation = transform.localRotation; // wherever it's placed in the scene = the closed pose

        //round the coordinates to centimetres FIRST, then multiply. multiplying the float by a huge prime and only
        //then converting overflows int (a house at x=-71 gives -71 * 73856093, about -5.2 billion against an int
        //ceiling of 2.1 billion) - everything clamps to the same value and comes out identical. int overflow on the
        //multiply below is fine: C# wraps it deterministically, so every machine still lands on the same seed.
        int positionSeed = (Mathf.RoundToInt(transform.position.x * 100f) * 73856093)
                         ^ (Mathf.RoundToInt(transform.position.y * 100f) * 83492791)
                         ^ (Mathf.RoundToInt(transform.position.z * 100f) * 19349663);
        System.Random hingeRandom = new System.Random(positionSeed & 0x7FFFFFFF); //force non-negative: System.Random(int.MinValue) throws on some runtimes

        myOpenAngle = openAngle + RollBetween(hingeRandom, -angleVariation, angleVariation);
        mySwingSpeed = swingSpeed * RollBetween(hingeRandom, 1f - speedVariation, 1f + speedVariation);
        mySwayAngle = RollBetween(hingeRandom, minSwayAngle, maxSwayAngle);
        mySwayFrequency = RollBetween(hingeRandom, minSwayFrequency, maxSwayFrequency);

        //built in code so a hinge needs nothing wired in the inspector beyond the clips. 3D on purpose: one swinging
        //across the house should be faint, one behind you should make you jump.
        hingeAudio = gameObject.AddComponent<AudioSource>();
        hingeAudio.playOnAwake = false;
        hingeAudio.loop = false;
        hingeAudio.spatialBlend = 1f;
        hingeAudio.rolloffMode = AudioRolloffMode.Linear;
        hingeAudio.minDistance = 1.5f;
        hingeAudio.maxDistance = soundMaxDistance;
    }

    private static float RollBetween(System.Random source, float min, float max)
    {
        return min + (float)source.NextDouble() * (max - min);
    }

    private void Update()
    {
        //ease toward the current state every frame - a clean swing, not a snap. any collider rides along with the
        //object, so as it rotates open the gap physically clears; rotate closed and it blocks again.
        float targetAngle = isOpen ? myOpenAngle : 0f;
        float angleBeforeThisFrame = currentAngle;
        currentAngle = Mathf.MoveTowards(currentAngle, targetAngle, mySwingSpeed * Time.deltaTime);

        //the exact frame it finishes swinging OPEN, kick off the settle wobble. closing doesn't wobble - a shut door
        //is stopped dead by its own frame. the "moved this frame" half stops it re-triggering while it rests.
        bool justFinishedOpening = isOpen && angleBeforeThisFrame != currentAngle && Mathf.Approximately(currentAngle, targetAngle);
        if (justFinishedOpening)
        {
            swayTimer = 0f;
        }

        float swayOffset = 0f;
        if (swayTimer >= 0f)
        {
            swayTimer += Time.deltaTime;
            float decay = Mathf.Exp(-swayDamping * swayTimer); //exponential decay = wobbles hard, then noticeably less, then stops
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

    public void SetOpen(bool open)
    {
        if (isOpen == open)
        {
            return; //already in that state - bail before the sound, so a caller re-asserting "open" every tick can't machine-gun the creak
        }

        isOpen = open; //explicit state, not a toggle - two callers on the same tick can't flip it twice and cancel out
        PlaySound(open);
    }

    private void PlaySound(bool opening)
    {
        //fall back to the open clips if no close clips were assigned, so one set of sounds is enough to get started
        AudioClip[] clips = (!opening && closeClips != null && closeClips.Length > 0) ? closeClips : openClips;
        if (clips == null || clips.Length == 0 || hingeAudio == null)
        {
            return;
        }

        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null)
        {
            return; //an empty slot in the array
        }

        hingeAudio.pitch = Random.Range(minPitch, maxPitch); //random pitch per play - the cheapest way to stop repetition fatigue
        hingeAudio.PlayOneShot(clip, volume);
    }
}
