using System.Collections.Generic;
using UnityEngine;

// Keeps its GameObject alive across scene loads - and, crucially, makes sure only ONE of it survives.
//
// THE BUG THIS FIXES: DontDestroyOnLoad on its own is not enough when the object lives in a scene you RE-ENTER. The
// NetworkManager sits in Outdoor, and the run loop is Outdoor -> Indoor -> Outdoor. The first survivor persisted
// correctly, then coming back to the van loaded Outdoor's own copy again and that copy also made itself immortal.
// Every return trip added another, each carrying a NetworkRunner, a Photon VoiceConnection, a Recorder and a
// MicLoudnessProbe - so you got duplicate network callbacks, multiple microphones capturing at once, and
// MicLoudnessProbe.Instance repointed at whichever copy happened to wake last rather than the one transmitting.
// The mic mechanic stopped working after the first trip home and nothing said so.
//
// Keyed by name rather than a single static instance, because this component is generic - two DIFFERENT persistent
// objects must not evict each other.
public class PersistAcrossScenes : MonoBehaviour
{
    private static readonly Dictionary<string, PersistAcrossScenes> survivors = new Dictionary<string, PersistAcrossScenes>();

    [SerializeField] private string persistenceKey = ""; //leave empty to key on the object's name

    private string myKey;

    private void Awake()
    {
        myKey = string.IsNullOrEmpty(persistenceKey) ? gameObject.name : persistenceKey;

        //A survivor from an earlier visit is already here, so this scene's fresh copy is the duplicate - not the
        //other way round. The != null matters: with Enter Play Mode's domain reload disabled these statics outlive
        //the play session, so the entry may point at something Unity destroyed a session ago.
        if (survivors.TryGetValue(myKey, out PersistAcrossScenes existing) && existing != null && existing != this)
        {
            Destroy(gameObject);
            return;
        }

        survivors[myKey] = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        //only clear the slot if WE are the one in it - a duplicate tidying up must not free the real survivor's key
        if (myKey != null && survivors.TryGetValue(myKey, out PersistAcrossScenes current) && current == this)
        {
            survivors.Remove(myKey);
        }
    }
}
