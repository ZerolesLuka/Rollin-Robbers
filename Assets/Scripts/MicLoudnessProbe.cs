using UnityEngine;
using Photon.Voice.Unity;

public class MicLoudnessProbe : MonoBehaviour
{
    public static MicLoudnessProbe Instance;

    [SerializeField] private float loudThreshold = 0.6f;    // mic peak is 0..1; above this counts as "loud"
    [SerializeField] private float mustStayLoudFor = 0.2f;  // seconds it must stay loud to trigger

    private Recorder recorder; //picks up mic
    private float peakAmplitude; //large loudness
    private float loudTimer; //how long your loud
    private bool isTooLoud; //after timer you're too loud

    private void Awake()
    {
        Instance = this;
        recorder = GetComponent<Recorder>();
    }
    public float VoiceLoudness
    {
        get {return peakAmplitude;}
    }

    private void Update()
    {
        var meter = recorder != null ? recorder.LevelMeter : null;
        if (meter == null)
        {
            return;
        }

        peakAmplitude = meter.CurrentPeakAmp; //finds peak noise

        if (peakAmplitude >= loudThreshold) //checks if we are louder then threshold
        {
            loudTimer += Time.deltaTime; //starts tiemr
        }
        else
        {
            loudTimer = 0f; //reset timer
        }

        isTooLoud = loudTimer >= mustStayLoudFor; //bool
    }

    private void OnGUI()
    {
        GUI.Label(new Rect(20, 200, 400, 30), $"peak {peakAmplitude:F3}   loudFor {loudTimer:F2}s");
        GUI.Box(new Rect(20, 230, Mathf.Clamp01(peakAmplitude) * 400f, 22), "");
        if (isTooLoud)
        {
            GUI.Label(new Rect(20, 260, 400, 40), "<<< TOO LOUD >>>");
        }
    }
}