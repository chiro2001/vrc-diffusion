using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UdonSharpEditor;
using Object=UnityEngine.Object;

public static class BuildSDUserExperience {
    static Font font;static Transform canvas;static SDController c;
    const string Root="Assets/DiffusionLab/SD/Shared/";
    static void Save(UdonSharp.UdonSharpBehaviour b){UdonSharpEditorUtility.CopyProxyToUdon(b);EditorUtility.SetDirty(b);EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(b));}
    static GameObject Rect(string name,Transform parent,float x,float y,float w,float h,params Type[] types){
        var g=new GameObject(name,new[]{typeof(RectTransform),typeof(CanvasRenderer)}.Concat(types).ToArray());g.transform.SetParent(parent,false);g.layer=17;
        var r=g.GetComponent<RectTransform>();r.anchoredPosition=new Vector2(x,y);r.sizeDelta=new Vector2(w,h);return g;
    }
    static Text Text(string name,string value,float x,float y,float w,float h,int size=22,Transform parent=null){
        var t=Rect(name,parent??canvas,x,y,w,h,typeof(Text)).GetComponent<Text>();t.font=font;t.text=value;t.fontSize=size;t.color=new Color(.84f,.94f,1);t.alignment=TextAnchor.MiddleLeft;t.raycastTarget=false;return t;
    }
    static void Button(string label,string command,int value,float x,float y,float width=420){
        var g=Rect(command+value,canvas,x,y,width,52,typeof(Image),typeof(Button),typeof(CanvasGroup));g.GetComponent<Image>().color=new Color(.07f,.19f,.26f);
        var ui=g.GetComponent<Button>();ui.targetGraphic=g.GetComponent<Image>();ui.navigation=new Navigation{mode=Navigation.Mode.None};
        var b=g.AddUdonSharpComponent<SDButton>();b.controller=c;b.command=command;b.value=value;b.ui=ui;b.appearance=g.GetComponent<CanvasGroup>();Save(b);
        UnityEventTools.AddStringPersistentListener(ui.onClick,UdonSharpEditorUtility.GetBackingUdonBehaviour(b).SendCustomEvent,"Press");
        Text("Label",label,0,0,width-16,48,20,g.transform).alignment=TextAnchor.MiddleCenter;
    }
    static InputField Input(string name,float y,string value){
        var g=Rect(name,canvas,0,y,1420,125,typeof(Image),typeof(InputField));g.GetComponent<Image>().color=new Color(.035f,.09f,.13f);
        var t=Text("InputText",value,0,0,1380,112,22,g.transform);t.raycastTarget=true;t.supportRichText=false;t.alignment=TextAnchor.UpperLeft;
        var input=g.GetComponent<InputField>();input.targetGraphic=g.GetComponent<Image>();input.textComponent=t;input.text=value;input.lineType=InputField.LineType.MultiLineNewline;input.characterLimit=512;input.navigation=new Navigation{mode=Navigation.Mode.None};return input;
    }
    public static object Apply(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");c=Object.FindObjectOfType<SDController>();if(c.inputs==null)throw new Exception("Build extensions first");
        font=AssetDatabase.LoadAssetAtPath<Font>("Assets/DiffusionLab/Localization/FontSources/IBMPlexMono-Regular.ttf");var world=GameObject.Find("DiffusionLabWorld").transform;
        var existing=world.Find("SDInputsBoard");if(existing!=null)throw new Exception("Inputs board exists; update it rather than replacing user edits");
        var board=new GameObject("SDInputsBoard");board.transform.SetParent(world,false);
        var back=GameObject.CreatePrimitive(PrimitiveType.Quad);back.name="SDInputsBackboard";back.transform.SetParent(board.transform,false);back.transform.position=new Vector3(-12.65f,2.8f,3.15f);back.transform.localScale=new Vector3(5.15f,5.8f,1);back.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Panel.mat");Object.DestroyImmediate(back.GetComponent<Collider>());
        var cg=new GameObject("SDInputsInterface",typeof(RectTransform),typeof(Canvas),typeof(GraphicRaycaster),typeof(VRC.SDK3.Components.VRCUiShape),typeof(BoxCollider));cg.transform.SetParent(board.transform,false);cg.layer=17;cg.transform.position=new Vector3(-12.65f,2.8f,3.14f);cg.transform.localScale=Vector3.one*.0033f;canvas=cg.transform;
        cg.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;cg.GetComponent<RectTransform>().sizeDelta=new Vector2(1540,1740);cg.GetComponent<BoxCollider>().size=new Vector3(1540,1740,1);
        var pg=new GameObject("SD Performance");pg.transform.SetParent(c.transform,false);var perf=pg.AddUdonSharpComponent<SDPerformance>();perf.controller=c;c.performance=perf;
        Text("Title","PERFORMANCE / INPUTS",0,815,1420,58,34);
        perf.settingsText=Text("Settings","LOCAL PERFORMANCE / PREVIEWS",0,647,1420,250,20);
        var track=Rect("ProgressTrack",canvas,0,488,1420,22,typeof(Image));track.GetComponent<Image>().color=new Color(.035f,.075f,.1f);
        var fill=Rect("ProgressFill",track.transform,0,0,1420,22,typeof(Image)).GetComponent<Image>();fill.sprite=AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");fill.color=new Color(.04f,.7f,.75f);fill.type=Image.Type.Filled;fill.fillMethod=Image.FillMethod.Horizontal;fill.fillAmount=0;fill.raycastTarget=false;perf.progressFill=fill;
        perf.progressText=Text("ProgressText","PROGRESS 0%",0,426,1420,100,19);
        Text("PromptFormatTitle","PROMPT FORMAT",0,344,1420,42,23);
        Text("PromptFormat","Plain English, comma-separated descriptions. Positive: subject, appearance, scene, lighting, style. Negative: unwanted content. 75 content tokens maximum; longer text is truncated. A1111 weights and schedules are unsupported.",0,265,1420,112,19);
        Text("PositiveTitle","POSITIVE PROMPT",0,183,1420,35,21);c.inputs.positiveInput=Input("PositivePromptInput",96,c.inputs.presetPrompts[c.preset]);
        Text("NegativeTitle","NEGATIVE PROMPT",0,8,1420,35,21);c.inputs.negativeInput=Input("NegativePromptInput",-80,c.inputs.defaultNegative);
        Button("PREVIOUS PRESET","PreviousPreset",0,-475,-184,450);Button("APPLY CUSTOM PROMPT","ApplyPrompt",0,0,-184,450);Button("NEXT PRESET","NextPreset",0,475,-184,450);
        var photo=Rect("Photograph",canvas,-535,-355,300,300,typeof(RawImage)).GetComponent<RawImage>();photo.texture=c.inputs.photo;photo.raycastTarget=false;c.inputs.photoView=photo;
        Text("PhotoTitle","PHOTO INPUT",220,-233,910,45,24);
        Text("PhotoHelp","Take the Polaroid camera. Request SD control before shooting. A photograph replaces the input with a center square crop. Enable PHOTO INIT and Generate. Higher strength changes more.",220,-350,910,170,23);
        Button("PHOTO INIT ON / OFF","TogglePhoto",0,240,-472,850);
        Button("STRENGTH 0.35","SetStrength",35,-475,-550,450);Button("STRENGTH 0.60","SetStrength",60,0,-550,450);Button("STRENGTH 0.85","SetStrength",85,475,-550,450);
        Button("LOCAL SPEED 4 / 2ms","SetSpeed",4,-475,-620,450);Button("LOCAL SPEED 16 / 4ms","SetSpeed",16,0,-620,450);Button("LOCAL SPEED 32 / 8ms","SetSpeed",32,475,-620,450);
        Button("CAMERA EXPORT ON / OFF","ToggleExport",0,-355,-690,690);Button("REQUEST CONTROL","RequestControl",0,355,-690,690);
        Text("Locks","Two independent locks: MNIST and SD. Shared generation controls are locked; local preview and performance switches remain available.",0,-780,1420,100,22);
        var setup=world.Find("LocalPlayerSetup");var setupGo=setup!=null?setup.gameObject:new GameObject("LocalPlayerSetup");setupGo.transform.SetParent(world,false);var player=setupGo.GetComponent<DiffusionPlayerSetup>()??setupGo.AddUdonSharpComponent<DiffusionPlayerSetup>();Save(player);
        foreach(var b in Object.FindObjectsOfType<SDButton>()){b.ui=b.GetComponent<Button>();b.appearance=b.GetComponent<CanvasGroup>()??b.gameObject.AddComponent<CanvasGroup>();Save(b);}
        foreach(var b in Object.FindObjectsOfType<DiffusionButton>()){b.ui=b.GetComponent<Button>();b.appearance=b.GetComponent<CanvasGroup>()??b.gameObject.AddComponent<CanvasGroup>();Save(b);}
        c.buttons=Object.FindObjectsOfType<SDButton>().Where(b=>b.controller==c).ToArray();
        var mnist=Object.FindObjectOfType<DiffusionController>();mnist.buttons=Object.FindObjectsOfType<DiffusionButton>().Where(b=>b.controller==mnist&&b.teaching==null&&b.localization==null).ToArray();mnist.authorityId=-1;Save(mnist);
        c.details=false;c.vaePreviews=false;c.passesPerFrame=4;c.frameBudgetMs=2;Save(perf);Save(c.inputs);Save(c);
        AssetDatabase.SaveAssets();var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        return new{floorScale=GameObject.Find("Floor").transform.localScale,independentLocks=true,defaultPreviewsOff=true,jumpImpulse=player.jumpImpulse};
    }
    public static object Camera(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");c=Object.FindObjectOfType<SDController>();var world=GameObject.Find("DiffusionLabWorld").transform;
        var path=AssetDatabase.FindAssets("Polaroid t:Prefab").Select(AssetDatabase.GUIDToAssetPath).Single(p=>p.EndsWith("/Polaroid.prefab"));
        var old=world.Find("SD Polaroid");var go=old!=null?old.gameObject:(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));go.name="SD Polaroid";go.transform.SetParent(world,true);go.transform.position=new Vector3(-10,1.1f,.2f);
        var manager=go.GetComponentsInChildren<UdonSharp.UdonSharpBehaviour>(true).Single(b=>b.GetType().Name=="PolaroidManager");
        var bg=world.Find("SD Polaroid Bridge");var bridgeGo=bg!=null?bg.gameObject:new GameObject("SD Polaroid Bridge");bridgeGo.transform.SetParent(world,false);var bridge=bridgeGo.GetComponent<SDPolaroidBridge>()??bridgeGo.AddUdonSharpComponent<SDPolaroidBridge>();
        bridge.inputs=c.inputs;bridge.polaroidManager=UdonSharpEditorUtility.GetBackingUdonBehaviour(manager);bridge.photographs=(RenderTexture[])manager.GetType().GetField("renderTexture").GetValue(manager);Save(bridge);
        var camera=(UnityEngine.Camera)manager.GetType().GetField("cameraPhoto").GetValue(manager);camera.enabled=false;EditorUtility.SetDirty(camera);
        var zone=new GameObject("SD Camera Save Zone");zone.transform.SetParent(world,false);zone.transform.position=new Vector3(-7,1.5f,.4f);
        var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);quad.name="PhotoCameraOnlyOutput";quad.transform.SetParent(zone.transform,false);Object.DestroyImmediate(quad.GetComponent<Collider>());
        var mesh=Object.Instantiate(quad.GetComponent<MeshFilter>().sharedMesh);mesh.name="PhotoExportFullscreen";mesh.bounds=new Bounds(Vector3.zero,Vector3.one*10000);string mp=Root+"PhotoExportMesh.asset";var oldMesh=AssetDatabase.LoadAssetAtPath<Mesh>(mp);if(oldMesh==null){AssetDatabase.CreateAsset(mesh,mp);oldMesh=mesh;}else Object.DestroyImmediate(mesh);quad.GetComponent<MeshFilter>().sharedMesh=oldMesh;
        var material=AssetDatabase.LoadAssetAtPath<Material>(Root+"PhotoExport.mat");if(material==null){material=new Material(Shader.Find("DiffusionLab/SDPhotoExport"));AssetDatabase.CreateAsset(material,Root+"PhotoExport.mat");}
        material.SetVector("_StationCenter",zone.transform.position);material.SetFloat("_Radius",.65f);material.SetFloat("_Enabled",1);material.SetTexture("_MainTex",c.images[c.resolution]);quad.GetComponent<Renderer>().sharedMaterial=material;c.photoExport=material;EditorUtility.SetDirty(material);
        var marker=GameObject.CreatePrimitive(PrimitiveType.Cylinder);marker.name="CameraZoneMarker";marker.transform.SetParent(zone.transform,false);marker.transform.localPosition=new Vector3(0,-1.45f,0);marker.transform.localScale=new Vector3(1.3f,.01f,1.3f);Object.DestroyImmediate(marker.GetComponent<Collider>());marker.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Panel.mat");
        font=AssetDatabase.LoadAssetAtPath<Font>("Assets/DiffusionLab/Localization/FontSources/IBMPlexMono-Regular.ttf");
        var sign=new GameObject("CameraSaveInstructions",typeof(RectTransform),typeof(Canvas));sign.transform.SetParent(world,false);sign.transform.position=new Vector3(-7,.6f,1.2f);sign.transform.localScale=Vector3.one*.002f;sign.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;sign.GetComponent<RectTransform>().sizeDelta=new Vector2(1300,300);canvas=sign.transform;
        var signBack=GameObject.CreatePrimitive(PrimitiveType.Quad);signBack.name="CameraSaveBackboard";signBack.transform.SetParent(world,false);signBack.transform.position=new Vector3(-7,.6f,1.21f);signBack.transform.localScale=new Vector3(2.65f,.65f,1);signBack.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Panel.mat");Object.DestroyImmediate(signBack.GetComponent<Collider>());
        Text("Title","CAMERA SAVE ZONE",0,100,1260,60,32);Text("Help","Move your VRChat photo camera into the marked zone. The output fills its view. Press the native shutter to save to your photo folder.",0,-20,1260,150,25);
        Save(c);AssetDatabase.SaveAssets();var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);return new{prefab=path,photoSize=bridge.photographs[0].width,zone=zone.transform.position,radius=.65};
    }
    public static object CameraStand(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");var world=GameObject.Find("DiffusionLabWorld").transform;
        if(world.Find("Polaroid Stand")==null){var stand=GameObject.CreatePrimitive(PrimitiveType.Cube);stand.name="Polaroid Stand";stand.transform.SetParent(world,false);stand.transform.position=new Vector3(-10,.54f,.2f);stand.transform.localScale=new Vector3(.55f,1.05f,.45f);stand.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Panel.mat");}
        var zone=GameObject.Find("SD Camera Save Zone").transform;
        if(zone.Find("LensFrame")==null){var frame=new GameObject("LensFrame");frame.transform.SetParent(zone,false);var mat=AssetDatabase.LoadAssetAtPath<Material>(Root+"CameraZone.mat");if(mat==null){mat=new Material(Shader.Find("Unlit/Color"));mat.color=new Color(.03f,.7f,.75f);AssetDatabase.CreateAsset(mat,Root+"CameraZone.mat");}
            for(int i=0;i<4;i++){var edge=GameObject.CreatePrimitive(PrimitiveType.Cube);edge.name="Edge"+i;edge.transform.SetParent(frame.transform,false);edge.transform.localPosition=i<2?new Vector3(i==0?-.5f:.5f,0,0):new Vector3(0,i==2?-.5f:.5f,0);edge.transform.localScale=i<2?new Vector3(.012f,1.012f,.012f):new Vector3(1.012f,.012f,.012f);edge.GetComponent<Renderer>().sharedMaterial=mat;Object.DestroyImmediate(edge.GetComponent<Collider>());}
        }
        AssetDatabase.SaveAssets();var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);return new{cameraStand=true,visibleLensFrame=true};
    }
    public static object Polish(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode required");
        var zone=GameObject.Find("SD Camera Save Zone");zone.transform.position=new Vector3(-16.6f,1.5f,.4f);
        var controller=Object.FindObjectOfType<SDController>();controller.photoExport.SetVector("_StationCenter",zone.transform.position);EditorUtility.SetDirty(controller.photoExport);
        foreach(string name in new[]{"CameraSaveInstructions","CameraSaveBackboard"}){var t=GameObject.Find(name).transform;var p=t.position;p.x=-16.6f;t.position=p;}
        GameObject.Find("HistoryExplanation").GetComponent<Text>().text="VAE PREVIEWS OFF: LINEAR LATENT. ON: REAL RGB. FINAL: FULL VAE DECODE.";
        AssetDatabase.SaveAssets();var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);return new{saveZone=zone.transform.position,historyCaptionAccurate=true};
    }
}
