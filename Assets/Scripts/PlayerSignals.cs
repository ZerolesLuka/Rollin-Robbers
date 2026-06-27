using UnityEngine;
using Fusion;

public class PlayerSignals : NetworkBehaviour
{
    private Player player;

    public override void Spawned()
    {
        player = GetComponent<Player>();
    }
    private void Update()
    {
        if(!HasInputAuthority || player.playerInputActions == null) //if not our player or has no control return
        {
            return;
        }
        if (player.playerInputActions.Player.Signal1.WasPressedThisFrame())
        {
            RPC_ShowSignal(1);
        }
        else if (player.playerInputActions.Player.Signal2.WasPressedThisFrame())
        {
            RPC_ShowSignal(2);
        }
        else if (player.playerInputActions.Player.Signal3.WasPressedThisFrame())
        {
            RPC_ShowSignal(3);
        }
        else if (player.playerInputActions.Player.Signal4.WasPressedThisFrame()) //point
        {
            RPC_ShowSignal(4);
        }
        else if (player.playerInputActions.Player.Signal5.WasPressedThisFrame()) //stop
        {
            RPC_ShowSignal(5);
        }
        else if (player.playerInputActions.Player.Signal6.WasPressedThisFrame()) //middle finger
        {
            RPC_ShowSignal(6);
        }
    }
    [Rpc(RpcSources.InputAuthority, RpcTargets.All)] //I press it, everyone runs this
    private void RPC_ShowSignal(int id)
    {
        Debug.Log($"{name} signaled {id}"); //TEMP - later: trigger the matching finger gesture here
    }

}
