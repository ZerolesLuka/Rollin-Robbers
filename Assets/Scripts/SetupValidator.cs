using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

// Says out loud what is wired and what isn't, once per scene load.
//
// WHY: almost every setup mistake in this project fails SILENTLY. A null tripwirePrefab doesn't throw - GuardPatrol
// just returns out of SetTrap and he never wires a doorway again. A null worldItemPrefab on the Safe doesn't throw -
// StockContents returns on its first line and the safe simply opens empty. A scene with no NoteSpawner doesn't throw -
// there are just no codes anywhere. Each of those looks identical to a gameplay bug from the player's chair, and
// tracking one down costs an hour that this file costs nothing.
//
// It reports CONSEQUENCES, not field names. "tripwirePrefab is null" tells you where to click; "he can never set a
// tripwire" tells you what you are about to spend the evening confused by.
//
// Runs in the editor and development builds only, and reads the static registries every system already maintains
// rather than scanning the scene - same rule as the AIs, no FindObjectsByType anywhere.
public static class SetupValidator
{
    private const float SettleSeconds = 2f; //safes, notes and loot are runtime-SPAWNED, so their registries are empty for the first frames

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Hook()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded; //domain reload can be switched off, and a double subscription reports twice
        SceneManager.sceneLoaded += OnSceneLoaded;
        CoroutineHost.Instance.Run(); //the first scene is already loaded by the time this method runs, so it never raises the event
    }

    private static void OnSceneLoaded(Scene _, LoadSceneMode __) => CoroutineHost.Instance.Run();
#endif

    public static IEnumerator Check()
    {
        yield return new WaitForSeconds(SettleSeconds);

        List<string> dead = new List<string>();  //a whole system cannot run
        List<string> warn = new List<string>();  //degraded, or content missing

        string scene = SceneManager.GetActiveScene().name;

        //---- machine-wide ----
        if (LayerMask.NameToLayer("Enviorment") < 0)
        {
            dead.Add("There is no layer called 'Enviorment'. AudioOcclusion falls back to blocking on EVERYTHING, so " +
                     "players and loot muffle sound like walls do, and the guard's hearing stops agreeing with his sight.");
        }

        //---- the guard's kit ----
        if (GuardPatrol.Instance != null)
        {
            if (IsNull(GuardPatrol.Instance, "tripwirePrefab")) dead.Add("Guard has no tripwirePrefab - he can never wire a doorway. TrapPoints in the scene do nothing.");
            if (IsNull(GuardPatrol.Instance, "bearTrapPrefab")) dead.Add("Guard has no bearTrapPrefab - the trap that PINS you can never be placed.");
            if (IsNull(GuardPatrol.Instance, "alarmPrefab")) dead.Add("Guard has no alarmPrefab - the only trap that pulls the dog in can never be placed.");
            if (IsNull(GuardPatrol.Instance, "baitLootPrefab")) warn.Add("Guard has no baitLootPrefab - he will never plant fake loot, so every item you see is genuine.");
            if (TrapPoint.All.Count == 0) warn.Add($"'{scene}' has no TrapPoints - he can still drop floor traps, but no tripwire will ever be strung.");
        }

        //---- the crew's kit ----
        if (Player.LocalPlayer != null)
        {
            if (IsNull(Player.LocalPlayer, "doorWedgePrefab")) dead.Add("Player has no doorWedgePrefab - G near a shut door can never wedge it. The whole side-of-the-door mechanic is off.");
            if (IsNull(Player.LocalPlayer, "jammerDevicePrefab")) dead.Add("Player has no jammerDevicePrefab - Q does nothing and the 550-credit Signal Jammer is unbuyable value.");
            if (IsNull(Player.LocalPlayer, "worldItemPrefab")) dead.Add("Player has no worldItemPrefab - dropping loot with G destroys it instead of putting it on the floor.");
            if (IsNull(Player.LocalPlayer, "spectatorCamera")) warn.Add("Player has no spectatorCamera - eliminated players get a frozen view instead of orbiting the house.");
            if (IsNull(Player.LocalPlayer, "pauseMenuRoot")) warn.Add("Player has no pauseMenuRoot - Escape does nothing.");
        }

        //---- the house ----
        if (Safe.AllSafes.Count == 0)
        {
            warn.Add($"'{scene}' spawned no safes - if this is the house, the SafeSpawner is missing or has no child markers.");
        }
        else
        {
            foreach (Safe safe in Safe.AllSafes)
            {
                if (IsNull(safe, "worldItemPrefab"))
                {
                    dead.Add("A Safe has no worldItemPrefab - it stocks nothing, so cracking it reveals an empty box and " +
                             "the entire safe mechanic pays out zero.");
                    break;
                }
            }
            if (SafeNote.AllNotes.Count == 0)
            {
                warn.Add($"'{scene}' has {Safe.AllSafes.Count} safe(s) but zero notes - nobody can ever LEARN a code, " +
                         "so every safe has to be brute-forced on the meter. Add a NoteSpawner.");
            }
        }

        if (SwingingHinge.AllHinges.Count == 0) warn.Add($"'{scene}' has no SwingingHinges - nothing in it opens.");

        //---- the money loop ----
        bool anyShop = Shopkeeper.AllKeepers.Count > 0 || ToolShop.AllShops.Count > 0;
        if (anyShop)
        {
            if (Shopkeeper.AllKeepers.Count == 0) dead.Add($"'{scene}' has a ToolShop but no Shopkeeper - you can spend money here but never earn any.");
            if (ToolShop.AllShops.Count == 0) dead.Add($"'{scene}' has a Shopkeeper but no ToolShop - you can sell here but never buy anything.");
        }

        Report(scene, dead, warn);
    }

    //the fields worth checking are all [SerializeField] private, which is correct - they belong to their own script,
    //not to this one. reflection is the honest cost of not punching holes in every class just to be inspectable.
    private static bool IsNull(Object owner, string field)
    {
        FieldInfo info = owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
        if (info == null)
        {
            Debug.LogWarning($"[Setup] Checked for a field called '{field}' on {owner.GetType().Name} and it no longer exists. Rename it here too.");
            return false; //don't cry wolf about a field that moved
        }
        return info.GetValue(owner) as Object == null;
    }

    private static void Report(string scene, List<string> dead, List<string> warn)
    {
        if (dead.Count == 0 && warn.Count == 0)
        {
            Debug.Log($"[Setup] '{scene}' - everything this can check is wired.");
            return;
        }

        if (dead.Count > 0)
        {
            Debug.LogError($"[Setup] '{scene}' - {dead.Count} system(s) CANNOT RUN:\n  - " + string.Join("\n  - ", dead));
        }
        if (warn.Count > 0)
        {
            Debug.LogWarning($"[Setup] '{scene}' - {warn.Count} thing(s) degraded:\n  - " + string.Join("\n  - ", warn));
        }
    }
}

// A static class can't run a coroutine, and the validator has to WAIT - the things it checks are runtime-spawned and
// don't exist on frame one. One hidden object, created on demand, rather than asking every scene to carry a component
// that only matters in the editor.
public class CoroutineHost : MonoBehaviour
{
    private static CoroutineHost instance;

    public static CoroutineHost Instance
    {
        get
        {
            if (instance != null) return instance;
            GameObject go = new GameObject("[CoroutineHost]") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            instance = go.AddComponent<CoroutineHost>();
            return instance;
        }
    }

    private Coroutine running;

    public void Run()
    {
        if (running != null) StopCoroutine(running); //a fast scene change shouldn't leave two reports racing
        running = StartCoroutine(SetupValidator.Check());
    }
}
