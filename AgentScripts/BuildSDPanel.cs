using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using UdonSharpEditor;
using Object=UnityEngine.Object;
public static class BuildSDPanel {
    const int Layer=17;
    static Transform canvas;
    static Font font;
    static SDController controller;
    static GameObject Rect(string name,Transform parent,float x,float y,float w,float h,params Type[] extra){
        var g=new GameObject(name,new[]{typeof(RectTransform),typeof(CanvasRenderer)}.Concat(extra).ToArray());g.transform.SetParent(parent,false);g.layer=Layer;
        var r=g.GetComponent<RectTransform>();r.anchoredPosition=new Vector2(x,y);r.sizeDelta=new Vector2(w,h);return g;
    }
    static Text Label(string name,string value,float x,float y,float w,float h,int size=24,Transform parent=null){
        var g=Rect(name,parent??canvas,x,y,w,h,typeof(Text));var t=g.GetComponent<Text>();t.font=font;t.text=value;t.fontSize=size;t.color=new Color(.86f,.94f,1);t.alignment=TextAnchor.MiddleCenter;t.raycastTarget=false;return t;
    }
    static void Button(string text,string command,int value,float x,float y,float w=200){
        var g=Rect(command+value,canvas,x,y,w,52,typeof(Image),typeof(UnityEngine.UI.Button));var im=g.GetComponent<Image>();im.color=new Color(.07f,.19f,.26f);
        var ui=g.GetComponent<UnityEngine.UI.Button>();ui.targetGraphic=im;ui.navigation=new Navigation{mode=Navigation.Mode.None};
        var b=g.AddUdonSharpComponent<SDButton>();b.controller=controller;b.command=command;b.value=value;UdonSharpEditorUtility.CopyProxyToUdon(b);
        UnityEventTools.AddStringPersistentListener(ui.onClick,UdonSharpEditorUtility.GetBackingUdonBehaviour(b).SendCustomEvent,"Press");
        Label("Label",text,0,0,w-10,44,20,g.transform);
    }
    static RawImage Picture(string name,Texture tex,float x,float y,float size){
        var g=Rect(name,canvas,x,y,size,size,typeof(RawImage));var im=g.GetComponent<RawImage>();im.texture=tex;im.raycastTarget=false;return im;
    }
    public static object Apply(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode only");var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();if(scene.isDirty){string backup=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"tmp/vrc-diffusion/sd-before-panel.unity");EditorSceneManager.SaveScene(scene,backup,true);}
        controller=Object.FindObjectOfType<SDController>();if(controller==null)throw new Exception("Build graphs first");
        var world=scene.GetRootGameObjects().Single(g=>g.name=="DiffusionLabWorld");controller.transform.SetParent(world.transform,true);
        var old=world.transform.Find("SDAnimeBoard");if(old!=null)throw new Exception("SD panel already exists; update existing assets");
        var board=new GameObject("SDAnimeBoard");board.transform.SetParent(world.transform,false);
        var backing=GameObject.CreatePrimitive(PrimitiveType.Quad);backing.name="SDBackboard";backing.transform.SetParent(board.transform,false);backing.transform.position=new Vector3(-7,2.8f,3.15f);backing.transform.localScale=new Vector3(5.5f,5.5f,1);backing.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Panel.mat");Object.DestroyImmediate(backing.GetComponent<Collider>());
        var cg=new GameObject("SDInterface",typeof(RectTransform),typeof(Canvas),typeof(GraphicRaycaster),typeof(VRCUiShape),typeof(BoxCollider));cg.transform.SetParent(board.transform,false);cg.layer=Layer;cg.transform.position=new Vector3(-7,2.8f,3.14f);cg.transform.localScale=Vector3.one*.0033f;
        cg.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;cg.GetComponent<RectTransform>().sizeDelta=new Vector2(1600,1640);cg.GetComponent<BoxCollider>().size=new Vector3(1600,1640,1);canvas=cg.transform;
        font=AssetDatabase.LoadAssetAtPath<Font>("Assets/DiffusionLab/Localization/FontSources/IBMPlexMono-Regular.ttf");
        Label("Title","SD1.x ANIME · COUNTERFEIT-V3.0",0,765,1500,70,35);
        controller.presetText=Label("Preset","PRESET: Anime portrait",0,690,1500,60,27);
        controller.finalView=Picture("FinalImage",controller.images[0],-395,335,620);Picture("RealMemory",controller.debugMemory,355,370,430);
        Label("MemoryHeader","REAL SHADER MEMORY · 16 CHANNELS",355,622,740,45,21);
        controller.memoryText=Label("MemoryText","NHWC4 tensor · live data",355,70,760,190,19);
        controller.statusText=Label("Status","Ready",-390,-78,740,155,21);
        controller.algorithmText=Label("Algorithm","REAL COMPUTATION",365,-170,730,270,19);
        for(int i=0;i<8;i++)Picture("History"+i,controller.history[i],-690+i*197,-376,175);
        Label("HistoryExplanation","NOISE → LINEAR LATENT PREVIEW → REAL VAE OUTPUT",0,-481,1500,40,20);
        for(int i=0;i<4;i++)Button(new[]{"PORTRAIT","COAST","CAFE","CAT"}[i],"SetPreset",i,-595+i*397,-551,375);
        Button("GENERATE","StartGeneration",0,-560,-619,350);Button("PAUSE / RUN","PauseResume",0,-175,-619,350);Button("DETAILS ON / OFF","ToggleDetails",0,215,-619,350);Button("REQUEST CONTROL","RequestControl",0,600,-619,350);
        for(int i=0;i<3;i++)Button(new[]{"256 NATIVE","384 NATIVE","512 NATIVE"}[i],"SetResolution",i,-605+i*238,-686,220);
        Button("10 STEPS","SetSteps",10,135,-686,210);Button("20 STEPS","SetSteps",20,365,-686,210);Button("30 STEPS","SetSteps",30,595,-686,210);
        var input=Rect("SeedInput",canvas,-565,-752,370,55,typeof(Image),typeof(InputField));input.GetComponent<Image>().color=new Color(.05f,.12f,.18f);
        var text=Label("SeedText","1234",0,0,344,49,24,input.transform);text.raycastTarget=true;
        var field=input.GetComponent<InputField>();field.textComponent=text;field.targetGraphic=input.GetComponent<Image>();field.contentType=InputField.ContentType.IntegerNumber;field.characterLimit=11;field.text="1234";field.navigation=new Navigation{mode=Navigation.Mode.None};controller.seedInput=field;
        Button("APPLY SEED","SetSeed",0,-190,-752,340);Button("SEED +1","NextSeed",0,170,-752,340);Button("LOCAL SPEED 16","SetSpeed",16,560,-752,380);
        UdonSharpEditorUtility.CopyProxyToUdon(controller);EditorUtility.SetDirty(controller);EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(controller));
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);return new{panel="SDAnimeBoard",position=new Vector3(-7,2.8f,3.14f),rayUI=true,backboardGap=.01};
    }
}
