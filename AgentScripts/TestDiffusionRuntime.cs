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
    static float[] Read(RenderTexture rt){
        var prev=RenderTexture.active;var tex=new Texture2D(rt.width,rt.height,TextureFormat.RFloat,false,true);
        try{RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false);tex.Apply();return tex.GetRawTextureData<float>().ToArray();}
        finally{RenderTexture.active=prev;Object.DestroyImmediate(tex);}
    }
    static UdonBehaviour Backing(){var c=Object.FindObjectOfType<DiffusionController>();if(c==null)throw new Exception("No controller");return UdonSharpEditorUtility.GetBackingUdonBehaviour(c);}
    static UdonBehaviour Teaching(){return UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<DiffusionTeaching>());}
    static int Int(UdonBehaviour b,string n){return (int)b.GetProgramVariable(n);}
    static async Task Until(Func<bool> ok,int timeoutMs=90000){var start=DateTime.UtcNow;while(!ok()){if((DateTime.UtcNow-start).TotalMilliseconds>timeoutMs)throw new Exception("Runtime wait timed out");await Task.Delay(30);}}
    static void Value(UdonBehaviour b,int value,string action){b.SetProgramVariable("commandValue",value);b.SendCustomEvent(action);}
    public static async Task<object> Run(){
        if(!EditorApplication.isPlaying)throw new Exception("Enter PlayMode first");var b=Backing();var teach=Teaching();b.SendCustomEvent("RequestControl");b.SetProgramVariable("passesPerFrame",8);
        var errors=new float[2];
        for(int model=0;model<2;model++){
            Value(b,model,"SelectModel");b.SetProgramVariable("validationRunOnStart",true);b.SendCustomEvent("BeginValidationRun");int steps=Int(b,"totalSteps");
            await Until(()=>Int(b,"localStep")==steps);await Until(()=>(bool)b.GetProgramVariable("validationPassed"),5000);errors[model]=(float)b.GetProgramVariable("validationMaxError");
        }
        b.SetProgramVariable("validationRunOnStart",false);b.SendCustomEvent("ResetRun");
        var seed=(InputField)b.GetProgramVariable("seedInput");seed.text="987654320";b.SendCustomEvent("ApplySeed");b.SendCustomEvent("PauseResume");var noiseA=Read((RenderTexture)b.GetProgramVariable("stateA"));
        seed.text="987654321";b.SendCustomEvent("ApplySeed");b.SendCustomEvent("PauseResume");var noiseB=Read((RenderTexture)b.GetProgramVariable("stateA"));float difference=0;for(int i=0;i<noiseA.Length;i++)difference=Mathf.Max(difference,Mathf.Abs(noiseA[i]-noiseB[i]));if(difference<.1f)throw new Exception("High seed precision collapsed");
        Value(b,7,"SelectDigit");b.SendCustomEvent("Generate");b.SendCustomEvent("PauseResume");
        int previous=Int(b,"localLayer");b.SendCustomEvent("StepOperator");await Until(()=>Int(b,"localLayer")!=previous);if(Int(b,"localStep")!=0)throw new Exception("Operator step advanced a full step");
        b.SendCustomEvent("StepDenoise");await Until(()=>Int(b,"localStep")==1);await Task.Delay(100);if(Int(b,"phase")!=2||Int(b,"localStep")!=1)throw new Exception("Denoise step did not stay paused");
        await Until(()=>(bool)teach.GetProgramVariable("probeReady"),5000);
        var v=(float[])teach.GetProgramVariable("probeValues");float alpha=(float)teach.GetProgramVariable("probeAlpha"),next=(float)teach.GetProgramVariable("probeNextAlpha");
        float eps=v[2]+3*(v[1]-v[2]);float x0=(v[0]-Mathf.Sqrt(1-alpha)*v[3])/Mathf.Sqrt(alpha);float xNext=Mathf.Sqrt(next)*v[4]+Mathf.Sqrt(1-next)*v[3];
        float formulaError=Mathf.Max(Mathf.Abs(eps-v[3]),Mathf.Max(Mathf.Abs(x0-v[4]),Mathf.Abs(xNext-v[5])));if(formulaError>.003f)throw new Exception("Teaching data/formula mismatch "+formulaError);
        int[] sizes={28,32,64,128};for(int output=0;output<4;output++){Value(b,output,"SelectOutput");var display=(Material)b.GetProgramVariable("mainDisplay");if(display.GetTexture("_MainTex").width!=sizes[output])throw new Exception("Output switch failed");}
        // Same-epoch late snapshot must immediately apply output choice even while paused.
        int revision=Int(b,"revision");b.SetProgramVariable("outputIndex",0);b.SetProgramVariable("revision",revision+1);b.SendCustomEvent("ApplyReceivedState");if(((Material)b.GetProgramVariable("mainDisplay")).GetTexture("_MainTex").width!=28)throw new Exception("Paused shared output switch failed");
        b.SendCustomEvent("PauseResume");await Until(()=>Int(b,"localStep")==64);
        int expectedEpoch=Int(b,"epoch"),expectedRevision=Int(b,"revision");b.SetProgramVariable("revision",expectedRevision-1);b.SetProgramVariable("epoch",expectedEpoch-1);b.SetProgramVariable("modelIndex",0);b.SetProgramVariable("outputIndex",3);b.SetProgramVariable("seed",0);b.SendCustomEvent("ApplyReceivedState");
        if(Int(b,"epoch")!=expectedEpoch||Int(b,"revision")!=expectedRevision||Int(b,"modelIndex")!=1||Int(b,"seed")!=987654321||Int(b,"outputIndex")!=0)throw new Exception("Stale shared snapshot was not rejected");
        b.SetProgramVariable("revision",expectedRevision+1);b.SetProgramVariable("modelIndex",100);b.SendCustomEvent("ApplyReceivedState");if(Int(b,"modelIndex")!=1)throw new Exception("Invalid model snapshot was not rejected");
        teach.SendCustomEvent("NextPage");if(Int(teach,"page")!=1)throw new Exception("Teaching page button failed");
        var report=new{passed=true,numericMaxAbs=errors,highSeedNoiseDifference=difference,teachingFormulaMaxAbs=formulaError,controls=new[]{"model 32/28","seed","paused noise","class","generate","pause","operator","denoise","resume","output 28/32/64/128","same-epoch output","stale snapshot","invalid model snapshot","teaching page","GPU pixel probe"}};
        Directory.CreateDirectory("Reports/DiffusionLab");File.WriteAllText("Reports/DiffusionLab/runtime.json",JsonConvert.SerializeObject(report,Formatting.Indented));return report;
    }
    public static object Status(){var b=Backing();return new{step=Int(b,"localStep"),layer=Int(b,"localLayer"),phase=Int(b,"phase"),model=Int(b,"modelIndex"),validationPassed=b.GetProgramVariable("validationPassed"),error=b.GetProgramVariable("validationMaxError"),status=((Text)b.GetProgramVariable("statusText")).text};}
}
