using UnityEngine;

// Walls muffle sound. Put this next to an AudioSource (or attach it from code with AudioOcclusion.Attach) and the
// source gets quieter and duller when there's geometry between it and your ears.
//
// This matters more here than in most games. The whole design rests on players straining to hear where the guard is,
// and on their own noise giving them away - but an unoccluded AudioSource sounds EXACTLY the same through a brick
// wall as it does stood in the doorway. Distance rolloff alone can't tell you "he's in the next room" from "he's in
// this room but further away", which is the single most important thing you're listening for.
//
// HOW IT WORKS: a periodic raycast from this source to the listener, counting how many pieces of geometry are in the
// way. More walls = lower low-pass cutoff and lower volume. The measured value is smoothed every frame, because
// snapping the filter as someone walks through a doorway sounds like a broken radio.
//
// PERFORMANCE: raycasts are staggered, not run every frame. Each instance picks a random phase at startup so they
// don't all land on the same frame, and a source that isn't playing or is out of earshot doesn't cast at all.
[RequireComponent(typeof(AudioSource))]
public class AudioOcclusion : MonoBehaviour
{
    [SerializeField] private LayerMask wallMask = ~0;          //what counts as blocking. set this to your geometry layers - leaving it as Everything means players and loot muffle sound too
    [SerializeField] private float clearCutoff = 22000f;       //low-pass with nothing in the way, i.e. no audible effect
    [SerializeField] private float blockedCutoff = 800f;       //fully muffled. lower = more "through a wall", higher = more "through a curtain"
    [SerializeField, Range(0f, 1f)] private float blockedVolume = 0.45f; //how much quieter a fully blocked sound is
    [SerializeField] private int maxObstructions = 3;          //walls beyond this don't muffle further. stops a big house muting everything to nothing
    [SerializeField] private float checkInterval = 0.12f;      //seconds between raycasts. the smoothing hides the gap between checks
    [SerializeField] private float smoothing = 6f;             //how fast it eases toward the measured value. lower = laggier and smoother

    private AudioSource source;
    private AudioLowPassFilter lowPass;
    private float baseVolume;        //the volume the source was authored at, so we scale rather than overwrite
    private float occlusion;         //0 = clear, 1 = fully blocked. smoothed
    private float targetOcclusion;   //what the last raycast measured
    private float nextCheckTime;

    private static readonly RaycastHit[] hitBuffer = new RaycastHit[8]; //shared and reused - RaycastAll would allocate on every check, on every source

    //For AudioSources built in code, or living on prefabs we'd rather not hand-edit one by one. Safe to call twice -
    //a source that's already occluded keeps the component it has rather than stacking a second one.
    public static AudioOcclusion Attach(AudioSource target)
    {
        if (target == null) return null;

        AudioOcclusion existing = target.GetComponent<AudioOcclusion>();
        if (existing != null) return existing;

        AudioOcclusion occluder = target.gameObject.AddComponent<AudioOcclusion>();
        occluder.source = target;
        occluder.wallMask = DefaultWallMask;
        return occluder;
    }

    //THE LAYER THAT COUNTS AS A WALL. Resolved by name once, from the same layer the guard already uses to block his
    //line of sight - if he can't see through it, you shouldn't hear cleanly through it either, and having one answer
    //for both means sight and sound can never disagree about what's solid.
    private static int cachedWallMask;
    private static bool wallMaskResolved;

    public static LayerMask DefaultWallMask
    {
        get
        {
            if (wallMaskResolved) return cachedWallMask;
            wallMaskResolved = true;

            int layer = LayerMask.NameToLayer("Enviorment"); //spelled as it is in the project, not as it should be
            if (layer >= 0)
            {
                cachedWallMask = 1 << layer;
            }
            else
            {
                cachedWallMask = ~0; //no such layer - occlude on everything rather than silently doing nothing at all
                Debug.LogWarning("[AudioOcclusion] No 'Enviorment' layer found, so sound is being blocked by EVERYTHING including players and loot. Point DefaultWallMask at your geometry layers.");
            }
            return cachedWallMask;
        }
    }

