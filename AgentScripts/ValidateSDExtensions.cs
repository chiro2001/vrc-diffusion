using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UdonSharpEditor;
using VRC.Udon;
using Object=UnityEngine.Object;
public static class ValidateSDExtensions {
    const string Root="E:/tmp/vrc-diffusion/sd-extensions";
    static float[] Read(RenderTexture rt){var r=AsyncGPUReadback.Request(rt,0,TextureFormat.RGBAFloat);r.WaitForCompletion();if(r.hasError)throw new Exception("Readback failed");return r.GetData<float>().ToArray();}
    static void Report(string file,object o){Directory.CreateDirectory(Root);File.WriteAllText(Root+"/"+file,JsonConvert.SerializeObject(o,Formatting.Indented));}
    static Texture2D Input(string file,int width,int height){byte[] raw=File.ReadAllBytes(Root+"/reference/"+file),pad=new byte[width*height*16];Buffer.BlockCopy(raw,0,pad,0,raw.Length);var t=new Texture2D(width,height,TextureFormat.RGBAFloat,false,true);t.LoadRawTextureData(pad);t.filterMode=FilterMode.Point;t.Apply();return t;}
    public static object Tokenizer(){
        var tokenizer=Object.FindObjectOfType<SDTokenizer>();var fixtures=JToken.Parse(File.ReadAllText(Root+"/tokenizer-fixtures.json"));
        var tests=fixtures is JArray?(JArray)fixtures:(JArray)(fixtures["fixtures"]??fixtures["cases"]);if(tests==null)throw new Exception("Fixture schema: "+fixtures.ToString().Substring(0,120));
        var rows=new List<object>();foreach(var f in tests){string text=(string)(f["input"]??f["text"]??f["prompt"]);int[] expected=(f["inputIds"]??f["tokenIds"]).ToObject<int[]>();
            if(!tokenizer.Encode(text))throw new Exception("Tokenizer initialization failed");bool equal=tokenizer.tokenIds.SequenceEqual(expected);rows.Add(new{text,equal,actual=tokenizer.tokenIds.ToArray(),expected,content=tokenizer.contentTokens});}
        bool passed=rows.All(r=>(bool)r.GetType().GetProperty("equal").GetValue(r));Report("tokenizer.json",new{passed,rows});return new{passed,cases=rows.Count,failures=rows.Where(r=>!(bool)r.GetType().GetProperty("equal").GetValue(r)).Select(r=>r.GetType().GetProperty("text").GetValue(r)).ToArray()};
    }
    public static object TokenizerPlay(){
        if(!EditorApplication.isPlaying)throw new Exception("PlayMode required");var b=UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<SDTokenizer>());var fixtures=(JArray)JToken.Parse(File.ReadAllText(Root+"/tokenizer-fixtures.json"))["fixtures"];var rows=new List<object>();bool passed=true;
        foreach(var f in fixtures){string text=(string)f["text"];int[] expected=f["inputIds"].ToObject<int[]>();b.SetProgramVariable("input",text);b.SendCustomEvent("EncodeInput");var actual=(int[])b.GetProgramVariable("tokenIds");bool equal=actual!=null&&actual.SequenceEqual(expected);passed&=equal;rows.Add(new{id=(string)f["id"],equal,actual=actual==null?null:actual.ToArray(),expected});}
        Report("tokenizer-udon.json",new{passed,rows});if(!passed)throw new Exception("Actual Udon tokenizer differs from HF fixtures");return new{passed,cases=rows.Count};
    }
    public static async Task<object> Graph(string part="clip"){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");var c=Object.FindObjectOfType<SDController>();var e=c.executor;var g=part=="clip"?c.inputs.textGraph:c.inputs.encoderGraphs[0];
        var input=part=="clip"?Input("clip-token-ids.bytes",128,1):Input("encoder-input.bytes",1024,64);e.graph=g;e.latent=input;e.Begin();var rows=new List<object>();int last=-1,draws=0;var watch=System.Diagnostics.Stopwatch.StartNew();
        try{while(!e.finished){e.Compute();draws++;
            if(e.lastCompletedNode!=last&&e.lastCompletedNode>=0){last=e.lastCompletedNode;string path=Root+"/reference/"+part+"-node-"+last+".bytes";
                if(File.Exists(path)){byte[] raw=File.ReadAllBytes(path);float[] expected=new float[raw.Length/4];Buffer.BlockCopy(raw,0,expected,0,raw.Length);float[] actual=Read(e.lastMemory);double error=0,norm=0;float max=0;int bad=0;
                    for(int i=0;i<expected.Length;i++){float d=actual[i]-expected[i];error+=d*d;norm+=expected[i]*expected[i];max=Mathf.Max(max,Mathf.Abs(d));if(float.IsNaN(actual[i])||float.IsInfinity(actual[i]))bad++;}
                    var row=new{node=last,name=g.nodeNames[last],rmse=Math.Sqrt(error/expected.Length),relativeRMSE=Math.Sqrt(error/Math.Max(norm,1e-20)),maxAbs=max,nonFinite=bad};rows.Add(row);if(bad>0)throw new Exception("Non-finite "+part+" node "+last);if(last==g.outputNode&&row.relativeRMSE>.06)throw new Exception("Final Shader/reference error >6%: "+row.relativeRMSE);
                }
                Report(part+"-graph.json",new{status=e.finished?"complete":"running",nodes=e.node,draws,seconds=watch.Elapsed.TotalSeconds,rows});
            }
            if(draws%12==0)await Task.Delay(1);
        }}catch(Exception ex){Report(part+"-graph.json",new{status="failed",node=e.node,draws,rows,error=ex.ToString()});throw;}
        finally{Object.DestroyImmediate(input);}
        return new{part,nodes=e.node,draws,seconds=watch.Elapsed.TotalSeconds,rows};
    }
    public static object PlayState(){
        if(!EditorApplication.isPlaying)throw new Exception("PlayMode required");var c=UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<SDController>());var m=UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<DiffusionController>());var p=UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<SDPerformance>());
        var result=new{sdAuthority=c.GetProgramVariable("authorityId"),mnistAuthority=m.GetProgramVariable("authorityId"),phase=c.GetProgramVariable("phase"),step=c.GetProgramVariable("localStep"),stage=c.GetProgramVariable("algorithmStage"),progress=p.GetProgramVariable("progress"),activeSeconds=c.GetProgramVariable("activeSeconds"),status=((UnityEngine.UI.Text)p.GetProgramVariable("settingsText")).text};Report("play-state.json",result);return result;
    }
}
