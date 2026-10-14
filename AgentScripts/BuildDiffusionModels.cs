using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

public static class BuildDiffusionModels {
    const string Root="Assets/DiffusionLab";
    static void Folder(string p){if(AssetDatabase.IsValidFolder(p))return;int i=p.LastIndexOf('/');Folder(p.Substring(0,i));AssetDatabase.CreateFolder(p.Substring(0,i),p.Substring(i+1));}
    static T Asset<T>(string path,Func<T> make)where T:Object{var a=AssetDatabase.LoadAssetAtPath<T>(path);if(a!=null)return a;a=make();AssetDatabase.CreateAsset(a,path);return a;}
    static RenderTexture RT(string prefix,string name,int size,int channels=1){
        var r=Asset(Root+"/Textures/"+prefix+"/"+name+".renderTexture",()=>new RenderTexture(size*channels,size,0,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear));
        if(r.width!=size*channels||r.height!=size){r.Release();r.width=size*channels;r.height=size;}
        r.filterMode=FilterMode.Point;r.wrapMode=TextureWrapMode.Clamp;r.useMipMap=false;EditorUtility.SetDirty(r);return r;
    }
    static void Save(UdonSharpBehaviour b){UdonSharpEditorUtility.CopyProxyToUdon(b);EditorUtility.SetDirty(b);EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(b));}
    static DiffusionModel New(Transform parent,string name){var g=new GameObject(name);g.transform.SetParent(parent,false);return g.AddUdonSharpComponent<DiffusionModel>();}
    static RenderTexture[] Outputs(string prefix,int size){return new[]{RT(prefix,"OutputNative",size),RT(prefix,"Output32",32),RT(prefix,"Output64",64),RT(prefix,"Output128",128)};}
    public static object RefreshOperators() {
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");
        var c=Object.FindObjectOfType<DiffusionController>();var tea=c.models[1];
        var spec=JObject.Parse(File.ReadAllText(Root+"/Data/Tea/model.json"));var entries=(JArray)spec["layers"];
        string[] keys={"Op","InSize","InChannels","PrimaryChannels","OutSize","OutChannels","Kernel","Stride","Padding","WeightOffset","BiasOffset","ConditionOffset","ScaleOffset","Activation","Groups"};
        string[] json={"op","inputSize","inputChannels","primaryChannels","size","outputChannels","kernel","stride","padding","weightOffset","biasOffset","conditionOffset","scaleOffset","activation","groups"};
        for(int i=0;i<entries.Count;i++) {
            var e=entries[i];var m=tea.layers[i];
            for(int j=0;j<keys.Length;j++)m.SetInt("_"+keys[j],(int?)e[json[j]]??0);
            m.SetFloat("_ResidualScale",(float?)e["residualScale"]??1);m.SetFloat("_Epsilon",(float?)e["epsilon"]??.00001f);
            m.SetInt("_Timesteps",(int)spec["timesteps"]);m.SetInt("_WeightWidth",(int)spec["textureWidth"]);
            int primary=(int)e["primary"],skip=(int)e["skip"];
            if(primary>=0)m.SetTexture("_Input",tea.memory[primary]);
            if(skip>=0){m.SetTexture("_Skip",tea.memory[skip]);m.SetInt("_SkipSize",tea.memory[skip].height);}
            // Bind the operator after all other properties, then verify the saved material layout.
            m.SetInt("_Op",(int)e["op"]);
            EditorUtility.SetDirty(m);
        }
        AssetDatabase.SaveAssets();
        for(int i=0;i<entries.Count;i++)if(tea.layers[i].GetInt("_Op")!=(int)entries[i]["op"])throw new Exception("Operator material was not persisted: "+i);
        return new{refreshed=entries.Count};
    }
    public static object Import() {
        if(EditorApplication.isPlaying)throw new Exception("Stop PlayMode first");
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if(scene.isDirty)throw new Exception("Save own scene changes before importing");
        var c=Object.FindObjectOfType<DiffusionController>();
        if(c.models!=null&&c.models.Length>0)throw new Exception("Rebuild base scene before importing models");
        Folder(Root+"/Textures/Legacy");Folder(Root+"/Textures/Tea");Folder(Root+"/Materials/Tea");
        var legacy=New(c.transform,"Legacy MNIST 32");legacy.displayName="Legacy MNIST 32 (comparison)";legacy.version=c.modelVersion;legacy.layers=c.layers;legacy.memory=c.layerMemory;legacy.channels=c.layerChannels;
        legacy.stateA=c.stateA;legacy.stateB=c.stateB;legacy.clean=c.cleanPrediction;legacy.conditional=c.conditionalNoise;legacy.guided=c.guidedNoise;legacy.unconditional=RT("Legacy","Unconditional",32);
        legacy.alpha=c.alphaCumprod;legacy.history=c.historyMemory;legacy.outputs=Outputs("Legacy",32);legacy.validationInput=c.validationInput;legacy.validationExpected=c.validationExpected;legacy.leadingSpacing=true;
        var oldEntries=(JArray)JObject.Parse(File.ReadAllText(Root+"/Data/model.json"))["layers"];
        legacy.operatorNames=Enumerable.Range(0,legacy.layers.Length).Select(i=>"Conv "+i).ToArray();
        legacy.primaryLayers=Enumerable.Range(0,legacy.layers.Length).Select(i=>i-1).ToArray();legacy.skipLayers=oldEntries.Select(e=>(int)e["skip"]).ToArray();
        JObject spec=JObject.Parse(File.ReadAllText(Root+"/Data/Tea/model.json"));var entries=(JArray)spec["layers"];
        int size=(int)spec["size"],tw=(int)spec["textureWidth"],count=(int)spec["weightCount"],th=(count+tw-1)/tw;
        byte[] raw=File.ReadAllBytes(Root+"/Data/Tea/weights.bytes");
        int version=BitConverter.ToInt32(System.Security.Cryptography.SHA256.Create().ComputeHash(raw),0)&int.MaxValue;
        if(raw.Length!=count*4||version!=(int)spec["modelVersion"])throw new Exception("Invalid pretrained weight hash");
        byte[] padded=new byte[tw*th*4];Buffer.BlockCopy(raw,0,padded,0,raw.Length);
        var weights=Asset(Root+"/Textures/Tea/Weights.asset",()=>new Texture2D(tw,th,TextureFormat.RFloat,false,true));
        if(weights.width!=tw||weights.height!=th)weights.Reinitialize(tw,th,TextureFormat.RFloat,false);
        weights.LoadRawTextureData(padded);weights.filterMode=FilterMode.Point;weights.wrapMode=TextureWrapMode.Clamp;weights.Apply();EditorUtility.SetDirty(weights);
        var tea=New(c.transform,"TeaPearce Pretrained 28");tea.displayName="TeaPearce MNIST 28";tea.version=version;tea.nativeSize=size;tea.timestepMin=(int)spec["timestepMin"];tea.timestepMax=(int)spec["timestepMax"];tea.defaultSteps=64;tea.leadingSpacing=false;tea.clipPrediction=false;tea.valueMin=0;tea.valueMax=1;
        tea.operatorNames=entries.Select(e=>(string)e["name"]).ToArray();tea.primaryLayers=entries.Select(e=>(int)e["primary"]).ToArray();tea.skipLayers=entries.Select(e=>(int)e["skip"]).ToArray();
        tea.alpha=spec["alphaCumprod"].ToObject<float[]>();tea.layers=new Material[entries.Count];tea.memory=new RenderTexture[entries.Count];tea.channels=new int[entries.Count];
        for(int i=0;i<entries.Count;i++) {var e=entries[i];tea.channels[i]=(int)e["outputChannels"];tea.memory[i]=RT("Tea","Layer"+i.ToString("D2"),(int)e["size"],tea.channels[i]);}
        string[] keys={"Op","InSize","InChannels","PrimaryChannels","OutSize","OutChannels","Kernel","Stride","Padding","WeightOffset","BiasOffset","ConditionOffset","ScaleOffset","Activation","Groups"};
        string[] json={"op","inputSize","inputChannels","primaryChannels","size","outputChannels","kernel","stride","padding","weightOffset","biasOffset","conditionOffset","scaleOffset","activation","groups"};
        for(int i=0;i<entries.Count;i++) {
            var e=entries[i];var m=Asset(Root+"/Materials/Tea/Layer"+i.ToString("D2")+".mat",()=>new Material(Shader.Find("DiffusionLab/Operator")));m.shader=Shader.Find("DiffusionLab/Operator");tea.layers[i]=m;
            for(int j=0;j<keys.Length;j++)m.SetInt("_"+keys[j],(int?)e[json[j]]??0);
            m.SetFloat("_ResidualScale",(float?)e["residualScale"]??1);m.SetFloat("_Epsilon",(float?)e["epsilon"]??.00001f);
            m.SetInt("_Timesteps",(int)spec["timesteps"]);m.SetInt("_WeightWidth",tw);m.SetTexture("_Weights",weights);
            int primary=(int)e["primary"],skip=(int)e["skip"];
            if(primary>=0)m.SetTexture("_Input",tea.memory[primary]);
            if(skip>=0){m.SetTexture("_Skip",tea.memory[skip]);m.SetInt("_SkipSize",tea.memory[skip].height);}
            // Bind the operator after all other properties, then verify the saved material layout.
            m.SetInt("_Op",(int)e["op"]);
            EditorUtility.SetDirty(m);
        }
        tea.stateA=RT("Tea","StateA",size);tea.stateB=RT("Tea","StateB",size);tea.clean=RT("Tea","Clean",size);tea.conditional=RT("Tea","Conditional",size);tea.guided=RT("Tea","Guided",size);tea.unconditional=RT("Tea","Unconditional",size);
        tea.history=Enumerable.Range(0,8).Select(i=>RT("Tea","History"+i,size)).ToArray();tea.outputs=Outputs("Tea",size);
        tea.validationInput=Asset(Root+"/Textures/Tea/ValidationInput.asset",()=>new Texture2D(size,size,TextureFormat.RFloat,false,true));tea.validationInput.LoadRawTextureData(File.ReadAllBytes(Root+"/Data/Tea/reference-input.bytes"));tea.validationInput.filterMode=FilterMode.Point;tea.validationInput.Apply();EditorUtility.SetDirty(tea.validationInput);
        raw=File.ReadAllBytes(Root+"/Data/Tea/reference-final.bytes");tea.validationExpected=new float[raw.Length/4];Buffer.BlockCopy(raw,0,tea.validationExpected,0,raw.Length);
        c.models=new[]{legacy,tea};c.modelVersion=unchecked(legacy.version*31+tea.version)&int.MaxValue;c.networkModelVersion=c.modelVersion;c.schemaVersion=2;c.modelIndex=1;c.outputIndex=0;c.totalSteps=64;
        c.layers=tea.layers;c.layerMemory=tea.memory;c.layerChannels=tea.channels;c.stateA=tea.stateA;c.stateB=tea.stateB;c.cleanPrediction=tea.clean;c.conditionalNoise=tea.conditional;c.unconditionalNoise=tea.unconditional;c.guidedNoise=tea.guided;c.alphaCumprod=tea.alpha;c.historyMemory=tea.history;c.outputMemory=tea.outputs;c.nativeSize=size;c.timestepMin=1;c.timestepMax=400;c.leadingSpacing=false;c.validationInput=tea.validationInput;c.validationExpected=tea.validationExpected;
        c.mainDisplay.SetTexture("_MainTex",tea.outputs[0]);c.cleanDisplay.SetTexture("_MainTex",tea.clean);c.noiseDisplay.SetTexture("_MainTex",tea.guided);c.memoryDisplay.SetTexture("_MainTex",tea.memory[0]);c.memoryDisplay.SetFloat("_Channels",tea.channels[0]);
        foreach(var m in new[]{c.mainDisplay,c.cleanDisplay}.Concat(c.historyDisplays)){m.SetFloat("_ValueMin",0);m.SetFloat("_ValueMax",1);EditorUtility.SetDirty(m);}
        Save(legacy);Save(tea);Save(c);AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        return new{models=2,nativeSizes=new[]{32,28},defaultModel=tea.displayName,operators=tea.layers.Length,weightBytes=count*4,registryVersion=c.modelVersion};
    }
}
