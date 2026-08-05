using Fusion;
using UnityEngine;

// Player - bargaining at the pawn shop desk. All of this is LOCAL: the negotiation is between you and him, and the
// only thing that ever crosses the wire is the agreed price, sent once when you shake on it.
//
// You sell ONE ITEM AT A TIME now. Handing over the whole bag for a lump sum meant the loot you risked most for was
// worth exactly as much as the vase you grabbed on the way out - the value was there but you never felt it. Choosing
// what to shift, and what to push him on, is where that finally lands.
//
// His patience is spent across the WHOLE visit and does NOT reset when you walk away, or you'd simply step back and
// forth over the doormat until every item sold at the ceiling.
public partial class Player
{
    private Shopkeeper currentKeeper;
    private int hagglingIndex = -1;   //index into inventory, or -1 when we're still choosing
    private int currentOffer;
    private int pushesUsed;
    private bool offerIsFinal;        //he's snapped: this number is it

    public Shopkeeper CurrentKeeper => currentKeeper;
    public bool IsTalkingToKeeper => currentKeeper != null;
    public bool IsHagglingItem => hagglingIndex >= 0 && hagglingIndex < inventory.Count;
    public InventoryItem HagglingItem => IsHagglingItem ? inventory[hagglingIndex] : default;
    public int CurrentOffer => currentOffer;
    public bool OfferIsFinal => offerIsFinal;
    public int PatienceLeft => currentKeeper != null ? Mathf.Max(0, currentKeeper.Patience - pushesUsed) : 0;

    public void EnterKeeper(Shopkeeper keeper)
    {
        currentKeeper = keeper;
        hagglingIndex = -1;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void ExitKeeper()
    {
        currentKeeper = null;
        hagglingIndex = -1;

        //pushesUsed is deliberately NOT cleared. it's how far you've worn him down this visit, and resetting it here
        //would make walking two steps away the optimal move before every single sale.
        if (!KeyboardIsCaptured) //currentKeeper is already null above, so this asks whether anything ELSE still wants the cursor free
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    public void BeginHaggle(int inventoryIndex)
    {
        if (currentKeeper == null || inventoryIndex < 0 || inventoryIndex >= inventory.Count) return;

        hagglingIndex = inventoryIndex;
        offerIsFinal = false;

        //he opens low. how low doesn't depend on how annoyed he is - his mood shows up in how far you can push, not
        //where he starts, so the opening number stays a stable thing you can judge the item against.
        currentOffer = Mathf.RoundToInt(inventory[hagglingIndex].value * currentKeeper.OpeningFraction);
    }

    public void PushOffer()
    {
        if (!IsHagglingItem || currentKeeper == null || offerIsFinal) return;

        int trueValue = inventory[hagglingIndex].value;

        //out of patience: this push is the one that breaks him. the offer COLLAPSES below where he opened, so pushing
        //a fourth time isn't a neutral gamble - it can leave you worse off than if you'd never haggled at all.
        if (pushesUsed >= currentKeeper.Patience)
        {
            currentOffer = Mathf.RoundToInt(trueValue * currentKeeper.InsultedFraction);
            offerIsFinal = true;
            return;
        }

        pushesUsed++;
        int raised = currentOffer + Mathf.RoundToInt(trueValue * currentKeeper.HaggleStep);
        int ceiling = Mathf.RoundToInt(trueValue * currentKeeper.CeilingFraction);
        currentOffer = Mathf.Min(raised, ceiling); //you can talk him ABOVE the sticker price, which is what makes the risk worth taking
    }

    public void AcceptOffer()
    {
        if (!IsHagglingItem) return;
        if (RunManager.Instance == null || RunManager.Instance.Object == null || !RunManager.Instance.Object.IsValid) return;

        RunManager.Instance.RPC_SellItems(currentOffer); //the agreed price, not the item's worth - the whole point of haggling
        inventory.RemoveAt(hagglingIndex);
        PublishCarriedCount();

        hagglingIndex = -1;
        offerIsFinal = false;
    }

    public void CancelHaggle() //back out of THIS item. the pushes you already spent are gone, because he remembers
    {
        hagglingIndex = -1;
        offerIsFinal = false;
    }

    //Walked off, or got hauled away mid-sentence. Same reason the tool shop does it: leaving a frozen menu on screen
    //for the rest of the run is worse than any lost sale.
    private void UpdateKeeperProximity()
    {
        if (currentKeeper == null) return;

        bool stillHere = Vector3.Distance(transform.position, currentKeeper.transform.position) <= currentKeeper.interactRange + 0.5f;
        if (!stillHere || IsEliminated || IsLockedUp || IsBearTrapped || isBeingDragged)
        {
            ExitKeeper();
        }
    }
}
