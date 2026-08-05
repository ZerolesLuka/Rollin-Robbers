using UnityEngine;
using UnityEngine.UI;
using Fusion;

// Silent co-op comms: press a signal key and a label pops above your head for everyone to see. This is the safe
// counterpart to voice - talking gives you away to the guard's mic-hearing, so signals let the crew coordinate
// without making a sound. Input is read only on the owner; the RPC targets All so every client sees the pop-up.
public class PlayerSignals : NetworkBehaviour
{
    private Player player;

    [SerializeField] private GameObject signalRoot;     //world-space canvas above the head; starts hidden, shown while signaling
    [SerializeField] private Text signalText;           //the label shown for the current signal
    [SerializeField] private float signalDuration = 2f; //how long a signal stays up before fading out
    [SerializeField] private string[] signalLabels = { "Go", "Wait", "Loot", "Point", "Stop", "!" }; //one per signal id (1-6) - edit these, or swap the Text for an Image + sprites later for real icons

    private float signalHideTimer;

    public override void Spawned()
    {
        player = GetComponent<Player>();
        if (signalRoot != null)
        {
            signalRoot.SetActive(false); //hidden until someone signals
        }
    }

    private void Update()
    {
        //KeyboardIsCaptured matters most for the SAFE KEYPAD: signals live on 1-6 and the keypad reads the number row,
        //so typing a four-digit code fired up to four signals - "Go", "Wait", "Loot" - popping over the head of
        //someone crouched at a safe trying to be invisible. Silent comms that announce you every time you crack a
        //safe are worse than no comms. It also covers the pause menu, both shop counters and the van computer.
        if (HasInputAuthority && player.playerInputActions != null && !player.KeyboardIsCaptured
            && !player.IsHiding && !player.IsEliminated && !player.IsLockedUp) //only our own ACTIVE player reads the keys - no signalling while hidden, jailed, or out (would broadcast your position)
        {
            ReadSignalInput();
        }

        //display side runs on ALL clients so everyone sees this player's signal; billboard it so it's readable from any angle
        if (signalRoot != null && signalRoot.activeSelf)
        {
            signalHideTimer -= Time.deltaTime;
            if (signalHideTimer <= 0f)
            {
                signalRoot.SetActive(false);
            }
            else
            {
                BillboardToViewer();
            }
        }
    }

    private void ReadSignalInput()
    {
        if (player.playerInputActions.Player.Signal1.WasPressedThisFrame()) { RPC_ShowSignal(1); }
        else if (player.playerInputActions.Player.Signal2.WasPressedThisFrame()) { RPC_ShowSignal(2); }
        else if (player.playerInputActions.Player.Signal3.WasPressedThisFrame()) { RPC_ShowSignal(3); }
        else if (player.playerInputActions.Player.Signal4.WasPressedThisFrame()) { RPC_ShowSignal(4); } //point
        else if (player.playerInputActions.Player.Signal5.WasPressedThisFrame()) { RPC_ShowSignal(5); } //stop
        else if (player.playerInputActions.Player.Signal6.WasPressedThisFrame()) { RPC_ShowSignal(6); } //middle finger
    }

    private void BillboardToViewer() //face the label at the LOCAL viewer's camera (each client its own), upright and readable
    {
        Camera viewerCamera = Player.LocalPlayer != null ? Player.LocalPlayer.ViewCamera : Camera.main;
        if (viewerCamera == null)
        {
            return;
        }
        //flip 180 around up: matching the camera's rotation directly points the canvas's readable face AWAY from the viewer (mirrored text), so we spin it to face them
        signalRoot.transform.rotation = viewerCamera.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.All)] //I press it, everyone runs this so every client sees my signal pop up
    private void RPC_ShowSignal(int id)
    {
        if (signalText != null && signalLabels != null && id >= 1 && id <= signalLabels.Length)
        {
            signalText.text = signalLabels[id - 1];
        }
        if (signalRoot != null)
        {
            signalRoot.SetActive(true);
        }
        signalHideTimer = signalDuration;
    }
}
