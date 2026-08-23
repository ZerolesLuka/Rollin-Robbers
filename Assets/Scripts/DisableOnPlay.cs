using UnityEngine;

// Switches this GameObject off the moment the game starts, leaving it visible in the editor.
//
// WHY: the house is deliberately pitch black now, which is correct for playing it and useless for building it. So the
// scene keeps a group of bright work lights that let you actually see what you're placing - and this makes sure they
// never survive into play mode or a build. You get a lit scene view and a dark game from one set of objects, with
// nothing to remember to switch off before pressing Play.
//
// Not editor-only on purpose. If this were wrapped in UNITY_EDITOR the work lights would ship in a BUILD and the game
// would be fully lit for players while looking perfect to you - the worst possible failure, because you'd never see
// it. Disabling at runtime means the build behaves exactly like play mode.
//
// Also useful for the temporary camera parked in a room to preview lighting, and anything else that exists purely to
// help you author the scene.
public class DisableOnPlay : MonoBehaviour
{
    [SerializeField] private bool keepEnabledForNow; //tick to temporarily keep these alive in play mode - handy when you're chasing something in a dark room and need to see

    //Awake, not Start. Awake runs before the first frame is drawn, so the object is gone before anything renders -
    //Start would leave one frame where the work lights are visible, which reads as a flash on entering play.
    private void Awake()
    {
        if (keepEnabledForNow)
        {
            Debug.LogWarning($"[DisableOnPlay] '{name}' is being kept alive on purpose. Untick 'Keep Enabled For Now' before building, or these will ship.", this);
            return;
        }

        gameObject.SetActive(false);
    }
}
