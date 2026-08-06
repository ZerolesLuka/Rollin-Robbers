using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Builds the ten HUD elements that exist in code and nowhere on screen, into the Canvas that's already in the scene.
//
// WHY: HUD.cs reads fifteen serialized fields and ten of them are empty in Indoor - money, timer, the four inventory
// slots, the status line, the crack meter and the success panel. Every one of those is a system that already works
// and simply can't be seen, and "can't be seen" is indistinguishable from "broken" the first time you play. The rest
// of the bugs in this project are going to be found by playing it, so being able to read what it's doing is worth
// more right now than any further code auditing.
//
// Ugly on purpose, same bargain as the OnGUI shop and the safe keypad: real layout later, legibility today. Replacing
// any of it means deleting the object and assigning your own - nothing here is referenced by name at runtime.
//
// RUN IT PER SCENE. It works on whatever scene is open (Indoor and Outdoor both have a HUD), skips anything already
// assigned, and never touches a field you have wired yourself.
public static class HudCanvasBuilder
{
    [MenuItem("Tools/Rollin' Robbers/Build HUD Canvas (current scene)")]
    public static void Build()
    {
        HUD hud = Object.FindAnyObjectByType<HUD>(FindObjectsInactive.Include);
        if (hud == null)
        {
            Debug.LogError("[HudCanvasBuilder] No HUD in this scene. Open Indoor or Outdoor and run it again.");
            return;
        }

        //explicit null check rather than ??, which bypasses Unity's overloaded == and can hand back a destroyed object
        Canvas canvas = hud.GetComponentInParent<Canvas>();
        if (canvas == null) canvas = Object.FindAnyObjectByType<Canvas>(FindObjectsInactive.Include);
        if (canvas == null)
        {
            Debug.LogError("[HudCanvasBuilder] Found the HUD but no Canvas above it.");
            return;
        }

        SerializedObject so = new SerializedObject(hud);
        int built = 0;

        //---- top left: money under the loot line that's already there ----
        built += Fill(so, "moneyText", () => Label(canvas, "Money", new Vector2(0f, 1f), new Vector2(20f, -54f), 320f, 30f, 22, TextAnchor.UpperLeft).gameObject);

        //---- top centre: the heist clock ----
        built += Fill(so, "timerText", () => Label(canvas, "Timer", new Vector2(0.5f, 1f), new Vector2(-90f, -20f), 180f, 34f, 26, TextAnchor.UpperCenter).gameObject);

        //---- bottom left: four loot slots, stacked upward so slot 0 sits lowest ----
        SerializedProperty slots = so.FindProperty("inventorySlotTexts");
        if (slots != null && slots.arraySize == 0)
        {
            slots.arraySize = 4;
            for (int i = 0; i < 4; i++)
            {
                Text slot = Label(canvas, $"Slot {i}", new Vector2(0f, 0f), new Vector2(20f, 24f + i * 26f), 380f, 24f, 18, TextAnchor.LowerLeft);
                slots.GetArrayElementAtIndex(i).objectReferenceValue = slot;
            }
            built += 4;
        }

        //---- bottom centre: the status line. Root is a dim strip so text stays readable over a lit room ----
        GameObject statusRoot = null;
        built += Fill(so, "statusRoot", () =>
        {
            statusRoot = Panel(canvas, "Status Root", new Vector2(0.5f, 0f), new Vector2(-330f, 96f), 660f, 40f, new Color(0f, 0f, 0f, 0.55f));
            return statusRoot;
        });
        built += Fill(so, "statusText", () =>
        {
            Transform parent = statusRoot != null ? statusRoot.transform : canvas.transform;
            return Label(parent, "Status Text", new Vector2(0.5f, 0.5f), new Vector2(-320f, -14f), 640f, 28f, 20, TextAnchor.MiddleCenter).gameObject;
        });

        //---- centre: the crack meter, hidden until you're actually on a safe ----
        GameObject crackPanel = null;
        built += Fill(so, "crackPanel", () =>
        {
            crackPanel = Panel(canvas, "Crack Panel", new Vector2(0.5f, 0.5f), new Vector2(-160f, -120f), 320f, 56f, new Color(0f, 0f, 0f, 0.6f));
            crackPanel.SetActive(false); //HUD turns it on; leaving it on would put a meter over the menu
            return crackPanel;
        });
        built += Fill(so, "crackFillImage", () =>
        {
            Transform parent = crackPanel != null ? crackPanel.transform : canvas.transform;
            GameObject fill = MakeRect(parent, "Crack Fill", new Vector2(0.5f, 0.5f), new Vector2(-140f, -22f), 280f, 18f);
            Image image = fill.AddComponent<Image>();
            image.color = new Color(0.95f, 0.72f, 0.2f);
            //FILLED is the whole point - HUD drives fillAmount off the safe's networked CrackProgress. A Simple image
            //would sit there at full width looking like a finished bar from the moment it appears.
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillAmount = 0f;
            return fill;
        });
        built += Fill(so, "crackText", () =>
        {
            Transform parent = crackPanel != null ? crackPanel.transform : canvas.transform;
            return Label(parent, "Crack Text", new Vector2(0.5f, 0.5f), new Vector2(-140f, 2f), 280f, 22f, 18, TextAnchor.MiddleCenter).gameObject;
        });

        //---- fullscreen: the payout screen. caughtPanel is already wired, this is its missing twin ----
        GameObject successPanel = null;
        built += Fill(so, "successPanel", () =>
        {
            successPanel = Panel(canvas, "Success Panel", new Vector2(0.5f, 0.5f), new Vector2(-400f, -220f), 800f, 440f, new Color(0.05f, 0.2f, 0.08f, 0.9f));
            successPanel.SetActive(false);
            return successPanel;
        });
        if (successPanel != null)
        {
            built += Fill(so, "successCanvasGroup", () => successPanel.AddComponent<CanvasGroup>().gameObject);
            built += Fill(so, "successBackground", () => successPanel);
            built += Fill(so, "successText", () => Label(successPanel.transform, "Success Headline", new Vector2(0.5f, 1f), new Vector2(-380f, -80f), 760f, 50f, 38, TextAnchor.UpperCenter).gameObject);
            built += Fill(so, "successDetailText", () => Label(successPanel.transform, "Success Detail", new Vector2(0.5f, 0.5f), new Vector2(-380f, -110f), 760f, 220f, 22, TextAnchor.UpperCenter).gameObject);
        }

        //interactPromptText is DELIBERATELY left alone. WorldInteractPrompt already draws the label on the object it
        //describes, and wiring this one too shows the same line twice - once in the world, once at the crosshair.

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(hud);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        Debug.Log($"[HudCanvasBuilder] Built and assigned {built} element(s) on '{SceneManager.GetActiveScene().name}'. " +
                  "Anything already wired was left untouched. Save the scene.");
    }

