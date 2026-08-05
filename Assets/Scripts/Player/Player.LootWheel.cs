using UnityEngine;
using UnityEngine.InputSystem;

// Player - hold middle mouse to pick which piece of loot you're holding. G then drops THAT one instead of blindly
// dropping whatever you grabbed last, which is what it used to do.
//
// The wheel is on MMB and the hand signals stay on 1-6 on purpose, and it's worth writing down why, because the
// instinct is usually the other way round. Signals are the thing you reach for in a panic with the guard in the room
// and no safe way to speak - they have to be one instant keypress. Choosing loot happens standing still in a quiet
// room or the van, so it can afford to be slow. The urgent input keeps the fast key; the rare one gets the wheel.
//
// Selection is LOCAL. Nobody else needs to know which slot you're pointing at - only the resulting drop is networked,
// and that already goes through the item spawn.
//
// This file owns the input and the maths. The visuals are HUD's job: it reads IsLootWheelOpen and SelectedSlot and
// lights up the right slot, so the wheel can be restyled without touching any of this.
public partial class Player
{
    [Header("Loot wheel (hold middle mouse)")]
    [SerializeField] private float wheelDeadzone = 40f; //how far the mouse must travel before it counts as pointing somewhere. below this we keep the slot you already had, so a twitchy hand doesn't reassign it

    public bool IsLootWheelOpen { get; private set; }
    public int SelectedSlot { get; private set; } //index into Inventory. clamped on use, so a stale value can never index out of range

    private Vector2 wheelAim; //mouse travel accumulated since the wheel opened

    private void UpdateLootWheel()
    {
        if (Mouse.current == null)
        {
            return;
        }

        //nothing to choose between, and no wheel while you're parked at a terminal or typing a safe code
        bool canOpen = inventory.Count > 0 && !KeyboardIsCaptured; //also missed both counters - the wheel opened straight over the haggling menu

        if (Mouse.current.middleButton.wasPressedThisFrame && canOpen)
        {
            IsLootWheelOpen = true;
            wheelAim = Vector2.zero; //start from centre: point somewhere to change slot, release straight away to keep the one you had
        }

        if (!IsLootWheelOpen)
        {
            return;
        }

        if (Mouse.current.middleButton.wasReleasedThisFrame || !canOpen)
        {
            IsLootWheelOpen = false;
            return;
        }

        //accumulate raw mouse travel rather than using the cursor position - the cursor is locked during play, so it
        //has no position to read. direction from centre is all a radial actually needs.
        wheelAim += Mouse.current.delta.ReadValue();

        if (wheelAim.magnitude < wheelDeadzone)
        {
            return; //still near the middle, so they haven't committed to a direction yet
        }

        //straight up is slot 0 and it goes clockwise, which is what the slots should be drawn as
        float angle = Mathf.Atan2(wheelAim.x, wheelAim.y) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;

        int slotCount = inventory.Count;
        float sliceSize = 360f / slotCount;
        SelectedSlot = Mathf.Clamp(Mathf.FloorToInt((angle + sliceSize * 0.5f) % 360f / sliceSize), 0, slotCount - 1);
    }

    //The slot G will actually drop. Kept as a method rather than trusting SelectedSlot directly because the inventory
    //shrinks underneath it - dropping slot 3 of 4 leaves a stale index pointing past the end of the list.
    private int ResolveDropSlot()
    {
        if (inventory.Count == 0) return -1;
        return Mathf.Clamp(SelectedSlot, 0, inventory.Count - 1);
    }
}
