using UnityEngine;

// ONE ROOM'S worth of lights. Put this on an empty object and parent that room's lights underneath it.
//
// It owns NO state. RunManager.LitZoneMask is the only truth and this just matches the bulbs to it every frame - the
// same shape Safe.ApplyVisual uses for its door. That is what stops two players disagreeing about which rooms are lit:
// nothing here decides anything, every machine reads the same replicated number and reaches the same answer.
public class Lights : MonoBehaviour
{
    [SerializeField] private int zoneId; //which bit of RunManager.LitZoneMask is this room. 0-31, and NEVER reuse one
    public int ZoneId => zoneId; //read only

    private Light[] roomLights; //cached in Awake - looking them up every frame would be wasteful for an answer that never changes
    private bool lightsAreOn;   //what we last applied, so we only write to the Lights when it actually changes

    private void Awake()
    {
        //TRUE includes children whose GameObject is switched off in the editor. Without it, a light you happened to
        //have disabled while working on the room would never be found, and that room would come back on a bulb short.
        roomLights = GetComponentsInChildren<Light>(true);

        //THE HOUSE STARTS DARK. Killing them here is what lets you author the scene fully lit - so you can actually
        //see what you are placing - and still get a dark house at runtime, instead of hand-disabling thirty lights
        //every time you want to work and re-enabling them every time you want to play.
        SetLights(false);
    }

    private void Update()
    {
        if (RunManager.Instance == null)
        {
            return; //it spawns a few frames into a scene, and asking it anything before then throws
        }

        bool shouldBeOn = RunManager.Instance.IsZoneLit(zoneId);
        if (shouldBeOn != lightsAreOn)
        {
            SetLights(shouldBeOn);
        }
    }

    private void SetLights(bool on)
    {
        lightsAreOn = on;
        foreach (Light roomLight in roomLights)
        {
            if (roomLight == null)
            {
                continue; //deleted from the room since Awake - skip it rather than throwing once per frame forever
            }
            roomLight.enabled = on;
        }
    }
}
