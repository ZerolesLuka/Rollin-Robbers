using System.IO;
using Fusion;
using UnityEditor;
using UnityEngine;

// Builds the five NetworkObject prefabs that nothing in the project has ever had a mesh for - the three guard traps,
// the door wedge and the jammer - out of Unity primitives, then assigns them to the Guard and Player prefabs.
//
// WHY THIS EXISTS: traps, wedges and the jammer are three whole systems that cannot run at all while those fields are
// null, and the guard fails SILENTLY when they are (SetTrap just returns). That means several days of modelling stand
// between the code and the first time anyone finds out whether a bear trap is fun. This gets them running tonight as
// ugly primitives, so the modelling is a replacement rather than a prerequisite.
//
// Swapping a real mesh in later means deleting the "Visual" child and dropping the FBX in its place. Nothing else on
// the prefab is cosmetic - the components, the kinds and the radii are the real gameplay values.
//
// COLOUR IS DELIBERATE, not decoration: the guard's traps are red and dark, the crew's gear is cyan. In a dark house
// lit by one flashlight, silhouette and colour are the only information the player gets, and confusing "I placed this"
// with "this will pin me by the ankle" is a genuine failure. The real meshes should keep that split.
public static class PlaceholderPrefabBuilder
{
    private const string FolderPath = "Assets/Prefabs/Placeholder";
    private const string PlayerPrefabPath = "Assets/Resources/Player.prefab";
    private const string GuardPrefabPath = "Assets/Resources/Guard (1).prefab";
    private const string SafePrefabPath = "Assets/Prefabs/Safe Variant.prefab";
    private const string WorldItemPrefabPath = "Assets/Prefabs/WorldItem.prefab";

    private static readonly Color GuardRed = new Color(0.62f, 0.13f, 0.11f);
    private static readonly Color GuardDark = new Color(0.17f, 0.17f, 0.19f);
    private static readonly Color GuardAmber = new Color(0.85f, 0.45f, 0.06f);
    private static readonly Color CrewCyan = new Color(0.10f, 0.62f, 0.68f);
    private static readonly Color CrewYellow = new Color(0.85f, 0.72f, 0.15f);

