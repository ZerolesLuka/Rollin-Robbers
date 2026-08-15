using UnityEngine;

// Every player-facing setting, in one place, saved to disk and applied the moment it changes.
//
// Static and PlayerPrefs-backed on purpose: settings belong to the MACHINE, not to a player object. They have to
// survive scene loads, apply before anyone has spawned, and still be there next launch - none of which a component
// on a prefab does well.
//
// Setting any property here saves AND applies immediately, so a slider can write straight to it while being dragged
// and the change is audible under your finger. There's no Apply button to forget to press.
public static class GameSettings
{
    private const string SensitivityKey  = "rr_sensitivity";
    private const string InvertYKey      = "rr_invert_y";
    private const string MasterVolKey    = "rr_master_volume";
    private const string VoiceVolKey     = "rr_voice_volume";
    private const string CameraMotionKey = "rr_camera_motion";

    //the values a fresh install starts on. sensitivity matches what the Player prefab used to carry, so nobody's
    //existing feel changes the day this lands.
    public const float DefaultSensitivity = 0.5f;
    public const float MinSensitivity = 0.05f;
    public const float MaxSensitivity = 3f;

    private static bool loaded;

    private static float mouseSensitivity = DefaultSensitivity;
    private static bool invertLookY;
    private static float masterVolume = 1f;
    private static float voiceVolume = 1f;
    private static float cameraMotionAmount = 1f;

    public static float MouseSensitivity
    {
        get { EnsureLoaded(); return mouseSensitivity; }
        set
        {
            EnsureLoaded();
            mouseSensitivity = Mathf.Clamp(value, MinSensitivity, MaxSensitivity); //clamped so a slider bug or a hand-edited prefs file can't leave someone unable to aim
            PlayerPrefs.SetFloat(SensitivityKey, mouseSensitivity);
        }
    }

    public static bool InvertLookY
    {
        get { EnsureLoaded(); return invertLookY; }
        set
        {
            EnsureLoaded();
            invertLookY = value;
            PlayerPrefs.SetInt(InvertYKey, value ? 1 : 0);
        }
    }

    //Everything the game plays, voice included. AudioListener.volume rather than an AudioMixer because there ISN'T a
    //mixer yet - every AudioSource in the project is standalone. When one exists this should move onto it.
    public static float MasterVolume
    {
        get { EnsureLoaded(); return masterVolume; }
        set
        {
            EnsureLoaded();
            masterVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(MasterVolKey, masterVolume);
            AudioListener.volume = masterVolume; //applied live, so dragging the slider is audible under your finger
        }
    }

    //Teammates' voices only. Worth its own slider in THIS game more than most: proximity voice means one friend with
    //a hot mic sits on top of the footsteps and creaks you're straining to hear, and turning the master down to fix
    //that also turns down the guard. Player applies this to each Speaker as it appears.
    public static float VoiceVolume
    {
        get { EnsureLoaded(); return voiceVolume; }
        set
        {
            EnsureLoaded();
            voiceVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(VoiceVolKey, voiceVolume);
        }
    }

    //Scales every bit of camera movement that ISN'T the mouse: head bob, the landing dip, breathing sway, strafe tilt
    //and the sprint FOV push. One slider instead of five toggles, because someone who needs one of them off almost
    //always needs the lot off - and 0 has to mean a genuinely still camera, not "mostly still".
    //
    //This exists at all because head bob is the single most common motion-sickness complaint in first-person games,
    //and shipping it with no way to turn it down would lock those players out of the game entirely.
    public static float CameraMotionAmount
    {
        get { EnsureLoaded(); return cameraMotionAmount; }
        set
        {
            EnsureLoaded();
            cameraMotionAmount = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(CameraMotionKey, cameraMotionAmount);
        }
    }

    //Effective look sensitivity for the vertical axis, sign included - so nothing else has to remember the invert flag.
    public static float LookSensitivityY => MouseSensitivity * (InvertLookY ? -1f : 1f);

    private static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true; //set FIRST: the property setters below call back into here, and without this it recurses

        mouseSensitivity = Mathf.Clamp(PlayerPrefs.GetFloat(SensitivityKey, DefaultSensitivity), MinSensitivity, MaxSensitivity);
        invertLookY = PlayerPrefs.GetInt(InvertYKey, 0) == 1;
        masterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(MasterVolKey, 1f));
        voiceVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(VoiceVolKey, 1f));
        cameraMotionAmount = Mathf.Clamp01(PlayerPrefs.GetFloat(CameraMotionKey, 1f));

        AudioListener.volume = masterVolume; //the saved volume has to take effect before anything plays, not when the menu is first opened
    }

    public static void Save()
    {
        PlayerPrefs.Save(); //the setters already write the values; this just forces them to disk rather than waiting for a clean quit
    }

    public static void ResetToDefaults()
    {
        MouseSensitivity = DefaultSensitivity;
        InvertLookY = false;
        MasterVolume = 1f;
        VoiceVolume = 1f;
        CameraMotionAmount = 1f;
        Save();
    }
}
