using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using Object=UnityEngine.Object;

public static class BuildDiffusionTeaching {
    const string Root="Assets/DiffusionLab";
    const int Layer=17;
    static Transform canvas;
    static Font font;
    static DiffusionTeaching teaching;
    static GameObject Rect(string name,Transform parent,float x,float y,float w,float h,params Type[] extra){
        var g=new GameObject(name,new[]{typeof(RectTransform),typeof(CanvasRenderer)}.Concat(extra).ToArray());g.transform.SetParent(parent,false);g.layer=Layer;
        var r=g.GetComponent<RectTransform>();r.anchoredPosition=new Vector2(x,y);r.sizeDelta=new Vector2(w,h);return g;
    }
    static Text Text(string name,string value,Transform parent,float x,float y,float w,float h,int size){
        var g=Rect(name,parent,x,y,w,h,typeof(Text));var t=g.GetComponent<Text>();t.font=font;t.text=value;t.fontSize=size;t.color=new Color(.86f,.94f,1);t.alignment=TextAnchor.MiddleCenter;t.raycastTarget=false;return t;
    }
    static Material Display(string name,Texture texture,int channels=1){
        string p=Root+"/Materials/Teaching/"+name+".mat";var m=AssetDatabase.LoadAssetAtPath<Material>(p);
        if(m==null){m=new Material(Shader.Find("DiffusionLab/MemoryDisplay"));AssetDatabase.CreateAsset(m,p);}
        m.SetTexture("_MainTex",texture);m.SetFloat("_Channels",channels);m.SetFloat("_Range",3);m.SetFloat("_Signed",1);EditorUtility.SetDirty(m);return m;
    }
    static void Picture(string name,Transform parent,Material m,float x,float y,float size){
        var g=GameObject.CreatePrimitive(PrimitiveType.Quad);g.name=name;g.transform.SetParent(parent,false);
        g.transform.localPosition=new Vector3(x,y,-.5f);g.transform.localScale=new Vector3(size,size,1);g.GetComponent<Renderer>().sharedMaterial=m;Object.DestroyImmediate(g.GetComponent<Collider>());
    }
    static void Button(string label,string command,float x,float y,float w){
        var g=Rect(command,canvas,x,y,w,48,typeof(Image),typeof(UnityEngine.UI.Button));var im=g.GetComponent<Image>();im.color=new Color(.07f,.19f,.26f);
        var ui=g.GetComponent<UnityEngine.UI.Button>();ui.targetGraphic=im;ui.navigation=new Navigation{mode=Navigation.Mode.None};
        var b=g.AddUdonSharpComponent<DiffusionButton>();b.teaching=teaching;b.command=command;UdonSharpEditorUtility.CopyProxyToUdon(b);
        UnityEventTools.AddStringPersistentListener(ui.onClick,UdonSharpEditorUtility.GetBackingUdonBehaviour(b).SendCustomEvent,"Press");
        Text("Label",label,g.transform,0,0,w-10,42,20);
    }
    static void Folder(string p){if(AssetDatabase.IsValidFolder(p))return;int i=p.LastIndexOf('/');Folder(p.Substring(0,i));AssetDatabase.CreateFolder(p.Substring(0,i),p.Substring(i+1));}
    static Vector2 Position(int i){int row=i/7,col=i%7;if(row%2==1)col=6-col;return new Vector2(-645+col*215,175-row*115);}
    static Image Edge(Transform root,int from,int to,bool skip){
        if(from<0)return null;
        Vector2 a=Position(from),b=Position(to);var g=Rect("Edge "+from+"-"+to,root,(a.x+b.x)/2,(a.y+b.y)/2,(b-a).magnitude,skip?3:2,typeof(Image));
        var im=g.GetComponent<Image>();im.color=skip?new Color(.95f,.66f,.16f,.7f):new Color(.25f,.6f,.7f,.65f);im.raycastTarget=false;
        g.transform.localRotation=Quaternion.Euler(0,0,Mathf.Atan2(b.y-a.y,b.x-a.x)*Mathf.Rad2Deg);return im;
    }
    public static object Apply(){
        if(EditorApplication.isPlaying)throw new Exception("Stop PlayMode first");var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();if(scene.isDirty)throw new Exception("Save own scene changes first");
        var c=Object.FindObjectOfType<DiffusionController>();if(c.models==null||c.models.Length<2)throw new Exception("Import models first");
        var world=scene.GetRootGameObjects().Single(g=>g.name=="DiffusionLabWorld");
        var old=world.transform.Find("TeachingBoard");if(old!=null)Object.DestroyImmediate(old.gameObject);
        Folder(Root+"/Materials/Teaching");Folder(Root+"/Textures/Teaching");
        var board=new GameObject("TeachingBoard");board.transform.SetParent(world.transform,false);
        var backing=GameObject.CreatePrimitive(PrimitiveType.Quad);backing.name="TeachingBackboard";backing.transform.SetParent(board.transform,false);backing.transform.position=new Vector3(7.0f,2.8f,3.15f);backing.transform.localScale=new Vector3(5.4f,5.5f,1);backing.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>(Root+"/Materials/Panel.mat");Object.DestroyImmediate(backing.GetComponent<Collider>());
        var cg=new GameObject("TeachingInterface",typeof(RectTransform),typeof(Canvas),typeof(GraphicRaycaster),typeof(VRCUiShape),typeof(BoxCollider));cg.transform.SetParent(board.transform,false);cg.layer=Layer;cg.transform.position=new Vector3(7,2.8f,3.14f);cg.transform.localScale=Vector3.one*.0033f;
        cg.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;cg.GetComponent<RectTransform>().sizeDelta=new Vector2(1600,1640);cg.GetComponent<BoxCollider>().size=new Vector3(1600,1640,1);canvas=cg.transform;
        font=AssetDatabase.LoadAssetAtPath<Font>(Root+"/Localization/FontSources/IBMPlexMono-Regular.ttf");
        teaching=board.AddUdonSharpComponent<DiffusionTeaching>();teaching.controller=c;teaching.localization=world.GetComponent<DiffusionLocalization>();c.teaching=teaching;
        var pages=JArray.Parse(File.ReadAllText(Root+"/Localization/teaching.json"));teaching.pageTitles=pages.Select(v=>(string)v["title"][0]).ToArray();teaching.pageBodies=pages.Select(v=>(string)v["body"][0]).ToArray();
        Text("TeachingHeader","INSIDE DIFFUSION · LIVE DATA",canvas,0,770,1500,70,38);
        teaching.titleText=Text("PageTitle",teaching.pageTitles[0],canvas,0,680,1150,54,30);
        Button("<","PreviousPage",-715,680,100);Button(">","NextPage",715,680,100);
        teaching.bodyText=Text("PageBody",teaching.pageBodies[0],canvas,0,513,1480,250,23);teaching.bodyText.alignment=TextAnchor.MiddleLeft;
        Text("GraphLegend","LIVE OPERATOR GRAPH · cyan: active · gold: skip · signed range ±3",canvas,0,346,1490,45,20);
        teaching.graphRoots=new GameObject[c.models.Length];teaching.graphOffsets=new int[c.models.Length];var highlights=new List<Image>();var primaries=new List<Image>();var skips=new List<Image>();
        for(int mi=0;mi<c.models.Length;mi++){
            var model=c.models[mi];var graph=Rect("Graph"+mi,canvas,0,0,1500,460);teaching.graphRoots[mi]=graph;teaching.graphOffsets[mi]=highlights.Count;
            for(int i=0;i<model.memory.Length;i++){primaries.Add(Edge(graph.transform,model.primaryLayers[i],i,false));skips.Add(Edge(graph.transform,model.skipLayers[i],i,true));}
            for(int i=0;i<model.memory.Length;i++){
                Vector2 p=Position(i);var card=Rect("Node"+i,graph.transform,p.x,p.y,195,100,typeof(Image));var im=card.GetComponent<Image>();im.color=new Color(.08f,.18f,.24f);im.raycastTarget=false;highlights.Add(im);
                Text("Name",i+" "+model.operatorNames[i],card.transform,0,34,186,30,13);
                var display=Display("Model"+mi+"Node"+i,model.memory[i],model.channels[i]);Picture("Tensor",card.transform,display,-57,-14,60);
                Text("Shape",model.memory[i].height+"×"+model.memory[i].height+"\n"+model.channels[i]+" channels",card.transform,36,-13,110,63,17);
            }
            graph.SetActive(mi==c.modelIndex);
        }
        teaching.highlights=highlights.ToArray();teaching.primaryEdges=primaries.ToArray();teaching.skipEdges=skips.ToArray();
        teaching.tensorText=Text("TensorStatus","ACTIVE OPERATOR",canvas,0,-275,1490,70,21);
        var specs=new[]{"CONDITIONAL ε_c","NULL ε_null","GUIDED ε","CLEAN ESTIMATE x_0","NEXT STATE x_next"};
        teaching.algorithmHighlights=new Image[5];
        var mats=new Material[5];for(int i=0;i<5;i++){
            var stage=Rect("Stage"+i,canvas,-600+i*300,-493,290,45,typeof(Image));stage.GetComponent<Image>().raycastTarget=false;teaching.algorithmHighlights[i]=stage.GetComponent<Image>();
            mats[i]=Display("Detail"+i,c.stateA);Picture(specs[i],canvas,mats[i],-600+i*300,-403,130);Text("DetailLabel"+i,specs[i],canvas,-600+i*300,-482,290,35,17);Text("StageLabel"+i,new[]{"1 CLASS","2 NULL","3 CFG","4 x_0","5 DDIM"}[i],canvas,-600+i*300,-512,290,25,16);}
        teaching.conditionalDisplay=mats[0];teaching.nullDisplay=mats[1];teaching.guidedDisplay=mats[2];teaching.cleanDisplay=mats[3];teaching.nextDisplay=mats[4];
        for(int i=3;i<5;i++){mats[i].SetFloat("_Signed",0);mats[i].SetFloat("_ValueMin",0);mats[i].SetFloat("_ValueMax",1);EditorUtility.SetDirty(mats[i]);}
        teaching.algorithmText=Text("Algorithm","DDIM η=0",canvas,-365,-602,720,145,20);
        teaching.probeText=Text("ProbeValues","PIXEL PROBE",canvas,385,-602,720,145,18);
        string pp=Root+"/Textures/Teaching/Probe.renderTexture";teaching.probeTexture=AssetDatabase.LoadAssetAtPath<RenderTexture>(pp);if(teaching.probeTexture==null){teaching.probeTexture=new RenderTexture(7,1,0,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear);AssetDatabase.CreateAsset(teaching.probeTexture,pp);}teaching.probeTexture.filterMode=FilterMode.Point;
        pp=Root+"/Materials/Teaching/Probe.mat";teaching.probeMaterial=AssetDatabase.LoadAssetAtPath<Material>(pp);if(teaching.probeMaterial==null){teaching.probeMaterial=new Material(Shader.Find("DiffusionLab/Probe"));AssetDatabase.CreateAsset(teaching.probeMaterial,pp);}
        Button("PROBE X −","ProbeLeft",-605,-733,255);Button("PROBE X +","ProbeRight",-305,-733,255);Button("PROBE Y −","ProbeDown",-5,-733,255);Button("PROBE Y +","ProbeUp",295,-733,255);Button("CHANNEL +","ProbeChannel",605,-733,255);
        UdonSharpEditorUtility.CopyProxyToUdon(teaching);UdonSharpEditorUtility.CopyProxyToUdon(c);EditorUtility.SetDirty(c);EditorUtility.SetDirty(teaching);
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        return new{pages=pages.Count,graphs=c.models.Length,nodes=highlights.Count,probe="7 real GPU values",boardPlaneGap=.01};
    }
}
