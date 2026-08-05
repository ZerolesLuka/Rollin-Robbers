using Fusion;
using UnityEngine;

// Player - standing at the tool shop. Same shape as the van computer: you're frozen in place with the cursor freed
// while you browse, and E backs you out.
//
// Everything here is LOCAL. Shopping isn't a state anyone else needs to see - the only thing that crosses the wire is
// the purchase itself, which the authority decides in RunManager.RPC_BuyTool. That also means two players can browse
// the same counter at once without any locking, unlike the van computer where they'd fight over one screen.
public partial class Player
{
    private ToolShop currentShop;

    public ToolShop CurrentShop => currentShop; //the shop draws its own UI and asks this whether it's the one we're at
    public bool IsShopping => currentShop != null;

    //The shop's UI needs the shared balance, and it only has a Player to ask.
    public int ShopMoney => (RunManager.Instance != null && RunManager.Instance.Object != null && RunManager.Instance.Object.IsValid)
        ? RunManager.Instance.Money
        : 0;

    public void EnterShop(ToolShop shop)
    {
        currentShop = shop;
        Cursor.lockState = CursorLockMode.None; //free the mouse for the buttons
        Cursor.visible = true;
    }

    public void ExitShop()
    {
        currentShop = null;

        //hand the cursor back only if nothing ELSE still wants it loose - the computer and the safe keypad both free
        //it too, and whoever left last shouldn't be able to re-lock it under the others. This missed the fence's desk,
        //so closing the shop while stood at the keeper re-locked the cursor underneath his still-open menu.
        if (!KeyboardIsCaptured)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    //Walked away from the counter mid-browse. Checked every tick rather than trusting the player to press E, because
    //otherwise being dragged off by the guard would leave the shop open on a frozen screen for the rest of the run.
    private void UpdateShopProximity()
    {
        if (currentShop == null) return;

        bool stillHere = Vector3.Distance(transform.position, currentShop.transform.position) <= currentShop.interactRange + 0.5f;
        if (!stillHere || IsEliminated || IsLockedUp || IsBearTrapped || isBeingDragged)
        {
            ExitShop();
        }
    }
}
