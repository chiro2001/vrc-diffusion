using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

public static class ValidateDiffusionModels {
    public static Task batchTask;
    static float[] Floats(string path){byte[] b=File.ReadAllBytes(path);float[] f=new float[b.Length/4];Buffer.BlockCopy(b,0,f,0,b.Length);return f;}
    public static float[] Read(RenderTexture rt){
        var active=RenderTexture.active;var tex=new Texture2D(rt.width,rt.height,TextureFormat.RFloat,false,true);
        try{RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false);tex.Apply();return tex.GetRawTextureData<float>().ToArray();}
        finally{RenderTexture.active=active;Object.DestroyImmediate(tex);}
    }
    static object Compare(float[] got,float[] expected,int size,int channels,string name,double limit=.003){
        if(got.Length!=expected.Length)throw new Exception("Shape mismatch "+name);double max=0,ss=0;int bad=0;
        for(int c=0;c<channels;c++)for(int y=0;y<size;y++)for(int x=0;x<size;x++){
            float v=got[y*size*channels+c*size+x],e=expected[(c*size+y)*size+x];if(float.IsNaN(v)||float.IsInfinity(v))bad++;
            double d=Math.Abs(v-e);max=Math.Max(max,d);ss+=d*d;
        }
        if(max>limit||bad>0)throw new Exception(name+" maxAbs="+max+" nonfinite="+bad);
        return new{name,maxAbs=max,rmse=Math.Sqrt(ss/got.Length),nonfinite=bad};
    }
    static void Forward(DiffusionModel model,Texture input,int digit,int time){
        for(int i=0;i<model.layers.Length;i++){
            var mat=model.layers[i];if(i==0)mat.SetTexture("_Input",input);mat.SetInt("_Class",digit);mat.SetInt("_Timestep",time);
            Graphics.Blit(null,model.memory[i],mat);
        }
    }
    static int TimeAt(DiffusionModel model,int step,int steps){
        if(model.leadingSpacing)return (steps-1-step)*(model.alpha.Length/steps);
        return model.timestepMin+Mathf.RoundToInt((model.timestepMax-model.timestepMin)*(1f-step/(float)(steps-1)));
    }
    public static object StartBatch(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");
        Directory.CreateDirectory("Reports/DiffusionLab/GpuBatch");
        File.WriteAllText("Reports/DiffusionLab/gpu-batch.json","{\"status\":\"running\",\"completed\":0}");
        batchTask=Batch();return new{started=true,assembly=typeof(ValidateDiffusionModels).Assembly.GetName().Name,report="Reports/DiffusionLab/gpu-batch.json"};
    }
    static async Task Batch(){
        var c=Object.FindObjectOfType<DiffusionController>();var model=c.models[1];var sampler=new Material(c.sampler);var old=model.layers[0].GetTexture("_Input");
        string root="Assets/DiffusionLab/Data/TeaQuality";var config=JObject.Parse(File.ReadAllText(root+"/batch.json"));var cases=(JArray)config["samples"];int steps=(int)config["steps"];var results=new List<object>();
        try{
            sampler.SetFloat("_ClipPrediction",0);sampler.SetFloat("_ValueMin",0);sampler.SetFloat("_ValueMax",1);sampler.SetFloat("_Guidance",3);
            for(int caseIndex=0;caseIndex<cases.Count;caseIndex++){
                int seed=(int)cases[caseIndex]["seed"],digit=(int)cases[caseIndex]["digit"];
                sampler.SetTexture("_State",null);sampler.SetTexture("_Noise",null);sampler.SetTexture("_UnconditionedNoise",null);
                sampler.SetInt("_SeedLo",seed&65535);sampler.SetInt("_SeedHi",seed>>16);sampler.SetInt("_Mode",0);Graphics.Blit(null,model.stateA,sampler);
                RenderTexture current=model.stateA,next=model.stateB;
                for(int step=0;step<steps;step++){
                    int t=TimeAt(model,step,steps);Forward(model,current,digit,t);Graphics.Blit(model.memory.Last(),model.conditional);Forward(model,current,10,t);
                    sampler.SetTexture("_State",current);sampler.SetTexture("_Noise",model.conditional);sampler.SetTexture("_UnconditionedNoise",model.memory.Last());
                    sampler.SetFloat("_Alpha",model.alpha[t]);sampler.SetFloat("_NextAlpha",step+1>=steps?1:model.alpha[TimeAt(model,step+1,steps)]);sampler.SetInt("_Mode",1);Graphics.Blit(null,next,sampler);var swap=current;current=next;next=swap;
                    if(step%4==3)await Task.Yield();
                }
                float[] raw=Read(current);var comparison=Compare(raw,Floats(root+"/"+(string)cases[caseIndex]["file"]),model.nativeSize,1,"digit "+digit+" seed "+seed);
                byte[] b=new byte[raw.Length*4];Buffer.BlockCopy(raw,0,b,0,b.Length);File.WriteAllBytes("Reports/DiffusionLab/GpuBatch/case-"+caseIndex.ToString("D2")+".bytes",b);
                results.Add(new{digit,seed,comparison});File.WriteAllText("Reports/DiffusionLab/gpu-batch.json",JsonConvert.SerializeObject(new{status="running",completed=caseIndex+1,results},Formatting.Indented));await Task.Yield();
            }
            File.WriteAllText("Reports/DiffusionLab/gpu-batch.json",JsonConvert.SerializeObject(new{status="completed",passed=true,samples=cases.Count,steps,source="actual Shader hash noise + Direct3D11 fragment graph",results},Formatting.Indented));
        }catch(Exception e){File.WriteAllText("Reports/DiffusionLab/gpu-batch.json",JsonConvert.SerializeObject(new{status="failed",error=e.ToString(),results},Formatting.Indented));Debug.LogException(e);}
        finally{model.layers[0].SetTexture("_Input",old);Object.DestroyImmediate(sampler);}
    }
    public static async Task<object> Numeric(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");
        var c=Object.FindObjectOfType<DiffusionController>();var report=new List<object>();
        for(int mi=0;mi<c.models.Length;mi++){
            var model=c.models[mi];string root="Assets/DiffusionLab/Data"+(mi==1?"/Tea":"");int size=model.nativeSize;
            var input=new Texture2D(size,size,TextureFormat.RFloat,false,true){filterMode=FilterMode.Point};input.LoadRawTextureData(File.ReadAllBytes(root+"/reference-input.bytes"));input.Apply();
            var oldInput=model.layers[0].GetTexture("_Input");var sampler=new Material(c.sampler);
            var comparisons=new List<object>();var start=DateTime.UtcNow;
            try{
                Forward(model,input,3,128);
                for(int i=0;i<model.layers.Length;i++)comparisons.Add(Compare(Read(model.memory[i]),Floats(root+"/reference-layer-"+i.ToString("D2")+".bytes"),model.memory[i].height,model.channels[i],"model "+mi+" node "+i));
                Graphics.Blit(input,model.stateA);RenderTexture current=model.stateA,next=model.stateB;int steps=model.defaultSteps;
                sampler.SetFloat("_ClipPrediction",model.clipPrediction?1:0);sampler.SetFloat("_ValueMin",model.valueMin);sampler.SetFloat("_ValueMax",model.valueMax);
                Directory.CreateDirectory("Reports/DiffusionLab/Model"+mi);
                for(int step=0;step<steps;step++){
                    int t=TimeAt(model,step,steps);Forward(model,current,3,t);Graphics.Blit(model.memory.Last(),model.conditional);
                    if(model.classifierFreeGuidance)Forward(model,current,10,t);
                    sampler.SetTexture("_State",current);sampler.SetTexture("_Noise",model.classifierFreeGuidance?model.conditional:model.memory.Last());sampler.SetTexture("_UnconditionedNoise",model.memory.Last());
                    sampler.SetFloat("_Guidance",model.classifierFreeGuidance?model.guidanceScale:1);sampler.SetFloat("_Alpha",model.alpha[t]);sampler.SetFloat("_NextAlpha",step+1>=steps?1:model.alpha[TimeAt(model,step+1,steps)]);
                    sampler.SetInt("_Mode",1);Graphics.Blit(null,next,sampler);var swap=current;current=next;next=swap;
                    var raw=Read(current);byte[] b=new byte[raw.Length*4];Buffer.BlockCopy(raw,0,b,0,b.Length);File.WriteAllBytes("Reports/DiffusionLab/Model"+mi+"/step-"+(step+1).ToString("D2")+".bytes",b);
                    if(step==0||step==steps/2-1||step==steps-1)comparisons.Add(Compare(raw,Floats(root+"/reference-step-"+(step+1).ToString("D2")+".bytes"),size,1,"model "+mi+" DDIM "+(step+1)));
                    if(step%4==3)await Task.Yield();
                }
                report.Add(new{model=model.displayName,version=model.version,size,steps,seconds=(DateTime.UtcNow-start).TotalSeconds,comparisons});
            }finally{model.layers[0].SetTexture("_Input",oldInput);Object.DestroyImmediate(input);Object.DestroyImmediate(sampler);}
        }
        var result=new{passed=true,device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),models=report};
        Directory.CreateDirectory("Reports/DiffusionLab");File.WriteAllText("Reports/DiffusionLab/models-numeric.json",JsonConvert.SerializeObject(result,Formatting.Indented));return result;
    }
}
