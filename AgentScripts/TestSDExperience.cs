using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharpEditor;
using VRC.SDKBase;
using VRC.Udon;
using Object=UnityEngine.Object;
public static class TestSDExperience {
    const string Root="E:/tmp/vrc-diffusion/sd-extensions";
    static UdonBehaviour U(UdonSharp.UdonSharpBehaviour b){return UdonSharpEditorUtility.GetBackingUdonBehaviour(b);}
    static UdonBehaviour C(){return U(Object.FindObjectOfType<SDController>());}
    static float[] Read(RenderTexture rt){var r=AsyncGPUReadback.Request(rt,0,TextureFormat.RGBAFloat);r.WaitForCompletion();if(r.hasError)throw new Exception("Readback failed");return r.GetData<float>().ToArray();}
    static void Report(string name,object value){Directory.CreateDirectory(Root);File.WriteAllText(Root+"/"+name,JsonConvert.SerializeObject(value,Formatting.Indented));}
    static void Png(string name,RenderTexture rt){var d=Read(rt);if(d.Any(x=>float.IsNaN(x)||float.IsInfinity(x)))throw new Exception("Non-finite "+name);var t=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false,true);var pixels=new Color[rt.width*rt.height];for(int i=0;i<pixels.Length;i++){var color=new Color(d[i*4],d[i*4+1],d[i*4+2],1);pixels[i]=QualitySettings.activeColorSpace==ColorSpace.Linear?color.gamma:color;}t.SetPixels(pixels);t.Apply();File.WriteAllBytes(Root+"/"+name,t.EncodeToPNG());Object.DestroyImmediate(t);}
    public static object Prepare(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");var s=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.SaveScene(s);
        var a=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);var sampler=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Sampler.mat");
        Report("play-baseline.json",new{virtualVoices=a.FindProperty("m_VirtualVoiceCount").intValue,realVoices=a.FindProperty("m_RealVoiceCount").intValue,noise=AssetDatabase.GetAssetPath(sampler.GetTexture("_Noise")),negative=AssetDatabase.GetAssetPath(sampler.GetTexture("_UnconditionedNoise"))});
        bool udonError=UdonSharp.UdonSharpProgramAsset.AnyUdonSharpScriptHasError();if(udonError)throw new Exception("Udon compilation errors remain");return new{prepared=true,floor=GameObject.Find("Floor").transform.localScale,udonError};
    }
    public static async Task<object> Smoke(){
        if(!EditorApplication.isPlaying)throw new Exception("PlayMode required");var c=C();var proxy=Object.FindObjectOfType<DiffusionController>();var m=U(proxy);var player=Networking.LocalPlayer;
        for(int attempt=0;attempt<50;attempt++){player=Networking.LocalPlayer;if(player!=null&&(int)c.GetProgramVariable("authorityId")==player.playerId&&(int)m.GetProgramVariable("authorityId")==player.playerId&&player.GetJumpImpulse()>=3)break;await Task.Delay(200);}
        int id=player==null?-1:player.playerId,sd=(int)c.GetProgramVariable("authorityId"),mnist=(int)m.GetProgramVariable("authorityId");
        if(player!=null&&(sd!=id||mnist!=id||player.GetJumpImpulse()<3))throw new Exception("Initial control/jump setup failed");
        m.SetProgramVariable("authorityId",-999);m.SendCustomEvent("UpdateLabels");
        var shared=proxy.buttons.Where(b=>!new[]{"RequestControl","Faster","Slower","NextMemory","PreviousMemory"}.Contains(b.command)).ToArray();
        bool disabled;try{disabled=shared.All(b=>!b.ui.interactable&&Mathf.Abs(b.ui.targetGraphic.color.r-b.ui.targetGraphic.color.g)<.001f&&b.ui.targetGraphic.color.a<.6f);}finally{m.SetProgramVariable("authorityId",mnist);m.SendCustomEvent("UpdateLabels");}if(!disabled)throw new Exception("MNIST locked buttons remain enabled or colored");
        var result=new{playerId=id,sdAuthority=sd,mnistAuthority=mnist,jumpImpulse=player==null?0:player.GetJumpImpulse(),lockedMnistButtons=shared.Length,disabled,independentLocks=proxy.gameObject!=Object.FindObjectOfType<SDController>().gameObject};Report("smoke.json",result);return result;
    }
    public static object Shoot(){
        var bridge=Object.FindObjectOfType<SDPolaroidBridge>();var b=bridge.polaroidManager;int previous=(int)b.GetProgramVariable("photoIndex");b.SendCustomEvent("SetPhotoIndex");int current=(int)b.GetProgramVariable("photoIndex");if(current!=previous+1)throw new Exception("Polaroid shot failed");return new{previous,current};
    }
    public static object Photo(){
        var b=U(Object.FindObjectOfType<SDInputs>());bool captured=(bool)b.GetProgramVariable("hasPhoto");if(!captured)throw new Exception("Bridge did not capture Polaroid photograph");var photo=(RenderTexture)b.GetProgramVariable("photo");var data=Read(photo);Png("photograph.png",photo);var result=new{captured,min=data.Min(),max=data.Max(),width=photo.width,height=photo.height};Report("photo.json",result);return result;
    }
    public static object StartRun(){
        var c=C();var input=U(Object.FindObjectOfType<SDInputs>());var positive=(InputField)input.GetProgramVariable("positiveInput");var negative=(InputField)input.GetProgramVariable("negativeInput");
        positive.text="masterpiece, best quality, anime illustration, an adult woman with blue hair, blue cardigan, window light, cherry blossoms, detailed portrait";
        negative.text=(string)input.GetProgramVariable("defaultNegative");c.SendCustomEvent("ApplyPrompt");
        c.SetProgramVariable("commandValue",32);c.SendCustomEvent("SetSpeed");c.SetProgramVariable("commandValue",20);c.SendCustomEvent("SetSteps");
        if(!(bool)c.GetProgramVariable("usePhoto"))c.SendCustomEvent("TogglePhoto");c.SetProgramVariable("commandValue",85);c.SendCustomEvent("SetStrength");
        c.SendCustomEvent("StartGeneration");if((int)c.GetProgramVariable("phase")!=1||!(bool)c.GetProgramVariable("customPrompt"))throw new Exception("Photo/custom prompt run failed to start");return State();
    }
    public static object State(){
        var c=C();var perf=U(Object.FindObjectOfType<SDPerformance>());var e=U(Object.FindObjectOfType<SDGraphExecutor>());var input=U(Object.FindObjectOfType<SDInputs>());
        var result=new{phase=c.GetProgramVariable("phase"),stage=c.GetProgramVariable("algorithmStage"),step=c.GetProgramVariable("localStep"),firstStep=c.GetProgramVariable("firstStep"),node=e.GetProgramVariable("node"),progress=perf.GetProgramVariable("progress"),activeSeconds=c.GetProgramVariable("activeSeconds"),elapsed=c.GetProgramVariable("elapsed"),photoSize=c.GetProgramVariable("photoSize"),photoBytes=((byte[])c.GetProgramVariable("photoBytes")).Length,notice=input.GetProgramVariable("notice")};Report("experience-state.json",result);return result;
    }
    public static object Capture(){
        var c=C();if((int)c.GetProgramVariable("algorithmStage")!=6)throw new Exception("Generation is not complete");Png("img2img-final.png",(RenderTexture)c.GetProgramVariable("displayedImage"));var history=(RenderTexture[])c.GetProgramVariable("history");for(int i=0;i<history.Length;i++)Png("img2img-history-"+i+".png",history[i]);return State();
    }
    public static object PhotoExport(){
        var controller=Object.FindObjectOfType<SDController>();var material=controller.photoExport;var source=(RenderTexture)material.GetTexture("_MainTex");var zone=material.GetVector("_StationCenter");
        var go=new GameObject("Temporary photo export validation");var camera=go.AddComponent<Camera>();camera.enabled=false;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.magenta;camera.transform.position=zone;camera.transform.rotation=Quaternion.identity;camera.nearClipPlane=.01f;camera.farClipPlane=100;
        var square=new RenderTexture(512,512,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);var expected=new RenderTexture(512,512,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);var wide=new RenderTexture(1024,512,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);float previousMode=Shader.GetGlobalFloat("_VRChatCameraMode"),previousMirror=Shader.GetGlobalFloat("_VRChatMirrorMode");
        try{
            Shader.SetGlobalFloat("_VRChatCameraMode",2);Shader.SetGlobalFloat("_VRChatMirrorMode",0);camera.targetTexture=square;camera.Render();Graphics.Blit(source,expected);var actual=Read(square);var reference=Read(expected);double sq=0;for(int i=0;i<actual.Length;i++)if(i%4!=3){float d=actual[i]-reference[i];sq+=d*d;}double rmse=Math.Sqrt(sq/(512*512*3));Png("camera-export-square.png",square);
            camera.targetTexture=wide;camera.Render();var letterbox=Read(wide);int border=(256*1024+20)*4;bool black=Math.Abs(letterbox[border])+Math.Abs(letterbox[border+1])+Math.Abs(letterbox[border+2])<.01;Png("camera-export-wide.png",wide);
            camera.targetTexture=square;Shader.SetGlobalFloat("_VRChatCameraMode",0);camera.Render();var normal=Read(square);double difference=0;for(int i=0;i<actual.Length;i++)difference+=Math.Abs(actual[i]-normal[i]);difference/=actual.Length;
            Shader.SetGlobalFloat("_VRChatCameraMode",2);camera.transform.position=(Vector3)zone+Vector3.right*3;camera.Render();var outside=Read(square);double outsideDiff=0;for(int i=0;i<actual.Length;i++)outsideDiff+=Math.Abs(actual[i]-outside[i]);outsideDiff/=actual.Length;
            var result=new{rmse,letterboxBlack=black,normalViewDifference=difference,outsideZoneDifference=outsideDiff,mode="Simulated VRChat shader globals; native client still pending",playMode=EditorApplication.isPlaying};Report("photo-export.json",result);if(rmse>.04||!black||difference<.01||outsideDiff<.01)throw new Exception("Photo export render validation failed: "+JsonConvert.SerializeObject(result));return result;
        }finally{Shader.SetGlobalFloat("_VRChatCameraMode",previousMode);Shader.SetGlobalFloat("_VRChatMirrorMode",previousMirror);camera.targetTexture=null;Object.DestroyImmediate(go);Object.DestroyImmediate(square);Object.DestroyImmediate(wide);Object.DestroyImmediate(expected);}
    }
    public static object Restore(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");var b=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Root+"/play-baseline.json"));var a=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);a.FindProperty("m_VirtualVoiceCount").intValue=(int)b["virtualVoices"];a.FindProperty("m_RealVoiceCount").intValue=(int)b["realVoices"];a.ApplyModifiedProperties();
        var sampler=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Sampler.mat");sampler.SetTexture("_Noise",AssetDatabase.LoadAssetAtPath<Texture>((string)b["noise"]));sampler.SetTexture("_UnconditionedNoise",AssetDatabase.LoadAssetAtPath<Texture>((string)b["negative"]));EditorUtility.SetDirty(sampler);AssetDatabase.SaveAssets();return new{restored=true};
    }
    public static object UI(){
        var c=C();var perf=U(Object.FindObjectOfType<SDPerformance>());var proxy=Object.FindObjectOfType<SDController>();var buttons=proxy.buttons;
        bool detail=(bool)c.GetProgramVariable("details"),preview=(bool)c.GetProgramVariable("vaePreviews");buttons.First(b=>b.command=="ToggleDetails").ui.onClick.Invoke();if((bool)c.GetProgramVariable("details")==detail)throw new Exception("Details click failed");buttons.First(b=>b.command=="ToggleDetails").ui.onClick.Invoke();
        buttons.First(b=>b.command=="TogglePreviews").ui.onClick.Invoke();if((bool)c.GetProgramVariable("vaePreviews")==preview)throw new Exception("VAE toggle click failed");buttons.First(b=>b.command=="TogglePreviews").ui.onClick.Invoke();perf.SendCustomEvent("Refresh");
        foreach(int speed in new[]{4,16,32}){buttons.First(b=>b.command=="SetSpeed"&&b.value==speed).ui.onClick.Invoke();if((int)c.GetProgramVariable("passesPerFrame")!=speed)throw new Exception("Speed click failed");}
        bool busy=(int)c.GetProgramVariable("phase")==1;var shared=buttons.Where(b=>new[]{"StartGeneration","SetPreset","SetResolution","SetSteps","ApplyPrompt","TogglePhoto","SetStrength","NextPreset","PreviousPreset"}.Contains(b.command)).ToArray();
        bool gray=!busy||shared.All(b=>!b.ui.interactable&&Mathf.Abs(b.ui.targetGraphic.color.r-b.ui.targetGraphic.color.g)<.001f);if(!gray)throw new Exception("SD busy controls not gray/disabled");
        var locale=Object.FindObjectOfType<DiffusionLocalization>();var l=U(locale);int original=(int)l.GetProgramVariable("languageOverride");
        try{for(int language=0;language<3;language++){l.SetProgramVariable("languageOverride",language);l.SendCustomEvent("SetLanguage");for(int i=0;i<locale.sources.Length;i++){var target=language==0?locale.english[i]:language==1?locale.chinese[i]:locale.japanese[i];if(!target.gameObject.activeSelf)throw new Exception("Language overlay inactive");}}}
        finally{l.SetProgramVariable("languageOverride",original);l.SendCustomEvent("SetLanguage");}
        var result=new{buttons=buttons.Length,toggles=true,speedButtons=true,threeLanguages=true,busyControlsGray=gray,settings=((Text)perf.GetProgramVariable("settingsText")).text};Report("experience-ui.json",result);return result;
    }
    public static object World(){
        var go=new GameObject("Temporary experience screenshot");var camera=go.AddComponent<Camera>();var rt=new RenderTexture(1920,1080,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
        try{camera.transform.position=new Vector3(-9.8f,3.1f,-8);camera.transform.LookAt(new Vector3(-9.8f,2.8f,3.14f));camera.fieldOfView=34;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.018f,.025f,.04f);camera.targetTexture=rt;camera.Render();Png("experience-world.png",rt);return new{path=Root+"/experience-world.png"};}
        finally{camera.targetTexture=null;Object.DestroyImmediate(rt);Object.DestroyImmediate(go);}
    }
    public static object ColorTransfer(){
        var m=new Material(Object.FindObjectOfType<SDController>().sampler);var source=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);var decoded=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);var packed=new RenderTexture(1024,1,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);var display=new RenderTexture(1,1,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
        try{var rgb=new Color(.25f,.5f,.75f,1);source.SetPixel(0,0,rgb);source.Apply();decoded.SetPixel(0,0,new Color(-.5f,0,.5f,0));decoded.Apply();m.SetTexture("_SourceImage",source);m.SetFloat("_SourceAspect",1);m.SetInt("_RGBSize",1);m.SetInt("_Mode",11);Graphics.Blit(null,packed,m);var p=Read(packed);var expected=QualitySettings.activeColorSpace==ColorSpace.Linear?rgb.gamma:rgb;float inputError=0;for(int i=0;i<3;i++)inputError=Mathf.Max(inputError,Mathf.Abs(p[i]-(expected[i]*2-1)));
            m.SetTexture("_RGB",decoded);m.SetInt("_RGBShift",0);m.SetInt("_Mode",5);Graphics.Blit(null,display,m);var d=Read(display);expected=QualitySettings.activeColorSpace==ColorSpace.Linear?rgb.linear:rgb;float outputError=0;for(int i=0;i<3;i++)outputError=Mathf.Max(outputError,Mathf.Abs(d[i]-expected[i]));var result=new{space=QualitySettings.activeColorSpace.ToString(),inputMaxAbs=inputError,outputMaxAbs=outputError};Report("color-transfer.json",result);if(inputError>.006||outputError>.006)throw new Exception("sRGB/linear transfer failed");return result;
        }finally{Object.DestroyImmediate(m);Object.DestroyImmediate(source);Object.DestroyImmediate(decoded);Object.DestroyImmediate(packed);Object.DestroyImmediate(display);}
    }
}
