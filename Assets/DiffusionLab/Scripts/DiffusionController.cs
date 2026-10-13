using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon.Common;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiffusionController : UdonSharpBehaviour {
    public Material[] layers;
    public RenderTexture[] layerMemory;
    public int[] layerChannels;
    public RenderTexture stateA,stateB,cleanPrediction,conditionalNoise,guidedNoise;
    public bool classifierFreeGuidance;
    public float guidanceScale=3f;
    public Material sampler,mainDisplay,cleanDisplay,noiseDisplay,memoryDisplay;
    public RenderTexture[] historyMemory;
    public Material[] historyDisplays;
    public Text[] historyLabels;
    public Text statusText,memoryText,controlText,configText;
    public InputField seedInput;
    public DiffusionControlRequest[] requests;
    public float[] alphaCumprod;
    public int modelVersion=1;
    public int passesPerFrame=1;
    public float passInterval=.016f;
    public int commandValue;
    public bool validationRunOnStart;
    public Texture2D validationInput;
    public float[] validationExpected;
    public float validationMaxError;
    public bool validationPassed;
    private float[] validationReadback;
    private float runStartedAt;
    private int validationReadEpoch=-1;
    [UdonSynced] public int schemaVersion=1;
    [UdonSynced] public int networkModelVersion=1;
    [UdonSynced] public int epoch;
    [UdonSynced] public int revision;
    [UdonSynced] public int authorityId=-1;
    [UdonSynced] public int digit=3;
    [UdonSynced] public int seed=1234;
    [UdonSynced] public int totalSteps=32;
    [UdonSynced] public int completedSteps;
    // Idle 0, Running 1, Paused 2, Complete 3.
    [UdonSynced] public int phase;
    [UdonSynced] public int grantedId=-1;
    [UdonSynced] public bool fixtureRun;
    public int localStep;
    public int localLayer;
    public int localEpoch=-1;
    private int selectedMemory;
    private int pendingPasses;
    private int stepTarget;
    private RenderTexture current,next;
    private float lastPass;
    private float lastPublish;
    private float lastLabels;
    private int handoffId=-1;
    private bool handoffWaiting;
    private string notice="";
    private int appliedRevision=-1;
    private int savedSchema,savedModel,savedEpoch,savedAuthority,savedDigit,savedSeed,savedSteps,savedCompleted,savedPhase,savedGrant;
    private bool savedFixture;
    private void CacheSnapshot() {
        appliedRevision=revision;savedSchema=schemaVersion;savedModel=networkModelVersion;savedEpoch=epoch;
        savedAuthority=authorityId;savedDigit=digit;savedSeed=seed;savedSteps=totalSteps;
        savedCompleted=completedSteps;savedPhase=phase;savedGrant=grantedId;savedFixture=fixtureRun;
    }
    private void RestoreSnapshot() {
        revision=appliedRevision;schemaVersion=savedSchema;networkModelVersion=savedModel;epoch=savedEpoch;
        authorityId=savedAuthority;digit=savedDigit;seed=savedSeed;totalSteps=savedSteps;
        completedSteps=savedCompleted;phase=savedPhase;grantedId=savedGrant;fixtureRun=savedFixture;
    }

    private bool IsController() {
        return Networking.LocalPlayer==null ||
          (Networking.IsOwner(gameObject)&&authorityId==Networking.LocalPlayer.playerId);
    }
    private bool CanEdit() {
        if(IsController()&&!handoffWaiting) return true;
        notice="Please REQUEST CONTROL first";
        UpdateLabels();return false;
    }
    private void Start() {
        current=stateA;next=stateB;
        CacheSnapshot();ApplyState();UpdateLabels();
        if(validationRunOnStart) SendCustomEventDelayedSeconds(nameof(BeginValidationRun),2f);
    }
    public void ApplyState() {
        if(networkModelVersion!=modelVersion||schemaVersion!=1) {
            notice="Incompatible model/protocol version";return;
        }
        if(localEpoch!=epoch) {
            localEpoch=epoch;localStep=0;localLayer=0;pendingPasses=0;stepTarget=0;
            validationPassed=false;validationMaxError=0;validationReadEpoch=-1;runStartedAt=Time.realtimeSinceStartup;
            current=stateA;next=stateB;
            sampler.SetTexture("_State",null);sampler.SetTexture("_Noise",null);sampler.SetTexture("_UnconditionedNoise",null);
            sampler.SetInt("_SeedLo",seed&65535);sampler.SetInt("_SeedHi",(seed>>16)&65535);
            sampler.SetInt("_Mode",phase==0?3:0);
            VRCGraphics.Blit(null,current,sampler);
            sampler.SetInt("_Mode",3);VRCGraphics.Blit(null,next,sampler);
            VRCGraphics.Blit(null,cleanPrediction,sampler);
            VRCGraphics.Blit(null,conditionalNoise,sampler);VRCGraphics.Blit(null,guidedNoise,sampler);
            for(int i=0;i<layerMemory.Length;i++) VRCGraphics.Blit(null,layerMemory[i],sampler);
            for(int i=0;i<historyMemory.Length;i++) {
                VRCGraphics.Blit(null,historyMemory[i],sampler);
                if(historyLabels!=null&&i<historyLabels.Length) historyLabels[i].text=i==0?"NOISE":i==historyMemory.Length-1?"FINAL":"—";
            }
            BindDisplays();
            if(phase!=0) {
                if(fixtureRun&&validationInput!=null) VRCGraphics.Blit(validationInput,current);
                VRCGraphics.Blit(current,historyMemory[0]);
            }
        }
        UpdateLabels();
    }
    private void BindDisplays() {
        mainDisplay.SetTexture("_MainTex",current);
        cleanDisplay.SetTexture("_MainTex",cleanPrediction);
        noiseDisplay.SetTexture("_MainTex",guidedNoise);
        memoryDisplay.SetTexture("_MainTex",layerMemory[selectedMemory]);
        memoryDisplay.SetFloat("_Channels",layerChannels[selectedMemory]);
        for(int i=0;i<historyDisplays.Length;i++) historyDisplays[i].SetTexture("_MainTex",historyMemory[i]);
    }
    private int TimeAt(int step) {
        if(totalSteps<=1) return 0;
        if(classifierFreeGuidance) return (totalSteps-1-step)*(alphaCumprod.Length/totalSteps);
        return Mathf.RoundToInt((alphaCumprod.Length-1)*(1f-step/(float)(totalSteps-1)));
    }
    private void Update() {
        if(networkModelVersion!=modelVersion||schemaVersion!=1) return;
        if(phase!=0&&localStep<totalSteps&&!handoffWaiting) {
            bool owner=IsController();
            bool advance=owner?(phase==1||localStep<completedSteps||localStep<stepTarget||pendingPasses>0):localStep<completedSteps;
            if(advance&&Time.realtimeSinceStartup-lastPass>=passInterval) {
                lastPass=Time.realtimeSinceStartup;
                for(int p=0;p<passesPerFrame;p++) {
                    if(localStep>=totalSteps) break;
                    if(owner&&phase!=1&&localStep>=completedSteps&&localStep>=stepTarget&&pendingPasses<=0) break;
                    if(!owner&&localStep>=completedSteps) break;
                    ComputePass();
                    if(pendingPasses>0) pendingPasses--;
                }
            }
        }
        if(Time.realtimeSinceStartup-lastLabels>.2f) {lastLabels=Time.realtimeSinceStartup;UpdateLabels();}
    }
    public void ComputePass() {
        if(localStep>=totalSteps) return;
        int t=TimeAt(localStep);
        int evaluations=classifierFreeGuidance?2:1;
        if(localLayer<layers.Length*evaluations) {
            int index=localLayer%layers.Length;
            Material mat=layers[index];
            if(index==0) mat.SetTexture("_Input",current);
            mat.SetInt("_Class",localLayer<layers.Length?digit:10);mat.SetInt("_Timestep",t);
            VRCGraphics.Blit(null,layerMemory[index],mat);
            localLayer++;
            if(classifierFreeGuidance&&localLayer==layers.Length) VRCGraphics.Blit(layerMemory[index],conditionalNoise);
            return;
        }
        sampler.SetTexture("_State",current);
        sampler.SetTexture("_Noise",classifierFreeGuidance?conditionalNoise:layerMemory[layerMemory.Length-1]);
        sampler.SetTexture("_UnconditionedNoise",layerMemory[layerMemory.Length-1]);
        sampler.SetFloat("_Guidance",classifierFreeGuidance?guidanceScale:1f);
        sampler.SetFloat("_Alpha",alphaCumprod[t]);
        sampler.SetFloat("_NextAlpha",localStep+1>=totalSteps?1f:alphaCumprod[TimeAt(localStep+1)]);
        sampler.SetInt("_Mode",5);VRCGraphics.Blit(null,guidedNoise,sampler);
        sampler.SetInt("_Mode",2);VRCGraphics.Blit(null,cleanPrediction,sampler);
        sampler.SetInt("_Mode",1);VRCGraphics.Blit(null,next,sampler);
        RenderTexture swap=current;current=next;next=swap;
        localStep++;localLayer=0;
        mainDisplay.SetTexture("_MainTex",current);
        int previousBucket=(localStep-1)*(historyMemory.Length-1)/totalSteps;
        int bucket=localStep*(historyMemory.Length-1)/totalSteps;
        if(bucket>previousBucket) {
            VRCGraphics.Blit(current,historyMemory[bucket]);
            if(historyLabels!=null&&bucket<historyLabels.Length) historyLabels[bucket].text="STEP "+localStep;
        }
        if(localStep>=totalSteps) {
            Debug.Log("DIFFUSION_COMPLETE role="+(IsController()?"controller":"follower")+" class="+digit+" seed="+seed+" steps="+localStep+" shared="+completedSteps+" seconds="+(Time.realtimeSinceStartup-runStartedAt));
            if(fixtureRun&&validationExpected!=null&&validationExpected.Length==1024) {
                validationReadback=new float[1024];validationReadEpoch=epoch;
                VRCAsyncGPUReadback.Request(current,0,this);
            }
        }
        if(IsController()) {
            if(localStep>completedSteps) completedSteps=localStep;
            if(localStep>=totalSteps) phase=3;
            if(phase!=1||Time.realtimeSinceStartup-lastPublish>.25f||completedSteps%4==0) Publish();
        }
    }
    private void Publish() {
        if(Networking.LocalPlayer!=null&&!Networking.IsOwner(gameObject)) return;
        revision++;CacheSnapshot();lastPublish=Time.realtimeSinceStartup;RequestSerialization();UpdateLabels();
    }
    public void Generate() {
        if(!CanEdit()) return;
        fixtureRun=validationRunOnStart;
        epoch++;completedSteps=0;phase=1;grantedId=-1;notice="";runStartedAt=Time.realtimeSinceStartup;
        ApplyState();
        Publish();
    }
    public void BeginValidationRun() {
        if(Networking.LocalPlayer!=null&&!Networking.IsOwner(gameObject)) {
            validationRunOnStart=false;Debug.Log("DIFFUSION_VALIDATION_FOLLOW owner="+authorityId);return;
        }
        RequestControl();
        digit=3;totalSteps=32;
        Generate();validationRunOnStart=false;
        Debug.Log("DIFFUSION_VALIDATION_START model="+modelVersion+" class="+digit+" steps="+totalSteps);
    }
    public void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request) {
        if(validationReadEpoch!=epoch) return;
        if(request.hasError||!request.TryGetData(validationReadback)) {
            Debug.LogError("DIFFUSION_VALIDATION_FAIL readback_error");return;
        }
        float max=0;bool finite=true;
        for(int i=0;i<validationReadback.Length;i++) {
            float v=validationReadback[i];
            if(float.IsNaN(v)||float.IsInfinity(v)) finite=false;
            max=Mathf.Max(max,Mathf.Abs(v-validationExpected[i]));
        }
        validationMaxError=max;validationPassed=finite&&max<.003f;
        if(validationPassed) Debug.Log("DIFFUSION_VALIDATION_PASS max_abs="+max+" steps="+localStep+" seconds="+(Time.realtimeSinceStartup-runStartedAt));
        else Debug.LogError("DIFFUSION_VALIDATION_FAIL max_abs="+max+" finite="+finite);
    }
    public void ResetRun() {
        if(!CanEdit()) return;
        fixtureRun=false;epoch++;phase=0;completedSteps=0;notice="";ApplyState();Publish();
    }
    public void PauseResume() {
        if(!CanEdit()) return;
        if(phase==0||phase==3) {
            fixtureRun=false;epoch++;completedSteps=0;phase=2;notice="";
            ApplyState();Publish();return;
        }
        if(phase==1) {phase=2;completedSteps=localStep;pendingPasses=0;stepTarget=localStep;}
        else phase=1;
        Publish();
    }
    public void StepDenoise() {
        if(!CanEdit()||phase!=2||localStep!=completedSteps) return;
        stepTarget=Mathf.Min(localStep+1,totalSteps);
    }
    public void StepOperator() {
        if(!CanEdit()||phase!=2||localStep!=completedSteps) return;
        pendingPasses=1;
    }
    private void ConfigChanged() {
        notice="";fixtureRun=false;epoch++;phase=0;completedSteps=0;ApplyState();Publish();
    }
    public void SelectDigit() {if(!CanEdit())return;digit=Mathf.Clamp(commandValue,0,9);ConfigChanged();}
    public void SelectSteps() {if(!CanEdit())return;totalSteps=Mathf.Clamp(commandValue,4,128);ConfigChanged();}
    public void RandomSeed() {if(!CanEdit())return;seed=Random.Range(1,1000000000);ConfigChanged();}
    public void ApplySeed() {
        if(!CanEdit()||seedInput==null) return;
        // Udon-compatible bounded decimal input; reject rather than silently wrap.
        string text=seedInput.text;long n=0;
        if(text.Length==0||text.Length>9) {notice="Seed: enter 1-9 decimal digits";UpdateLabels();return;}
        for(int i=0;i<text.Length;i++) {
            int c=(int)text[i]-48;
            if(c<0||c>9) {notice="Seed accepts digits only";UpdateLabels();return;}
            n=n*10+c;
        }
        seed=(int)n;ConfigChanged();
    }
    public void NextMemory() {selectedMemory=(selectedMemory+1)%layerMemory.Length;BindDisplays();UpdateLabels();}
    public void PreviousMemory() {selectedMemory=(selectedMemory+layerMemory.Length-1)%layerMemory.Length;BindDisplays();UpdateLabels();}
    public void Faster() {passesPerFrame=Mathf.Min(8,passesPerFrame*2);passInterval=.016f;UpdateLabels();}
    public void Slower() {passesPerFrame=Mathf.Max(1,passesPerFrame/2);if(passesPerFrame==1)passInterval=Mathf.Min(1f,passInterval*2);UpdateLabels();}
    public void RequestControl() {
        if(Networking.LocalPlayer==null) {authorityId=0;Publish();return;}
        if(IsController()) return;
        if(authorityId<0||VRCPlayerApi.GetPlayerById(authorityId)==null) {
            Networking.SetOwner(Networking.LocalPlayer,gameObject);
            notice="";authorityId=Networking.LocalPlayer.playerId;grantedId=-1;phase=phase==1?2:phase;
            ClearOwnRequests();Publish();return;
        }
        for(int i=0;i<requests.Length;i++) {
            if(requests[i].requesterId==Networking.LocalPlayer.playerId&&Networking.IsOwner(requests[i].gameObject)) {notice="Control request pending";return;}
        }
        for(int i=0;i<requests.Length;i++) {
            int index=(Networking.LocalPlayer.playerId+i)%requests.Length;
            int id=requests[index].requesterId;
            if(id<0||VRCPlayerApi.GetPlayerById(id)==null||id==authorityId) {
                requests[index].Claim();notice="Control requested; waiting for grant";UpdateLabels();return;
            }
        }
        notice="Request queue full; try later";UpdateLabels();
    }
    public void GrantControl() {
        if(!CanEdit()||Networking.LocalPlayer==null) return;
        if(localStep<completedSteps) {notice="Catching up to shared state; please wait";UpdateLabels();return;}
        int id=-1;double oldest=double.MaxValue;
        for(int i=0;i<requests.Length;i++) {
            int p=requests[i].requesterId;
            if(p>=0&&p!=authorityId&&VRCPlayerApi.GetPlayerById(p)!=null&&requests[i].requestedAt<oldest) {
                id=p;oldest=requests[i].requestedAt;
            }
        }
        if(id<0) {notice="No pending requests";UpdateLabels();return;}
        // Pause at the last complete DDIM step; transfer only after its durable publication.
        phase=phase==1?2:phase;completedSteps=localStep;
        grantedId=id;handoffId=id;handoffWaiting=true;Publish();
    }
    public override void OnPostSerialization(SerializationResult result) {
        if(!handoffWaiting) return;
        if(!result.success) {SendCustomEventDelayedSeconds(nameof(RetryHandoff),.5f);return;}
        VRCPlayerApi p=VRCPlayerApi.GetPlayerById(handoffId);
        if(p!=null) Networking.SetOwner(p,gameObject);
        else {grantedId=-1;Publish();}
        handoffWaiting=false;
    }
    public void RetryHandoff() {if(handoffWaiting&&Networking.IsOwner(gameObject)) RequestSerialization();}
    private void ClearOwnRequests() {
        for(int i=0;i<requests.Length;i++) if(requests[i].requesterId==authorityId&&Networking.IsOwner(requests[i].gameObject))requests[i].ClearRequest();
    }
    public override bool OnOwnershipRequest(VRCPlayerApi requestingPlayer,VRCPlayerApi requestedOwner) {
        return requestedOwner!=null&&(authorityId<0||VRCPlayerApi.GetPlayerById(authorityId)==null||requestedOwner.playerId==authorityId||requestedOwner.playerId==grantedId);
    }
    public override void OnOwnershipTransferred(VRCPlayerApi player) {
        handoffWaiting=false;
        if(player==null||!player.isLocal) return;
        if(grantedId==player.playerId) {notice="";authorityId=player.playerId;grantedId=-1;ClearOwnRequests();Publish();}
        else if(authorityId>=0&&VRCPlayerApi.GetPlayerById(authorityId)==null) {
            authorityId=-1;phase=phase==1?2:phase;grantedId=-1;Publish();
        }
    }
    public override void OnPlayerLeft(VRCPlayerApi player) {
        if(Networking.IsOwner(gameObject)&&player.playerId==authorityId) {
            authorityId=-1;phase=phase==1?2:phase;grantedId=-1;Publish();
        }
    }
    public override void OnPlayerJoined(VRCPlayerApi player) {if(Networking.IsOwner(gameObject)) RequestSerialization();}
    public override void OnDeserialization() {ApplyReceivedState();}
    public void ApplyReceivedState() {
        if(revision<appliedRevision) {RestoreSnapshot();UpdateLabels();return;}
        CacheSnapshot();ApplyState();
    }
    public void UpdateLabels() {
        string owner="available";
        if(authorityId>=0) {VRCPlayerApi p=VRCPlayerApi.GetPlayerById(authorityId);if(p!=null)owner=p.displayName;}
        if(Networking.LocalPlayer==null) owner="Editor local";
        int waiting=0;
        for(int i=0;i<requests.Length;i++) {
            int id=requests[i].requesterId;
            if(id>=0&&id!=authorityId&&VRCPlayerApi.GetPlayerById(id)!=null)waiting++;
        }
        string phaseName=phase==0?"IDLE":phase==1?"RUNNING":phase==2?"PAUSED":"COMPLETE";
        if(statusText!=null) statusText.text=phaseName+"  · shared "+completedSteps+"/"+totalSteps+"  · local "+localStep+"/"+totalSteps+"\n"+
            "t="+(localStep<totalSteps?TimeAt(localStep):0)+"  · operator "+localLayer+"/"+(layers.Length*(classifierFreeGuidance?2:1)+1)+"  · CONTROL "+owner+"\n"+notice;
        if(configText!=null) configText.text="CLASS "+digit+"   SEED "+seed+"   DDIM "+totalSteps+" steps   · "+passesPerFrame+" passes/frame";
        if(controlText!=null) controlText.text="REQUESTS "+waiting+" · Shared settings / local GPU inference · floating-point results may differ";
        if(memoryText!=null) memoryText.text="LIVE GPU MEMORY · layer "+selectedMemory+" · "+layerChannels[selectedMemory]+" channels\n"+
            layerMemory[selectedMemory].width+" × "+layerMemory[selectedMemory].height+" RFloat · signed color range ±3";
    }
}
