using UnityEngine;
using Photon.Voice.Unity;

public class MicLoudnessProbe : MonoBehaviour
{
    public static MicLoudnessProbe Instance;

    private Recorder recorder; //picks up mic

    public float VoiceLoudness { get; private set; } //mic peak this frame, 0..1 - Player folds this into NoiseLevel so talking is noise the guard hears

    private void Awake()
    {
        if (Instance != null && Instance != this) return; //the extra NetworkManager copy from a scene reload - PersistAcrossScenes is about to destroy it, so don't point Instance at it
        Instance = this;
        recorder = GetComponent<Recorder>();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        var meter = recorder != null ? recorder.LevelMeter : null;
        if (meter == null)
        {
            return;
        }

        VoiceLoudness = meter.CurrentPeakAmp; //finds peak noise
    }
}
