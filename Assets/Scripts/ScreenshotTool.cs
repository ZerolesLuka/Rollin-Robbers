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

    //4 renders the frame at FOUR TIMES the window's resolution and scales it down. That downsample is free
    //anti-aliasing, and it's why a captured shot looks noticeably crisper than anything grabbed off a video.
    private const int ResolutionMultiplier = 4;

    private bool uiHidden;
    private int shotCount;

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
        shotCount++;

        //written beside the project folder rather than inside Assets, so Unity doesn't import a pile of PNGs as game
        //assets every time you take one
        string path = System.IO.Path.Combine(Application.dataPath, "../Screenshots");
        System.IO.Directory.CreateDirectory(path);

        string file = System.IO.Path.Combine(path, $"shot_{shotCount:000}.png");
        ScreenCapture.CaptureScreenshot(file, ResolutionMultiplier);

        Debug.Log($"[Screenshot] saved {file} at {ResolutionMultiplier}x resolution.");
    }
#endif
}
