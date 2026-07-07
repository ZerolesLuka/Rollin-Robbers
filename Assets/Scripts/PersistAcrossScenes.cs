using UnityEngine;

public class PersistAcrossScenes : MonoBehaviour
{
    private void Awake() => DontDestroyOnLoad(gameObject);
}
