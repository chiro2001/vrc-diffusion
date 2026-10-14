using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

// Isolated validation prefab; not attached to the diffusion world controller.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class W8A8ValidationRunner : UdonSharpBehaviour {
    public Material first,second;
    public RenderTexture firstAccumulator,quantized,secondAccumulator,output,integerReadback;
    public bool runOnStart;
    public int runs;
    public byte[] integerBytes;
    public bool readReady,readError;
    public int bytesRead;
    private void Start(){if(runOnStart)Execute();}
    public void Execute(){
        VRCGraphics.Blit(null,firstAccumulator,first,0);
        VRCGraphics.Blit(null,quantized,first,2);
        second.SetTexture("_PackedInput",quantized);
        VRCGraphics.Blit(null,secondAccumulator,second,0);
        VRCGraphics.Blit(null,output,second,1);
        VRCGraphics.Blit(null,integerReadback,second,9);
        runs++;
        readReady=false;readError=false;
        integerBytes=new byte[integerReadback.width*integerReadback.height*4];
        VRCAsyncGPUReadback.Request(integerReadback,0,this);
    }
    public void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request){
        readError=request.hasError||!request.TryGetData(integerBytes);
        bytesRead=readError?0:integerBytes.Length;
        readReady=true;
    }
}
