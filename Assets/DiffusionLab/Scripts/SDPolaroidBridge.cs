using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDPolaroidBridge : UdonSharpBehaviour {
    public SDInputs inputs;
    public UdonBehaviour polaroidManager;
    public RenderTexture[] photographs;
    private int lastIndex;
    private float nextCheck;
    private void Update(){
        if(Time.realtimeSinceStartup<nextCheck)return;nextCheck=Time.realtimeSinceStartup+.1f;
        int index=(int)polaroidManager.GetProgramVariable("photoIndex");
        if(index==lastIndex)return;lastIndex=index;
        if(index<1||index>photographs.Length)return;
        if(!inputs.controller.HasControl())return;
        if(Networking.LocalPlayer!=null&&!Networking.IsOwner(polaroidManager.gameObject))return;
        SendCustomEventDelayedSeconds(nameof(CaptureDevelopedPhoto),.35f);
    }
    public void CaptureDevelopedPhoto(){
        if(!inputs.controller.HasControl())return;
        int index=(int)polaroidManager.GetProgramVariable("photoIndex");if(index<1||index>photographs.Length)return;
        inputs.photoSource=photographs[index-1];inputs.CapturePhoto();
    }
}
