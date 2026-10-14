using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DiffusionModel : UdonSharpBehaviour {
    public string displayName;
    public int version,nativeSize=32,timestepMin=0,timestepMax=255,defaultSteps=32;
    public bool leadingSpacing,classifierFreeGuidance=true,clipPrediction=true;
    public float valueMin=-1,valueMax=1,guidanceScale=3;
    public string[] operatorNames;
    public int[] primaryLayers,skipLayers;
    public Material[] layers;
    public RenderTexture[] memory;
    public int[] channels;
    public RenderTexture stateA,stateB,clean,conditional,unconditional,guided;
    public RenderTexture[] history,outputs;
    public float[] alpha;
    public Texture2D validationInput;
    public float[] validationExpected;
}
