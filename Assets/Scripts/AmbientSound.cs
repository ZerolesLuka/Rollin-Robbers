using UnityEngine;

// Local-only atmosphere: a looping room-tone bed PLUS optional random one-shots (creaks, ticks, distant thumps)
// that make the house feel lived-in instead of dead silent. Purely cosmetic - it runs on each client's own
// machine, is NEVER networked, and NEVER feeds the guard's hearing (his ears only read player NoiseLevel, not
// AudioSources), so this can't accidentally give a player away. Drop it on an empty GameObject in a scene.
//   - Global house bed  -> set the AudioSource's Spatial Blend to 2D (0). Same volume everywhere.
//   - Positional ambient -> set Spatial Blend to 3D (1) and place it AT the source (a fridge hum, a ticking clock).
[RequireComponent(typeof(AudioSource))]
public class AmbientSound : MonoBehaviour
{
    [Header("Continuous bed - the always-on tone")]
    [SerializeField] private AudioClip loopClip;
    [SerializeField, Range(0f, 1f)] private float loopVolume = 0.3f;

    [Header("Random one-shots (optional) - creaks, ticks, distant thumps")]
    [SerializeField] private AudioClip[] oneShotClips;
    [SerializeField, Range(0f, 1f)] private float oneShotVolume = 0.5f;
    [SerializeField] private float minSecondsBetweenOneShots = 10f; // shortest wait before the next random sound
    [SerializeField] private float maxSecondsBetweenOneShots = 30f; // longest wait - the gap is randomized in this range so it never feels like a metronome

    private AudioSource bedSource;     // plays the looping tone
    private AudioSource oneShotSource; // a SEPARATE source, so a one-shot can never duck or cut off the bed
    private float nextOneShotTime;

    private void Awake()
    {
        //the looping bed uses this object's own AudioSource (the [RequireComponent] guarantees one is here)
        bedSource = GetComponent<AudioSource>();
        bedSource.clip = loopClip;
        bedSource.loop = true;
        bedSource.volume = loopVolume;
        bedSource.playOnAwake = false;
        if (loopClip != null)
        {
            bedSource.Play();
        }

        //one-shots get their OWN source, added in code so there's nothing extra to wire in the inspector. copy
        //the bed's spatial settings so a positional (3D) ambient's creaks come from the same spot as its hum.
        oneShotSource = gameObject.AddComponent<AudioSource>();
        oneShotSource.playOnAwake = false;
        oneShotSource.loop = false;
        oneShotSource.spatialBlend = bedSource.spatialBlend;
        oneShotSource.minDistance = bedSource.minDistance;
        oneShotSource.maxDistance = bedSource.maxDistance;
        oneShotSource.rolloffMode = bedSource.rolloffMode;

        ScheduleNextOneShot();
    }

    private void Update()
    {
        if (oneShotClips == null || oneShotClips.Length == 0)
        {
            return; //no spice clips assigned - just the bed, nothing to schedule
        }

        if (Time.time >= nextOneShotTime)
        {
            AudioClip clip = oneShotClips[Random.Range(0, oneShotClips.Length)];
            if (clip != null)
            {
                oneShotSource.PlayOneShot(clip, oneShotVolume);
            }
            ScheduleNextOneShot();
        }
    }

    private void ScheduleNextOneShot()
    {
        nextOneShotTime = Time.time + Random.Range(minSecondsBetweenOneShots, maxSecondsBetweenOneShots);
    }
}
