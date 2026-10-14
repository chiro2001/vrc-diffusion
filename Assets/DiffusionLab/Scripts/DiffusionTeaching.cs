using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DiffusionTeaching : UdonSharpBehaviour {
    public DiffusionController controller;
    public DiffusionLocalization localization;
    public string[] pageTitles,pageBodies;
    public Text titleText,bodyText,algorithmText,probeText,tensorText;
    public GameObject[] graphRoots;
    public Image[] highlights,primaryEdges,skipEdges,algorithmHighlights;
    public int[] graphOffsets;
    public Material conditionalDisplay,nullDisplay,guidedDisplay,cleanDisplay,nextDisplay;
    public Material probeMaterial;
    public RenderTexture probeTexture;
    public int page,probeX=14,probeY=14,probeChannel,commandValue;
    public float[] probeValues=new float[7];
    public bool probeReady;
    public int probeStep,probeT;
    public float probeAlpha,probeNextAlpha;
    private int shownModel=-1,shownEpoch=-1,lastHighlight=-1;
    private bool readPending;
    private int readEpoch,readModel,readX,readY,readChannel;
    private float nextRefresh;
    public void NextPage() {page=(page+1)%pageTitles.Length;RefreshPage();}
    public void PreviousPage() {page=(page+pageTitles.Length-1)%pageTitles.Length;RefreshPage();}
    public void ProbeLeft(){probeX=Mathf.Max(0,probeX-1);CaptureStep();}
    public void ProbeRight(){probeX=Mathf.Min(controller.nativeSize-1,probeX+1);CaptureStep();}
    public void ProbeDown(){probeY=Mathf.Max(0,probeY-1);CaptureStep();}
    public void ProbeUp(){probeY=Mathf.Min(controller.nativeSize-1,probeY+1);CaptureStep();}
    public void ProbeChannel(){probeChannel++;CaptureStep();}
    private void Start() {RefreshPage();Refresh();}
    private void Update(){if(Time.time>=nextRefresh){nextRefresh=Time.time+.12f;Refresh();}}
    private void RefreshPage(){titleText.text=pageTitles[page];bodyText.text=pageBodies[page];}
    public void Refresh() {
        if(controller.models==null||controller.models.Length==0||controller.modelIndex<0||controller.modelIndex>=controller.models.Length)return;
        DiffusionModel model=controller.models[controller.modelIndex];
        if(shownEpoch!=controller.epoch){shownEpoch=controller.epoch;probeReady=false;}
        if(shownModel!=controller.modelIndex){
            shownModel=controller.modelIndex;probeReady=false;lastHighlight=-1;
            for(int i=0;i<graphRoots.Length;i++)graphRoots[i].SetActive(i==shownModel);
            probeX=Mathf.Clamp(probeX,0,model.nativeSize-1);probeY=Mathf.Clamp(probeY,0,model.nativeSize-1);probeChannel=0;
        }
        int node=Mathf.Clamp(controller.lastComputedLayer,0,model.memory.Length-1);
        int global=controller.lastComputedLayer<0?-1:graphOffsets[shownModel]+node;
        if(global!=lastHighlight){
            for(int i=0;i<highlights.Length;i++){
                highlights[i].color=i==global?new Color(.1f,.75f,.65f):new Color(.08f,.18f,.24f);
                if(primaryEdges[i]!=null)primaryEdges[i].color=i==global?new Color(.1f,1,.85f):new Color(.25f,.6f,.7f,.4f);
                if(skipEdges[i]!=null)skipEdges[i].color=i==global?new Color(1,.75f,.1f):new Color(.95f,.66f,.16f,.4f);
            }
            lastHighlight=global;
        }
        for(int i=0;i<algorithmHighlights.Length;i++)algorithmHighlights[i].color=controller.lastAlgorithmStage==i+1?new Color(.1f,.75f,.65f):new Color(.08f,.18f,.24f);
        conditionalDisplay.SetTexture("_MainTex",controller.conditionalNoise);
        nullDisplay.SetTexture("_MainTex",controller.unconditionalNoise);
        guidedDisplay.SetTexture("_MainTex",controller.guidedNoise);
        cleanDisplay.SetTexture("_MainTex",controller.cleanPrediction);nextDisplay.SetTexture("_MainTex",controller.displayedState);
        cleanDisplay.SetFloat("_ValueMin",model.valueMin);cleanDisplay.SetFloat("_ValueMax",model.valueMax);nextDisplay.SetFloat("_ValueMin",model.valueMin);nextDisplay.SetFloat("_ValueMax",model.valueMax);
        tensorText.text="ACTIVE OPERATOR "+node+"  "+model.operatorNames[node]+"\n"+
            model.memory[node].height+"×"+model.memory[node].height+"×"+model.channels[node]+"  · "+(controller.lastUnconditional?"NULL BRANCH":"CLASS BRANCH")+"  · "+controller.localStep+"/"+controller.totalSteps;
        int t=controller.localStep<controller.totalSteps?TimeAt(controller.localStep):model.timestepMin;
        float alpha=model.alpha[t],nextAlpha=controller.localStep+1>=controller.totalSteps?1:model.alpha[TimeAt(controller.localStep+1)];
        if(probeReady){t=probeT;alpha=probeAlpha;nextAlpha=probeNextAlpha;}
        algorithmText.text=(probeReady?"LAST UPDATE · ":"NEXT UPDATE · ")+"DDIM η=0  ·  t="+t+"  ·  ᾱ="+alpha.ToString("F6")+"  →  "+nextAlpha.ToString("F6")+"\n"+
            "ε = ε_null + "+model.guidanceScale+" × (ε_class − ε_null)\n"+
            "x_0 = (x_t − √(1−ᾱ_t) × ε) / √ᾱ_t\n"+
            "x_next = √ᾱ_next × x_0 + √(1−ᾱ_next) × ε";
        if(model.clipPrediction)algorithmText.text=(probeReady?"LAST UPDATE · ":"NEXT UPDATE · ")+"DDIM η=0  · t="+t+"  · ᾱ="+alpha.ToString("F6")+"\n"+
            "ε = ε_null + 3 × (ε_class − ε_null)\n"+
            "x_0 = clip((x_t − √(1−ᾱ_t) × ε)/√ᾱ_t, −1, 1)\n"+
            "ε_corr = (x_t − √ᾱ_t × x_0)/√(1−ᾱ_t)\n"+
            "x_next = √ᾱ_next × x_0 + √(1−ᾱ_next) × ε_corr";
        if(probeReady)probeText.text="PIXEL PROBE ("+readX+","+readY+") · step "+probeStep+" · t="+probeT+"\n"+
            "x_t="+probeValues[0].ToString("F4")+"  ε_class="+probeValues[1].ToString("F4")+"  ε_null="+probeValues[2].ToString("F4")+"\n"+
            "ε_guided="+probeValues[3].ToString("F4")+"  x_0="+probeValues[4].ToString("F4")+"  x_next="+probeValues[5].ToString("F4")+"\n"+
            "MEMORY channel "+readChannel+" = "+probeValues[6].ToString("F4")+"  · ᾱ="+probeAlpha.ToString("F6");
        else probeText.text="PIXEL PROBE · completes with a denoise step\n"+"("+probeX+","+probeY+") · GPU texture readback";
    }
    private int TimeAt(int step){
        if(controller.totalSteps<=1)return controller.timestepMin;
        if(controller.leadingSpacing)return (controller.totalSteps-1-step)*(controller.alphaCumprod.Length/controller.totalSteps);
        return controller.timestepMin+Mathf.RoundToInt((controller.timestepMax-controller.timestepMin)*(1f-step/(float)(controller.totalSteps-1)));
    }
    public void CaptureStep() {
        if(readPending||controller.localLayer!=0||controller.localStep<=0||controller.stepInput==null||controller.models==null)return;
        int node=Mathf.Clamp(controller.lastComputedLayer,0,controller.layerMemory.Length-1);
        probeChannel%=controller.layerChannels[node];
        readEpoch=controller.epoch;readModel=controller.modelIndex;readX=probeX;readY=probeY;readChannel=probeChannel;
        probeStep=controller.localStep;probeT=controller.sampledT;probeAlpha=controller.sampledAlpha;probeNextAlpha=controller.sampledNextAlpha;
        probeMaterial.SetTexture("_Before",controller.stepInput);probeMaterial.SetTexture("_Conditional",controller.conditionalNoise);
        probeMaterial.SetTexture("_Null",controller.unconditionalNoise);probeMaterial.SetTexture("_Guided",controller.guidedNoise);
        probeMaterial.SetTexture("_Clean",controller.cleanPrediction);probeMaterial.SetTexture("_After",controller.displayedState);
        // displayedState is rebound after the callback; use stateA/B by the completed step parity.
        probeMaterial.SetTexture("_After",controller.localStep%2==0?controller.stateA:controller.stateB);
        probeMaterial.SetTexture("_Memory",controller.layerMemory[node]);
        probeMaterial.SetInt("_X",probeX);probeMaterial.SetInt("_Y",probeY);probeMaterial.SetInt("_Size",controller.nativeSize);
        probeMaterial.SetInt("_Channel",probeChannel);probeMaterial.SetInt("_MemorySize",controller.layerMemory[node].height);
        VRCGraphics.Blit(null,probeTexture,probeMaterial);readPending=true;
        VRCAsyncGPUReadback.Request(probeTexture,0,this);
    }
    public void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request){
        readPending=false;
        if(readEpoch!=controller.epoch||readModel!=controller.modelIndex){probeReady=false;return;}
        probeReady=!request.hasError&&request.TryGetData(probeValues);Refresh();
    }
}
