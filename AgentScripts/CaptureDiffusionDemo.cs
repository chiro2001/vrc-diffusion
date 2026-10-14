using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UdonSharpEditor;
using VRC.Udon;
using Object=UnityEngine.Object;
public static class CaptureDiffusionDemo {
 static int Int(UdonBehaviour b,string n){return (int)b.GetProgramVariable(n);}
 static async Task Until(Func<bool> ready){var t=DateTime.UtcNow;while(!ready()){if((DateTime.UtcNow-t).TotalSeconds>15)throw new Exception("Denoise step capture timed out");await Task.Delay(20);}}
 static void Save(RenderTexture rt,int step){
  var active=RenderTexture.active;var tex=new Texture2D(rt.width,rt.height,TextureFormat.RFloat,false,true);
  try{RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false);tex.Apply();var raw=tex.GetRawTextureData<float>().ToArray();byte[] bytes=new byte[raw.Length*4];Buffer.BlockCopy(raw,0,bytes,0,bytes.Length);File.WriteAllBytes("Reports/DiffusionLab/PlayTrajectory/step-"+step.ToString("D2")+".bytes",bytes);}
  finally{RenderTexture.active=active;Object.DestroyImmediate(tex);}
 }
 public static async Task<object> Run(){
  if(!EditorApplication.isPlaying)throw new Exception("Use PlayMode");Directory.CreateDirectory("Reports/DiffusionLab/PlayTrajectory");
  var proxy=Object.FindObjectOfType<DiffusionController>();var b=UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
  var loc=UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<DiffusionLocalization>());loc.SetProgramVariable("languageOverride",1);loc.SendCustomEvent("SetLanguage");
  b.SendCustomEvent("RequestControl");b.SetProgramVariable("validationRunOnStart",false);b.SetProgramVariable("passesPerFrame",8);b.SetProgramVariable("commandValue",1);b.SendCustomEvent("SelectModel");b.SetProgramVariable("commandValue",3);b.SendCustomEvent("SelectDigit");
  var input=(UnityEngine.UI.InputField)b.GetProgramVariable("seedInput");input.text="1234";b.SendCustomEvent("ApplySeed");b.SendCustomEvent("PauseResume");Save((RenderTexture)b.GetProgramVariable("stateA"),0);
  for(int step=1;step<=64;step++){b.SendCustomEvent("StepDenoise");int target=step;await Until(()=>Int(b,"localStep")==target);Save((RenderTexture)b.GetProgramVariable("displayedState"),step);}
  await Until(()=>(bool)UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<DiffusionTeaching>()).GetProgramVariable("probeReady"));
  var result=new{passed=true,model=Int(b,"modelIndex"),digit=Int(b,"digit"),seed=Int(b,"seed"),steps=64,source="PlayMode Udon/VRCGraphics actual map hash noise",frames=65};File.WriteAllText("Reports/DiffusionLab/play-trajectory.json",JsonConvert.SerializeObject(result,Formatting.Indented));return result;
 }
}
