using UnityEngine;
using UnityEngine.InputSystem;

// F9 hides every bit of UI. F10 takes a high-resolution screenshot.
//
// WHY: store screenshots and capsule source both need clean frames, and getting one currently means hunting through
// the Hierarchy unticking things mid-play, then remembering to tick them back. This makes it one key, and makes it
// impossible to leave the HUD half-hidden by accident.
//
// Creates itself on play in the editor and development builds, and compiles to nothing in a release build - same
// pattern as DebugPanel, so there's nothing to remember to strip before shipping.
public class ScreenshotTool : MonoBehaviour
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        GameObject go = new GameObject("[ScreenshotTool]") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(go);
        go.AddComponent<ScreenshotTool>();
    }

    //1 = capture at exactly the Game view's resolution, so a 1920x1080 Game view gives a 1920x1080 file ready to
    //upload with no resizing.
    //
    //Raise it to 2 or 4 when shooting CAPSULE source rather than screenshots: it renders that many times larger and
    //the downscale you do afterwards is free anti-aliasing, which matters when you're cropping a 920x430 header out
    //of a frame and can't afford to upscale.
    private const int ResolutionMultiplier = 1;

    private bool uiHidden;
    private int shotCount = -1; //-1 = not yet seeded from disk. See Capture.

    private void Update()
    {
        if (Keyboard.current == null) return;

        if (Keyboard.current.f9Key.wasPressedThisFrame)
        {
            uiHidden = !uiHidden;
            ApplyUiVisibility();
        }

        if (Keyboard.current.f10Key.wasPressedThisFrame)
        {
            Capture();
        }
    }

    private void ApplyUiVisibility()
    {
        //every Canvas in the scene, not a serialized list - a list would silently miss the canvas you added last week,
        //and you'd only find out when a half-hidden HUD turned up in a screenshot on the store page
        foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            canvas.enabled = !uiHidden;
        }

        //the crosshair draws in OnGUI, so it isn't on any Canvas and has to be switched off separately
        foreach (Crosshair crosshair in FindObjectsByType<Crosshair>(FindObjectsSortMode.None))
        {
            crosshair.enabled = !uiHidden;
        }

        Debug.Log(uiHidden ? "[Screenshot] UI hidden - F9 to bring it back, F10 to capture." : "[Screenshot] UI restored.");
    }

    private void Capture()
    {
        //written beside the project folder rather than inside Assets, so Unity doesn't import a pile of PNGs as game
        //assets every time you take one. GetFullPath so the log prints a real path you can paste, not one with an
        //"Assets/.." still in the middle of it.
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../Screenshots"));
        System.IO.Directory.CreateDirectory(path);

        //This object is rebuilt by RuntimeInitializeOnLoadMethod on every Play, so a counter that just started at
        //zero silently overwrote the previous session's shots - shot_001 landed on top of shot_001 and a whole
        //session's work vanished with no error. Read the highest number already on disk instead, once, the first
        //time a shot is taken. Once per session rather than per shot because CaptureScreenshot writes the file at
        //end of frame, so scanning every time would race against a capture that hasn't landed yet.
        if (shotCount < 0)
        {
            shotCount = HighestExistingShotNumber(path);
        }

        shotCount++;

        string file = System.IO.Path.Combine(path, $"shot_{shotCount:000}.png");
        ScreenCapture.CaptureScreenshot(file, ResolutionMultiplier);

        Debug.Log($"[Screenshot] saved {file} at {ResolutionMultiplier}x resolution.");
    }

    //Highest shot_NNN already sitting in the folder, or 0 if there are none.
    private static int HighestExistingShotNumber(string directory)
    {
        const string prefix = "shot_";
        int highest = 0;

        foreach (string existing in System.IO.Directory.GetFiles(directory, prefix + "*.png"))
        {
            //anything that doesn't parse is a file someone dropped in by hand - skip it rather than letting it
            //reset the count and start overwriting again
            string digits = System.IO.Path.GetFileNameWithoutExtension(existing).Substring(prefix.Length);
            if (int.TryParse(digits, out int number) && number > highest)
            {
                highest = number;
            }
        }

        return highest;
    }
#endif
}
