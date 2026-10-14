using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

public static class BuildSDModel {
    const string Root="Assets/DiffusionLab/SD";
    const string Data="Assets/DiffusionLab/Data/SD";
    static void Folder(string p){if(AssetDatabase.IsValidFolder(p))return;int i=p.LastIndexOf('/');Folder(p.Substring(0,i));AssetDatabase.CreateFolder(p.Substring(0,i),p.Substring(i+1));}
    static int Shift(int n){int s=0;while((1<<s)<n)s++;return s;}
    static T Asset<T>(string p,Func<T> make) where T:Object{var a=AssetDatabase.LoadAssetAtPath<T>(p);if(a!=null)return a;a=make();AssetDatabase.CreateAsset(a,p);return a;}
    static RenderTexture RT(string dir,string name,int count,RenderTextureFormat format,out int shift,int widthLimit=1024){
        int width=Math.Min(widthLimit,1<<Shift(Math.Max(1,count))),height=(count+width-1)/width;shift=Shift(width);
        var r=Asset(dir+"/"+name+".renderTexture",()=>new RenderTexture(width,height,0,format,RenderTextureReadWrite.Linear));
        if(r.width!=width||r.height!=height||r.format!=format){r.Release();r.width=width;r.height=height;r.format=format;}
        r.filterMode=FilterMode.Point;r.wrapMode=TextureWrapMode.Clamp;r.useMipMap=false;r.autoGenerateMips=false;r.enableRandomWrite=false;EditorUtility.SetDirty(r);return r;
    }
    static Texture2D FloatTexture(string path,string input,int channels,int widthLimit=1024){
        int width=Math.Min(widthLimit,1<<Shift(channels)),height=(channels+width-1)/width;
        var tex=Asset<Texture2D>(path,()=>new Texture2D(width,height,TextureFormat.RGBAFloat,false,true));
        byte[] raw=File.ReadAllBytes(input),padded=new byte[width*height*16];if(raw.Length>padded.Length)throw new Exception("Float texture overflow");Buffer.BlockCopy(raw,0,padded,0,raw.Length);
        if(!tex.isReadable)tex.Reinitialize(width,height,TextureFormat.RGBAFloat,false);
        tex.LoadRawTextureData(padded);tex.filterMode=FilterMode.Point;tex.wrapMode=TextureWrapMode.Clamp;tex.Apply(false,false);EditorUtility.SetDirty(tex);return tex;
    }
    static void Save(UdonSharpBehaviour b){UdonSharpEditorUtility.CopyProxyToUdon(b);EditorUtility.SetDirty(b);EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(b));}
    public static object Prepare(){
        Folder(Root+"/Programs");
        foreach(string name in new[]{"SDGraphData","SDGraphExecutor","SDController","SDButton","SDTeaching"}){
            var script=AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/DiffusionLab/Scripts/"+name+".cs");
            var program=Asset(Root+"/Programs/"+name+".asset",()=>ScriptableObject.CreateInstance<UdonSharpProgramAsset>());
            program.sourceCsScript=script;EditorUtility.SetDirty(program);
        }
        AssetDatabase.SaveAssets();UdonSharpProgramAsset.CompileAllCsPrograms(true,true);return new{compilationRequested=true};
    }
    public static object ClearFailedSetup(){
        var go=GameObject.Find("SD Anime Inference");
        if(go!=null){if(go.GetComponentsInChildren<SDGraphData>().Length>0)throw new Exception("Graph content exists; preserve it");Object.DestroyImmediate(go);}
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.SaveScene(scene);return true;
    }
    public static object ImportWeights(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode only");Folder(Root+"/Parameters");Folder(Root+"/Conditions");
        var spec=JObject.Parse(File.ReadAllText(Data+"/model.json"));int pngs=0,parameters=0;
        AssetDatabase.StartAssetEditing();
        try{
            foreach(var property in ((JObject)spec["weights"]).Properties()){
                var w=property.Value;string file=(string)w["weightFile"];
                if(file!=null){
                    var im=(TextureImporter)AssetImporter.GetAtPath(Data+"/"+file);if(im==null)throw new Exception("Unimported texture "+file);
                    im.textureType=TextureImporterType.Default;im.sRGBTexture=false;im.alphaIsTransparency=false;im.mipmapEnabled=false;
                    im.npotScale=TextureImporterNPOTScale.None;im.isReadable=false;im.textureCompression=TextureImporterCompression.Uncompressed;
                    im.filterMode=FilterMode.Point;im.wrapMode=TextureWrapMode.Clamp;im.maxTextureSize=8192;im.alphaSource=TextureImporterAlphaSource.FromInput;
                    var settings=new TextureImporterPlatformSettings{name="Standalone",overridden=true,format=TextureImporterFormat.RGBA32,maxTextureSize=8192,textureCompression=TextureImporterCompression.Uncompressed};im.SetPlatformTextureSettings(settings);
                    im.SaveAndReimport();pngs++;
                }
            }
        }finally{AssetDatabase.StopAssetEditing();}
        foreach(var property in ((JObject)spec["weights"]).Properties()){
            var w=property.Value;string stem=property.Name.Replace('.','_');
            FloatTexture(Root+"/Parameters/"+stem+".asset",Data+"/"+(string)w["parameterFile"],(int)w["channels"]);parameters++;
            if(w["weightFile"]!=null){var tex=AssetDatabase.LoadAssetAtPath<Texture2D>(Data+"/"+(string)w["weightFile"]);if(tex.width!=(int)w["width"]||tex.height!=(int)w["height"])throw new Exception("Weight texture was resized: "+stem);}
        }
        for(int i=0;i<5;i++)FloatTexture(Root+"/Conditions/Embedding"+i+".asset",Data+"/conditions/embedding-"+i+".bytes",77*768/4);
        AssetDatabase.SaveAssets();return new{pngs,parameters};
    }
    static SDGraphData Graph(Transform parent,JObject spec,string part,int size){
        string dir=Root+"/"+size+"/"+part;Folder(dir+"/Memory");Folder(dir+"/Operators");
        var old=parent.Find(part+size);var go=old!=null?old.gameObject:new GameObject(part+size);go.transform.SetParent(parent,false);
        var g=go.GetComponent<SDGraphData>()??go.AddUdonSharpComponent<SDGraphData>();
        var graph=spec["graphs"][size.ToString()][part];var nodes=(JArray)graph["nodes"];int length=nodes.Count;
        g.displayName=part;g.nativeSize=size;g.weightLayout=(string)spec["weightLayout"]=="KY_KX_IC4_OC_LANES"?1:0;g.outputNode=(int)graph["output"];
        g.operators=new Material[length];g.nodeNames=new string[length];g.op=new int[length];g.inputA=new int[length];g.inputB=new int[length];g.inputC=new int[length];g.memoryIndex=new int[length];g.shifts=new int[length];g.counts=new int[length];g.inH=new int[length];g.inW=new int[length];g.inChannels=new int[length];g.outChannels=new int[length];g.groups=new int[length];g.heads=new int[length];g.keyRows=new int[length];
        int[] last=Enumerable.Repeat(-1,length).ToArray();for(int i=0;i<length;i++)foreach(string k in new[]{"a","b","c"}){int dep=(int)nodes[i][k];if(dep>=0)last[dep]=i;}last[g.outputNode]=length;
        var capacities=new List<int>();var availableAfter=new List<int>();var pool=new List<RenderTexture>();var shifts=new List<int>();
        int maxInput=1,maxOutput=1,maxPartial=1,maxStats=1,maxScore=1,maxAttStats=1,maxAttAccum=1;
        for(int i=0;i<length;i++){
            var n=nodes[i];int h=(int)n["h"],w=(int)n["w"],c=(int)n["channels"],count=h*w*((c+3)/4);
            int slot=-1,best=int.MaxValue;for(int p=0;p<pool.Count;p++)if(availableAfter[p]<i&&capacities[p]>=count&&capacities[p]<best){slot=p;best=capacities[p];}
            if(slot<0){var rt=RT(dir+"/Memory","Pool"+pool.Count,count,RenderTextureFormat.ARGBHalf,out int shift);slot=pool.Count;pool.Add(rt);capacities.Add(rt.width*rt.height);availableAfter.Add(-1);shifts.Add(shift);}
            availableAfter[slot]=last[i];g.memoryIndex[i]=slot;g.shifts[i]=shifts[slot];g.counts[i]=count;
            g.nodeNames[i]=(string)n["name"];g.op[i]=(int)n["op"];g.inputA[i]=(int)n["a"];g.inputB[i]=(int)n["b"];g.inputC[i]=(int)n["c"];
            g.inH[i]=(int)n["inH"];g.inW[i]=(int)n["inW"];g.inChannels[i]=(int)n["inChannels"];g.outChannels[i]=c;
            g.groups[i]=(int?)n["groups"]??0;g.heads[i]=(int?)n["heads"]??0;g.keyRows[i]=(int?)n["keyRows"]??0;
            var mat=Asset(dir+"/Operators/Node"+i.ToString("D3")+".mat",()=>new Material(Shader.Find("DiffusionLab/SDTensor")));mat.shader=Shader.Find("DiffusionLab/SDTensor");g.operators[i]=mat;
            string[] names={"InH","InW","InC","OutH","OutW","OutC","BH","BW","BC","Kernel","Stride","Padding","Groups","Activation","Heads","KeyRows","Op"};
            string[] keys={"inH","inW","inChannels","h","w","channels","bH","bW","bChannels","kernel","stride","padding","groups","activation","heads","keyRows","op"};
            for(int j=0;j<keys.Length;j++)mat.SetInt("_"+names[j],(int?)n[keys[j]]??0);
            mat.SetInt("_Count",g.inH[i]*g.inW[i]*((g.inChannels[i]+3)/4));mat.SetInt("_OutCount",count);
            mat.SetFloat("_Epsilon",(float?)n["epsilon"]??.00001f);mat.SetFloat("_Multiplier",(float?)n["multiplier"]??1);
            if(n["weight"]!=null){
                string key=part+"."+(string)n["weight"],stem=key.Replace('.','_');var weight=spec["weights"][key];
                var par=AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Parameters/"+stem+".asset");mat.SetTexture("_Params",par);mat.SetInt("_ParamShift",Shift(par.width));
                if(weight["weightFile"]!=null){var wt=AssetDatabase.LoadAssetAtPath<Texture2D>(Data+"/"+(string)weight["weightFile"]);mat.SetTexture("_Weights",wt);mat.SetInt("_WeightShift",Shift(wt.width));mat.SetInt("_WeightHeight",wt.height);mat.SetInt("_WeightLayout",(string)spec["weightLayout"]=="KY_KX_IC4_OC_LANES"?1:0);}
            }
            EditorUtility.SetDirty(mat);
            maxInput=Math.Max(maxInput,g.inH[i]*g.inW[i]*((g.inChannels[i]+3)/4));maxOutput=Math.Max(maxOutput,count);
            if(g.op[i]==1){int normGroups=g.groups[i]==0?g.inH[i]*g.inW[i]:g.groups[i];int elements=g.groups[i]==0?g.inChannels[i]:g.inH[i]*g.inW[i]*g.inChannels[i]/g.groups[i];maxPartial=Math.Max(maxPartial,normGroups*((elements+255)/256));maxStats=Math.Max(maxStats,normGroups);}
            if(g.op[i]==6){int rows=h*w*g.heads[i];maxScore=Math.Max(maxScore,rows*16);maxAttStats=Math.Max(maxAttStats,rows);maxAttAccum=Math.Max(maxAttAccum,count);}
        }
        g.memory=pool.ToArray();g.memoryShifts=shifts.ToArray();maxPartial=Math.Max(maxPartial,(maxInput+255)/256);
        g.quantized=RT(dir+"/Memory","Quantized",maxInput,RenderTextureFormat.ARGB32,out g.quantizedShift);
        g.partial=RT(dir+"/Memory","Partial",maxPartial,RenderTextureFormat.ARGBFloat,out g.partialShift);
        g.stats=RT(dir+"/Memory","Stats",maxStats,RenderTextureFormat.ARGBFloat,out g.statsShift);
        g.scale=RT(dir+"/Memory","ActivationScale",1,RenderTextureFormat.ARGBFloat,out int ignored);
        g.tile=RT(dir+"/Memory","ConvTile",8192,RenderTextureFormat.ARGBHalf,out g.tileShift,128);
        g.scratch=RT(dir+"/Memory","ConvScratch",maxOutput,RenderTextureFormat.ARGBHalf,out g.scratchShift);
        g.attentionScores=RT(dir+"/Memory","AttentionScores",maxScore,RenderTextureFormat.ARGBFloat,out g.scoreShift);
        g.attentionStatsA=RT(dir+"/Memory","AttentionStatsA",maxAttStats,RenderTextureFormat.ARGBFloat,out g.attentionStatsShift);
        g.attentionStatsB=RT(dir+"/Memory","AttentionStatsB",maxAttStats,RenderTextureFormat.ARGBFloat,out ignored);
        g.attentionAccumA=RT(dir+"/Memory","AttentionAccumA",maxAttAccum,RenderTextureFormat.ARGBFloat,out g.attentionAccumShift);
        g.attentionAccumB=RT(dir+"/Memory","AttentionAccumB",maxAttAccum,RenderTextureFormat.ARGBFloat,out ignored);
        Save(g);return g;
    }
    public static object SetWeightLayout(){
        var spec=JObject.Parse(File.ReadAllText(Data+"/model.json"));int layout=(string)spec["weightLayout"]=="KY_KX_IC4_OC_LANES"?1:0;
        var c=Object.FindObjectOfType<SDController>();int changed=0;
        foreach(var g in c.unetGraphs.Concat(c.vaeGraphs)){g.weightLayout=layout;Save(g);for(int i=0;i<g.operators.Length;i++)if(g.op[i]==0){g.operators[i].SetInt("_WeightLayout",layout);EditorUtility.SetDirty(g.operators[i]);changed++;}}
        AssetDatabase.SaveAssets();return new{layout,changed};
    }
    public static object BuildGraphs(){
        if(EditorApplication.isPlaying||EditorApplication.isCompiling)throw new Exception("EditMode only");
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();if(scene.isDirty)throw new Exception("Preserve unsaved scene edits before building");
        Folder(Root);var existing=GameObject.Find("SD Anime Inference");var go=existing??new GameObject("SD Anime Inference");
        var c=go.GetComponent<SDController>()??go.AddUdonSharpComponent<SDController>();
        var executor=go.GetComponent<SDGraphExecutor>()??go.AddUdonSharpComponent<SDGraphExecutor>();c.executor=executor;
        var spec=JObject.Parse(File.ReadAllText(Data+"/model.json"));int[] sizes={256,384,512};c.unetGraphs=new SDGraphData[3];c.vaeGraphs=new SDGraphData[3];
        c.latentA=new RenderTexture[3];c.latentB=new RenderTexture[3];c.cleanMemory=new RenderTexture[3];c.conditionalMemory=new RenderTexture[3];c.unconditionalMemory=new RenderTexture[3];c.images=new RenderTexture[3];
        for(int i=0;i<3;i++){
            int size=sizes[i];c.unetGraphs[i]=Graph(go.transform,spec,"unet",size);c.vaeGraphs[i]=Graph(go.transform,spec,"vae",size);Folder(Root+"/"+size+"/State");string d=Root+"/"+size+"/State";
            int count=size*size/64;c.latentA[i]=RT(d,"LatentA",count,RenderTextureFormat.ARGBHalf,out int shift,64);c.latentB[i]=RT(d,"LatentB",count,RenderTextureFormat.ARGBHalf,out shift,64);
            c.cleanMemory[i]=RT(d,"CleanPrediction",count,RenderTextureFormat.ARGBHalf,out shift,64);c.conditionalMemory[i]=RT(d,"Conditional",count,RenderTextureFormat.ARGBHalf,out shift,64);c.unconditionalMemory[i]=RT(d,"Unconditional",count,RenderTextureFormat.ARGBHalf,out shift,64);
            c.images[i]=RT(d,"OutputRGB",size*size,RenderTextureFormat.ARGBHalf,out shift,size);c.images[i].filterMode=FilterMode.Bilinear;
        }
        Folder(Root+"/Shared");c.sampler=Asset(Root+"/Shared/Sampler.mat",()=>new Material(Shader.Find("DiffusionLab/SDSampler")));c.memoryDisplay=Asset(Root+"/Shared/MemoryCapture.mat",()=>new Material(Shader.Find("DiffusionLab/SDMemoryDisplay")));
        c.timeMemory=RT(Root+"/Shared","TimeEmbedding",128,RenderTextureFormat.ARGBHalf,out int s,128);c.debugMemory=RT(Root+"/Shared","MemoryView",512*512,RenderTextureFormat.ARGBHalf,out s,512);
        c.history=Enumerable.Range(0,8).Select(i=>RT(Root+"/Shared","History"+i,128*128,RenderTextureFormat.ARGBHalf,out int sh,128)).ToArray();
        c.embeddings=Enumerable.Range(0,5).Select(i=>AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Conditions/Embedding"+i+".asset")).ToArray();c.presetNames=new[]{"Anime portrait","Coastal village","Autumn cafe","Garden cat"};c.alpha=spec["alphaCumprod"].ToObject<float[]>();c.protocol=1;c.phase=0;c.resolution=0;c.totalSteps=20;
        Save(executor);Save(c);AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        return new{sizes,unetNodes=c.unetGraphs[0].operators.Length,vaeNodes=c.vaeGraphs[0].operators.Length,weightTextures=((JObject)spec["weights"]).Properties().Count(p=>p.Value["weightFile"]!=null),memorySlots=c.unetGraphs.Select(g=>g.memory.Length).ToArray()};
    }
}