    //only fills a field that is genuinely empty, so re-running this is safe and hand-wired elements survive
    private static int Fill(SerializedObject so, string field, System.Func<GameObject> make)
    {
        SerializedProperty prop = so.FindProperty(field);
        if (prop == null)
        {
            Debug.LogWarning($"[HudCanvasBuilder] HUD has no field called '{field}' any more - rename it here too.");
            return 0;
        }
        if (prop.objectReferenceValue != null) return 0; //already yours, don't touch it

        GameObject made = make();
        if (made == null) return 0;

        //the field might be typed GameObject, Text, Image or CanvasGroup - hand it whichever the property wants
        System.Type wanted = TypeOf(prop);
        prop.objectReferenceValue = wanted == typeof(GameObject) ? made : (Object)made.GetComponent(wanted);
        return 1;
    }

    private static System.Type TypeOf(SerializedProperty prop)
    {
        string name = prop.type; //arrives as "PPtr<$Text>"
        if (name.Contains("Text")) return typeof(Text);
        if (name.Contains("CanvasGroup")) return typeof(CanvasGroup);
        if (name.Contains("Image")) return typeof(Image);
        return typeof(GameObject);
    }

    private static GameObject MakeRect(Transform parent, string name, Vector2 anchor, Vector2 offset, float width, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0f, 0f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = offset;
        return go;
    }

    private static GameObject MakeRect(Canvas canvas, string name, Vector2 anchor, Vector2 offset, float width, float height)
        => MakeRect(canvas.transform, name, anchor, offset, width, height);

    private static GameObject Panel(Canvas canvas, string name, Vector2 anchor, Vector2 offset, float width, float height, Color colour)
    {
        GameObject go = MakeRect(canvas, name, anchor, offset, width, height);
        go.AddComponent<Image>().color = colour;
        return go;
    }

    private static Text Label(Transform parent, string name, Vector2 anchor, Vector2 offset, float width, float height, int size, TextAnchor align)
    {
        GameObject go = MakeRect(parent, name, anchor, offset, width, height);
        Text text = go.AddComponent<Text>();
        text.font = BuiltinFont();
        text.fontSize = size;
        text.alignment = align;
        text.color = Color.white;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false; //HUD text should never eat a click meant for the shop menus underneath
        return text;
    }

    private static Text Label(Canvas canvas, string name, Vector2 anchor, Vector2 offset, float width, float height, int size, TextAnchor align)
        => Label(canvas.transform, name, anchor, offset, width, height, size, align);

    //Arial.ttf was removed from Unity's builtin resources; LegacyRuntime.ttf replaced it. Try the modern name first
    //so this doesn't silently produce invisible text on Unity 6.
    private static Font BuiltinFont()
    {
        Font modern = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (modern != null) return modern;
        return Resources.GetBuiltinResource<Font>("Arial.ttf");
    }
}