    private void Awake()
    {
        if (source == null) source = GetComponent<AudioSource>();
        baseVolume = source.volume;

        lowPass = GetComponent<AudioLowPassFilter>();
        if (lowPass == null)
        {
            lowPass = gameObject.AddComponent<AudioLowPassFilter>(); //added here so nothing needs wiring per source
        }
        lowPass.cutoffFrequency = clearCutoff;

        nextCheckTime = Time.time + Random.value * checkInterval; //random phase so every source in the house doesn't raycast on the same frame
    }

    private void OnEnable()
    {
        //start from whatever we last measured rather than snapping to clear - re-enabling shouldn't produce a blip
        ApplyOcclusion(occlusion);
    }

    private void Update()
    {
        if (source == null) return;

        //PlayOneShot doesn't set isPlaying reliably for very short clips, so don't gate the raycast on it - a door
        //creak would finish before we ever measured it. gate on earshot instead, which is the expensive case anyway.
        AudioListener listener = ActiveListener();
        if (listener == null) return;

        float distance = Vector3.Distance(transform.position, listener.transform.position);
        if (distance > source.maxDistance)
        {
            targetOcclusion = 0f; //out of earshot entirely - don't spend a raycast deciding how muffled silence is
        }
        else if (Time.time >= nextCheckTime)
        {
            nextCheckTime = Time.time + checkInterval;
            targetOcclusion = MeasureOcclusion(listener.transform.position, distance);
        }

        occlusion = Mathf.Lerp(occlusion, targetOcclusion, 1f - Mathf.Exp(-smoothing * Time.deltaTime));
        ApplyOcclusion(occlusion);
    }

    private float MeasureOcclusion(Vector3 listenerPosition, float distance)
    {
        Vector3 toListener = listenerPosition - transform.position;
        if (distance < 0.01f) return 0f;

        //QueryTriggerInteraction.Ignore matters: hiding spots, interaction volumes and pickup triggers are all
        //colliders too, and none of them are walls. without this a squeaky toy inside a trigger volume sounds muffled.
        int hits = Physics.RaycastNonAlloc(transform.position, toListener.normalized, hitBuffer, distance, wallMask, QueryTriggerInteraction.Ignore);
        if (hits <= 0) return 0f;

        return Mathf.Clamp01((float)hits / Mathf.Max(1, maxObstructions));
    }

    //Other systems that want to muffle this same source clamp the cutoff through here instead of grabbing the filter
    //themselves. One owner, many contributors - two scripts both writing cutoffFrequency and enabled just means
    //whichever ran last wins, which is exactly how the taped-mouth effect and occlusion would have fought.
    public float ExtraMuffleCutoff { get; set; } = float.MaxValue;

    private void ApplyOcclusion(float amount)
    {
        if (lowPass != null)
        {
            //lowest wins: a taped mouth heard through a wall should be as muffled as the worse of the two, not average out
            lowPass.cutoffFrequency = Mathf.Min(Mathf.Lerp(clearCutoff, blockedCutoff, amount), ExtraMuffleCutoff);
            lowPass.enabled = true; //always on now. it's the single point that decides how dull this source sounds
        }
        if (source != null)
        {
            source.volume = baseVolume * Mathf.Lerp(1f, blockedVolume, amount);
        }
    }

    //The ears we're measuring to. Cached per frame across all instances rather than each one running a scene search:
    //there's exactly one active listener and it moves as a unit.
    private static AudioListener cachedListener;
    private static int cachedListenerFrame = -1;

    private static AudioListener ActiveListener()
    {
        if (cachedListenerFrame == Time.frameCount) return cachedListener;
        cachedListenerFrame = Time.frameCount;

        if (cachedListener != null && cachedListener.isActiveAndEnabled) return cachedListener;

        //FindObjectsByType is the thing this codebase avoids for PLAYERS, and for the same reason - but there's no
        //self-registering list of listeners, it only runs when the cached one has genuinely gone, and a scene has one.
        foreach (AudioListener candidate in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
        {
            if (candidate.isActiveAndEnabled)
            {
                cachedListener = candidate;
                return cachedListener;
            }
        }
        cachedListener = null;
        return null;
    }
}
