using UnityEngine;

// A crosshair. Drawn in code rather than as a UI Image so it needs no sprite, no Canvas child and no wiring - drop
// this on the HUD object and it works.
//
// It matters more than it used to: doors are grabbed by LOOKING at them now, so without a centre mark you're guessing
// where the raycast goes. It also brightens whenever you're aimed at something you can actually use, which turns
// "is this door in reach" from a question into something you can see.
public class Crosshair : MonoBehaviour
{
    [SerializeField] private float gapFromCentre = 4f;   // hole in the middle. 0 makes it a plus, bigger makes it four ticks
    [SerializeField] private float armLength = 6f;       // how long each of the four strokes is
    [SerializeField] private float thickness = 2f;
    [SerializeField] private Color restingColour = new Color(1f, 1f, 1f, 0.45f);   // faint when there's nothing to do
    [SerializeField] private Color highlightColour = new Color(1f, 1f, 1f, 0.95f); // bright when something's in reach
    [SerializeField] private bool showCentreDot = true;

    private void OnGUI()
    {
        Player me = Player.LocalPlayer;
        if (me == null || me.Object == null || !me.Object.IsValid)
        {
            return;
        }

        //HIDDEN whenever the mouse isn't aiming. A crosshair over the shop menu or the van computer is pointing at
        //nothing, and one on a spectator camera implies you can still act - which you can't.
        if (me.KeyboardIsCaptured || me.IsEliminated || me.IsHiding || me.IsLockedUp)
        {
            return;
        }

        //InteractPrompt is set by the SAME scan that E acts on, so the crosshair can't lie about what's in reach.
        //Dragging a door counts too - your hand is on something even though there's no prompt for it.
        bool somethingInReach = !string.IsNullOrEmpty(me.InteractPrompt) || me.IsDraggingDoor;
        Color colour = somethingInReach ? highlightColour : restingColour;

        float centreX = Screen.width * 0.5f;
        float centreY = Screen.height * 0.5f;

        //Texture2D.whiteTexture tinted by GUI.color - the cheapest possible way to put a rectangle on screen, and it
        //means there is no art asset to lose track of.
        Color previousColour = GUI.color;
        GUI.color = colour;

        //left, right, up, down
        DrawRect(centreX - gapFromCentre - armLength, centreY - thickness * 0.5f, armLength, thickness);
        DrawRect(centreX + gapFromCentre, centreY - thickness * 0.5f, armLength, thickness);
        DrawRect(centreX - thickness * 0.5f, centreY - gapFromCentre - armLength, thickness, armLength);
        DrawRect(centreX - thickness * 0.5f, centreY + gapFromCentre, thickness, armLength);

        if (showCentreDot)
        {
            DrawRect(centreX - thickness * 0.5f, centreY - thickness * 0.5f, thickness, thickness);
        }

        GUI.color = previousColour; //leave the GUI state as we found it, or every label drawn after this inherits the tint
    }

    private static void DrawRect(float x, float y, float width, float height)
    {
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
    }
}
