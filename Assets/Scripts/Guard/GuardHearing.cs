using UnityEngine;

// Reusable ears for any guard archetype. Pure perception, no networked state and no FSM decisions:
// it answers "what's the loudest thing I can hear right now, and where?" - the brain decides what to DO with it.
// Config lives here so each archetype (homeowner, dog, ...) tweaks its own values in its own inspector.
public class GuardHearing : MonoBehaviour
{
    [SerializeField] private float noiseRange = 30f;          //how close a noise has to be to register at all
    [SerializeField] private float noiseSpeedThreshold = 5f;  //below this the noise is too quiet to care (crouch = silent)

    public struct Heard
    {
        public float loudness;   //0 = heard nothing
        public Vector3 position; //where the loudest noise came from (only meaningful when loudness > 0)
    }

    public Heard LoudestNoise()
    {
        Heard result = new Heard { loudness = 0f, position = transform.position };
        foreach (Player player in Player.ActivePlayers) //live list, so players who joined after the guard spawned still count
        {
            if (player.IsEliminated) continue; //eliminated players make no noise the guard cares about
            float distanceToPlayer = Vector3.Distance(transform.position, player.transform.position);
            float distanceFactor = Mathf.Clamp01((noiseRange - distanceToPlayer) / noiseRange); //in range or not
            float audibleLoudness = Mathf.Max(0f, player.NoiseLevel - noiseSpeedThreshold); //below the floor (crouch) = silent
            float perceived = audibleLoudness * distanceFactor; //loud+close = big, loud+far = ~0
            if (perceived > result.loudness) //louder than anything else heard this pass
            {
                result.loudness = perceived;
                result.position = player.transform.position; //remember WHERE the loudest noise came from
            }
        }
        return result;
    }
}
