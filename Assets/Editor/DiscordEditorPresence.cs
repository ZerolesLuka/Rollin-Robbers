using System;
using DiscordRPC;
using UnityEditor;
using UnityEngine.SceneManagement;

// Shows "Rollin' Robbers" on your Discord profile the whole time the Unity editor is open, with the scene you're in
// and how long you've been at it.
//
// This lives in an Editor folder ON PURPOSE. Anything in Assets/Editor is stripped from builds, so none of this ships
// to players - it's a dev-side toy, not a game feature. The day the game itself wants presence ("Robbing a house,
// 2/4 crew") that's a separate runtime script using the same Application ID.
//
// [InitializeOnLoad] runs the static constructor every time Unity loads the editor OR finishes recompiling scripts.
// That second case is the awkward one: a recompile wipes all static state, so without the SessionState below the
// elapsed timer would reset to zero every single time you saved a script.
[InitializeOnLoad]
public static class DiscordEditorPresence
{
    private const string ApplicationId = "1531809469464576183";

    //upload art at Developer Portal > Rich Presence > Art Assets, then put the NAME you gave it here. blank = text
    //only, which looks perfectly fine - the image is decoration, not a requirement.
    private const string LargeImageKey = "";
    private const string LargeImageText = "Rollin' Robbers";

    //SessionState survives a script recompile but NOT closing Unity, which is exactly the lifetime we want the timer
    //to have: "how long have I been working today", reset when I actually stop.
    private const string SessionStartKey = "DiscordEditorPresence.SessionStart";
    private const double SecondsBetweenUpdates = 5.0;

    private static DiscordRpcClient client;
    private static double nextUpdateTime;
    private static string lastSentState;
    private static DateTime sessionStartUtc;

    static DiscordEditorPresence()
    {
        Connect();
    }

    //kept separate from the static constructor so the Reconnect menu item can call it. a type initializer only ever
    //runs once per domain, so there's no legitimate way to "re-run" it - the work has to live somewhere callable.
    private static void Connect()
    {
        //a recompile re-runs the constructor, so recover the original start time rather than stamping a new one
        string storedStart = SessionState.GetString(SessionStartKey, string.Empty);
        if (!string.IsNullOrEmpty(storedStart) && long.TryParse(storedStart, out long storedTicks))
        {
            sessionStartUtc = new DateTime(storedTicks, DateTimeKind.Utc);
        }
        else
        {
            sessionStartUtc = DateTime.UtcNow;
            SessionState.SetString(SessionStartKey, sessionStartUtc.Ticks.ToString());
        }

        client = new DiscordRpcClient(ApplicationId);
        client.Initialize();

        EditorApplication.update += OnEditorUpdate;

        //Discord holds the pipe open until it's told otherwise. without these two the status lingers as a ghost after
        //a recompile, and you end up with a stale "Rollin' Robbers" sat on your profile hours after closing Unity.
        AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        EditorApplication.quitting += Shutdown;
    }

    private static void OnEditorUpdate()
    {
        if (client == null || client.IsDisposed)
        {
            return;
        }

        client.Invoke(); //pumps Discord's callbacks - the library queues them and does nothing until this is called

        //EditorApplication.update fires every editor frame, which is far more often than Discord needs. throttling
        //also means we're not rebuilding the presence object hundreds of times a second for no reason.
        if (EditorApplication.timeSinceStartup < nextUpdateTime)
        {
            return;
        }
        nextUpdateTime = EditorApplication.timeSinceStartup + SecondsBetweenUpdates;

        string sceneName = SceneManager.GetActiveScene().name;
        if (string.IsNullOrEmpty(sceneName))
        {
            sceneName = "Untitled";
        }
        string state = EditorApplication.isPlaying ? $"Playtesting {sceneName}" : $"Editing {sceneName}";

        if (state == lastSentState)
        {
            return; //nothing changed - don't spend a pipe write saying the same thing again
        }
        lastSentState = state;

        //Discord already prints the APPLICATION's name as the header line, so repeating "Rollin' Robbers" in Details
        //just shows the title twice. Details is the line that changes, State is the fixed tagline underneath:
        //
        //  Rollin' Robbers        <- the app name from the Developer Portal
        //  Editing Indoor         <- Details
        //  Co-op stealth heist    <- State
        //  02:14 elapsed          <- Timestamps
        RichPresence presence = new RichPresence
        {
            Details = state,
            State = "Co-op stealth heist",
            Timestamps = new Timestamps(sessionStartUtc) //Discord renders this as a live counting-up timer
        };

        if (!string.IsNullOrEmpty(LargeImageKey))
        {
            presence.Assets = new Assets
            {
                LargeImageKey = LargeImageKey,
                LargeImageText = LargeImageText
            };
        }

        client.SetPresence(presence);
    }

    //force a fresh connection without restarting Unity - handy when Discord was launched after the editor
    [MenuItem("Tools/Discord/Reconnect")]
    private static void Reconnect()
    {
        Shutdown();
        lastSentState = null; //otherwise the throttle decides nothing changed and never sends the first presence
        nextUpdateTime = 0.0;
        SessionState.SetString(SessionStartKey, string.Empty); //fresh timer on a manual reconnect
        Connect();
    }

    private static void Shutdown()
    {
        EditorApplication.update -= OnEditorUpdate;
        AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
        EditorApplication.quitting -= Shutdown;

        if (client != null && !client.IsDisposed)
        {
            client.ClearPresence();
            client.Dispose();
        }
        client = null;
    }
}
