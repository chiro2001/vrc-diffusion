using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class SDController : UdonSharpBehaviour {
    public SDGraphExecutor executor;
    public SDInputs inputs;
    public SDPerformance performance;
    public Material photoExport;
    public SDButton[] buttons;
    private bool buttonStateReady,lastButtonOwner;
    private int lastButtonPhase=-1;
    public SDGraphData[] unetGraphs,vaeGraphs;
    public Texture2D[] embeddings;
    public string[] presetNames;
    public TextAsset modelLicense;
    public float[] alpha;
    public Material sampler,memoryDisplay,quantCaptureMaterial;
    public RenderTexture[] latentA,latentB,cleanMemory,conditionalMemory,unconditionalMemory,images;
    public RenderTexture timeMemory,debugMemory,quantDebugMemory;
    public RenderTexture[] history;
    public Text statusText,algorithmText,memoryText,presetText;
    public RawImage finalView;
    public InputField seedInput;
    public bool details=true,localInference=false,vaePreviews=true,autoStartForValidation;
    public int passesPerFrame=4,commandValue;
    public float frameBudgetMs=2;
    [UdonSynced] public int protocol=1,epoch,preset,resolution,seed=1234,totalSteps=20,phase;
    [UdonSynced] public float guidance=7.5f;
    [UdonSynced] public int authorityId=-1;
    [UdonSynced] public bool customPrompt,usePhoto;
    [UdonSynced] public string positivePrompt="",negativePrompt="";
    [UdonSynced] public float imageStrength=.6f,photoScale=1;
    [UdonSynced] public int photoSize;
    [UdonSynced] public byte[] photoBytes=new byte[0];
    public int firstStep;
    public float activeSeconds;
    public int localEpoch=-1,localStep,algorithmStage,timestep,selectedNode=-1;
    public float elapsed;
    public RenderTexture displayedImage,currentLatent;
    private RenderTexture nextLatent;
    private float runStart,nextLabel;
    private int boundResolution=-1,lastHistory=-1,lastDebugNode=-1,lastQuantNode=-1;
    private bool unconditional,previewDecode;
    private int shownSeed=-1;
    private bool Owner() {return Networking.LocalPlayer==null||Networking.IsOwner(gameObject);}
    public bool HasControl(){return Owner();}
    public bool CanEditConfiguration(){return Edit();}
    public void ConfigurationChanged(){ResetConfiguration();}
    public void PublishInputs(){Publish();}
    public bool CanCommand(string command){
        if(command=="ToggleDetails"||command=="TogglePreviews"||command=="ToggleInference"||command=="SetSpeed"||command=="ToggleExport")return true;
        if(command=="RequestControl")return !Owner()&&phase!=1;
        if(!Owner())return false;
        if(command=="PauseResume")return phase==1||phase==2;
        return phase!=1;
    }
    private void Publish(){RequestSerialization();}
    private bool Edit(){return Owner()&&phase!=1;}
    private void ClearSamplerInputs(){sampler.SetTexture("_State",null);sampler.SetTexture("_Conditional",null);sampler.SetTexture("_Unconditional",null);sampler.SetTexture("_RGB",null);}
    private void Start(){InitializeAuthority();Apply();if(autoStartForValidation)SendCustomEventDelayedSeconds(nameof(StartGeneration),2f);}
    public void InitializeAuthority(){
        if(Networking.LocalPlayer==null){SendCustomEventDelayedSeconds(nameof(InitializeAuthority),1);return;}
        if(Owner()&&(authorityId<0||VRCPlayerApi.GetPlayerById(authorityId)==null)){authorityId=Networking.LocalPlayer.playerId;Publish();}
    }
    private void Bind(){
        if(boundResolution>=0&&boundResolution!=resolution){unetGraphs[boundResolution].ReleaseMemory();vaeGraphs[boundResolution].ReleaseMemory();if(inputs!=null)inputs.ReleaseResolution(boundResolution);}
        boundResolution=resolution;currentLatent=latentA[resolution];nextLatent=latentB[resolution];displayedImage=images[resolution];if(finalView!=null)finalView.texture=displayedImage;
        sampler.SetInt("_LatentSize",unetGraphs[resolution].nativeSize/8);sampler.SetInt("_RGBSize",unetGraphs[resolution].nativeSize);
        if(photoExport!=null)photoExport.SetTexture("_MainTex",displayedImage);
    }
    private void Apply(){
        if(protocol!=1||resolution<0||resolution>=unetGraphs.Length||preset<0||preset>=presetNames.Length||totalSteps<1||totalSteps>50)return;
        if(boundResolution!=resolution)Bind();
        if(localEpoch!=epoch){
            localEpoch=epoch;localStep=0;firstStep=0;activeSeconds=0;algorithmStage=0;lastHistory=-1;lastDebugNode=-1;runStart=Time.realtimeSinceStartup;
            if(performance!=null)performance.ResetProgress();
            currentLatent=latentA[resolution];nextLatent=latentB[resolution];
            ClearSamplerInputs();
            sampler.SetInt("_SeedLo",seed&65535);sampler.SetInt("_SeedHi",(seed>>16)&65535);sampler.SetInt("_Mode",0);
            VRCGraphics.Blit(null,currentLatent,sampler);Preview();
            for(int i=0;i<history.Length;i++)VRCGraphics.Blit(displayedImage,history[i]);
            sampler.SetInt("_Mode",8);VRCGraphics.Blit(null,debugMemory,sampler);if(quantDebugMemory!=null)VRCGraphics.Blit(null,quantDebugMemory,sampler);if(memoryText!=null)memoryText.text="Awaiting computed tensor";
            if(phase!=0){if(inputs!=null)inputs.Begin();else BeginUNet(true);}
        }
        Labels();
    }
    public override void OnDeserialization(){Apply();}
    public override void OnOwnershipTransferred(VRCPlayerApi player){if(Owner()){if(phase==1)phase=2;if(player!=null)authorityId=player.playerId;Publish();}Labels();}
    public override bool OnOwnershipRequest(VRCPlayerApi requester,VRCPlayerApi newOwner){return phase!=1||newOwner!=null&&newOwner.playerId==authorityId;}
    public override void OnPlayerJoined(VRCPlayerApi player){if(Owner()){InitializeAuthority();Publish();}}
    public override void OnPlayerLeft(VRCPlayerApi player){if(Owner()&&player.playerId==authorityId){if(phase==1)phase=2;InitializeAuthority();}}
    public void RequestControl(){
        // Ownership is available between runs or while paused; running owner keeps control.
        if(phase==1)return;
        if(Networking.LocalPlayer!=null)Networking.SetOwner(Networking.LocalPlayer,gameObject);
        if(Owner()&&Networking.LocalPlayer!=null){authorityId=Networking.LocalPlayer.playerId;Publish();}
        Labels();
    }
    private void ResetConfiguration(){phase=0;epoch++;Publish();Apply();}
    public void ToggleInference(){localInference=!localInference;if(!localInference&&Owner()&&phase==1){phase=2;Publish();}Labels();}
    public void StartGeneration(){if(!Edit()||inputs!=null&&!inputs.CanStart())return;if(inputs!=null&&inputs.NeedsPhotoEncoding()){photoSize=0;photoBytes=new byte[0];}localInference=true;epoch++;phase=1;Publish();Apply();}
    public void PauseResume(){if(!Owner())return;if(phase==1)phase=2;else if(phase==2){localInference=true;phase=1;}Publish();Labels();}
    public void SetPreset(){if(!Edit())return;preset=Mathf.Clamp(commandValue,0,presetNames.Length-1);customPrompt=false;if(inputs!=null)inputs.LoadPresetText();ResetConfiguration();}
    public void NextPreset(){commandValue=(preset+1)%presetNames.Length;SetPreset();}
    public void PreviousPreset(){commandValue=(preset+presetNames.Length-1)%presetNames.Length;SetPreset();}
    public void ApplyPrompt(){if(inputs!=null)inputs.ApplyPrompt();}
    public void TogglePhoto(){if(!Edit())return;usePhoto=!usePhoto;ResetConfiguration();}
    public void SetStrength(){if(!Edit())return;imageStrength=Mathf.Clamp(commandValue/100f,.05f,1f);ResetConfiguration();}
    public void ToggleExport(){if(performance!=null)performance.ToggleExport();}
    public void SetResolution(){if(!Edit())return;resolution=Mathf.Clamp(commandValue,0,unetGraphs.Length-1);phase=0;epoch++;Publish();Apply();}
    public void SetSteps(){if(!Edit())return;totalSteps=Mathf.Clamp(commandValue,1,50);ResetConfiguration();}
    public void SetSeed(){if(!Edit()||seedInput==null)return;int parsed;if(int.TryParse(seedInput.text,out parsed)){seed=parsed;ResetConfiguration();}}
    public void NextSeed(){if(!Edit())return;seed=seed==int.MaxValue?0:seed+1;ResetConfiguration();}
    public void ToggleDetails(){details=!details;Labels();}
    public void TogglePreviews(){vaePreviews=!vaePreviews;Labels();}
    public void SelectMemory(){selectedNode=commandValue;lastDebugNode=-1;Labels();}
    public void SetSpeed(){passesPerFrame=Mathf.Clamp(commandValue,1,32);frameBudgetMs=passesPerFrame>=32?8:passesPerFrame>=16?4:2;Labels();}
    private int TimeAt(int step){return (totalSteps-1-step)*(1000/totalSteps)+1;}
    private int HistorySlot(){return Mathf.Min(history.Length-2,1+(localStep-firstStep)*(history.Length-2)/Mathf.Max(1,totalSteps-firstStep));}
    public void InputsReady(){
        if(usePhoto&&inputs!=null){
            firstStep=totalSteps-Mathf.Clamp(Mathf.FloorToInt(totalSteps*imageStrength),1,totalSteps);localStep=firstStep;timestep=TimeAt(localStep);
            ClearSamplerInputs();sampler.SetTexture("_SourceLatent",inputs.photoLatentTexture);sampler.SetFloat("_Alpha",alpha[timestep]);sampler.SetInt("_Mode",9);VRCGraphics.Blit(null,currentLatent,sampler);Preview();
            for(int i=0;i<history.Length;i++)VRCGraphics.Blit(displayedImage,history[i]);
        }
        BeginUNet(true);
    }
    private void BeginUNet(bool negative){
        unconditional=negative;algorithmStage=negative?1:2;timestep=TimeAt(localStep);
        sampler.SetInt("_Mode",6);sampler.SetInt("_Timestep",timestep);VRCGraphics.Blit(null,timeMemory,sampler);
        lastQuantNode=-1;executor.graph=unetGraphs[resolution];executor.latent=currentLatent;executor.condition=customPrompt&&inputs!=null?(Texture)(negative?inputs.negativeCondition:inputs.positiveCondition):embeddings[negative?0:preset+1];executor.timeEmbedding=timeMemory;executor.Begin();
    }
    private void BeginVAE(){previewDecode=localStep<totalSteps;lastQuantNode=-1;algorithmStage=5;executor.graph=vaeGraphs[resolution];executor.latent=currentLatent;executor.Begin();}
    private void CopyEpsilon(RenderTexture target){
        ClearSamplerInputs();
        sampler.SetInt("_Mode",7);sampler.SetTexture("_RGB",executor.Output());sampler.SetInt("_RGBShift",executor.graph.shifts[executor.graph.outputNode]);VRCGraphics.Blit(null,target,sampler);
    }
    private void Preview(){sampler.SetTexture("_State",currentLatent);sampler.SetInt("_Mode",4);VRCGraphics.Blit(null,displayedImage,sampler);}
    private void DebugCapture(){
        if(!details||executor.lastCompletedNode<0||executor.lastCompletedNode==lastDebugNode)return;
        int n=executor.lastCompletedNode;if(selectedNode>=0&&selectedNode!=n)return;
        lastDebugNode=n;SDGraphData g=executor.graph;Material m=g.operators[n];
        // Dedicated retained RT: pool contents may be reused as soon as this node dies.
        memoryDisplay.SetTexture("_MainTex",executor.lastMemory);memoryDisplay.SetInt("_Shift",g.shifts[n]);
        memoryDisplay.SetInt("_H",m.GetInt("_OutH"));memoryDisplay.SetInt("_W",m.GetInt("_OutW"));memoryDisplay.SetInt("_Channels",g.outChannels[n]);
        VRCGraphics.Blit(null,debugMemory,memoryDisplay);
        if(memoryText!=null)memoryText.text=g.displayName+" / "+n+"\n"+g.nodeNames[n]+"\n"+m.GetInt("_OutH")+" x "+m.GetInt("_OutW")+" x "+g.outChannels[n]+" | NHWC4 FP16\nOrange = positive; blue = negative";
    }
    private void QuantCapture(){
        if(!details||quantDebugMemory==null||executor.finished||executor.substage!=3||executor.tileOffset!=0)return;
        var g=executor.graph;int n=executor.node;if(g.floatActivation||g.op[n]!=0||n==lastQuantNode)return;lastQuantNode=n;
        quantCaptureMaterial.SetTexture("_MainTex",g.quantized);quantCaptureMaterial.SetInt("_Shift",g.quantizedShift);
        quantCaptureMaterial.SetInt("_H",g.inH[n]);quantCaptureMaterial.SetInt("_W",g.inW[n]);quantCaptureMaterial.SetInt("_Channels",g.inChannels[n]);quantCaptureMaterial.SetInt("_Packed",1);
        VRCGraphics.Blit(null,quantDebugMemory,quantCaptureMaterial);
    }
    private void Advance(){
        if(algorithmStage>=7&&inputs!=null){inputs.Advance();DebugCapture();QuantCapture();return;}
        if(algorithmStage==1||algorithmStage==2||algorithmStage==5){
            if(!executor.finished){executor.Compute();DebugCapture();QuantCapture();return;}
            if(algorithmStage==1){CopyEpsilon(unconditionalMemory[resolution]);BeginUNet(false);return;}
            if(algorithmStage==2){CopyEpsilon(conditionalMemory[resolution]);algorithmStage=3;return;}
            sampler.SetInt("_Mode",5);sampler.SetTexture("_RGB",executor.Output());sampler.SetInt("_RGBShift",executor.graph.shifts[executor.graph.outputNode]);VRCGraphics.Blit(null,displayedImage,sampler);
            if(previewDecode){VRCGraphics.Blit(displayedImage,history[HistorySlot()]);BeginUNet(true);return;}
            VRCGraphics.Blit(displayedImage,history[history.Length-1]);algorithmStage=6;elapsed=Time.realtimeSinceStartup-runStart;
            if(Owner()){phase=3;Publish();}Labels();return;
        }
        if(algorithmStage==3){
            sampler.SetTexture("_State",currentLatent);sampler.SetTexture("_Conditional",conditionalMemory[resolution]);sampler.SetTexture("_Unconditional",unconditionalMemory[resolution]);
            sampler.SetFloat("_Alpha",alpha[timestep]);sampler.SetFloat("_NextAlpha",localStep+1>=totalSteps?alpha[0]:alpha[TimeAt(localStep+1)]);sampler.SetFloat("_Guidance",guidance);
            sampler.SetInt("_Mode",2);VRCGraphics.Blit(null,cleanMemory[resolution],sampler);algorithmStage=4;return;
        }
        if(algorithmStage==4){
            sampler.SetInt("_Mode",3);VRCGraphics.Blit(null,nextLatent,sampler);RenderTexture swap=currentLatent;currentLatent=nextLatent;nextLatent=swap;localStep++;
            if(!vaePreviews){Preview();int slot=HistorySlot();if(slot>lastHistory){VRCGraphics.Blit(displayedImage,history[slot]);lastHistory=slot;}}
            if(localStep>=totalSteps||(vaePreviews&&(localStep-firstStep)%Mathf.Max(1,(totalSteps-firstStep)/6)==0))BeginVAE();else BeginUNet(true);return;
        }
    }
    private void Update(){
        if(localInference&&(phase==1||phase==3)&&localEpoch==epoch&&algorithmStage>0&&algorithmStage!=6){
            activeSeconds+=Time.unscaledDeltaTime;
            float started=Time.realtimeSinceStartup;
            for(int i=0;i<passesPerFrame;i++){Advance();if((Time.realtimeSinceStartup-started)*1000>=frameBudgetMs||algorithmStage==6||algorithmStage==0)break;}
        }
        if(Time.realtimeSinceStartup>=nextLabel){nextLabel=Time.realtimeSinceStartup+.25f;Labels();}
    }
    private void Labels(){
        bool owner=Owner();if(!buttonStateReady||owner!=lastButtonOwner||phase!=lastButtonPhase){buttonStateReady=true;lastButtonOwner=owner;lastButtonPhase=phase;if(buttons!=null)for(int i=0;i<buttons.Length;i++)buttons[i].Refresh();}
        bool edit=Edit();if(seedInput!=null)seedInput.interactable=edit;
        if(inputs!=null){if(inputs.positiveInput!=null)inputs.positiveInput.interactable=edit;if(inputs.negativeInput!=null)inputs.negativeInput.interactable=edit;}
        if(seedInput!=null&&seed!=shownSeed&&!seedInput.isFocused){seedInput.text=seed.ToString();shownSeed=seed;}
        if(statusText!=null)statusText.text="Counterfeit-V3.0 | W8A8 Conv/Linear\n"+unetGraphs[resolution].nativeSize+" x "+unetGraphs[resolution].nativeSize+" native | seed "+seed+" | "+localStep+" / "+totalSteps+" steps\n"+(!localInference?"LOCAL INFERENCE OFF":phase==0?"IDLE":phase==2?"PAUSED":algorithmStage==6?"COMPLETE":"RUNNING")+" | "+(phase==0?0f:algorithmStage==6?elapsed:Time.realtimeSinceStartup-runStart).ToString("F1")+" s\n"+(Owner()?"You have control":"Shared configuration; request control between runs")+" | details "+(details?"ON":"OFF");
        if(presetText!=null)presetText.text=customPrompt?"CUSTOM PROMPT · CLIP W8A16":"PRESET "+(preset+1)+" / "+presetNames.Length+": "+presetNames[preset];
        if(algorithmText!=null)algorithmText.text="REAL COMPUTATION\n"+(algorithmStage==1?"U-Net: negative condition":algorithmStage==2?"U-Net: prompt condition":algorithmStage==3?"CFG: eps = eps_u + s (eps_c - eps_u)":algorithmStage==4?"DDIM: x_t to x_prev":algorithmStage==5?"VAE: latent / 0.18215 to RGB":algorithmStage==6?"VAE decoded image":algorithmStage==7?"CLIP: causal text Transformer":algorithmStage==9?"VAE encoder: RGB to latent moments":algorithmStage==10?"Photo latent readback / synchronization":"Ready")+"\nt = "+timestep+" | alpha = "+alpha[Mathf.Clamp(timestep,0,999)].ToString("F6")+"\n"+(executor.graph==null?"":executor.node+" / "+executor.graph.operators.Length+" nodes | tile "+executor.tileOffset+" | attention key "+executor.keyOffset)+"\n"+(vaePreviews?"Intermediate images: real VAE previews. Final: real VAE decode.":"Intermediate images: linear latent preview. Final: real VAE decode.");
    }
}
