using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;
using Object=UnityEngine.Object;

public static class TestDiffusionRuntime {
    static UdonBehaviour Backing() {var c=Object.FindObjectOfType<DiffusionController>();if(c==null)throw new Exception("No controller");return UdonSharpEditorUtility.GetBackingUdonBehaviour(c);}
    static int Int(UdonBehaviour b,string n){return (int)b.GetProgramVariable(n);}
    static float[] ReadNoise(RenderTexture rt) {
        var previous=RenderTexture.active;var tex=new Texture2D(32,32,TextureFormat.RFloat,false,true);
        try {RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,32,32),0,0,false);tex.Apply();return tex.GetRawTextureData<float>().ToArray();}
        finally {RenderTexture.active=previous;Object.DestroyImmediate(tex);}
    }
    static async Task Until(Func<bool> ok,int timeoutMs=25000){var start=DateTime.UtcNow;while(!ok()){if((DateTime.UtcNow-start).TotalMilliseconds>timeoutMs)throw new Exception("Runtime wait timed out");await Task.Delay(50);}}
    public static async Task<object> Run() {
        if(!EditorApplication.isPlaying)throw new Exception("Enter PlayMode first");
        var b=Backing();
        b.SetProgramVariable("passesPerFrame",4);
        b.SetProgramVariable("validationRunOnStart",true);
        b.SendCustomEvent("BeginValidationRun");
        await Until(()=>Int(b,"localStep")==32);
        await Until(()=>(bool)b.GetProgramVariable("validationPassed"),5000);
        float error=(float)b.GetProgramVariable("validationMaxError");
        b.SetProgramVariable("validationRunOnStart",false);
        b.SendCustomEvent("ResetRun");
        if(Int(b,"phase")!=0||Int(b,"localStep")!=0)throw new Exception("Reset failed");
        var seedField=(InputField)b.GetProgramVariable("seedInput");
        seedField.text="987654320";b.SendCustomEvent("ApplySeed");b.SendCustomEvent("PauseResume");
        if(Int(b,"seed")!=987654320||Int(b,"phase")!=2||Int(b,"localStep")!=0)throw new Exception("Paused noise initialization failed");
        var noiseA=ReadNoise((RenderTexture)b.GetProgramVariable("stateA"));
        seedField.text="987654321";b.SendCustomEvent("ApplySeed");b.SendCustomEvent("PauseResume");
        var noiseB=ReadNoise((RenderTexture)b.GetProgramVariable("stateA"));
        float difference=0;for(int i=0;i<noiseA.Length;i++)difference=Mathf.Max(difference,Mathf.Abs(noiseA[i]-noiseB[i]));
        if(Int(b,"seed")!=987654321||difference<.1f)throw new Exception("Adjacent high seeds lost precision");
        b.SendCustomEvent("ResetRun");
        b.SetProgramVariable("commandValue",7);b.SendCustomEvent("SelectDigit");
        if(Int(b,"digit")!=7)throw new Exception("Class input failed");
        b.SendCustomEvent("Generate");b.SendCustomEvent("PauseResume");
        if(Int(b,"phase")!=2)throw new Exception("Pause failed");
        int layer=Int(b,"localLayer");b.SendCustomEvent("StepOperator");await Until(()=>Int(b,"localLayer")!=layer);
        if(Int(b,"localStep")!=0)throw new Exception("Operator step advanced too far");
        b.SendCustomEvent("StepDenoise");await Until(()=>Int(b,"localStep")==1);
        await Task.Delay(100);
        if(Int(b,"phase")!=2||Int(b,"localStep")!=1)throw new Exception("Single denoise failed");
        b.SendCustomEvent("PauseResume");await Until(()=>Int(b,"localStep")==32);
        int expectedEpoch=Int(b,"epoch");int expectedRevision=Int(b,"revision");
        b.SetProgramVariable("revision",expectedRevision-1);b.SetProgramVariable("phase",0);b.SetProgramVariable("epoch",expectedEpoch-1);b.SetProgramVariable("seed",0);
        b.SendCustomEvent("ApplyReceivedState");
        if(Int(b,"phase")!=3||Int(b,"epoch")!=expectedEpoch||Int(b,"revision")!=expectedRevision||Int(b,"seed")!=987654321)throw new Exception("Stale snapshot was not rejected");
        var report=new{passed=true,numericMaxAbs=error,highSeedNoiseDifference=difference,controls=new[]{"class","seed","paused noise initialization","generate","pause","operator step","denoise step","resume","reset","stale snapshot rejection"},status=((UnityEngine.UI.Text)b.GetProgramVariable("statusText")).text};
        Directory.CreateDirectory("Reports/DiffusionLab");File.WriteAllText("Reports/DiffusionLab/runtime.json",JsonConvert.SerializeObject(report,Formatting.Indented));return report;
    }
    public static object Status(){var b=Backing();return new{step=Int(b,"localStep"),layer=Int(b,"localLayer"),phase=Int(b,"phase"),validationPassed=b.GetProgramVariable("validationPassed"),error=b.GetProgramVariable("validationMaxError"),status=((UnityEngine.UI.Text)b.GetProgramVariable("statusText")).text};}
}
