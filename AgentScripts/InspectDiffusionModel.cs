using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
public static class InspectDiffusionModel {
    static float[] Read(RenderTexture rt){
        var prev=RenderTexture.active;var tex=new Texture2D(rt.width,rt.height,TextureFormat.RFloat,false,true);
        try{RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false);tex.Apply();return tex.GetRawTextureData<float>().ToArray();}
        finally{RenderTexture.active=prev;Object.DestroyImmediate(tex);}
    }
 public static object Rebind(){
  var c=Object.FindObjectOfType<DiffusionController>();var m=c.models[1].layers[2];var e=JObject.Parse(File.ReadAllText("Assets/DiffusionLab/Data/Tea/model.json"))["layers"][2];
  int old=m.GetInt("_Op"),json=(int)e["op"];m.SetInt("_Op",json);int set=m.GetInt("_Op");EditorUtility.SetDirty(m);AssetDatabase.SaveAssets();int saved=m.GetInt("_Op");
  return new{old,json,set,saved,result=Node2()};
 }
 public static object Node2(){
  var c=Object.FindObjectOfType<DiffusionController>();var model=c.models[1];
  for(int j=0;j<=2;j++){if(j==0)model.layers[j].SetTexture("_Input",model.validationInput);model.layers[j].SetInt("_Class",3);model.layers[j].SetInt("_Timestep",128);Graphics.Blit(null,model.memory[j],model.layers[j]);}
  var raw=Read(model.memory[2]);Directory.CreateDirectory("Reports/DiffusionLab");byte[] data=new byte[raw.Length*4];Buffer.BlockCopy(raw,0,data,0,data.Length);File.WriteAllBytes("Reports/DiffusionLab/node2.bytes",data);
  var m=model.layers[2];return new{op=m.GetInt("_Op"),size=m.GetInt("_OutSize"),scale=m.GetFloat("_ResidualScale"),input=m.GetTexture("_Input").name,skip=m.GetTexture("_Skip").name,range=new[]{raw.Min(),raw.Max()},raw=raw.Take(10).ToArray(),range0=Read(model.memory[0]).Take(10).ToArray(),range1=Read(model.memory[1]).Take(10).ToArray()};
 }
 public static object First(){
  var c=Object.FindObjectOfType<DiffusionController>();var m=c.models[1];var layer=m.layers[0];var json=JObject.Parse(File.ReadAllText("Assets/DiffusionLab/Data/Tea/model.json"));
  var before=layer.GetTexture("_Input");layer.SetTexture("_Input",m.validationInput);layer.SetInt("_Class",3);layer.SetInt("_Timestep",128);Graphics.Blit(null,m.memory[0],layer);
  var prev=RenderTexture.active;var tex=new Texture2D(m.memory[0].width,m.memory[0].height,TextureFormat.RFloat,false,true);RenderTexture.active=m.memory[0];tex.ReadPixels(new Rect(0,0,tex.width,tex.height),0,0,false);tex.Apply();var raw=tex.GetRawTextureData<float>().ToArray();RenderTexture.active=prev;Object.DestroyImmediate(tex);layer.SetTexture("_Input",before);
  Directory.CreateDirectory("Reports/DiffusionLab");byte[] data=new byte[raw.Length*4];Buffer.BlockCopy(raw,0,data,0,data.Length);File.WriteAllBytes("Reports/DiffusionLab/first-node.bytes",data);
  var keys=new[]{"_Op","_InSize","_InChannels","_PrimaryChannels","_OutSize","_OutChannels","_Kernel","_Stride","_Padding","_WeightOffset","_BiasOffset","_ConditionOffset","_ScaleOffset","_Activation","_WeightWidth"};
  return new{shader=layer.shader.name,supported=layer.shader.isSupported,errors=ShaderUtil.GetShaderMessages(layer.shader).Select(e=>new{e.message,e.line,e.severity}).ToArray(),values=keys.ToDictionary(k=>k,k=>layer.GetInt(k)),expected=json["layers"][0],input=m.validationInput.width,range=new[]{raw.Min(),raw.Max()},first=raw.Take(10).ToArray(),weights=layer.GetTexture("_Weights").width};
 }
}
