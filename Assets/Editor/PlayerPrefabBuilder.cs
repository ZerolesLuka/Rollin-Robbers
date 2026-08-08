using UnityEditor;
using UnityEngine;

// Builds the pieces of the Player prefab that exist as fields in code and as nothing in the scene. Right now that's
// the spectator camera; the pause menu, nameplate and world-prompt canvases belong here too when they're wanted.
//
// WHY THE SPECTATOR CAMERA FIRST: getting caught currently leaves you staring at your own frozen body for whatever
// is left of the heist, which in a 2-4 player game is most of a session watching nothing. Player.Spectate.cs is
// finished and correct - it orbits a living teammate, left click cycles the crew - and the entire reason none of it
// happens is that `spectatorCamera` is null, in which case UpdateSpectator returns on its first line and says
// nothing about it.
//
// Re-running is safe: anything already assigned is left exactly as it is.
public static class PlayerPrefabBuilder
{
    private const string PlayerPrefabPath = "Assets/Resources/Player.prefab";
    private const string SpectatorName = "Spectator Camera";
    private const string EnvironmentLayer = "Enviorment"; //spelled this way project-wide - see AudioOcclusion and GuardVision

    [MenuItem("Tools/Rollin' Robbers/Build Spectator Camera")]
    public static void BuildSpectatorCamera()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null)
        {
            Debug.LogError($"[PlayerPrefabBuilder] Player prefab not found at {PlayerPrefabPath}.");
            return;
        }

        GameObject contents = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            //GetComponentInChildren, not GetComponent - don't assume Player sits on the prefab root
            Player player = contents.GetComponentInChildren<Player>(true);
            if (player == null)
            {
                Debug.LogError("[PlayerPrefabBuilder] No Player component anywhere on the Player prefab.");
                return;
            }

            SerializedObject so = new SerializedObject(player);
            SerializedProperty cameraProp = so.FindProperty("spectatorCamera");
            SerializedProperty maskProp = so.FindProperty("spectateWallMask");

            if (cameraProp == null)
            {
                Debug.LogError("[PlayerPrefabBuilder] Player has no 'spectatorCamera' field any more - rename it here too.");
                return;
            }

            if (cameraProp.objectReferenceValue != null)
            {
                Debug.Log("[PlayerPrefabBuilder] spectatorCamera is already assigned - left alone.");
            }
            else
            {
                Camera spectator = MakeSpectatorCamera(contents);
                cameraProp.objectReferenceValue = spectator;
                Debug.Log($"[PlayerPrefabBuilder] Created '{SpectatorName}' and assigned it to Player.spectatorCamera.");
            }

            //A mask of 0 hits nothing, so the Linecast that keeps the camera out of walls would never fire and you'd
            //spend most of your time spectating the inside of a wall. Same layer the guard's vision blocks on, so
            //what stops the camera is exactly what stops him seeing.
            if (maskProp != null && maskProp.intValue == 0)
            {
                int layer = LayerMask.NameToLayer(EnvironmentLayer);
                if (layer < 0)
                {
                    Debug.LogWarning($"[PlayerPrefabBuilder] No layer called '{EnvironmentLayer}' - spectateWallMask left empty, so the camera will clip through walls.");
                }
                else
                {
                    maskProp.intValue = 1 << layer;
                    Debug.Log($"[PlayerPrefabBuilder] spectateWallMask set to '{EnvironmentLayer}' (layer {layer}).");
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(contents, PlayerPrefabPath);
            Debug.Log("[PlayerPrefabBuilder] Done. F1 > 'send him to me', let him catch you, and you should orbit a teammate - left click cycles the crew.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    private static Camera MakeSpectatorCamera(GameObject playerRoot)
    {
        GameObject go = new GameObject(SpectatorName);
        go.transform.SetParent(playerRoot.transform, false);

        Camera spectator = go.AddComponent<Camera>();

        //Copy the first-person camera's settings rather than inventing them, so switching between the two doesn't
        //change the field of view or what's visible - it should feel like the same game from a different seat.
        Camera existing = FindFirstPersonCamera(playerRoot);
        if (existing != null)
        {
            spectator.fieldOfView = existing.fieldOfView;
            spectator.nearClipPlane = existing.nearClipPlane;
            spectator.farClipPlane = existing.farClipPlane;
            spectator.cullingMask = existing.cullingMask;
            spectator.clearFlags = existing.clearFlags;
            spectator.backgroundColor = existing.backgroundColor;
            spectator.depth = existing.depth;
        }

        //NO AudioListener on purpose. Unity allows exactly one active listener and the first-person camera already
        //owns it; a second would spam warnings the moment this switched on, and spectating from a teammate's ears
        //rather than your own body is a bigger design decision than this script should be making quietly.

        //OFF at both levels. SetSpectatorCameraActive toggles the GameObject AND the component, because "disabled in
        //the inspector" means either one depending on who set it up - and a live second camera on the player prefab
        //fights the first-person one for the screen from the moment you spawn.
        spectator.enabled = false;
        go.SetActive(false);

        return spectator;
    }

    //The player's own view camera, found the same way the game finds it: a Camera in the children that isn't the one
    //we just made. Never Camera.main - that resolves to whatever is tagged MainCamera in the loaded scene, which
    //while editing a prefab in isolation is either nothing or something else entirely.
    private static Camera FindFirstPersonCamera(GameObject playerRoot)
    {
        foreach (Camera candidate in playerRoot.GetComponentsInChildren<Camera>(true))
        {
            if (candidate.gameObject.name != SpectatorName) return candidate;
        }
        return null;
    }
}
