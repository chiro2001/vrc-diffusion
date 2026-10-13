using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

public static class ValidateDiffusion {
    const string Root="Assets/DiffusionLab";
    static float[] ReadFloats(string path){byte[] b=File.ReadAllBytes(path);float[] f=new float[b.Length/4];Buffer.BlockCopy(b,0,f,0,b.Length);return f;}
    static Task<float[]> Read(RenderTexture rt){
        var previous=RenderTexture.active;
        var texture=new Texture2D(rt.width,rt.height,TextureFormat.RFloat,false,true);
        try {RenderTexture.active=rt;texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false);texture.Apply(false,false);return Task.FromResult(texture.GetRawTextureData<float>().ToArray());}
        finally {RenderTexture.active=previous;Object.DestroyImmediate(texture);}
    }
    static void SaveState(float[] data,int step) {
        Directory.CreateDirectory("Reports/DiffusionLab/GpuTrajectory");
        byte[] bytes=new byte[data.Length*4];Buffer.BlockCopy(data,0,bytes,0,bytes.Length);
        File.WriteAllBytes("Reports/DiffusionLab/GpuTrajectory/step-"+step.ToString("D2")+".bytes",bytes);
    }
    static object Compare(float[] gpu,float[] reference,int size,int channels,string name) {
        if(gpu.Length!=reference.Length)throw new Exception("Size mismatch "+name);
        double max=0,sq=0;int bad=0;
        for(int c=0;c<channels;c++)for(int y=0;y<size;y++)for(int x=0;x<size;x++) {
            float actual=gpu[y*size*channels+c*size+x];float expected=reference[(c*size+y)*size+x];
            if(float.IsNaN(actual)||float.IsInfinity(actual))bad++;
            double d=Math.Abs(actual-expected);max=Math.Max(max,d);sq+=d*d;
        }
        if(bad>0||max>.003)throw new Exception(name+" mismatch: max="+max+" nonfinite="+bad);
        return new{name,maxAbs=max,rmse=Math.Sqrt(sq/gpu.Length),nonfinite=bad};
    }
    public static async Task<object> Numeric() {
        if(EditorApplication.isPlaying)throw new Exception("Numeric test requires EditMode");
        var ctrl=Object.FindObjectOfType<DiffusionController>();if(ctrl==null)throw new Exception("Build/open scene first");
        // An imported CPU fixture isolates NN arithmetic from seed generator differences.
        var input=new Texture2D(32,32,TextureFormat.RFloat,false,true){filterMode=FilterMode.Point};
        input.LoadRawTextureData(File.ReadAllBytes(Root+"/Data/reference-input.bytes"));input.Apply();
        Texture previous=ctrl.layers[0].GetTexture("_Input");
        var reports=new List<object>();var sw=Stopwatch.StartNew();
        try {
            ctrl.layers[0].SetTexture("_Input",input);
            for(int i=0;i<ctrl.layers.Length;i++) {
                ctrl.layers[i].SetInt("_Class",3);ctrl.layers[i].SetInt("_Timestep",128);
                Graphics.Blit(null,ctrl.layerMemory[i],ctrl.layers[i]);
                var actual=await Read(ctrl.layerMemory[i]);int size=ctrl.layerMemory[i].height;
                reports.Add(Compare(actual,ReadFloats(Root+"/Data/reference-layer-"+i.ToString("D2")+".bytes"),size,ctrl.layerChannels[i],"layer "+i));
            }
            Graphics.Blit(input,ctrl.stateA);
            var initial=await Read(ctrl.stateA);
            reports.Add(Compare(initial,ReadFloats(Root+"/Data/reference-input.bytes"),32,1,"initial fixture copy"));SaveState(initial,0);
            RenderTexture current=ctrl.stateA,next=ctrl.stateB;
            for(int step=0;step<32;step++) {
                int t=ctrl.classifierFreeGuidance?(31-step)*8:Mathf.RoundToInt(255*(1f-step/31f));
                for(int i=0;i<ctrl.layers.Length;i++) {
                    if(i==0)ctrl.layers[i].SetTexture("_Input",current);
                    ctrl.layers[i].SetInt("_Class",3);ctrl.layers[i].SetInt("_Timestep",t);
                    Graphics.Blit(null,ctrl.layerMemory[i],ctrl.layers[i]);
                }
                if(ctrl.classifierFreeGuidance) {
                    Graphics.Blit(ctrl.layerMemory.Last(),ctrl.conditionalNoise);
                    for(int i=0;i<ctrl.layers.Length;i++) {
                        if(i==0)ctrl.layers[i].SetTexture("_Input",current);
                        ctrl.layers[i].SetInt("_Class",10);ctrl.layers[i].SetInt("_Timestep",t);
                        Graphics.Blit(null,ctrl.layerMemory[i],ctrl.layers[i]);
                    }
                }
                ctrl.sampler.SetTexture("_State",current);
                ctrl.sampler.SetTexture("_Noise",ctrl.classifierFreeGuidance?ctrl.conditionalNoise:ctrl.layerMemory.Last());
                ctrl.sampler.SetTexture("_UnconditionedNoise",ctrl.layerMemory.Last());
                ctrl.sampler.SetFloat("_Guidance",ctrl.classifierFreeGuidance?ctrl.guidanceScale:1f);
                ctrl.sampler.SetFloat("_Alpha",ctrl.alphaCumprod[t]);
                ctrl.sampler.SetFloat("_NextAlpha",step==31?1:ctrl.alphaCumprod[ctrl.classifierFreeGuidance?(30-step)*8:Mathf.RoundToInt(255*(1f-(step+1)/31f))]);
                ctrl.sampler.SetInt("_Mode",1);Graphics.Blit(null,next,ctrl.sampler);
                var swap=current;current=next;next=swap;
                var actualState=await Read(current);SaveState(actualState,step+1);
                if(step==0||step==15||step==31)reports.Add(Compare(actualState,ReadFloats(Root+"/Data/reference-step-"+(step+1).ToString("D2")+".bytes"),32,1,"DDIM step "+(step+1)));
            }
            sw.Stop();
            var report=new{passed=true,modelVersion=ctrl.modelVersion,classifierFreeGuidance=ctrl.classifierFreeGuidance,device=SystemInfo.graphicsDeviceName,graphicsApi=SystemInfo.graphicsDeviceType.ToString(),seconds=sw.Elapsed.TotalSeconds,comparisons=reports};
            Directory.CreateDirectory("Reports/DiffusionLab");File.WriteAllText("Reports/DiffusionLab/numeric.json",JsonConvert.SerializeObject(report,Formatting.Indented));return report;
        } finally {ctrl.layers[0].SetTexture("_Input",previous);Object.DestroyImmediate(input);}
    }
    public static object Capture() {
        var cam=Object.FindObjectsOfType<Camera>(true).First(c=>c.name=="PreviewCamera");
        var rt=new RenderTexture(1600,1000,24,RenderTextureFormat.ARGB32);rt.Create();
        var tex=new Texture2D(1600,1000,TextureFormat.RGB24,false);var prev=RenderTexture.active;
        cam.targetTexture=rt;cam.Render();RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,1600,1000),0,0);tex.Apply();
        Directory.CreateDirectory("Reports/DiffusionLab");File.WriteAllBytes("Reports/DiffusionLab/world.png",tex.EncodeToPNG());
        cam.targetTexture=null;RenderTexture.active=prev;Object.DestroyImmediate(tex);rt.Release();Object.DestroyImmediate(rt);return new{captured=true,path="Reports/DiffusionLab/world.png"};
    }
}
