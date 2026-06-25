using UnityEngine;
using UnityEngine.UI;

public class HUD : MonoBehaviour
{
    [SerializeField] private Image staminaImage; 

    private void Update()
    {
        if (Player.LocalPlayer != null)
        {
            staminaImage.fillAmount = Player.LocalPlayer.staminaNormalized;
        }
    }

}
