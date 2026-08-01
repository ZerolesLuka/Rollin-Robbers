using System.Collections.Generic;
using UnityEngine;

// Anything that opens: house doors, cupboards, drawers, the safe, a jewellery box lid. Put this on the part that
// MOVES, with its pivot at the hinge (or at the closed position, for a drawer) - the parent body gets nothing.
//
// This is the whole interaction: a prop needs NOTHING but this component to be openable. Door is a separate marker
// that only tells the guard AI "this particular openable is a house door worth shoving through".
//
// It's NOT a NetworkObject. Every change routes through RunManager.RPC_SetDoorOpen, which finds the hinge by its
// POSITION - static scene geometry sits at identical coordinates on every machine, so the same object resolves for
// everyone with nothing to number or wire per object.
//
// Each hinge also rolls its own personality from that position rather than a plain Random. Every client hashes the
// same coordinates and derives identical numbers, so a given drawer behaves like itself on every screen with zero
// syncing. Swapping this for UnityEngine.Random would leave the same drawer resting at a different depth per player.
public class SwingingHinge : MonoBehaviour
{
    //which local axis it turns around (swing) or travels along (slide)
    public enum HingeAxis
    {
        X,
        Y,
        Z
    }

    public static readonly List<SwingingHinge> AllHinges = new List<SwingingHinge>();

    [SerializeField] private float interactRange = 2f;  // how close a player must be. tighten this for small props
    [SerializeField] private HingeAxis axis = HingeAxis.Y;
    [SerializeField] private bool slidesOpen;           // TICK FOR CABINETS AND DRAWERS - pushes straight out along the axis instead of swinging round it
    [SerializeField] private float openAngle = 90f;     // swing mode: degrees. negative goes the other way
    [SerializeField] private float slideDistance = 0.4f; // slide mode: metres. negative goes the other way
    [SerializeField] private float openSpeed = 3f;      // how many times per second it could open fully. 3 = about a third of a second

    [Header("Sound - drop in as many clips as you like, one is picked at random")]
    [SerializeField] private AudioClip[] openClips;
    [SerializeField] private AudioClip[] closeClips;    // leave empty and it reuses the open clips

    //not worth an inspector slot each - these are feel constants, identical on every openable in the game
    private const float SpeedVariation = 0.25f;      // +/- fraction of openSpeed, so some are heavy and some are light
    private const float MinOpenFraction = 0.85f;     // an open never goes FURTHER than authored, just sometimes less
    private const float Volume = 0.7f;
    private const float MinPitch = 0.92f;
    private const float MaxPitch = 1.08f;
    private const float SoundMaxDistance = 18f;

    private Quaternion closedRotation;
    private Vector3 closedPosition;
    private bool isOpen;
    private AudioSource hingeAudio;

    private float openProgress;      // 0 = shut, 1 = fully open. drives both modes, so one speed covers degrees and metres alike
    private float mySpeed;
    private float thisOpenFraction = 1f; // re-rolled each time it opens, so it doesn't land in exactly the same spot twice
    private int openCount;           // feeds the per-open roll. bumped in SetOpen, which every client runs off the same RPC
    private int positionSeed;

    public bool IsOpen => isOpen;
    public float InteractRange => interactRange;

    private void OnEnable()
    {
        AllHinges.Add(this);
    }

    private void OnDisable()
    {
        AllHinges.Remove(this); //a scene change destroys these, and a stale entry would have the player reaching for something that no longer exists
    }

    //Nearest openable within its OWN interact range - each carries its own, because a wardrobe and a jewellery box
    //shouldn't be grabbable from the same distance.
    public static SwingingHinge FindNearest(Vector3 position)
    {
        SwingingHinge nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (SwingingHinge hinge in AllHinges)
        {
            float distance = Vector3.Distance(hinge.transform.position, position);
            if (distance <= hinge.interactRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = hinge;
            }
        }
        return nearest;
    }

    private void Awake()
    {
        closedRotation = transform.localRotation; // wherever it's placed in the scene IS the closed pose, for both modes
        closedPosition = transform.localPosition;

        //round the coordinates to centimetres FIRST, then multiply. multiplying the float by a huge prime and only
        //then converting overflows int (a house at x=-71 gives -71 * 73856093, about -5.2 billion against an int
        //ceiling of 2.1 billion) - everything clamps to the same value and comes out identical. int overflow on the
        //multiply below is fine: C# wraps it deterministically, so every machine still lands on the same seed.
        positionSeed = (Mathf.RoundToInt(transform.position.x * 100f) * 73856093)
                     ^ (Mathf.RoundToInt(transform.position.y * 100f) * 83492791)
                     ^ (Mathf.RoundToInt(transform.position.z * 100f) * 19349663);

        System.Random hingeRandom = new System.Random(positionSeed & 0x7FFFFFFF); //force non-negative: System.Random(int.MinValue) throws on some runtimes
        mySpeed = openSpeed * RollBetween(hingeRandom, 1f - SpeedVariation, 1f + SpeedVariation);

        //built in code so a hinge needs nothing wired in the inspector beyond the clips. 3D on purpose: one opening
        //across the house should be faint, one behind you should make you jump.
        hingeAudio = gameObject.AddComponent<AudioSource>();
        hingeAudio.playOnAwake = false;
        hingeAudio.loop = false;
        hingeAudio.spatialBlend = 1f;
        hingeAudio.rolloffMode = AudioRolloffMode.Linear;
        hingeAudio.minDistance = 1.5f;
        hingeAudio.maxDistance = SoundMaxDistance;
    }

    private static float RollBetween(System.Random source, float min, float max)
    {
        return min + (float)source.NextDouble() * (max - min);
    }

    private Vector3 AxisVector()
    {
        switch (axis)
        {
            case HingeAxis.X:
            {
                return Vector3.right;
            }
            case HingeAxis.Z:
            {
                return Vector3.forward;
            }
            default:
            {
                return Vector3.up;
            }
        }
    }

    private void Update()
    {
        //one 0-to-1 progress value drives both modes, which is why a single speed field can cover degrees and metres
        //without caring which. ease toward it every frame - a clean movement, not a snap. any collider rides along,
        //so as it opens the gap physically clears and closing blocks it again.
        float target = isOpen ? 1f : 0f;
        openProgress = Mathf.MoveTowards(openProgress, target, mySpeed * Time.deltaTime);

        if (slidesOpen)
        {
            transform.localPosition = closedPosition + AxisVector() * (slideDistance * thisOpenFraction * openProgress);
        }
        else
        {
            transform.localRotation = closedRotation * Quaternion.AngleAxis(openAngle * thisOpenFraction * openProgress, AxisVector());
        }
    }

    public void SetOpen(bool open)
    {
        if (isOpen == open)
        {
            return; //already in that state - bail before the sound, so a caller re-asserting "open" every tick can't machine-gun the creak
        }

        if (open)
        {
            //re-roll how far it goes THIS time, so the same drawer never lands in quite the same spot twice. seeded
            //from position AND how many times it's been opened, so every client rolls the same number - they all run
            //this off the same RPC, so their open counts advance together.
            openCount++;
            System.Random openRandom = new System.Random((positionSeed ^ (openCount * 73856093)) & 0x7FFFFFFF);
            thisOpenFraction = RollBetween(openRandom, MinOpenFraction, 1f);
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

        hingeAudio.pitch = Random.Range(MinPitch, MaxPitch); //random pitch per play - the cheapest way to stop repetition fatigue
        hingeAudio.PlayOneShot(clip, Volume);
    }
}
