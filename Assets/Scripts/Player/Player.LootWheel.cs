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

        //no selecting while you're parked at a terminal or typing a safe code. NOT gated on carrying anything any
        //more - selecting an empty slot is a legitimate thing to do now, it's how you end up holding nothing.
        bool canOpen = !KeyboardIsCaptured; //also missed both counters - the wheel opened straight over the haggling menu

        //capacity can SHRINK - drop the Duffel Bag and you lose two slots - so a selection made while it was bigger
        //would sit off the end and read as permanently empty
        if (SelectedSlot >= MaxInventorySlots)
        {
            SelectedSlot = MaxInventorySlots - 1;
        }

        if (Mouse.current.middleButton.wasPressedThisFrame && canOpen)
        {
            IsLootWheelOpen = true;
            wheelAim = Vector2.zero; //start from centre: point somewhere to change slot, release straight away to keep the one you had
        }

        //SCROLL CYCLES SLOTS with nothing open. The wheel is for choosing deliberately while stood still; this is for
        //flicking between two things as you walk. Both write the same SelectedSlot, so G, the top-right label and the
        //prop in your hand all follow whichever you used.
        if (canOpen && !IsLootWheelOpen)
        {
            float scrollAmount = Mouse.current.scroll.ReadValue().y;
            if (!Mathf.Approximately(scrollAmount, 0f))
            {
                //Windows reports 120 per notch rather than 1, so only the SIGN of this means anything. Flip the two
                //values below to reverse the direction.
                int direction = scrollAmount > 0f ? -1 : 1;

                //CYCLES EVERY SLOT, not just the full ones. Two reasons. Scrolling past the end reaches an EMPTY slot,
                //which is how you put your hands away - there was no way to hold nothing before. And cycling
                //inventory.Count meant that carrying a single item, every scroll landed straight back on slot 0 and
                //the feature looked broken.
                int slotsTotal = MaxInventorySlots;

                //the extra + slotsTotal matters: C# % returns a NEGATIVE result for a negative left operand, so
                //scrolling back off slot 0 would land on -1 and index out of range the moment anything read it
                SelectedSlot = ((SelectedSlot + direction) % slotsTotal + slotsTotal) % slotsTotal;
            }
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

        //MaxInventorySlots, matching the scroll wheel - the two have to share an index space or pointing the wheel at
        //slot 2 and scrolling to slot 2 would select different things. Empty slices are fine: they mean "hands away".
        int slotCount = MaxInventorySlots;
        float sliceSize = 360f / slotCount;
        SelectedSlot = Mathf.Clamp(Mathf.FloorToInt((angle + sliceSize * 0.5f) % 360f / sliceSize), 0, slotCount - 1);
    }

    //What's in your hand right now, for the HUD to print. Empty string when the bag is empty, so the label can just
    //disappear rather than announcing "None". Tools say so - a held crowbar reading exactly like a held vase is
    //confusing when only one of them is doing anything.
    public string HeldItemName
    {
        get
        {
            int slot = ResolveDropSlot();
            if (slot < 0)
            {
                return string.Empty;
            }
            InventoryItem item = inventory[slot];
            return item.IsTool ? item.name + "  (tool)" : item.name;
        }
    }

    //The slot G will actually drop, or -1 for EMPTY HANDS.
    //
    //It returns -1 rather than clamping into the list on purpose: an empty slot is now a real thing you can select,
    //so pointing at one means holding nothing, and G should do nothing rather than quietly dropping whatever happens
    //to be nearest the end. Everything reads through here - the hand prop, the top-right label and G - so all three
    //agree about empty automatically.
    private int ResolveDropSlot()
    {
        if (SelectedSlot < 0 || SelectedSlot >= inventory.Count) return -1;
        return SelectedSlot;
    }
}