    [MenuItem("Tools/Rollin' Robbers/Build Placeholder Prefabs")]
    public static void Build()
    {
        Directory.CreateDirectory(FolderPath);
        AssetDatabase.Refresh(); //CreateDirectory touches the disk; the AssetDatabase has to be told before we can write assets into it

        GameObject tripwire = BuildTrap("Tripwire", GuardTrap.TrapKind.Tripwire, 1.2f, 2f, root =>
        {
            //a long thin bar across a doorway. THICK on purpose - a 2cm wire is invisible at 4m in the dark, which
            //turns "I walked into a tripwire" into "the game killed me for no reason". Fair means visible if you look.
            Visual(root, PrimitiveType.Cube, new Vector3(0f, 0.35f, 0f), new Vector3(1.1f, 0.04f, 0.04f), GuardRed);
            Visual(root, PrimitiveType.Cube, new Vector3(-0.55f, 0.18f, 0f), new Vector3(0.06f, 0.36f, 0.06f), GuardDark);
            Visual(root, PrimitiveType.Cube, new Vector3(0.55f, 0.18f, 0f), new Vector3(0.06f, 0.36f, 0.06f), GuardDark);
        });

        GameObject bearTrap = BuildTrap("BearTrap", GuardTrap.TrapKind.BearTrap, 0.9f, 2f, root =>
        {
            Visual(root, PrimitiveType.Cylinder, new Vector3(0f, 0.03f, 0f), new Vector3(0.5f, 0.03f, 0.5f), GuardDark);
            for (int i = 0; i < 8; i++)
            {
                float a = i / 8f * Mathf.PI * 2f;
                Visual(root, PrimitiveType.Cube,
                    new Vector3(Mathf.Cos(a) * 0.2f, 0.11f, Mathf.Sin(a) * 0.2f),
                    new Vector3(0.05f, 0.16f, 0.05f), GuardRed);
            }
        });

        GameObject alarm = BuildTrap("ProximityAlarm", GuardTrap.TrapKind.ProximityAlarm, 3.5f, 4f, root =>
        {
            //widest trigger of the three, so it needs to LOOK like it has reach - the lens is the tell.
            Visual(root, PrimitiveType.Cube, new Vector3(0f, 0.09f, 0f), new Vector3(0.22f, 0.18f, 0.16f), GuardDark);
            Visual(root, PrimitiveType.Sphere, new Vector3(0f, 0.2f, 0f), new Vector3(0.13f, 0.13f, 0.13f), GuardAmber);
        });

        GameObject wedge = BuildSimple<DoorWedge>("DoorWedge", root =>
        {
            Visual(root, PrimitiveType.Cube, new Vector3(0f, 0.03f, 0f), new Vector3(0.1f, 0.06f, 0.18f), CrewYellow);
        });

        GameObject jammer = BuildSimple<JammerDevice>("JammerDevice", root =>
        {
            Visual(root, PrimitiveType.Cube, new Vector3(0f, 0.08f, 0f), new Vector3(0.24f, 0.16f, 0.18f), CrewCyan);
            Visual(root, PrimitiveType.Cylinder, new Vector3(0.08f, 0.3f, 0f), new Vector3(0.015f, 0.16f, 0.015f), CrewCyan);
        });

        NetworkObject worldItem = AssetDatabase.LoadAssetAtPath<GameObject>(WorldItemPrefabPath)?.GetComponent<NetworkObject>();

        Wire(GuardPrefabPath, ("tripwirePrefab", tripwire), ("bearTrapPrefab", bearTrap), ("alarmPrefab", alarm),
            ("baitLootPrefab", worldItem != null ? worldItem.gameObject : null));
        Wire(PlayerPrefabPath, ("jammerDevicePrefab", jammer), ("doorWedgePrefab", wedge));
        Wire(SafePrefabPath, ("worldItemPrefab", worldItem != null ? worldItem.gameObject : null));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[PlaceholderPrefabBuilder] Built 5 prefabs in " + FolderPath +
                  " and wired them onto the Guard, Player and Safe prefabs. If anything refuses to spawn at runtime, " +
                  "open the Fusion Network Project Config and confirm all five are listed.");
    }

    //the three traps only differ by kind, radius and shape - everything else about them is identical
    private static GameObject BuildTrap(string name, GuardTrap.TrapKind kind, float triggerRadius, float disarmRange,
        System.Action<GameObject> visuals)
    {
        GameObject go = NewRoot(name, visuals);
        go.AddComponent<NetworkObject>();
        GuardTrap trap = go.AddComponent<GuardTrap>();

        SerializedObject so = new SerializedObject(trap);
        so.FindProperty("kind").enumValueIndex = (int)kind;
        so.FindProperty("triggerRadius").floatValue = triggerRadius;
        so.FindProperty("disarmRange").floatValue = disarmRange;
        so.ApplyModifiedPropertiesWithoutUndo();

        return SaveAndClean(go, name);
    }

    private static GameObject BuildSimple<T>(string name, System.Action<GameObject> visuals) where T : NetworkBehaviour
    {
        GameObject go = NewRoot(name, visuals);
        go.AddComponent<NetworkObject>();
        go.AddComponent<T>();
        return SaveAndClean(go, name);
    }

    private static GameObject NewRoot(string name, System.Action<GameObject> visuals)
    {
        GameObject go = new GameObject(name);
        GameObject visual = new GameObject("Visual");
        visual.transform.SetParent(go.transform, false);
        visuals(visual);
        return go;
    }

    //colliders come off every primitive on purpose. all three traps, the wedge and the jammer detect by DISTANCE
    //against replicated positions (trigger colliders never fire for remote players - see the note in GuardTrap), so a
    //collider here would contribute nothing except a physical lump for players to trip over and shove around.
    private static void Visual(GameObject parent, PrimitiveType shape, Vector3 pos, Vector3 scale, Color colour)
    {
        GameObject piece = GameObject.CreatePrimitive(shape);
        Object.DestroyImmediate(piece.GetComponent<Collider>());
        piece.transform.SetParent(parent.transform, false);
        piece.transform.localPosition = pos;
        piece.transform.localScale = scale;
        piece.GetComponent<MeshRenderer>().sharedMaterial = MaterialFor(colour);
        piece.isStatic = false;
    }

    private static Material MaterialFor(Color colour)
    {
        string path = $"{FolderPath}/PH_{ColorUtility.ToHtmlStringRGB(colour)}.mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        //URP Lit, because this project has no built-in pipeline - a Standard material would import magenta
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        Material mat = new Material(shader) { color = colour };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", colour);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    private static GameObject SaveAndClean(GameObject go, string name)
    {
        string path = $"{FolderPath}/{name}.prefab";
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        return saved;
    }

    //assigns by SERIALIZED PROPERTY NAME rather than by reflection on the field, so a rename shows up here as a clear
    //console warning naming the field instead of silently leaving it null the way the inspector does.
    private static void Wire(string prefabPath, params (string field, GameObject value)[] assignments)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
        {
            Debug.LogWarning($"[PlaceholderPrefabBuilder] Prefab not found: {prefabPath}");
            return;
        }

        //LoadPrefabContents opens the prefab in an isolated scene and SaveAsPrefabAsset writes it back. Editing the
        //asset returned by LoadAssetAtPath and hoping SetDirty sticks is the flaky version of this, and a change that
        //silently fails to save is exactly the failure mode this whole script exists to get rid of.
        GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            foreach (MonoBehaviour behaviour in contents.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                SerializedObject so = new SerializedObject(behaviour);
                bool touched = false;

                foreach ((string field, GameObject value) in assignments)
                {
                    SerializedProperty prop = so.FindProperty(field);
                    if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (value == null)
                    {
                        Debug.LogWarning($"[PlaceholderPrefabBuilder] No source asset for '{field}' - left unassigned.");
                        continue;
                    }

                    //the field is typed NetworkObject, not GameObject, so hand it the component
                    prop.objectReferenceValue = value.GetComponent<NetworkObject>();
                    touched = true;
                    Debug.Log($"[PlaceholderPrefabBuilder] {Path.GetFileNameWithoutExtension(prefabPath)}.{field} = {value.name}");
                }

                if (touched) so.ApplyModifiedPropertiesWithoutUndo();
            }

            PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }
}
