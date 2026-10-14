using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UdonSharpEditor;
using Object=UnityEngine.Object;
public static class FinalizeSDAssets {
    static void Save(UdonSharp.UdonSharpBehaviour b){UdonSharpEditorUtility.CopyProxyToUdon(b);EditorUtility.SetDirty(b);EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(b));}
    static void Button(Transform canvas,SDController controller,string name,string label,string command,int value,float x,float w){
        var existing=canvas.Find(name);if(existing!=null)return;
        var g=new GameObject(name,typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(UnityEngine.UI.Button));g.transform.SetParent(canvas,false);g.layer=17;var r=g.GetComponent<RectTransform>();r.anchoredPosition=new Vector2(x,-815);r.sizeDelta=new Vector2(w,48);
        var im=g.GetComponent<Image>();im.color=new Color(.07f,.19f,.26f);var ui=g.GetComponent<UnityEngine.UI.Button>();ui.targetGraphic=im;ui.navigation=new Navigation{mode=Navigation.Mode.None};
        var b=g.AddUdonSharpComponent<SDButton>();b.controller=controller;b.command=command;b.value=value;Save(b);UnityEventTools.AddStringPersistentListener(ui.onClick,UdonSharpEditorUtility.GetBackingUdonBehaviour(b).SendCustomEvent,"Press");
        var tgo=new GameObject("Label",typeof(RectTransform),typeof(CanvasRenderer),typeof(Text));tgo.transform.SetParent(g.transform,false);tgo.layer=17;var tr=tgo.GetComponent<RectTransform>();tr.sizeDelta=new Vector2(w-8,44);
        var t=tgo.GetComponent<Text>();t.font=AssetDatabase.LoadAssetAtPath<Font>("Assets/DiffusionLab/Localization/FontSources/IBMPlexMono-Regular.ttf");t.fontSize=18;t.text=label;t.color=new Color(.86f,.94f,1);t.alignment=TextAnchor.MiddleCenter;t.raycastTarget=false;
    }
    public static object Apply(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");var c=Object.FindObjectOfType<SDController>();
        if(c.executor.gameObject==c.gameObject){
            var old=c.executor;var go=new GameObject("SDGraphExecutor");go.transform.SetParent(c.transform,false);c.executor=go.AddUdonSharpComponent<SDGraphExecutor>();Save(c.executor);Save(c);
            var backing=UdonSharpEditorUtility.GetBackingUdonBehaviour(old);if(backing!=null)Object.DestroyImmediate(backing);if(old!=null)Object.DestroyImmediate(old);
        }
        c.executor.graph=null;c.executor.latent=null;c.executor.condition=null;c.executor.timeEmbedding=null;c.executor.finished=true;c.executor.lastCompletedNode=-1;Save(c.executor);
        c.phase=0;c.localEpoch=-1;c.algorithmStage=0;c.localInference=false;c.autoStartForValidation=false;c.passesPerFrame=4;c.frameBudgetMs=2;
        foreach(var g in c.unetGraphs.Concat(c.vaeGraphs)){
            foreach(var m in g.operators){foreach(string key in new[]{"_A","_B","_C","_Packed","_Partial","_Stats","_Tile","_Previous","_Scores","_OldStats"})m.SetTexture(key,null);EditorUtility.SetDirty(m);}g.ReleaseMemory();
        }
        foreach(string key in new[]{"_State","_Conditional","_Unconditional","_RGB"})c.sampler.SetTexture(key,null);EditorUtility.SetDirty(c.sampler);
        c.finalView.texture=c.images[c.resolution];Save(c);
        var canvas=GameObject.Find("SDInterface").transform;var cr=canvas.GetComponent<RectTransform>();cr.sizeDelta=new Vector2(1600,1740);canvas.GetComponent<BoxCollider>().size=new Vector3(1600,1740,1);
        var back=GameObject.Find("SDBackboard");back.transform.localScale=new Vector3(5.5f,5.8f,1);
        Button(canvas,c,"LocalInference","LOCAL INFERENCE ON / OFF","ToggleInference",0,-390,720);Button(canvas,c,"LocalSlow","LOCAL SPEED 2","SetSpeed",2,200,390);Button(canvas,c,"LocalFast","LOCAL SPEED 32","SetSpeed",32,610,390);
        var previewButton=canvas.Find("SetSpeed16")??canvas.Find("VAEPreviews");
        if(previewButton!=null){previewButton.name="VAEPreviews";var b=previewButton.GetComponent<SDButton>();b.command="TogglePreviews";Save(b);previewButton.GetComponentInChildren<Text>().text="VAE PREVIEWS ON / OFF";}
        AssetDatabase.SaveAssets();var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);return new{separateManualController=true,localInferenceOptIn=true};
    }
}
