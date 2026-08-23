using UnityEngine;
using UnityEngine.UI;

// The glue between the settings panel and GameSettings. All this does is push slider values in and pull saved values
// back out - the actual storing and applying lives in GameSettings, so the panel can be rebuilt or restyled without
// touching any logic.
//
// Wiring: put this on the settings panel. Assign whichever controls you've built (all optional - a panel with only a
// sensitivity slider works fine). Then hook each control's event to the matching method below:
//   Sensitivity Slider  -> OnValueChanged (Dynamic float)  -> SetSensitivity
//   Master Slider       -> OnValueChanged (Dynamic float)  -> SetMasterVolume
//   Voice Slider        -> OnValueChanged (Dynamic float)  -> SetVoiceVolume
//   Invert Y Toggle     -> OnValueChanged (Dynamic bool)   -> SetInvertY
//   Reset Button        -> OnClick                         -> ResetToDefaults
public class SettingsMenu : MonoBehaviour
{
    [SerializeField] private Slider sensitivitySlider;
    [SerializeField] private Toggle invertYToggle;
    [SerializeField] private Slider masterVolumeSlider;
    [SerializeField] private Slider voiceVolumeSlider;
    //no camera motion slider on purpose - the feel is authored on the Player prefab, identical for everyone

    [Header("Optional readouts next to the sliders")]
    [SerializeField] private Text sensitivityValueText;
    [SerializeField] private Text masterVolumeValueText;
    [SerializeField] private Text voiceVolumeValueText;

    private bool applyingSavedValues; //true while WE'RE setting the controls, so their change events don't write straight back

    private void OnEnable()
    {
        PullFromSettings(); //the panel might have been built with placeholder values, or changed on another machine
    }

    //Push what's saved onto the controls. Guarded, because assigning Slider.value fires onValueChanged - without the
    //flag, opening the panel would write the slider's inspector default back over the player's real setting.
    private void PullFromSettings()
    {
        applyingSavedValues = true;

        if (sensitivitySlider != null)
        {
            sensitivitySlider.minValue = GameSettings.MinSensitivity;
            sensitivitySlider.maxValue = GameSettings.MaxSensitivity;
            sensitivitySlider.value = GameSettings.MouseSensitivity;
        }
        if (invertYToggle != null) invertYToggle.isOn = GameSettings.InvertLookY;
        if (masterVolumeSlider != null) masterVolumeSlider.value = GameSettings.MasterVolume;
        if (voiceVolumeSlider != null) voiceVolumeSlider.value = GameSettings.VoiceVolume;
        applyingSavedValues = false;
        RefreshLabels();
    }

    public void SetSensitivity(float value)
    {
        if (applyingSavedValues) return;
        GameSettings.MouseSensitivity = value;
        RefreshLabels();
    }

    public void SetInvertY(bool value)
    {
        if (applyingSavedValues) return;
        GameSettings.InvertLookY = value;
    }

    public void SetMasterVolume(float value)
    {
        if (applyingSavedValues) return;
        GameSettings.MasterVolume = value;
        RefreshLabels();
    }

    public void SetVoiceVolume(float value)
    {
        if (applyingSavedValues) return;
        GameSettings.VoiceVolume = value;
        RefreshLabels();
    }

    public void ResetToDefaults()
    {
        GameSettings.ResetToDefaults();
        PullFromSettings(); //put the controls back where the defaults say, rather than leaving them showing the old values
    }

    private void OnDisable()
    {
        GameSettings.Save(); //flush to disk when the panel closes, rather than trusting the game to exit cleanly
    }

    private void RefreshLabels()
    {
        if (sensitivityValueText != null) sensitivityValueText.text = GameSettings.MouseSensitivity.ToString("0.00");
        if (masterVolumeValueText != null) masterVolumeValueText.text = Mathf.RoundToInt(GameSettings.MasterVolume * 100f) + "%";
        if (voiceVolumeValueText != null) voiceVolumeValueText.text = Mathf.RoundToInt(GameSettings.VoiceVolume * 100f) + "%";
    }
}
