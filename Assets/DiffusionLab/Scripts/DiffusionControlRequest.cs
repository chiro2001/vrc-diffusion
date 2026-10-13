using UdonSharp;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiffusionControlRequest : UdonSharpBehaviour {
    public DiffusionController controller;
    [UdonSynced] public int requesterId=-1;
    [UdonSynced] public double requestedAt;
    public void Claim() {
        if(Networking.LocalPlayer==null) return;
        Networking.SetOwner(Networking.LocalPlayer,gameObject);
        requesterId=Networking.LocalPlayer.playerId;
        requestedAt=Networking.GetServerTimeInSeconds();
        RequestSerialization();
    }
    public void ClearRequest() {
        if(!Networking.IsOwner(gameObject)) return;
        requesterId=-1;RequestSerialization();
    }
}

