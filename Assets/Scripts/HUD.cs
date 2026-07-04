using UnityEngine;
using UnityEngine.UI;

public class HUD : MonoBehaviour
{
    [SerializeField] private Image staminaImage;
    [SerializeField] private Image suffocationFade; //fullscreen black image; alpha ramps as the local player suffocates, full black on death

    private void Update()
    {
        if (Player.LocalPlayer != null)
        {
            staminaImage.fillAmount = Player.LocalPlayer.staminaNormalized;

            if (suffocationFade != null)
            {
                Color fadeColor = suffocationFade.color;
                fadeColor.a = Player.LocalPlayer.ScreenFade; //0 normal -> 1 blacked out
                suffocationFade.color = fadeColor;
            }
        }
    }

}
