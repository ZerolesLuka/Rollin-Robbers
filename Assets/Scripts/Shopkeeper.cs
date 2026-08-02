using System.Collections.Generic;
using UnityEngine;

// The fence behind the pawn shop desk. You don't shove your bag across a counter any more - you pick ONE item, he
// makes you an insulting offer, and you decide whether to push him.
//
// PUSH YOUR LUCK, not a dice roll. Every haggle raises the offer by a fixed step and costs a fixed slice of his
// patience; when patience is gone the next push makes him snap, the offer collapses to his floor, and that's final.
// Nothing is random except the moment he breaks, so the decision is always "is one more step worth it" rather than
// "did I get lucky" - and because you're on voice, that decision gets argued out loud, which is the point.
//
// His patience is spent across the WHOLE visit, not per item. Squeezing him dry over a cheap vase is what makes him
// mean about the jewellery, so the order you sell in matters.
public class Shopkeeper : MonoBehaviour
{
    public static readonly List<Shopkeeper> AllKeepers = new List<Shopkeeper>();

    [SerializeField] public float interactRange = 2.5f;

    [Header("How he bargains")]
    [SerializeField, Range(0.1f, 1f)] private float openingFraction = 0.6f;  //his first offer, as a fraction of what the item is actually worth
    [SerializeField, Range(0.01f, 0.5f)] private float haggleStep = 0.12f;   //how much of the true value each successful push adds
    [SerializeField, Range(0.1f, 1f)] private float ceilingFraction = 1.15f; //you CAN talk him above true value, which is what makes pushing tempting
    [SerializeField, Range(0f, 1f)] private float insultedFraction = 0.4f;   //what he drops to when he's had enough. deliberately below his opening offer, so pushing has a real cost
    [SerializeField] private int patience = 4;                               //total pushes across the whole visit before he snaps

    public float OpeningFraction => openingFraction;
    public float HaggleStep => haggleStep;
    public float CeilingFraction => ceilingFraction;
    public float InsultedFraction => insultedFraction;
    public int Patience => patience;

    private void OnEnable() => AllKeepers.Add(this);
    private void OnDisable() => AllKeepers.Remove(this);

    public static Shopkeeper NearestTo(Vector3 position)
    {
        Shopkeeper nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (Shopkeeper keeper in AllKeepers)
        {
            float distance = Vector3.Distance(keeper.transform.position, position);
            if (distance <= keeper.interactRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = keeper;
            }
        }
        return nearest;
    }

    //Placeholder interface, same deal as the tool shop and the safe keypad: ugly, complete, and replaceable without
    //touching a line of the bargaining itself. Drawn HERE rather than on Player because Player already has an OnGUI.
    private void OnGUI()
    {
        Player me = Player.LocalPlayer;
        if (me == null || me.CurrentKeeper != this) return;

        const float width = 470f;
        float x = Screen.width * 0.5f - width * 0.5f;
        float y = Screen.height * 0.5f - 190f;

        GUI.Box(new Rect(x - 12f, y - 12f, width + 24f, 400f), GUIContent.none);
        GUI.Label(new Rect(x, y, width, 24f), $"THE FENCE      Crew money: ${me.ShopMoney}");
        y += 26f;

        IReadOnlyList<InventoryItem> bag = me.Inventory;
        if (bag.Count == 0)
        {
            GUI.Label(new Rect(x, y, width, 24f), "\"Come back when you've got something worth my time.\"   E to leave.");
            return;
        }

        if (!me.IsHagglingItem)
        {
            GUI.Label(new Rect(x, y, width, 24f), "Pick something to shift.   E to leave.");
            y += 28f;

            for (int i = 0; i < bag.Count; i++)
            {
                if (GUI.Button(new Rect(x, y, width, 26f), $"{bag[i].name}   (worth ${bag[i].value})"))
                {
                    me.BeginHaggle(i);
                }
                y += 30f;
            }

            y += 8f;
            GUI.Label(new Rect(x, y, width, 24f), me.PatienceLeft > 0
                ? $"He'll take {me.PatienceLeft} more push{(me.PatienceLeft == 1 ? "" : "es")} before he sours."
                : "He's done being pushed. Next one and he'll lowball you.");
            return;
        }

        //mid-haggle on one item
        InventoryItem item = me.HagglingItem;
        GUI.Label(new Rect(x, y, width, 24f), $"{item.name}   (worth ${item.value})");
        y += 30f;
        GUI.Label(new Rect(x, y, width, 30f), $"His offer:  ${me.CurrentOffer}");
        y += 34f;

        if (me.OfferIsFinal)
        {
            GUI.Label(new Rect(x, y, width, 24f), "\"That's my last word on it.\"");
            y += 30f;
        }

        if (GUI.Button(new Rect(x, y, 150f, 30f), $"Accept ${me.CurrentOffer}"))
        {
            me.AcceptOffer();
        }

        GUI.enabled = !me.OfferIsFinal;
        if (GUI.Button(new Rect(x + 160f, y, 150f, 30f), "Push him"))
        {
            me.PushOffer();
        }
        GUI.enabled = true;

        if (GUI.Button(new Rect(x + 320f, y, 130f, 30f), "Keep it"))
        {
            me.CancelHaggle();
        }
        y += 38f;

        GUI.Label(new Rect(x, y, width, 40f), me.OfferIsFinal
            ? "Push him again and he won't budge - he's already dropped you."
            : $"Pushes left before he sours: {me.PatienceLeft}");
    }
}
