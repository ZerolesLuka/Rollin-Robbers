using System.Collections.Generic;
using UnityEngine;

// Where you spend the crew's money. Put this on the pawn shop counter (or its own prop next to it) and press E.
//
// The shop DRAWS ITSELF rather than living on Player. Player is already a partial class with an OnGUI in
// Player.SafeCode.cs, and a class can only have one - so a second placeholder UI has to live somewhere else. It's
// also just the right owner: the shop knows what it sells.
//
// The interface below is a deliberate placeholder in the same spirit as the safe keypad: plain, ugly, and completely
// functional, so tools can be bought and balanced TODAY instead of waiting on a canvas. Replace it with real UI later
// and none of the buying logic has to change - it all goes through Player.RequestBuyTool either way.
public class ToolShop : MonoBehaviour
{
    public static readonly List<ToolShop> AllShops = new List<ToolShop>();

    [SerializeField] public float interactRange = 2.5f;

    private void OnEnable() => AllShops.Add(this);
    private void OnDisable() => AllShops.Remove(this);

    public static ToolShop NearestTo(Vector3 position)
    {
        ToolShop nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (ToolShop shop in AllShops)
        {
            float distance = Vector3.Distance(shop.transform.position, position);
            if (distance <= shop.interactRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = shop;
            }
        }
        return nearest;
    }

    private void OnGUI()
    {
        Player me = Player.LocalPlayer;
        if (me == null || me.CurrentShop != this)
        {
            return; //not our shop, or nobody's stood at it
        }

        const float width = 460f;
        float x = Screen.width * 0.5f - width * 0.5f;
        float y = Screen.height * 0.5f - 200f;

        GUI.Box(new Rect(x - 12f, y - 12f, width + 24f, 430f), GUIContent.none);
        GUI.Label(new Rect(x, y, width, 24f), $"TOOLS      Crew money: ${me.ShopMoney}");
        GUI.Label(new Rect(x, y + 22f, width, 24f), "Tools take loot slots. E to leave.");
        y += 54f;

        foreach (ToolDefinition tool in ToolTable.All)
        {
            bool owned = me.HasTool(tool.type);
            bool affordable = me.CanAfford(tool.type);
            bool room = me.HasFreeToolSlot && me.HasRoomForTool(tool.type);
            bool buyable = !owned && affordable && room;

            //say WHY it's unavailable rather than just greying it out - "can't afford" and "bag too full" are very
            //different problems and only one of them is solved by robbing another house
            string status = owned ? "owned"
                          : !affordable ? "too expensive"
                          : !me.HasFreeToolSlot ? "no tool slot"
                          : !room ? "bag too full"
                          : $"${tool.cost}";

            GUI.enabled = buyable;
            if (GUI.Button(new Rect(x, y, 150f, 28f), $"{tool.name}  {status}"))
            {
                me.RequestBuyTool(tool.type); //the authority still vets it; this is only the ask
            }
            GUI.enabled = true;

            GUI.Label(new Rect(x + 158f, y + 4f, width - 158f, 24f), tool.description);
            y += 32f;
        }

        y += 12f;
        GUI.Label(new Rect(x, y, width, 24f), "Carrying:");
        y += 26f;

        for (int slot = 0; slot < ToolTable.SlotCount; slot++)
        {
            ToolType carried = me.ToolInSlot(slot);
            GUI.Label(new Rect(x, y + 4f, 150f, 24f), $"Slot {slot + 1}:  {ToolTable.NameOf(carried)}");

            GUI.enabled = carried != ToolType.None;
            if (GUI.Button(new Rect(x + 160f, y, 90f, 26f), "Drop"))
            {
                me.DropTool(slot); //no refund. dropping is for making room, not for changing your mind about a purchase
            }
            GUI.enabled = true;
            y += 30f;
        }
    }
}
