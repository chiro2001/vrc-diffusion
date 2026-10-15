using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharpEditor;
using VRC.Udon;
using Object=UnityEngine.Object;
public static class ValidateSDPlay {
    static string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"tmp/vrc-diffusion/sd-anime/playmode");
    static void Report(string name,object value){Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,name),JsonConvert.SerializeObject(value,Formatting.Indented));}
    static UdonBehaviour Controller(){return UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<SDController>());}
    static UdonBehaviour Executor(){return UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<SDGraphExecutor>());}
    static int Int(UdonBehaviour b,string k){return (int)b.GetProgramVariable(k);}
    static float[] Read(RenderTexture r){var req=AsyncGPUReadback.Request(r,0,TextureFormat.RGBAFloat);req.WaitForCompletion();if(req.hasError)throw new Exception("Readback failed");return req.GetData<float>().ToArray();}
    static void Raw(string file,RenderTexture rt){float[] data=Read(rt);byte[] bytes=new byte[data.Length*4];Buffer.BlockCopy(data,0,bytes,0,bytes.Length);Directory.CreateDirectory(Root);File.WriteAllBytes(Path.Combine(Root,file),bytes);}
    static void Png(string file,RenderTexture rt){
        float[] data=Read(rt);if(data.Any(x=>float.IsNaN(x)||float.IsInfinity(x)))throw new Exception("Non-finite image: "+file);
        var tex=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false,true);var colors=new Color[rt.width*rt.height];
        for(int i=0;i<colors.Length;i++){var color=new Color(data[i*4],data[i*4+1],data[i*4+2],1);colors[i]=QualitySettings.activeColorSpace==ColorSpace.Linear?color.gamma:color;}tex.SetPixels(colors);tex.Apply();
        Directory.CreateDirectory(Root);File.WriteAllBytes(Path.Combine(Root,file),tex.EncodeToPNG());Object.DestroyImmediate(tex);
    }
    public static object Prepare(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");Directory.CreateDirectory(Root);
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.SaveScene(scene);
        var audio=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);var sampler=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Sampler.mat");
        Report("baseline.json",new{virtualVoices=audio.FindProperty("m_VirtualVoiceCount").intValue,realVoices=audio.FindProperty("m_RealVoiceCount").intValue,noisePath=AssetDatabase.GetAssetPath(sampler.GetTexture("_Noise")),nullNoisePath=AssetDatabase.GetAssetPath(sampler.GetTexture("_UnconditionedNoise"))});
        return new{prepared=true,udonError=UdonSharp.UdonSharpProgramAsset.AnyUdonSharpScriptHasError()};
    }
    public static object StartRun(int seed=1234,int resolution=0,int steps=20){
        if(!EditorApplication.isPlaying)throw new Exception("PlayMode required");var c=Controller();c.SendCustomEvent("RequestControl");
        c.SetProgramVariable("passesPerFrame",32);c.SetProgramVariable("frameBudgetMs",8f);c.SetProgramVariable("details",false);
        c.SetProgramVariable("commandValue",resolution);c.SendCustomEvent("SetResolution");
        c.SetProgramVariable("commandValue",steps);c.SendCustomEvent("SetSteps");
        var input=(UnityEngine.UI.InputField)c.GetProgramVariable("seedInput");input.text=seed.ToString();c.SendCustomEvent("SetSeed");
        c.SendCustomEvent("StartGeneration");
        if(Int(c,"phase")!=1||Int(c,"seed")!=seed)throw new Exception("Udon control/seed binding failed");
        Raw("map-initial-"+seed+".bytes",(RenderTexture)c.GetProgramVariable("currentLatent"));Report("started.json",new{seed,resolution,steps,source="Real PlayMode Udon/VRCGraphics"});return Status();
    }
    public static object Status(){
        if(!EditorApplication.isPlaying)return new{playMode=false};var c=Controller();var e=Executor();
        var result=new{playMode=true,phase=Int(c,"phase"),epoch=Int(c,"epoch"),seed=Int(c,"seed"),step=Int(c,"localStep"),steps=Int(c,"totalSteps"),algorithm=Int(c,"algorithmStage"),node=Int(e,"node"),substage=Int(e,"substage"),tile=Int(e,"tileOffset"),key=Int(e,"keyOffset"),blits=Int(e,"blitCount"),elapsed=(float)c.GetProgramVariable("elapsed"),localInference=(bool)c.GetProgramVariable("localInference")};Report("status.json",result);return result;
    }
    public static object PauseResumeCheck(){
        var c=Controller();c.SendCustomEvent("PauseResume");int paused=Int(c,"phase");if(paused!=2)throw new Exception("Expected paused");c.SendCustomEvent("ToggleDetails");c.SendCustomEvent("ToggleDetails");c.SendCustomEvent("PauseResume");if(Int(c,"phase")!=1)throw new Exception("Resume failed");return new{paused,resumed=true};
    }
    public static object DetailsSnapshot(string label="details"){
        var c=Controller();if(!(bool)c.GetProgramVariable("details"))c.SendCustomEvent("ToggleDetails");
        var fp=(RenderTexture)c.GetProgramVariable("debugMemory");var qi=(RenderTexture)c.GetProgramVariable("quantDebugMemory");
        var f=Read(fp);var q=Read(qi);Png(label+"-fp16.png",fp);Png(label+"-int8.png",qi);
        var teacher=Object.FindObjectOfType<SDTeaching>();var cards=teacher.cards.Select(x=>new{x.name,color=x.color.ToString()}).ToArray();
        var result=new{label,details=(bool)c.GetProgramVariable("details"),fp16Min=f.Min(),fp16Max=f.Max(),int8Min=q.Min(),int8Max=q.Max(),cards};Report(label+".json",result);return result;
    }
    public static object ConfigurationCheck(){
        var c=Controller();if(Int(c,"phase")!=3)throw new Exception("Complete generation first");
        int before=Int(c,"seed"),epoch=Int(c,"epoch");c.SendCustomEvent("NextSeed");
        if(Int(c,"seed")!=before+1||Int(c,"epoch")!=epoch+1||Int(c,"phase")!=0||Int(c,"localStep")!=0)throw new Exception("Configuration reset failed");
        return new{configurationReset=true,seed=Int(c,"seed"),epoch=Int(c,"epoch")};
    }
    public static object LocalInferenceCheck(){
        var c=Controller();if(Int(c,"phase")==1)throw new Exception("Use idle or completed state");
        c.SendCustomEvent("StartGeneration");if(!(bool)c.GetProgramVariable("localInference")||Int(c,"phase")!=1)throw new Exception("Generator must opt in locally");
        c.SendCustomEvent("ToggleInference");if((bool)c.GetProgramVariable("localInference")||Int(c,"phase")!=2)throw new Exception("Owner local off must pause shared generation");
        c.SendCustomEvent("PauseResume");if(!(bool)c.GetProgramVariable("localInference")||Int(c,"phase")!=1)throw new Exception("Resume must opt owner back in");
        c.SendCustomEvent("PauseResume");return new{localOffPausesOwner=true,resumeEnablesLocal=true,leftPaused=Int(c,"phase")==2};
    }
    public static object Capture(){
        var c=Controller();if(Int(c,"algorithmStage")!=6)throw new Exception("Wait for actual VAE completion");int seed=Int(c,"seed");var rt=(RenderTexture)c.GetProgramVariable("displayedImage");
        float[] data=Read(rt);if(data.Any(x=>float.IsNaN(x)||float.IsInfinity(x)))throw new Exception("Non-finite RGB output");
        var tex=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false,true);var colors=new Color[rt.width*rt.height];for(int i=0;i<colors.Length;i++){var color=new Color(data[i*4],data[i*4+1],data[i*4+2],1);colors[i]=QualitySettings.activeColorSpace==ColorSpace.Linear?color.gamma:color;}tex.SetPixels(colors);tex.Apply();
        Directory.CreateDirectory(Root);File.WriteAllBytes(Path.Combine(Root,"final-"+seed+".png"),tex.EncodeToPNG());Object.DestroyImmediate(tex);Raw("latent-final-"+seed+".bytes",(RenderTexture)c.GetProgramVariable("currentLatent"));
        Report("completed-"+seed+".json",new{seed,steps=Int(c,"localStep"),elapsed=(float)c.GetProgramVariable("elapsed"),width=rt.width,height=rt.height,finite=true,source="Actual Udon, integer Conv/Linear, full VAE"});return Status();
    }
    public static object CaptureHistory(){
        var c=Controller();var history=(RenderTexture[])c.GetProgramVariable("history");int seed=Int(c,"seed");
        for(int i=0;i<history.Length;i++)Png("history-"+seed+"-"+i+".png",history[i]);return new{seed,count=history.Length};
    }
    public static object Restore(){
        if(EditorApplication.isPlaying)throw new Exception("Stop PlayMode first");var b=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Root,"baseline.json")));
        var audio=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);audio.FindProperty("m_VirtualVoiceCount").intValue=(int)b["virtualVoices"];audio.FindProperty("m_RealVoiceCount").intValue=(int)b["realVoices"];audio.ApplyModifiedProperties();
        var sampler=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Sampler.mat");sampler.SetTexture("_Noise",AssetDatabase.LoadAssetAtPath<Texture>((string)b["noisePath"]));sampler.SetTexture("_UnconditionedNoise",AssetDatabase.LoadAssetAtPath<Texture>((string)b["nullNoisePath"]));EditorUtility.SetDirty(sampler);AssetDatabase.SaveAssets();return new{restored=true};
    }
}
