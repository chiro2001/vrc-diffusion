using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;
using Object=UnityEngine.Object;

public static class BuildDiffusionLab {
    const string Root="Assets/DiffusionLab";
    const string ScenePath=Root+"/Scenes/DiffusionLab.unity";
    static Font font;
    static Transform world,canvas;
    static Material buttonMat;
    static DiffusionController controller;
    static void Folder(string p) {if(AssetDatabase.IsValidFolder(p))return;int i=p.LastIndexOf('/');if(i<0)return;Folder(p.Substring(0,i));AssetDatabase.CreateFolder(p.Substring(0,i),p.Substring(i+1));}
    static T Asset<T>(string p,Func<T> create) where T:Object {
        T x=AssetDatabase.LoadAssetAtPath<T>(p);if(x!=null)return x;
        if(AssetDatabase.LoadMainAssetAtPath(p)!=null)throw new Exception("Wrong asset type: "+p);
        x=create();AssetDatabase.CreateAsset(x,p);return x;
    }
    static Material Mat(string name,string shader) {
        var s=Shader.Find(shader);if(s==null)throw new Exception("Missing shader: "+shader);
        var m=Asset(Root+"/Materials/"+name+".mat",()=>new Material(s));m.shader=s;EditorUtility.SetDirty(m);return m;
    }
    static RenderTexture RT(string name,int w,int h) {
        var rt=Asset(Root+"/Textures/"+name+".renderTexture",()=>new RenderTexture(w,h,0,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear));
        rt.filterMode=FilterMode.Point;rt.wrapMode=TextureWrapMode.Clamp;rt.useMipMap=false;rt.autoGenerateMips=false;
        if(rt.width!=w||rt.height!=h){rt.Release();rt.width=w;rt.height=h;}
        EditorUtility.SetDirty(rt);return rt;
    }
    public static object Compile() {
        Folder(Root+"/Programs");
        foreach(string n in new[]{"DiffusionController","DiffusionButton","DiffusionControlRequest"}) {
            var p=Asset(Root+"/Programs/"+n+".asset",()=>ScriptableObject.CreateInstance<UdonSharpProgramAsset>());
            p.sourceCsScript=AssetDatabase.LoadAssetAtPath<MonoScript>(Root+"/Scripts/"+n+".cs");
            if(p.sourceCsScript==null)throw new Exception("Missing script "+n);
            EditorUtility.SetDirty(p);
        }
        AssetDatabase.SaveAssets();UdonSharpProgramAsset.CompileAllCsPrograms(true,true);
        if(UdonSharpProgramAsset.AnyUdonSharpScriptHasError())throw new Exception("Udon compile failed");
        return new{compiled=true};
    }
    public static object Build() {
        if(EditorApplication.isPlaying)throw new Exception("Stop PlayMode first");
        for(int j=0;j<SceneManager.sceneCount;j++)if(SceneManager.GetSceneAt(j).isDirty)throw new Exception("Preserve unsaved scene before build");
        foreach(string folder in new[]{"Scenes","Materials","Textures"})Folder(Root+"/"+folder);
        JObject spec=JObject.Parse(File.ReadAllText(Root+"/Data/model.json"));
        var entries=(JArray)spec["layers"];
        int tw=(int)spec["textureWidth"],count=(int)spec["weightCount"],th=(count+tw-1)/tw;
        byte[] raw=File.ReadAllBytes(Root+"/Data/weights.bytes");
        if(raw.Length!=count*4)throw new Exception("Weight byte count mismatch");
        if(spec["modelVersion"]!=null) {
            byte[] digest=System.Security.Cryptography.SHA256.Create().ComputeHash(raw);
            int actualVersion=BitConverter.ToInt32(digest,0)&int.MaxValue;
            if(actualVersion!=(int)spec["modelVersion"])throw new Exception("Weight/model metadata hash mismatch");
        }
        var padded=new byte[tw*th*4];Buffer.BlockCopy(raw,0,padded,0,raw.Length);
        var weights=Asset(Root+"/Textures/Weights.asset",()=>new Texture2D(tw,th,TextureFormat.RFloat,false,true));
        if(weights.width!=tw||weights.height!=th)weights.Reinitialize(tw,th,TextureFormat.RFloat,false);
        weights.filterMode=FilterMode.Point;weights.wrapMode=TextureWrapMode.Clamp;weights.LoadRawTextureData(padded);weights.Apply(false,false);EditorUtility.SetDirty(weights);
        var memory=new RenderTexture[entries.Count];var mats=new Material[entries.Count];var channels=new int[entries.Count];
        for(int i=0;i<entries.Count;i++) {
            var e=entries[i];int size=(int)e["size"],co=(int)e["outputChannels"];
            memory[i]=RT("Layer"+i.ToString("D2"),size*co,size);channels[i]=co;
            var m=Mat("Layer"+i.ToString("D2"),"DiffusionLab/Convolution");mats[i]=m;
            m.SetTexture("_Weights",weights);m.SetInt("_WeightWidth",tw);
            m.SetInt("_InSize",i==0?32:(int)entries[i-1]["size"]);
            m.SetInt("_InChannels",(int)e["inputChannels"]);
            m.SetInt("_PrimaryChannels",i==0?1:(int)entries[i-1]["outputChannels"]);
            m.SetInt("_OutSize",size);m.SetInt("_OutChannels",co);
            m.SetInt("_Stride",(int)e["stride"]);m.SetInt("_Upsample",(int)e["upsample"]);
            m.SetInt("_WeightOffset",(int)e["weightOffset"]);m.SetInt("_BiasOffset",(int)e["biasOffset"]);
            m.SetInt("_ConditionOffset",(int)e["conditionOffset"]);m.SetInt("_Timesteps",(int)spec["timesteps"]);
            m.SetInt("_Activation",i==entries.Count-1?0:1);
            if(i>0)m.SetTexture("_Input",memory[i-1]);
            int skip=(int)e["skip"];if(skip>=0)m.SetTexture("_Skip",memory[skip]);
            EditorUtility.SetDirty(m);
        }
        var stateA=RT("StateA",32,32);var stateB=RT("StateB",32,32);var clean=RT("CleanPrediction",32,32);
        var sampler=Mat("Sampler","DiffusionLab/Sampling");
        var main=Mat("CurrentImage","DiffusionLab/MemoryDisplay");main.SetFloat("_Range",1);main.SetFloat("_Signed",0);main.SetFloat("_Channels",1);main.SetTexture("_MainTex",stateA);
        var pred=Mat("CleanImage","DiffusionLab/MemoryDisplay");pred.SetFloat("_Range",1);pred.SetFloat("_Signed",0);pred.SetFloat("_Channels",1);pred.SetTexture("_MainTex",clean);
        var noise=Mat("NoiseImage","DiffusionLab/MemoryDisplay");noise.SetFloat("_Range",3);noise.SetFloat("_Signed",1);noise.SetFloat("_Channels",1);noise.SetTexture("_MainTex",memory.Last());
        var mem=Mat("MemoryAtlas","DiffusionLab/MemoryDisplay");mem.SetFloat("_Range",3);mem.SetFloat("_Signed",1);mem.SetFloat("_Channels",16);mem.SetTexture("_MainTex",memory[0]);
        var history=new RenderTexture[8];var hd=new Material[8];
        for(int i=0;i<8;i++){history[i]=RT("History"+i,32,32);hd[i]=Mat("History"+i,"DiffusionLab/MemoryDisplay");hd[i].SetFloat("_Range",1);hd[i].SetFloat("_Channels",1);hd[i].SetFloat("_Signed",0);hd[i].SetTexture("_MainTex",history[i]);}
        Scene scene=File.Exists(ScenePath)?EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single):EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        foreach(var go in scene.GetRootGameObjects())if(go.name=="DiffusionLabWorld")Object.DestroyImmediate(go);
        world=new GameObject("DiffusionLabWorld").transform;
        var dark=Mat("Panel","Unlit/Color");dark.color=new Color(.018f,.026f,.045f);
        var floor=Mat("Floor","Unlit/Color");floor.color=new Color(.05f,.065f,.09f);
        buttonMat=Mat("Buttons","Unlit/Color");buttonMat.color=new Color(.06f,.19f,.25f);
        Primitive("Floor",PrimitiveType.Cube,new Vector3(0,-.1f,0),new Vector3(18,.2f,18),floor,true);
        Primitive("Backboard",PrimitiveType.Quad,new Vector3(0,2.7f,3.15f),new Vector3(8.6f,5.1f,1),dark,false);
        var desc=new GameObject("VRCWorld");desc.transform.SetParent(world);var d=desc.AddComponent<VRCSceneDescriptor>();desc.AddComponent<VRC.Core.PipelineManager>();
        var spawn=new GameObject("Spawn");spawn.transform.SetParent(world);spawn.transform.position=new Vector3(0,.05f,-3.5f);d.spawns=new[]{spawn.transform};
        var cgo=new GameObject("InferenceController");cgo.transform.SetParent(world);controller=cgo.AddUdonSharpComponent<DiffusionController>();
        controller.modelVersion=(int)(spec["modelVersion"]??spec["trainingIteration"]);controller.networkModelVersion=controller.modelVersion;
        controller.layers=mats;controller.layerMemory=memory;controller.layerChannels=channels;
        var fixture=Asset(Root+"/Textures/ValidationInput.asset",()=>new Texture2D(32,32,TextureFormat.RFloat,false,true));
        fixture.LoadRawTextureData(File.ReadAllBytes(Root+"/Data/reference-input.bytes"));fixture.filterMode=FilterMode.Point;fixture.Apply();EditorUtility.SetDirty(fixture);
        controller.validationInput=fixture;
        byte[] expectedBytes=File.ReadAllBytes(Root+"/Data/reference-final.bytes");controller.validationExpected=new float[1024];Buffer.BlockCopy(expectedBytes,0,controller.validationExpected,0,expectedBytes.Length);
        controller.conditionalNoise=RT("ConditionalNoise",32,32);controller.guidedNoise=RT("GuidedNoise",32,32);
        controller.classifierFreeGuidance=(bool?)spec["classifierFreeGuidance"]??false;
        controller.guidanceScale=(float?)spec["guidanceDefault"]??3f;
        controller.stateA=stateA;controller.stateB=stateB;controller.cleanPrediction=clean;controller.sampler=sampler;
        controller.mainDisplay=main;controller.cleanDisplay=pred;controller.noiseDisplay=noise;controller.memoryDisplay=mem;
        controller.historyMemory=history;controller.historyDisplays=hd;controller.alphaCumprod=spec["alphaCumprod"].ToObject<float[]>();
        controller.requests=new DiffusionControlRequest[16];
        for(int i=0;i<controller.requests.Length;i++) {var g=new GameObject("ControlRequest"+i);g.transform.SetParent(world);var r=g.AddUdonSharpComponent<DiffusionControlRequest>();r.controller=controller;controller.requests[i]=r;SaveProxy(r);}
        Primitive("Current x_t",PrimitiveType.Quad,new Vector3(-2.15f,3.5f,3),new Vector3(1.95f,1.95f,1),main,false);
        Primitive("Predicted x0",PrimitiveType.Quad,new Vector3(-.25f,4.02f,3),new Vector3(.95f,.95f,1),pred,false);
        Primitive("Predicted epsilon",PrimitiveType.Quad,new Vector3(-.25f,2.8f,3),new Vector3(.95f,.95f,1),noise,false);
        Primitive("Live memory",PrimitiveType.Quad,new Vector3(2.15f,3.5f,3),new Vector3(2.25f,2.25f,1),mem,false);
        var cg=new GameObject("Interface",typeof(RectTransform),typeof(Canvas),typeof(GraphicRaycaster),typeof(VRCUiShape),typeof(BoxCollider));cg.transform.SetParent(world);
        cg.layer=5;
        var cvs=cg.GetComponent<Canvas>();cvs.renderMode=RenderMode.WorldSpace;canvas=cg.transform;
        cg.transform.position=new Vector3(0,2.7f,2.70f);cg.transform.localScale=Vector3.one*.004f;
        cg.GetComponent<RectTransform>().sizeDelta=new Vector2(2150,1300);
        var box=cg.GetComponent<BoxCollider>();box.size=new Vector3(370,65,1);box.center=new Vector3(0,-375,0);box.isTrigger=false;
        font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Label("Title","DIFFUSION LAB / MNIST",0,550,1900,65,40);
        Label("Subtitle","CLASS-CONDITIONAL U-NET · DDIM · FRAGMENT SHADER INFERENCE",0,492,1900,40,23);
        Label("CurrentLabel","CURRENT IMAGE  x_t",-537,470,510,35,24);
        Label("CleanLabel","PREDICTED x_0",-62,458,340,32,18);
        Label("NoiseLabel","PREDICTED NOISE",-62,170,340,32,18);
        controller.memoryText=Label("MemoryLabel","LIVE GPU MEMORY",537,470,660,65,19);
        controller.statusText=Label("Status","IDLE",-380,-100,1080,95,22);
        controller.controlText=Label("Control","CONTROL",0,-625,1950,30,16);
        controller.configText=Label("Config","CLASS / SEED / STEPS",537,-110,720,32,18);
        controller.historyLabels=new Text[8];
        for(int i=0;i<8;i++) {
            float x=-3.5f+i;
            Primitive("History "+i,PrimitiveType.Quad,new Vector3(x,1.68f,3),new Vector3(.68f,.68f,1),hd[i],false);
            Primitive("History header "+i,PrimitiveType.Quad,new Vector3(x,2.06f,2.98f),new Vector3(.68f,.10f,1),dark,false);
            controller.historyLabels[i]=Label("HistoryLabel"+i,i==0?"NOISE":i==7?"FINAL":"—",x/.004f,-160,170,24,16);
        }
        for(int i=0;i<10;i++)Button("CLASS "+i,"SelectDigit",i,-3.2f+i*.48f,.92f,.43f);
        Button("GENERATE","Generate",0,2.25f,.92f,1.5f);
        Button("RESET","ResetRun",0,3.55f,.92f,.85f);
        Button("INIT / PAUSE / RUN","PauseResume",0,-2.95f,.49f,1.5f);
        Button("STEP DENOISE","StepDenoise",0,-1.28f,.49f,1.55f);
        Button("STEP OPERATOR","StepOperator",0,.4f,.49f,1.55f);
        Button("REQUEST CONTROL","RequestControl",0,2.1f,.49f,1.55f);
        Button("GRANT","GrantControl",0,3.55f,.49f,.85f);
        Button("MEM <","PreviousMemory",0,3.8f,4.1f,.75f);
        Button("MEM >","NextMemory",0,3.8f,3.66f,.75f);
        Button("SLOWER","Slower",0,3.8f,3.22f,.75f);
        Button("FASTER","Faster",0,3.8f,2.78f,.75f);
        Button("16 STEPS","SelectSteps",16,-3.5f,1.20f,.7f);
        Button("32 STEPS","SelectSteps",32,-2.7f,1.20f,.7f);
        Button("64 STEPS","SelectSteps",64,-1.9f,1.20f,.7f);
        // Seed field uses the VRC world-space UI raycaster; explicit APPLY commits it.
        controller.seedInput=Input("SeedInput",new Vector2(0,-375),new Vector2(370,65));
        Button("APPLY SEED","ApplySeed",0,1.35f,1.20f,1.05f);
        Button("RANDOM SEED","RandomSeed",0,2.85f,1.20f,1.5f);
        var cameraGO=new GameObject("PreviewCamera");cameraGO.transform.SetParent(world);cameraGO.transform.position=new Vector3(0,2.65f,-7.8f);cameraGO.transform.LookAt(new Vector3(0,2.65f,3));
        var cam=cameraGO.AddComponent<Camera>();cam.enabled=false;cam.fieldOfView=34;cam.backgroundColor=new Color(.025f,.03f,.045f);cam.clearFlags=CameraClearFlags.SolidColor;
        RenderSettings.ambientLight=Color.white;
        SaveProxy(controller);AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene,ScenePath);
        EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
        return new{scene=ScenePath,layers=entries.Count,weights=count,shaderSupported=mats.All(m=>m.shader.isSupported),trainingIteration=(int)spec["trainingIteration"]};
    }
    static void SaveProxy(UdonSharpBehaviour b){UdonSharpEditorUtility.CopyProxyToUdon(b);EditorUtility.SetDirty(b);EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(b));}
    static GameObject Primitive(string name,PrimitiveType type,Vector3 pos,Vector3 scale,Material mat,bool collider) {
        var g=GameObject.CreatePrimitive(type);g.name=name;g.transform.SetParent(world);g.transform.position=pos;g.transform.localScale=scale;g.GetComponent<Renderer>().sharedMaterial=mat;
        if(!collider)Object.DestroyImmediate(g.GetComponent<Collider>());return g;
    }
    static Text Label(string name,string text,float x,float y,float w,float h,int size) {
        var g=new GameObject(name,typeof(RectTransform),typeof(CanvasRenderer),typeof(Text));g.transform.SetParent(canvas,false);
        var r=g.GetComponent<RectTransform>();r.anchoredPosition=new Vector2(x,y);r.sizeDelta=new Vector2(w,h);
        g.layer=5;var t=g.GetComponent<Text>();t.font=font;t.text=text;t.fontSize=size;t.color=new Color(.85f,.93f,1);t.alignment=TextAnchor.MiddleCenter;t.raycastTarget=false;return t;
    }
    static void Button(string label,string command,int value,float x,float y,float w) {
        var g=Primitive(label,PrimitiveType.Cube,new Vector3(x,y,2.83f),new Vector3(w,.26f,.16f),buttonMat,true);
        var b=g.AddUdonSharpComponent<DiffusionButton>();b.controller=controller;b.command=command;b.value=value;SaveProxy(b);
        var backing=UdonSharpEditorUtility.GetBackingUdonBehaviour(b);backing.interactText=label;backing.proximity=3;EditorUtility.SetDirty(backing);
        Label("Label "+label,label,x/.004f,(y-2.7f)/.004f,w/.004f-10,55,18);
    }
    static InputField Input(string name,Vector2 pos,Vector2 size) {
        var g=new GameObject(name,typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(InputField));g.transform.SetParent(canvas,false);
        g.layer=5;var rt=g.GetComponent<RectTransform>();rt.anchoredPosition=pos;rt.sizeDelta=size;g.GetComponent<Image>().color=new Color(.07f,.12f,.18f);
        Text txt=Label(name+"Text","1234",0,0,size.x-20,size.y-10,24);txt.transform.SetParent(g.transform,false);
        var f=g.GetComponent<InputField>();f.textComponent=txt;f.targetGraphic=g.GetComponent<Image>();f.contentType=InputField.ContentType.IntegerNumber;f.characterLimit=9;f.text="1234";f.navigation=new Navigation{mode=Navigation.Mode.None};return f;
    }
}
