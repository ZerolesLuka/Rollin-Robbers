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
    [SerializeField] private Text timerText;        //live heist clock, mm:ss (optional - leave unassigned to hide it)
    [SerializeField] private Text[] inventorySlotTexts; //4 slot labels; each shows the held item's name or empty
    [SerializeField] private Color emptySlotColor = new Color(0.5f, 0.5f, 0.5f, 0.5f); //dim tint for an empty inventory slot

    [SerializeField] private Text interactPromptText; //"E  Open the door" etc, near the crosshair. optional - leave unassigned to hide it. the text comes from Player.InteractPrompt, which is derived from the SAME scan that E acts on, so it can't lie

    [Header("Safe cracking - shown only while this player is holding a safe open")]
    [SerializeField] private GameObject crackPanel;  //parent of the meter; toggled on/off. optional
    [SerializeField] private Image crackFillImage;   //set Image Type to Filled - fillAmount is driven from the safe's networked CrackProgress
    [SerializeField] private Text crackText;         //optional percentage readout

    [SerializeField] private GameObject caughtPanel;    //shown when the whole team gets caught
    [SerializeField] private CanvasGroup caughtCanvasGroup; //on the same panel - drives the fade
    [SerializeField] private Image caughtBackground;    //the panel's background image - flashes red then settles dark
    [SerializeField] private Text caughtText;
    [SerializeField] private float caughtFadeDuration = 1.5f;
    [SerializeField] private float caughtHoldDuration = 3.5f; //how long "run failed" stays up before clearing itself - the run is still Caught, we just can't leave the panel covering the van computer you need to press next
    [SerializeField] private Color caughtFlashColor = new Color(0.6f, 0f, 0f, 0.85f); //punchy red flash on impact
    [SerializeField] private Color caughtRestColor = new Color(0f, 0f, 0f, 0.85f);    //settles to near-black

    [SerializeField] private GameObject successPanel;       //shown at the van when the crew gets away clean
    [SerializeField] private CanvasGroup successCanvasGroup;//fades the panel in
    [SerializeField] private Image successBackground;       //panel backdrop
    [SerializeField] private Text successText;              //the headline
    [SerializeField] private Text successDetailText;        //payout breakdown lines
    [SerializeField] private float successFadeDuration = 1.2f;
    [SerializeField] private float successHoldDuration = 5f; //payout screen stays up longer than the fail one - there are actual numbers to read - then clears so the van computer is reachable
    [SerializeField] private Color successColor = new Color(0.05f, 0.2f, 0.08f, 0.9f); //deep green for a clean getaway

    private bool wasCaught;
    private Coroutine caughtRoutine;
    private bool wasSuccess;
    private Coroutine successRoutine;

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

        if (timerText != null && RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            float runSeconds = RunManager.Instance.RunTime;
            int minutes = (int)(runSeconds / 60f);
            int seconds = (int)(runSeconds % 60f);
            timerText.text = $"{minutes:00}:{seconds:00}";
        }

        if (inventorySlotTexts != null && Player.LocalPlayer != null && Player.LocalPlayer.Object != null && Player.LocalPlayer.Object.IsValid)
        {
            IReadOnlyList<InventoryItem> inventory = Player.LocalPlayer.Inventory;
            for (int slot = 0; slot < inventorySlotTexts.Length; slot++)
            {
                if (inventorySlotTexts[slot] == null)
                {
                    continue;
                }
                if (slot < inventory.Count)
                {
                    InventoryItem carried = inventory[slot];
                    inventorySlotTexts[slot].text = $"{carried.name} (${carried.value})";
                    inventorySlotTexts[slot].color = LootRarityTable.ColorFor(carried.value); //tint the slot by how valuable the item is - orange = jackpot
                }
                else
                {
                    inventorySlotTexts[slot].text = "";
                    inventorySlotTexts[slot].color = emptySlotColor;
                }
            }
        }

        bool localPlayerLive = Player.LocalPlayer != null && Player.LocalPlayer.Object != null && Player.LocalPlayer.Object.IsValid;

        if (interactPromptText != null)
        {
            interactPromptText.text = localPlayerLive ? Player.LocalPlayer.InteractPrompt : "";
        }

        //safe meter: the player publishes WHICH safe they're holding (networked CrackingSafeId), we look it up and
        //read its shared CrackProgress. that means a teammate tagging in on the same safe moves YOUR bar too - the
        //meter belongs to the safe, not to whoever happens to be standing there.
        bool showCrackMeter = false;
        if (localPlayerLive)
        {
            Safe crackingSafe = Safe.FindById(Player.LocalPlayer.CrackingSafeId);
            if (crackingSafe != null && !crackingSafe.IsOpen)
            {
                showCrackMeter = true;
                if (crackFillImage != null) crackFillImage.fillAmount = crackingSafe.CrackProgress;
                if (crackText != null) crackText.text = $"Cracking  {Mathf.RoundToInt(crackingSafe.CrackProgress * 100f)}%";
            }
        }
        if (crackPanel != null && crackPanel.activeSelf != showCrackMeter)
        {
            crackPanel.SetActive(showCrackMeter);
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

        bool success = RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid
            && RunManager.Instance.State == RunManager.RunState.Success;

        if (success && !wasSuccess) //rising edge - play the payout summary once when the run is won
        {
            if (successRoutine != null)
            {
                StopCoroutine(successRoutine);
            }
            successRoutine = StartCoroutine(PlaySuccessSummary());
        }
        else if (!success && wasSuccess && successPanel != null)
        {
            successPanel.SetActive(false); //next run started - clear the summary
        }
        wasSuccess = success;
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

        //let the message land, then clear it. the run STAYS in the Caught state (that's what the van's House
        //button resets) - this only takes the panel off the screen, because otherwise it sits over the van
        //computer you have to walk up to and press to pick pawn shop or the next house.
        yield return new WaitForSeconds(caughtHoldDuration);

        float fadeOutTimer = 0f;
        while (fadeOutTimer < caughtFadeDuration)
        {
            fadeOutTimer += Time.deltaTime;
            float fadingOut = 1f - Mathf.SmoothStep(0f, 1f, fadeOutTimer / caughtFadeDuration);
            if (caughtCanvasGroup != null) caughtCanvasGroup.alpha = fadingOut;
            yield return null;
        }

        if (caughtCanvasGroup != null) caughtCanvasGroup.alpha = 0f;
        if (caughtPanel != null) caughtPanel.SetActive(false); //cleared - the van computer is usable again
    }

    private IEnumerator PlaySuccessSummary() //the getaway payout screen - grades how much of the house the crew cleared this run
    {
        if (successPanel != null)
        {
            successPanel.SetActive(true);
        }

        int stolen = 0;
        int houseTotal = 0;
        int money = 0;
        int best = 0;
        if (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        {
            stolen = RunManager.Instance.GatheredLootValue;
            houseTotal = RunManager.Instance.HouseLootTotal;
            money = RunManager.Instance.Money;
            best = RunManager.Instance.BestClearPercent;
        }
        int percentCleared = houseTotal > 0 ? Mathf.RoundToInt(100f * stolen / houseTotal) : 0;

        if (successText != null)
        {
            successText.text = "You got away.";
        }
        if (successDetailText != null)
        {
            successDetailText.text = $"${stolen} lifted of ${houseTotal}\n{percentCleared}% cleared - Grade {GradeFor(percentCleared)}\nBank: ${money}    Best run: {best}%";
        }

        if (successCanvasGroup != null)
        {
            successCanvasGroup.alpha = 0f;
        }
        if (successBackground != null)
        {
            successBackground.color = successColor;
        }

        Vector3 punchScale = Vector3.one * 1.25f;
        Vector3 restScale = Vector3.one;
        if (successText != null)
        {
            successText.transform.localScale = punchScale;
        }

        float timer = 0f;
        while (timer < successFadeDuration)
        {
            timer += Time.deltaTime;
            float t = timer / successFadeDuration;
            float eased = Mathf.SmoothStep(0f, 1f, t); //smooth ease in, matches the caught panel's feel
            if (successCanvasGroup != null)
            {
                successCanvasGroup.alpha = eased;
            }
            if (successText != null)
            {
                successText.transform.localScale = Vector3.Lerp(punchScale, restScale, eased); //headline punches in and settles
            }
            yield return null;
        }

        if (successCanvasGroup != null)
        {
            successCanvasGroup.alpha = 1f;
        }
        if (successText != null)
        {
            successText.transform.localScale = restScale;
        }

        //same deal as the caught panel - hold it long enough to read the payout, then get it off the screen
        //so the crew can actually reach the van computer and route to the pawn shop or the next house
        yield return new WaitForSeconds(successHoldDuration);

        float fadeOutTimer = 0f;
        while (fadeOutTimer < successFadeDuration)
        {
            fadeOutTimer += Time.deltaTime;
            float fadingOut = 1f - Mathf.SmoothStep(0f, 1f, fadeOutTimer / successFadeDuration);
            if (successCanvasGroup != null)
            {
                successCanvasGroup.alpha = fadingOut;
            }
            yield return null;
        }

        if (successCanvasGroup != null)
        {
            successCanvasGroup.alpha = 0f;
        }
        if (successPanel != null)
        {
            successPanel.SetActive(false); //cleared - van computer reachable again
        }
    }

    private string GradeFor(int percentCleared) //letter grade off the percentage of the house you emptied
    {
        if (percentCleared >= 90)
        {
            return "S";
        }
        if (percentCleared >= 75)
        {
            return "A";
        }
        if (percentCleared >= 50)
        {
            return "B";
        }
        if (percentCleared >= 25)
        {
            return "C";
        }
        return "D";
    }
}
