using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using Object=UnityEngine.Object;
public static class ValidateSDModel {
    static string Tmp=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"tmp/vrc-diffusion/sd-anime");
    static string Reports=Path.Combine(Tmp,"unity-validation");
    static void Report(string file,object o){Directory.CreateDirectory(Reports);File.WriteAllText(Path.Combine(Reports,file),JsonConvert.SerializeObject(o,Formatting.Indented));}
    static float[] Read(RenderTexture rt){var r=AsyncGPUReadback.Request(rt,0,TextureFormat.RGBAFloat);r.WaitForCompletion();if(r.hasError)throw new Exception("GPU readback failed");return r.GetData<float>().ToArray();}
    static Texture2D Input(string path,int width,int height){
        byte[] raw=File.ReadAllBytes(path),padded=new byte[width*height*16];Buffer.BlockCopy(raw,0,padded,0,raw.Length);var t=new Texture2D(width,height,TextureFormat.RGBAFloat,false,true);t.LoadRawTextureData(padded);t.filterMode=FilterMode.Point;t.Apply();return t;
    }
    public static object ShaderStatus(){return new[]{"SDTensor","SDSampler","SDMemoryDisplay"}.Select(name=>{var s=Shader.Find("DiffusionLab/"+name);return new{name,supported=s.isSupported,passes=s.passCount,messages=ShaderUtil.GetShaderMessages(s).Select(m=>new{m.message,m.line,severity=m.severity.ToString()}).ToArray()};}).ToArray();}
    public static async Task<object> Graph(string reference="reference-w8a8-256",int maxNodes=609){
        if(EditorApplication.isPlaying)throw new Exception("Numeric test requires EditMode");
        var c=Object.FindObjectOfType<SDController>();var e=c.executor;e.graph=c.unetGraphs[0];var g=e.graph;
        string root=Path.Combine(Tmp,reference);var input=Input(Path.Combine(root,"initial.bytes"),64,16);e.latent=input;e.condition=c.embeddings[1];e.timeEmbedding=c.timeMemory;
        c.sampler.SetInt("_Mode",6);c.sampler.SetInt("_Timestep",951);Graphics.Blit(null,c.timeMemory,c.sampler);e.Begin();
        var rows=new List<object>();var sw=System.Diagnostics.Stopwatch.StartNew();int draws=0,last=-1;
        try{
            while(!e.finished&&e.node<maxNodes){
                e.Compute();draws++;
                if(e.lastCompletedNode!=last&&e.lastCompletedNode>=0){
                    last=e.lastCompletedNode;string path=Path.Combine(root,"node-"+last+".bytes");
                    if(File.Exists(path)){
                        float[] actual=Read(e.lastMemory);byte[] bytes=File.ReadAllBytes(path);float[] expected=new float[bytes.Length/4];Buffer.BlockCopy(bytes,0,expected,0,bytes.Length);
                        double sq=0,norm=0;float max=0;int bad=0;
                        for(int j=0;j<expected.Length;j++){if(float.IsNaN(actual[j])||float.IsInfinity(actual[j]))bad++;float diff=actual[j]-expected[j];max=Math.Max(max,Math.Abs(diff));sq+=diff*diff;norm+=expected[j]*expected[j];}
                        if(bad>0)throw new Exception("Non-finite tensor at node "+last);if(last==g.outputNode&&Math.Sqrt(sq/Math.Max(norm,1e-20))>.03)throw new Exception("U-Net relative RMSE exceeds 3%");
                        rows.Add(new{node=last,name=g.nodeNames[last],maxAbs=max,rmse=Math.Sqrt(sq/expected.Length),referenceRMS=Math.Sqrt(norm/expected.Length),nonFinite=bad,count=expected.Length});
                    }
                    Report("graph.json",new{status="running",node=e.node,draws,seconds=sw.Elapsed.TotalSeconds,rows});
                }
                if(draws%12==0){await Task.Delay(1);}
            }
            var result=new{status="completed",nodes=e.node,draws,seconds=sw.Elapsed.TotalSeconds,rows};Report("graph.json",result);return result;
        }catch(Exception ex){Report("graph.json",new{status="failed",node=e.node,draws,rows,error=ex.ToString()});throw;}
        finally{Object.DestroyImmediate(input);}
    }
    public static object FirstNode(){
        var c=Object.FindObjectOfType<SDController>();var e=c.executor;e.graph=c.unetGraphs[0];e.latent=c.latentA[0];e.condition=c.embeddings[1];e.timeEmbedding=c.timeMemory;
        c.sampler.SetInt("_Mode",6);c.sampler.SetInt("_Timestep",951);Graphics.Blit(null,c.timeMemory,c.sampler);e.Begin();
        var time=Read(c.timeMemory).Take(24).ToArray();var stages=new List<object>();
        while(e.node==0){e.Compute();if(e.substage==2)stages.Add(new{stage="scale",data=Read(e.graph.scale).Take(4).ToArray()});if(e.substage==3)stages.Add(new{stage="quantized",data=Read(e.graph.quantized).Take(24).ToArray()});if(e.substage==4)stages.Add(new{stage="tile",data=Read(e.graph.tile).Take(24).ToArray()});}
        return new{time,stages,output=Read(e.lastMemory).Take(24).ToArray(),properties=new{layout=e.graph.operators[0].GetInt("_WeightLayout"),weight=e.graph.operators[0].GetTexture("_Weights").name,kernel=e.graph.operators[0].GetInt("_Kernel"),stride=e.graph.operators[0].GetInt("_Stride"),padding=e.graph.operators[0].GetInt("_Padding")},parameters=e.graph.operators[0].GetTexture("_Params").name};
    }
    public static object Progress(){string path=Path.Combine(Reports,"graph.json");return File.Exists(path)?File.ReadAllText(path):"not started";}
    public static object CaptureNoise(){
        var c=Object.FindObjectOfType<SDController>();c.sampler.SetInt("_Mode",0);c.sampler.SetInt("_SeedLo",1234);c.sampler.SetInt("_SeedHi",0);c.sampler.SetInt("_LatentSize",32);Graphics.Blit(null,c.latentA[0],c.sampler);
        var data=Read(c.latentA[0]);Directory.CreateDirectory(Reports);byte[] bytes=new byte[data.Length*4];Buffer.BlockCopy(data,0,bytes,0,bytes.Length);File.WriteAllBytes(Path.Combine(Reports,"map-initial-256.bytes"),bytes);return new{seed=1234,count=data.Length};
    }
}
