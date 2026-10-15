using UdonSharp;
using VRC.SDKBase;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DiffusionPlayerSetup : UdonSharpBehaviour {
    public float jumpImpulse=3.5f,walkSpeed=2f,runSpeed=4f,strafeSpeed=2f,gravityStrength=1f;
    private void Start(){InitializeLocalPlayer();}
    public void InitializeLocalPlayer(){
        VRCPlayerApi p=Networking.LocalPlayer;
        if(!Utilities.IsValid(p)){SendCustomEventDelayedSeconds(nameof(InitializeLocalPlayer),1f);return;}
        p.SetJumpImpulse(jumpImpulse);p.SetWalkSpeed(walkSpeed);p.SetRunSpeed(runSpeed);p.SetStrafeSpeed(strafeSpeed);p.SetGravityStrength(gravityStrength);
    }
    public override void OnPlayerRespawn(VRCPlayerApi player){if(player.isLocal)InitializeLocalPlayer();}
}
