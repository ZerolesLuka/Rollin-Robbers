using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls; //KeyControl, for reading raw number keys

// Player - the safe keypad. TAP E at a safe and this comes up; type the 4-digit code off the note and the safe pops
// instantly and silently. HOLD E instead and you brute-force the dial (see HandleCracking) - slow and loud.
//
// This is deliberately LOCAL-ONLY until you hit four digits. The code travels between players over VOICE ("it's
// four-seven-one-eight") and only enters the game as typed digits, so nothing here needs networking except the final
// answer. Typing does NOT freeze you - you're stood in the open fiddling with a keypad, which is the point.
public partial class Player
{
    private bool isEnteringSafeCode;
    private Safe keypadSafe;              // which safe we're typing at
    private string typedDigits = "";
    private float codeRejectedFlashTimer; // >0 = show the "wrong" flash
    //Codes we've read, keyed by which safe they open. A single int meant reading a second note silently threw the
    //first away - fine while a house has one safe, silently destructive the moment it has two, and the player would
    //have had no idea it happened. Local on purpose: teammates don't learn it, you read it out.
    private readonly Dictionary<int, int> knownSafeCodes = new Dictionary<int, int>();

    private const int SafeCodeLength = 4;

    private void OpenSafeKeypad(Safe safe)
    {
        isEnteringSafeCode = true;
        keypadSafe = safe;
        typedDigits = "";
        codeRejectedFlashTimer = 0f;
    }

    private void CloseSafeKeypad()
    {
        isEnteringSafeCode = false;
        keypadSafe = null;
        typedDigits = "";
    }

    public void OnSafeCodeRejected() //called back by the safe when the number was wrong
    {
        codeRejectedFlashTimer = 1f;
        typedDigits = "";
    }

    public void LearnSafeCode(int safeId, int code) //read a note - the number stays on OUR hud for the rest of the run
    {
        if (code <= 0 || safeId == Safe.NoSafe)
        {
            return; //the safe this note belonged to is gone
        }
        knownSafeCodes[safeId] = code; //local only on purpose. teammates don't magically learn it; you have to say it out loud
    }

    private void UpdateSafeKeypad() //called from Update, local player only
    {
        if (codeRejectedFlashTimer > 0f)
        {
            codeRejectedFlashTimer -= Time.deltaTime;
        }

        if (!isEnteringSafeCode)
        {
            return;
        }

        //walked away from it, or someone else opened it while we were typing - drop the keypad
        if (keypadSafe == null || keypadSafe.IsOpen
            || Vector3.Distance(transform.position, keypadSafe.transform.position) > keypadSafe.CrackRange + 0.5f)
        {
            CloseSafeKeypad();
            return;
        }

        if (Keyboard.current == null)
        {
            return;
        }

        if (Keyboard.current.escapeKey.wasPressedThisFrame || Keyboard.current.backquoteKey.wasPressedThisFrame)
        {
            CloseSafeKeypad();
            return;
        }

        if (Keyboard.current.backspaceKey.wasPressedThisFrame && typedDigits.Length > 0)
        {
            typedDigits = typedDigits.Substring(0, typedDigits.Length - 1);
        }

        ReadTypedDigits();

        if (typedDigits.Length >= SafeCodeLength) //auto-submit on the fourth digit - no enter key to hunt for
        {
            int attempt = 0;
            int.TryParse(typedDigits, out attempt);
            keypadSafe.RPC_TryCode(attempt, Object.InputAuthority);
            typedDigits = ""; //cleared either way; the safe calls OnSafeCodeRejected if it was wrong
        }
    }

    private void ReadTypedDigits()
    {
        //check the number row AND the numpad, so it doesn't matter which a player reaches for
        AppendIfPressed(Keyboard.current.digit0Key, Keyboard.current.numpad0Key, '0');
        AppendIfPressed(Keyboard.current.digit1Key, Keyboard.current.numpad1Key, '1');
        AppendIfPressed(Keyboard.current.digit2Key, Keyboard.current.numpad2Key, '2');
        AppendIfPressed(Keyboard.current.digit3Key, Keyboard.current.numpad3Key, '3');
        AppendIfPressed(Keyboard.current.digit4Key, Keyboard.current.numpad4Key, '4');
        AppendIfPressed(Keyboard.current.digit5Key, Keyboard.current.numpad5Key, '5');
        AppendIfPressed(Keyboard.current.digit6Key, Keyboard.current.numpad6Key, '6');
        AppendIfPressed(Keyboard.current.digit7Key, Keyboard.current.numpad7Key, '7');
        AppendIfPressed(Keyboard.current.digit8Key, Keyboard.current.numpad8Key, '8');
        AppendIfPressed(Keyboard.current.digit9Key, Keyboard.current.numpad9Key, '9');
    }

    private void AppendIfPressed(KeyControl rowKey, KeyControl numpadKey, char digit)
    {
        if (typedDigits.Length >= SafeCodeLength)
        {
            return;
        }
        if ((rowKey != null && rowKey.wasPressedThisFrame) || (numpadKey != null && numpadKey.wasPressedThisFrame))
        {
            typedDigits += digit;
        }
    }

    private void OnGUI()
    {
        if (!HasInputAuthority)
        {
            return; //only our own player draws this
        }

        //every code we've found, parked in the corner so you can read them out mid-run. stacked upward so the newest
        //sits nearest the bottom where your eye already is.
        int line = 0;
        foreach (KeyValuePair<int, int> known in knownSafeCodes)
        {
            GUI.Label(new Rect(14f, Screen.height - 34f - line * 22f, 300f, 24f), $"Safe #{known.Key} code: {known.Value}");
            line++;
        }

        if (codeRejectedFlashTimer > 0f)
        {
            GUI.color = new Color(1f, 0.35f, 0.35f);
            GUI.Label(new Rect(Screen.width / 2f - 60f, Screen.height / 2f + 60f, 200f, 30f), "WRONG CODE");
            GUI.color = Color.white;
        }

        if (!isEnteringSafeCode)
        {
            return;
        }

        //deliberately plain. it's a placeholder for a proper world-space keypad on the safe itself later.
        string shown = typedDigits.PadRight(SafeCodeLength, '_');
        GUI.Box(new Rect(Screen.width / 2f - 110f, Screen.height / 2f + 90f, 220f, 62f), GUIContent.none);
        GUI.Label(new Rect(Screen.width / 2f - 96f, Screen.height / 2f + 98f, 200f, 24f), $"SAFE CODE:   {shown}");
        GUI.Label(new Rect(Screen.width / 2f - 96f, Screen.height / 2f + 122f, 220f, 24f), "type 4 digits  ·  Esc to cancel");
    }
}
