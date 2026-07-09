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
        if (RunManager.Instance != null) RunManager.Instance.RPC_Route(houseSceneBuildIndex, houseSpawnPointId, true);
    }

    public void GoToPawnShop() //hooked to the "Drive to Pawn Shop" button - no new run
    {
        if (RunManager.Instance != null) RunManager.Instance.RPC_Route(pawnShopSceneBuildIndex, pawnShopSpawnPointId, false);
    }
}
