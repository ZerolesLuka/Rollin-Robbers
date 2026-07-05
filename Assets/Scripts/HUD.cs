using UnityEngine;
using UnityEngine.UI;

public class HUD : MonoBehaviour
{
    [SerializeField] private Image staminaImage;
    [SerializeField] private Image suffocationFade; //fullscreen black image; alpha ramps as the local player suffocates, full black on death
    [SerializeField] private Text lootText;         //shows team's gathered loot value; wire up a UI Text in the Inspector

    private void Update()
    {
        if (Player.LocalPlayer != null && Player.LocalPlayer.Object != null && Player.LocalPlayer.Object.IsValid)
        {
            staminaImage.fillAmount = Player.LocalPlayer.staminaNormalized;

            if (suffocationFade != null)
            {
                Color fadeColor = suffocationFade.color;
                fadeColor.a = Player.LocalPlayer.ScreenFade; //0 normal -> 1 blacked out
                suffocationFade.color = fadeColor;
            }
        }

        if (lootText != null && RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            lootText.text = $"Loot: ${RunManager.Instance.GatheredLootValue}";
        }
    }

}
