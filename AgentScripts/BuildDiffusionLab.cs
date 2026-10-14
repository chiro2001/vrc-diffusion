using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
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
    const int InteractionLayer=17;
    static DiffusionLocalization localization;
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
        foreach(string n in new[]{"DiffusionController","DiffusionButton","DiffusionControlRequest","DiffusionLocalization","DiffusionModel","DiffusionTeaching"}) {
            var source=AssetDatabase.LoadAssetAtPath<MonoScript>(Root+"/Scripts/"+n+".cs");
            if(source==null)throw new Exception("Import source scripts before compile: "+n);
            var p=Asset(Root+"/Programs/"+n+".asset",()=>ScriptableObject.CreateInstance<UdonSharpProgramAsset>());
            p.sourceCsScript=source;
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

        Primitive("Floor",PrimitiveType.Cube,new Vector3(0,-.1f,0),new Vector3(18,.2f,18),floor,true);
        Primitive("Backboard",PrimitiveType.Quad,new Vector3(0,2.7f,3.15f),new Vector3(8.6f,5.3f,1),dark,false);
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
        var cg=new GameObject("Interface",typeof(RectTransform),typeof(Canvas),typeof(GraphicRaycaster),typeof(VRCUiShape),typeof(BoxCollider));cg.transform.SetParent(world,false);
        cg.layer=InteractionLayer;
        var cvs=cg.GetComponent<Canvas>();cvs.renderMode=RenderMode.WorldSpace;canvas=cg.transform;
        // Every UI control shares this plane, 1 cm in front of the backing surface.
        cg.transform.localPosition=new Vector3(0,2.7f,3.14f);cg.transform.localScale=Vector3.one*.004f;
        cg.GetComponent<RectTransform>().sizeDelta=new Vector2(2150,1300);
        var box=cg.GetComponent<BoxCollider>();box.size=new Vector3(2150,1300,1);box.isTrigger=false;
        font=AssetDatabase.LoadAssetAtPath<Font>(Root+"/Localization/FontSources/IBMPlexMono-Regular.ttf");
        if(font==null)throw new Exception("Import font sources before building");
        localization=world.gameObject.AddUdonSharpComponent<DiffusionLocalization>();
        localization.languageOverride=1;localization.languageIndex=1;
        Label("Title","DIFFUSION LAB / MNIST",-330,555,1300,65,40);
        Label("Subtitle","CLASS-CONDITIONAL U-NET · DDIM · FRAGMENT SHADER INFERENCE",0,510,1950,40,21);
        localization.languageButtons=new Image[4];
        string[] langs={"AUTO","English","简体中文","日本語"};
        for(int i=0;i<4;i++) localization.languageButtons[i]=Button(langs[i],"SetLanguage",i-1,480+i*150,555,140,46,true);
        Picture("Current x_t",main,-615,205,480,480);
        Picture("Predicted x0",pred,-260,330,215,215);
        Picture("Predicted epsilon",noise,-260,65,215,215);
        Picture("Live memory",mem,365,195,480,480);
        Label("CurrentLabel","CURRENT IMAGE  x_t",-615,465,540,36,24);
        Label("CleanLabel","PREDICTED x_0",-260,455,290,30,18);
        Label("NoiseLabel","PREDICTED NOISE",-260,190,290,30,18);
        controller.memoryText=Label("MemoryLabel","LIVE GPU MEMORY",365,465,670,62,19);
        controller.statusText=Label("Status","IDLE",-445,-80,1100,70,20);
        controller.configText=Label("Config","CLASS / SEED / STEPS",450,-80,1050,70,18);
        controller.controlText=Label("Control","CONTROL",0,-627,2020,46,16);
        controller.historyLabels=new Text[8];
        for(int i=0;i<8;i++) {
            float x=-875+i*250;
            Picture("History "+i,hd[i],x,-207,165,165);
            controller.historyLabels[i]=Label("HistoryLabel"+i,i==0?"NOISE":i==7?"FINAL":"—",x,-307,190,26,17);
        }
        for(int i=0;i<10;i++)Button("CLASS "+i,"SelectDigit",i,-937+i*130,-350,120,50);
        Button("GENERATE","Generate",0,545,-350,280,50);
        Button("RESET","ResetRun",0,880,-350,240,50);
        Button("INIT / PAUSE / RUN","PauseResume",0,-800,-490,380,58);
        Button("STEP DENOISE","StepDenoise",0,-390,-490,390,58);
        Button("STEP OPERATOR","StepOperator",0,25,-490,390,58);
        Button("REQUEST CONTROL","RequestControl",0,445,-490,390,58);
        Button("GRANT","GrantControl",0,860,-490,340,58);
        Button("MEM <","PreviousMemory",0,880,365,210,60);
        Button("MEM >","NextMemory",0,880,285,210,60);
        Button("SLOWER","Slower",0,880,195,210,60);
        Button("FASTER","Faster",0,880,115,210,60);
        Button("16 STEPS","SelectSteps",16,-895,-420,210,55);
        Button("32 STEPS","SelectSteps",32,-665,-420,210,55);
        Button("64 STEPS","SelectSteps",64,-435,-420,210,55);
        Label("SeedLabel","SEED",-210,-420,150,55,20);
        controller.seedInput=Input("SeedInput",new Vector2(60,-420),new Vector2(360,55));
        Button("APPLY SEED","ApplySeed",0,405,-420,280,55);
        Button("RANDOM SEED","RandomSeed",0,820,-420,360,55);
        Button("MODEL Legacy compare","SelectModel",0,-775,-557,430,48);
        Button("MODEL TeaPearce 28","SelectModel",1,-310,-557,450,48);
        Button("OUTPUT native","SelectOutput",0,160,-557,340,48);
        Button("32","SelectOutput",1,485,-557,220,48);
        Button("64","SelectOutput",2,730,-557,220,48);
        Button("128","SelectOutput",3,975,-557,220,48);
        SaveProxy(localization);
        controller.validationRunOnStart=false;controller.fixtureRun=false;
        controller.phase=0;controller.epoch=0;controller.revision=0;controller.authorityId=-1;controller.grantedId=-1;
        controller.localEpoch=-1;controller.localStep=0;controller.localLayer=0;
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
        g.layer=InteractionLayer;var t=g.GetComponent<Text>();t.font=font;t.text=text;t.fontSize=size;t.color=new Color(.85f,.93f,1);t.alignment=TextAnchor.MiddleCenter;t.raycastTarget=false;return t;
    }
    static Image Button(string label,string command,int value,float x,float y,float w,float h,bool language=false) {
        var g=new GameObject(command+"_"+value,typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(UnityEngine.UI.Button));
        g.transform.SetParent(canvas,false);g.layer=InteractionLayer;
        var r=g.GetComponent<RectTransform>();r.anchoredPosition=new Vector2(x,y);r.sizeDelta=new Vector2(w,h);
        var image=g.GetComponent<Image>();image.color=new Color(.07f,.19f,.26f);image.raycastTarget=true;
        var ui=g.GetComponent<UnityEngine.UI.Button>();ui.targetGraphic=image;ui.navigation=new Navigation{mode=Navigation.Mode.None};
        var b=g.AddUdonSharpComponent<DiffusionButton>();b.controller=controller;b.command=command;b.value=value;
        if(language)b.localization=localization;
        SaveProxy(b);
        UnityEventTools.AddStringPersistentListener(ui.onClick,UdonSharpEditorUtility.GetBackingUdonBehaviour(b).SendCustomEvent,"Press");
        Text t=Label("Label",label,0,0,w-16,h-8,21);t.transform.SetParent(g.transform,false);
        t.resizeTextForBestFit=true;t.resizeTextMinSize=16;t.resizeTextMaxSize=21;
        return image;
    }
    static void Picture(string name,Material material,float x,float y,float w,float h) {
        // A mesh on the same backing plane keeps GPU textures independent of UI raycasts.
        var g=GameObject.CreatePrimitive(PrimitiveType.Quad);g.name=name;g.transform.SetParent(canvas,false);
        g.transform.localPosition=new Vector3(x,y,1.25f);g.transform.localScale=new Vector3(w,h,1);
        g.GetComponent<Renderer>().sharedMaterial=material;Object.DestroyImmediate(g.GetComponent<Collider>());
    }
    static InputField Input(string name,Vector2 pos,Vector2 size) {
        var g=new GameObject(name,typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(InputField));g.transform.SetParent(canvas,false);
        g.layer=InteractionLayer;var rt=g.GetComponent<RectTransform>();rt.anchoredPosition=pos;rt.sizeDelta=size;g.GetComponent<Image>().color=new Color(.07f,.12f,.18f);
        Text txt=Label(name+"Text","1234",0,0,size.x-24,size.y-10,26);txt.transform.SetParent(g.transform,false);
        txt.alignment=TextAnchor.MiddleLeft;
        var f=g.GetComponent<InputField>();f.textComponent=txt;f.targetGraphic=g.GetComponent<Image>();f.contentType=InputField.ContentType.IntegerNumber;f.characterLimit=9;f.text="1234";f.navigation=new Navigation{mode=Navigation.Mode.None};return f;
    }
}
