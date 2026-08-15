using System.Reflection;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

// F1. A test harness for a game whose full loop takes twenty minutes to reach honestly.
//
// WHY: verifying that haggling works currently means finding a note, cracking a safe, escaping the house, driving to
// the pawn shop and selling - and if anything in that chain is broken you never reach the thing you meant to test.
// Nearly six thousand lines of this project have never been played. The bottleneck on finding out what's broken is
// how long it takes to get to each system, so this makes every one of them reachable in a click.
//
// Everything here drives the REAL public API - RPC_GrantTool, RPC_GrantWedge, RPC_Route, AlertTo. It is a remote
// control, not a second implementation, so it cannot drift from the game or quietly test a path players never take.
// The two exceptions are Anger and the trap prefabs, which are private with no public route in; those use reflection
// and say so, rather than punching a debug-only hole in GuardPatrol that would outlive this file.
//
// Creates itself on play in the editor and development builds, and compiles to nothing in a release build.
public class DebugPanel : MonoBehaviour
{
    //DELIBERATELY OUTSIDE the #if below. Player reads this to know a menu owns the cursor, and that read has to
    //compile in a release build too - where this stays false forever and the whole thing costs one bool.
    public static bool IsOpen;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        GameObject go = new GameObject("[DebugPanel]") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(go);
        go.AddComponent<DebugPanel>();
    }

    private const int VanScene = 0, HouseScene = 1, ShopScene = 2;

    private bool open;
    private Vector2 scroll;
    private float anger = 40f;

    private void Update()
    {
        //Keyboard.current, not Input.GetKeyDown: this project is set to the new Input System ONLY
        //(activeInputHandler 1), where the legacy Input class throws the moment you touch it. Read straight off the
        //device rather than adding an action, so the generated PlayerInputActions wrapper stays untouched.
        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
        {
            open = !open;
            IsOpen = open;
            ApplyCursor();
        }
    }

    //A panel full of buttons is useless while the game holds the mouse captive for mouselook - you can see the thing
    //and never click it. Freeing the cursor is only half of it though: Player reads IsOpen through MenuOwnsCursor, so
    //WASD, Q, G and the loot wheel all stop too. Without that you'd be walking around blind behind the panel.
    private void ApplyCursor()
    {
        if (open)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return;
        }

        //Closing does NOT unconditionally grab the cursor back - you might have opened this while stood at the fence's
        //desk or sat at the van computer, and yanking mouselook back would leave their buttons on screen unclickable.
        Player localPlayer = Player.LocalPlayer;
        if (localPlayer == null || !localPlayer.MenuOwnsCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void OnGUI()
    {
        if (!open) return;

        //its own OnGUI on its own object: Player is a partial class that already has one in Player.SafeCode.cs, and a
        //class only gets a single OnGUI no matter how many files it is spread across.
        GUILayout.BeginArea(new Rect(10, 10, 300, Screen.height - 20), GUI.skin.box);
        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.Label("<b>DEBUG  (F1)</b>", Rich());

        Player me = Player.LocalPlayer;
        RunManager run = RunManager.Instance;

        if (me == null || run == null || run.Object == null || !run.Object.IsValid)
        {
            GUILayout.Label("Waiting for the session to come up...");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label($"money {run.Money}   loot {me.CarriedCount}/{me.MaxInventorySlots}   wedges {me.WedgesCarried}");
        //a readout rather than a one-off log: "did the wire actually tangle me" is a question worth being able to
        //answer at a glance every time, and a countdown also shows how long it lasted rather than just that it fired
        GUILayout.Label($"tangled {me.TangledSecondsLeft:F1}s   traps live {GuardTrap.AllTraps.Count}");
        GUILayout.Label($"tools  {me.ToolSlotA} / {me.ToolSlotB}");

        Header("Money");
        //RPC_SellItems is the real path money arrives by, so this exercises the same authority-side code a sale does
        if (GUILayout.Button("+5000")) run.RPC_SellItems(5000);

        Header("Tools");
        foreach (ToolType tool in System.Enum.GetValues(typeof(ToolType)))
        {
            if (tool == ToolType.None) continue;
            if (GUILayout.Button($"grant {tool}")) me.RPC_GrantTool((int)tool);
        }

        Header("Carry");
        //no separate wedge button - a wedge is an ordinary purchasable item now, so the "grant DoorWedge" button in
        //the tool list above does it, and two buttons for one thing is how the kit confusion started
        if (GUILayout.Button("+1 loot (1200)")) me.RPC_GrantPickup("Debug Loot", 1200, (int)ToolType.None); //None = ordinary loot rather than a tool

        Header("Camera");
        //DEV TUNING ONLY - this is not a player setting, and this panel compiles out of a release build. Drag it while
        //WALKING; tuning camera feel by editing a number, pressing play and walking a corridor is how you end up
        //nowhere. It edits the live Player, so whatever number feels right has to be copied onto the Player PREFAB by
        //hand to stick - it deliberately doesn't persist, so nobody can leave a playtest in a weird state.
        GUILayout.Label($"camera motion {me.CameraMotionScale:F2}   (authored value, copy onto the prefab)");
        me.CameraMotionScale = GUILayout.HorizontalSlider(me.CameraMotionScale, 0f, 1f);

        Header("Travel");
        if (GUILayout.Button("to the van")) run.RPC_Route(VanScene, 0, false);
        if (GUILayout.Button("to the house (new run)")) run.RPC_Route(HouseScene, 0, true);
        if (GUILayout.Button("to the pawn shop")) run.RPC_Route(ShopScene, 0, false);

        Header("Guard");
        if (GuardPatrol.Instance == null)
        {
            GUILayout.Label("no guard in this scene");
        }
        else
        {
            GUILayout.Label($"state {GuardPatrol.Instance.State}   anger {GuardPatrol.Instance.Anger:F0}");
            if (GUILayout.Button("send him to me")) GuardPatrol.Instance.AlertTo(me.transform.position);
            //skips the anger gate and the per-run trap budget on purpose - see DebugPlantWireNear. watch the state
            //readout above flip to Planting, then follow him: he walks one anchor, pauses, walks the other, wire exists
            if (GUILayout.Button("string a wire (nearest span)")) GuardPatrol.Instance.DebugPlantWireNear(me.transform.position);
            anger = GUILayout.HorizontalSlider(anger, 0f, 100f);
            if (GUILayout.Button($"set anger to {anger:F0}")) SetAnger(anger);
            GUILayout.Label("<i>traps need anger above 25 AND him having seen you</i>", Rich());
        }

        Header("Traps");
        if (GUILayout.Button("tripwire in front of me")) SpawnTrap("tripwirePrefab");
        if (GUILayout.Button("bear trap in front of me")) SpawnTrap("bearTrapPrefab");
        if (GUILayout.Button("alarm in front of me")) SpawnTrap("alarmPrefab");

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private static void Header(string text)
    {
        GUILayout.Space(6);
        GUILayout.Label($"<b>{text}</b>", Rich());
    }

    private static GUIStyle Rich() => new GUIStyle(GUI.skin.label) { richText = true };

    //Anger is [Networked] with a private setter, which is correct - the guard owns his own mood. Only the state
    //authority may write it, so on a client this does nothing rather than desyncing him.
    private static void SetAnger(float value)
    {
        GuardPatrol guard = GuardPatrol.Instance;
        if (guard == null || guard.Object == null || !guard.Object.HasStateAuthority)
        {
            Debug.LogWarning("[Debug] Only the master client owns the guard's anger.");
            return;
        }
        PropertyInfo prop = typeof(GuardPatrol).GetProperty("Anger");
        prop?.GetSetMethod(true)?.Invoke(guard, new object[] { value });
    }

    //the trap prefabs live as private fields on GuardPatrol because he is the only thing that should ever place one.
    //borrowing them by name here keeps that true - there is still no public way for gameplay code to spawn a trap.
    private void SpawnTrap(string fieldName)
    {
        GuardPatrol guard = GuardPatrol.Instance;
        Player me = Player.LocalPlayer;
        if (guard == null || me == null) return;
        if (guard.Object == null || !guard.Object.HasStateAuthority)
        {
            Debug.LogWarning("[Debug] Traps are spawned by the master client.");
            return;
        }

        FieldInfo field = typeof(GuardPatrol).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        NetworkObject prefab = field?.GetValue(guard) as NetworkObject;
        if (prefab == null)
        {
            Debug.LogWarning($"[Debug] GuardPatrol.{fieldName} is null - run Tools > Rollin' Robbers > Build Placeholder Prefabs.");
            return;
        }

        Vector3 spot = me.transform.position + me.transform.forward * 2.5f;
        guard.Runner.Spawn(prefab, spot, Quaternion.identity, PlayerRef.None, (runner, obj) =>
        {
            GuardTrap trap = obj.GetComponent<GuardTrap>();
            if (trap == null) return;
            //the same deferred-spawn safeguard every other spawned object uses: a deferred spawn drops the position
            //argument, so the authoritative position travels as networked state instead
            trap.SpawnPoint = spot;
            trap.UseSpawnPoint = true;
        });
    }
#endif
}
