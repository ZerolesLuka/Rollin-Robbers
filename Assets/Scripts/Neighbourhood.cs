using UnityEngine;

// Where the van can drive. The computer screen shows this as a street, not a menu - which matters more than it
// sounds, because a demo with ONE house reads very differently depending on how you frame it.
//
// A menu with a single button says "this is all there is". A street with one lit house and four dark ones says "this
// is the first one", and the empty plots become content the player is waiting for rather than absence they've
// noticed. Same scope either way. Only the framing changes.
//
// Locked entries are deliberately given real names and real reasons. "Coming soon" is a shrug; "Alarmed - need better
// tools" is a promise about the game you're building.

public struct Destination
{
    public string name;
    public string detail;     //what the crew knows about it
    public int sceneBuildIndex;
    public int spawnPointId;
    public bool startsNewRun; //true for a house: resets the run so the van ride doesn't instantly re-trigger
    public bool unlocked;
}

public static class Neighbourhood
{
    //BUILD INDICES MUST MATCH File > Build Settings. Outdoor is 0, Indoor 1, PawnShop 2 - and PawnShop still needs
    //adding, or driving there loads nothing at all.
    private static readonly Destination[] stops = new Destination[]
    {
        new Destination
        {
            name = "14 Maple Street",
            detail = "One guard, one dog. Nothing fancy.",
            //index 0 is OUTDOOR - where the van is ALREADY parked - not Indoor. The house stands right beside the van,
            //so taking this job isn't a journey, it's the crew agreeing on a target. Pointing it at Indoor teleported
            //everyone straight into the living room, skipping the walk up to the house and making the three outdoor
            //doors unreachable. RPC_Route sees the destination is the scene we're standing in and loads nothing: the
            //barrier just drops and the run begins. Indoor loads when somebody actually opens a door.
            sceneBuildIndex = 0, spawnPointId = 0, startsNewRun = true, unlocked = true,
        },
        new Destination
        {
            name = "Pawn shop",
            detail = "Sell what you lifted. Buy what you need.",
            sceneBuildIndex = 2, spawnPointId = 0, startsNewRun = false, unlocked = true,
        },

        //Locked entries were tried here and cut - three greyed-out rows read as padding rather than as a street with
        //more of it coming. The screen still SUPPORTS them, so adding one back is a single line if that judgement
        //changes: name, detail, unlocked = false, and it greys itself out.
    };

    public static Destination[] Stops => stops;
}
