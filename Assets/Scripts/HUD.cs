using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class HUD : MonoBehaviour
{
    [SerializeField] private Image staminaImage;
    [SerializeField] private Image suffocationFade; //fullscreen black image; alpha ramps as the local player suffocates, full black on death
    [SerializeField] private Text lootText;         //shows team's gathered loot value; wire up a UI Text in the Inspector
    [SerializeField] private Text moneyText;        //shows the team's banked cash
    [SerializeField] private Text[] inventorySlotTexts; //4 slot labels; each shows the held item's name or empty

    [SerializeField] private GameObject caughtPanel;    //shown when the whole team gets caught
    [SerializeField] private CanvasGroup caughtCanvasGroup; //on the same panel - drives the fade
    [SerializeField] private Image caughtBackground;    //the panel's background image - flashes red then settles dark
    [SerializeField] private Text caughtText;
    [SerializeField] private float caughtFadeDuration = 1.5f;
    [SerializeField] private Color caughtFlashColor = new Color(0.6f, 0f, 0f, 0.85f); //punchy red flash on impact
    [SerializeField] private Color caughtRestColor = new Color(0f, 0f, 0f, 0.85f);    //settles to near-black

    private bool wasCaught;
    private Coroutine caughtRoutine;

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

        if (lootText != null && Player.LocalPlayer != null && Player.LocalPlayer.Object != null && Player.LocalPlayer.Object.IsValid)
        {
            lootText.text = $"Carrying: ${Player.LocalPlayer.CarriedValue}"; //what you're holding, worth-wise - banked when you sell
        }

        if (moneyText != null && RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            moneyText.text = $"Money: ${RunManager.Instance.Money}";
        }

        if (inventorySlotTexts != null && Player.LocalPlayer != null && Player.LocalPlayer.Object != null && Player.LocalPlayer.Object.IsValid)
        {
            IReadOnlyList<InventoryItem> inventory = Player.LocalPlayer.Inventory;
            for (int slot = 0; slot < inventorySlotTexts.Length; slot++)
            {
                if (inventorySlotTexts[slot] == null) continue;
                inventorySlotTexts[slot].text = slot < inventory.Count ? $"{inventory[slot].name} (${inventory[slot].value})" : "";
            }
        }

        bool caught = RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid
            && RunManager.Instance.State == RunManager.RunState.Caught;

        if (caught && !wasCaught) //rising edge - the moment the run fails, play the impact once
        {
            if (caughtRoutine != null) StopCoroutine(caughtRoutine);
            caughtRoutine = StartCoroutine(PlayCaughtImpact());
        }
        else if (!caught && wasCaught && caughtPanel != null)
        {
            caughtPanel.SetActive(false); //reset instantly if the run somehow resets
        }
        wasCaught = caught;
    }

    private IEnumerator PlayCaughtImpact()
    {
        if (caughtPanel != null) caughtPanel.SetActive(true);
        if (caughtText != null) caughtText.text = "Everyone was caught. Run failed.";

        if (caughtCanvasGroup != null) caughtCanvasGroup.alpha = 0f;
        if (caughtBackground != null) caughtBackground.color = caughtFlashColor; //hit of red the instant it triggers

        Vector3 punchScale = Vector3.one * 1.4f;
        Vector3 restScale = Vector3.one;
        if (caughtText != null) caughtText.transform.localScale = punchScale;

        float timer = 0f;
        while (timer < caughtFadeDuration)
        {
            timer += Time.deltaTime;
            float t = timer / caughtFadeDuration;
            float eased = Mathf.SmoothStep(0f, 1f, t); //smooth ease instead of linear, feels less mechanical

            if (caughtCanvasGroup != null) caughtCanvasGroup.alpha = eased;
            if (caughtBackground != null) caughtBackground.color = Color.Lerp(caughtFlashColor, caughtRestColor, eased); //red flash cools into a dark vignette
            if (caughtText != null) caughtText.transform.localScale = Vector3.Lerp(punchScale, restScale, eased); //text punches in and settles

            yield return null;
        }

        if (caughtCanvasGroup != null) caughtCanvasGroup.alpha = 1f;
        if (caughtBackground != null) caughtBackground.color = caughtRestColor;
        if (caughtText != null) caughtText.transform.localScale = restScale;
    }
}
