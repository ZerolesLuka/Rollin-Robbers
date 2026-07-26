using UnityEngine;

// The buttons shown on the van computer screen while a player is "in" it. Wire each button's OnClick
// to one of these methods. Routing goes through RunManager so it happens for the whole crew.
public class ComputerScreenUI : MonoBehaviour
{
    [SerializeField] private int houseSceneBuildIndex = 1;
    [SerializeField] private int houseSpawnPointId = 0;
    [SerializeField] private int pawnShopSceneBuildIndex = 2;
    [SerializeField] private int pawnShopSpawnPointId = 0;

    public void GoToHouse() //hooked to the "Drive to House" button - starts a fresh heist
    {
        StandUpBeforeDriving();
        if (RunManager.Instance != null) RunManager.Instance.RPC_Route(houseSceneBuildIndex, houseSpawnPointId, true);
    }

    public void GoToPawnShop() //hooked to the "Drive to Pawn Shop" button - no new run
    {
        StandUpBeforeDriving();
        if (RunManager.Instance != null) RunManager.Instance.RPC_Route(pawnShopSceneBuildIndex, pawnShopSpawnPointId, false);
    }

    private void StandUpBeforeDriving()
    {
        //get out of the chair BEFORE the scene swaps. routing destroys this whole scene, terminal included, but the
        //player object survives it - so leaving isUsingComputer set stranded them frozen at the far end, and E
        //couldn't rescue them because the exit needs currentTerminal, which is now a destroyed object reading null.
        ComputerTerminal terminal = GetComponentInParent<ComputerTerminal>();
        if (terminal != null)
        {
            terminal.Exit(); //also releases the networked computer lock for the next player
        }
        else if (Player.LocalPlayer != null)
        {
            Player.LocalPlayer.ExitComputer(); //UI isn't parented under the terminal - at least unfreeze the player
        }
    }
}
