using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TMPro;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;
using Object=UnityEngine.Object;

// Run EditMode() in EditMode, then PlayMode() after entering PlayMode.
// This script only inspects scene content in EditMode. PlayMode changes are transient
// and it never saves a scene or modifies serialized Unity assets.
public static class TestDiffusionUI {
    const int InteractionLayer=17;
    const string ReportRoot="Reports/DiffusionLab";

    static void Require(bool condition,string message,List<string> errors) {
        if(!condition) errors.Add(message);
    }
    static void ThrowIfErrors(string title,List<string> errors) {
        if(errors.Count>0) throw new Exception(title+" failed:\n - "+string.Join("\n - ",errors));
    }
    static void WriteReport(string name,object report) {
        Directory.CreateDirectory(ReportRoot);
        File.WriteAllText(Path.Combine(ReportRoot,name),JsonConvert.SerializeObject(report,Formatting.Indented));
    }
    static string PathOf(Transform t) {
        if(t==null) return "<null>";
        string path=t.name;
        while(t.parent!=null) {t=t.parent;path=t.name+"/"+path;}
        return path;
    }
    static Transform WorldRoot() {
        var roots=Object.FindObjectsOfType<Transform>(true).Where(t=>t.name=="DiffusionLabWorld"&&!IsClientSim(t)).ToArray();
        if(roots.Length!=1) throw new Exception("Expected one DiffusionLabWorld root, found "+roots.Length);
        return roots[0];
    }
    static bool IsClientSim(Transform t) {
        while(t!=null) {
            if(t.name.IndexOf("ClientSim",StringComparison.OrdinalIgnoreCase)>=0) return true;
            t=t.parent;
        }
        return false;
    }
    static T[] InWorld<T>(Transform root) where T:Component {
        return root.GetComponentsInChildren<T>(true).Where(c=>!IsClientSim(c.transform)).ToArray();
    }
    static Renderer Backboard(Transform root) {
        return InWorld<Renderer>(root).FirstOrDefault(r=>r.gameObject.name=="Backboard");
    }
    static float PlaneDistance(Transform item,Renderer board) {
        return Mathf.Abs(Vector3.Dot(board.transform.forward,item.position-board.transform.position));
    }
    static bool Covers(Rect rect,BoxCollider box) {
        const float tolerance=.001f;
        Vector3 min=box.center-box.size*.5f,max=box.center+box.size*.5f;
        return box.size.z>0f&&min.x<=rect.xMin+tolerance&&max.x>=rect.xMax-tolerance&&
            min.y<=rect.yMin+tolerance&&max.y>=rect.yMax-tolerance;
    }

    public static object EditMode() {
        if(EditorApplication.isPlaying) throw new Exception("EditMode UI validation requires EditMode");
        var root=WorldRoot();
        var errors=new List<string>();
        var buttons=InWorld<Button>(root);
        var canvases=InWorld<Canvas>(root);
        var labels=InWorld<Text>(root).Where(t=>t.GetComponentInParent<Canvas>(true)!=null).ToArray();
        var tmpLabels=InWorld<TMP_Text>(root).Where(t=>t.GetComponentInParent<Canvas>(true)!=null).ToArray();
        var inputFields=InWorld<InputField>(root).Where(i=>i.GetComponentInParent<Canvas>(true)!=null).ToArray();
        var tmpInputFields=InWorld<TMP_InputField>(root).Where(i=>i.GetComponentInParent<Canvas>(true)!=null).ToArray();
        var board=Backboard(root);

        Require(buttons.Length>0,"No UGUI Buttons were found",errors);
        Require(canvases.Length>0,"No Canvas was found",errors);
        Require(board!=null,"Backboard renderer was not found",errors);
        foreach(var button in buttons) {
            var image=button.GetComponent<Image>();
            Require(image!=null,PathOf(button.transform)+" must have a UGUI Image",errors);
            Require(button.gameObject.layer==InteractionLayer,PathOf(button.transform)+" must use layer "+InteractionLayer,errors);
            if(image!=null) {
                Require(button.targetGraphic==image,PathOf(button.transform)+" Button.targetGraphic must reference its Image",errors);
                Require(image.raycastTarget,PathOf(button.transform)+" Image must be a raycast target",errors);
            }
        }
        foreach(var canvas in canvases) {
            var collider=canvas.GetComponent<BoxCollider>();
            Require(canvas.renderMode==RenderMode.WorldSpace,PathOf(canvas.transform)+" must be a World Space Canvas",errors);
            Require(canvas.GetComponent<GraphicRaycaster>()!=null,PathOf(canvas.transform)+" is missing GraphicRaycaster",errors);
            Require(canvas.GetComponent<VRCUiShape>()!=null,PathOf(canvas.transform)+" is missing VRCUiShape",errors);
            Require(collider!=null,PathOf(canvas.transform)+" is missing BoxCollider",errors);
            var rect=canvas.GetComponent<RectTransform>();
            if(collider!=null&&rect!=null)
                Require(Covers(rect.rect,collider),PathOf(canvas.transform)+" BoxCollider does not cover the full Canvas rectangle",errors);
        }
        foreach(var label in labels) {
            Require(!label.raycastTarget,PathOf(label.transform)+" Text.raycastTarget must be false",errors);
            if(board!=null) Require(PlaneDistance(label.transform,board)<=.02f,
                PathOf(label.transform)+" is more than 2 cm from the Backboard plane",errors);
        }
        foreach(var label in tmpLabels) {
            Require(!label.raycastTarget,PathOf(label.transform)+" TMP raycastTarget must be false",errors);
            if(board!=null) Require(PlaneDistance(label.transform,board)<=.02f,
                PathOf(label.transform)+" is more than 2 cm from the Backboard plane",errors);
        }
        foreach(var field in inputFields) if(board!=null)
            Require(PlaneDistance(field.transform,board)<=.02f,PathOf(field.transform)+" is more than 2 cm from the Backboard plane",errors);
        foreach(var field in tmpInputFields) if(board!=null)
            Require(PlaneDistance(field.transform,board)<=.02f,PathOf(field.transform)+" is more than 2 cm from the Backboard plane",errors);
        Require(inputFields.Any(i=>i.name=="SeedInput")||tmpInputFields.Any(i=>i.name=="SeedInput"),
            "SeedInput was not found under a Canvas",errors);
        ThrowIfErrors("EditMode UI validation",errors);

        float maxTextDistance=0f;
        if(board!=null) {
            foreach(var label in labels) maxTextDistance=Mathf.Max(maxTextDistance,PlaneDistance(label.transform,board));
            foreach(var label in tmpLabels) maxTextDistance=Mathf.Max(maxTextDistance,PlaneDistance(label.transform,board));
        }
        var report=new {
            passed=true,
            buttons=buttons.Length,
            canvases=canvases.Length,
            legacyText=labels.Length,
            tmpText=tmpLabels.Length,
            inputFields=inputFields.Length+tmpInputFields.Length,
            maxTextPlaneDistanceMeters=maxTextDistance,
            backboardPlaneDistanceLimitMeters=.02f,
            interactionLayer=InteractionLayer
        };
        WriteReport("ui-editmode.json",report);
        return report;
    }

    static UdonBehaviour Backing<T>(string label) where T:UdonSharp.UdonSharpBehaviour {
        var candidates=InWorld<T>(WorldRoot());
        if(candidates.Length!=1) throw new Exception("Expected one "+label+" under DiffusionLabWorld, found "+candidates.Length);
        var proxy=candidates[0];
        var backing=UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
        if(backing==null) throw new Exception("No backing UdonBehaviour for "+label);
        return backing;
    }
    static int Int(UdonBehaviour backing,string name) {
        return (int)backing.GetProgramVariable(name);
    }
    static Button NamedButton(string name,Transform root) {
        var matches=InWorld<Button>(root).Where(b=>b.gameObject.name==name).ToArray();
        if(matches.Length!=1) throw new Exception("Expected one Button named "+name+", found "+matches.Length);
        return matches[0];
    }
    static bool IsButtonHit(Button button,GameObject hit) {
        return hit==button.gameObject||hit.transform.IsChildOf(button.transform);
    }
    static Vector3 FrontCameraPosition(Canvas canvas) {
        return canvas.transform.position-canvas.transform.forward*12f;
    }
    static void AimCameraAtCanvas(Camera camera,Canvas canvas) {
        camera.transform.position=FrontCameraPosition(canvas);
        camera.transform.LookAt(canvas.transform.position,canvas.transform.up);
        camera.fieldOfView=40f;
    }
    static object RaycastButton(Button button,Camera camera,GraphicRaycaster raycaster,EventSystem eventSystem) {
        var rect=button.transform as RectTransform;
        if(rect==null) throw new Exception(PathOf(button.transform)+" has no RectTransform");
        Vector3 world=rect.TransformPoint(rect.rect.center);
        Vector3 viewport=camera.WorldToViewportPoint(world);
        if(viewport.z<=0f||viewport.x<0f||viewport.x>1f||viewport.y<0f||viewport.y>1f)
            throw new Exception(PathOf(button.transform)+" center is outside PreviewCamera view");
        Vector2 screen=RectTransformUtility.WorldToScreenPoint(camera,world);
        var eventData=new PointerEventData(eventSystem){position=screen,button=PointerEventData.InputButton.Left};
        var hits=new List<RaycastResult>();
        raycaster.Raycast(eventData,hits);
        if(hits.Count==0||!IsButtonHit(button,hits[0].gameObject)) {
            string top=hits.Count==0?"<none>":PathOf(hits[0].gameObject.transform);
            throw new Exception(PathOf(button.transform)+" center did not hit the Button (top hit: "+top+")");
        }
        return new {
            name=button.gameObject.name,
            screenX=Mathf.RoundToInt(screen.x),
            screenY=Mathf.RoundToInt(screen.y),
            firstHit=PathOf(hits[0].gameObject.transform)
        };
    }
    static string Capture(Camera camera,Canvas canvas,string fileName,bool oblique) {
        const int width=1600,height=1000;
        var worldRoot=WorldRoot();
        var worldCanvases=new HashSet<Canvas>(worldRoot.GetComponentsInChildren<Canvas>(true));
        var worldRenderers=new HashSet<Renderer>(worldRoot.GetComponentsInChildren<Renderer>(true));
        var outsideCanvasStates=new Dictionary<Canvas,bool>();
        var outsideRendererStates=new Dictionary<Renderer,bool>();
        foreach(var outside in Object.FindObjectsOfType<Canvas>(true).Where(c=>!worldCanvases.Contains(c))) {
            outsideCanvasStates[outside]=outside.enabled;
        }
        foreach(var outside in Object.FindObjectsOfType<Renderer>(true).Where(r=>!worldRenderers.Contains(r))) {
            outsideRendererStates[outside]=outside.enabled;
        }
        var target=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);
        var texture=new Texture2D(width,height,TextureFormat.RGB24,false);
        var previousTarget=camera.targetTexture;
        var previousActive=RenderTexture.active;
        var previousPosition=camera.transform.position;
        var previousRotation=camera.transform.rotation;
        var previousFov=camera.fieldOfView;
        try {
            foreach(var entry in outsideCanvasStates) entry.Key.enabled=false;
            foreach(var entry in outsideRendererStates) entry.Key.enabled=false;
            target.Create();
            camera.targetTexture=target;
            camera.transform.position=canvas.transform.position-canvas.transform.forward*8f;
            camera.transform.LookAt(canvas.transform.position,canvas.transform.up);
            camera.fieldOfView=40f;
            if(oblique) {
                camera.transform.position+=-canvas.transform.right*3.5f+canvas.transform.up*.3f;
                camera.transform.LookAt(canvas.transform.position,canvas.transform.up);
            }
            camera.Render();
            RenderTexture.active=target;
            texture.ReadPixels(new Rect(0,0,width,height),0,0,false);
            texture.Apply(false,false);
            Directory.CreateDirectory(ReportRoot);
            string path=Path.Combine(ReportRoot,fileName);
            File.WriteAllBytes(path,texture.EncodeToPNG());
            return path;
        } finally {
            camera.targetTexture=previousTarget;
            camera.transform.SetPositionAndRotation(previousPosition,previousRotation);
            camera.fieldOfView=previousFov;
            foreach(var entry in outsideCanvasStates) if(entry.Key!=null) entry.Key.enabled=entry.Value;
            foreach(var entry in outsideRendererStates) if(entry.Key!=null) entry.Key.enabled=entry.Value;
            RenderTexture.active=previousActive;
            Object.DestroyImmediate(texture);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
    public static string[] CaptureCurrent() {
        var root=WorldRoot();
        var camera=InWorld<Camera>(root).FirstOrDefault(c=>c.name=="PreviewCamera");
        if(camera==null) throw new Exception("PreviewCamera was not found");
        var canvases=InWorld<Canvas>(root).Where(c=>c.gameObject.activeInHierarchy&&c.isActiveAndEnabled).ToArray();
        if(canvases.Length<2) throw new Exception("CaptureCurrent requires main and teaching Canvases");
        Canvas mainCanvas=canvases.OrderBy(c=>Mathf.Abs(c.transform.position.x)).First();
        Canvas teachingCanvas=canvases.Where(c=>c!=mainCanvas).OrderBy(c=>Mathf.Abs(c.transform.position.x-7f)).First();
        var previousCameras=new Dictionary<Canvas,Camera>();
        try {
            foreach(var canvas in canvases) {previousCameras[canvas]=canvas.worldCamera;canvas.worldCamera=camera;}
            return new[] {
                Capture(camera,mainCanvas,"ui-main-front.png",false),
                Capture(camera,mainCanvas,"ui-main-oblique.png",true),
                Capture(camera,teachingCanvas,"ui-teaching-front.png",false)
            };
        } finally {
            foreach(var entry in previousCameras) if(entry.Key!=null) entry.Key.worldCamera=entry.Value;
        }
    }
    static bool HasCjk(string value) {
        if(value==null) return false;
        return value.Any(c=>c>='\u4e00'&&c<='\u9fff');
    }
    static int ValidateLanguage(UdonBehaviour localization,int expected,TextMeshProUGUI[] english,
        TextMeshProUGUI[] chinese,TextMeshProUGUI[] japanese) {
        int actual=Int(localization,"languageIndex");
        if(actual!=expected) throw new Exception("languageIndex expected "+expected+", got "+actual);
        TextMeshProUGUI[][] sets={english,chinese,japanese};
        int activeSet=0;
        for(int language=0;language<sets.Length;language++) {
            foreach(var text in sets[language]) {
                bool active=text!=null&&text.gameObject.activeSelf;
                if(active) activeSet++;
                if(active!=(language==expected))
                    throw new Exception("Language "+expected+" has an incorrect active TMP overlay at "+(text==null?"<null>":PathOf(text.transform)));
            }
        }
        if(expected==1&&!chinese.Any(t=>t!=null&&t.gameObject.activeSelf&&HasCjk(t.text)))
            throw new Exception("Chinese language did not activate TMP text containing Chinese characters");
        return activeSet;
    }

    static async Task Until(Func<bool> condition,int timeoutMs) {
        var start=DateTime.UtcNow;
        while(!condition()) {
            if((DateTime.UtcNow-start).TotalMilliseconds>timeoutMs)
                throw new Exception("PlayMode UI wait timed out while requesting controller ownership");
            await Task.Delay(50);
        }
    }
    static Canvas CanvasFor(Button button) {
        return button.GetComponentInParent<Canvas>(true);
    }
    public static async Task<object> PlayMode() {
        if(!EditorApplication.isPlaying) throw new Exception("PlayMode UI validation requires PlayMode");
        var root=WorldRoot();
        var errors=new List<string>();
        var camera=InWorld<Camera>(root).FirstOrDefault(c=>c.name=="PreviewCamera");
        if(camera==null) throw new Exception("PreviewCamera was not found");
        var controller=Backing<DiffusionController>("DiffusionController");
        var localization=Backing<DiffusionLocalization>("DiffusionLocalization");
        var canvases=InWorld<Canvas>(root).Where(c=>c.gameObject.activeInHierarchy&&c.isActiveAndEnabled).ToArray();
        var buttons=InWorld<Button>(root).Where(b=>b.gameObject.activeInHierarchy&&b.isActiveAndEnabled&&
            CanvasFor(b)!=null&&CanvasFor(b).isActiveAndEnabled).ToArray();
        Require(buttons.Length>0,"No active Buttons were found",errors);
        Require(canvases.Length>0,"No active Canvases were found",errors);
        ThrowIfErrors("PlayMode UI validation",errors);
        var previousCameras=new Dictionary<Canvas,Camera>();
        GameObject temporaryEventSystem=null;
        EventSystem eventSystem=EventSystem.current;
        if(eventSystem==null) {
            temporaryEventSystem=new GameObject("DiffusionUIValidationEventSystem");
            eventSystem=temporaryEventSystem.AddComponent<EventSystem>();
        }
        var raycastResults=new List<object>();
        var canvasResults=new List<object>();
        var previousPosition=camera.transform.position;
        var previousRotation=camera.transform.rotation;
        var previousFov=camera.fieldOfView;
        try {
            foreach(var canvas in canvases) {
                previousCameras[canvas]=canvas.worldCamera;
                canvas.worldCamera=camera;
                AimCameraAtCanvas(camera,canvas);
                Canvas.ForceUpdateCanvases();
                var canvasButtons=buttons.Where(b=>CanvasFor(b)==canvas).ToArray();
                var raycaster=canvas.GetComponent<GraphicRaycaster>();
                if(raycaster==null) throw new Exception(PathOf(canvas.transform)+" has no GraphicRaycaster");
                foreach(var button in canvasButtons)
                    raycastResults.Add(RaycastButton(button,camera,raycaster,eventSystem));
                canvasResults.Add(new{canvas=PathOf(canvas.transform),buttonCount=canvasButtons.Length,
                    cameraPosition=new[]{FrontCameraPosition(canvas).x,FrontCameraPosition(canvas).y,FrontCameraPosition(canvas).z},
                    fieldOfView=camera.fieldOfView});
            }

            var localPlayer=Networking.LocalPlayer;
            if(localPlayer==null) throw new Exception("PlayMode UI validation requires a ClientSim LocalPlayer");
            Button requestControl=NamedButton("RequestControl_0",root);
            requestControl.onClick.Invoke();
            await Until(()=>Int(controller,"authorityId")==localPlayer.playerId&&Networking.IsOwner(controller.gameObject),10000);
            int authorityId=Int(controller,"authorityId");

            var english=localization.GetProgramVariable("english") as TextMeshProUGUI[];
            var chinese=localization.GetProgramVariable("chinese") as TextMeshProUGUI[];
            var japanese=localization.GetProgramVariable("japanese") as TextMeshProUGUI[];
            if(english==null||chinese==null||japanese==null||english.Length==0||
                english.Length!=chinese.Length||english.Length!=japanese.Length)
                throw new Exception("Localization backing TMP arrays are missing or have different lengths");

            Button classSeven=NamedButton("SelectDigit_7",root);
            classSeven.onClick.Invoke();
            int digit=Int(controller,"digit");
            if(digit!=7) throw new Exception("Class 7 click did not update controller digit; got "+digit);

            var seedInput=controller.GetProgramVariable("seedInput") as InputField;
            if(seedInput==null) throw new Exception("Controller backing seedInput is not a UGUI InputField");
            Button applySeed=NamedButton("ApplySeed_0",root);
            seedInput.text="1234";
            applySeed.onClick.Invoke();
            int seedBeforeDraft=Int(controller,"seed");
            if(seedBeforeDraft!=1234) throw new Exception("Apply Seed click did not set seed to 1234; got "+seedBeforeDraft);

            eventSystem.SetSelectedGameObject(null);
            if(seedInput.isFocused) throw new Exception("SeedInput must be unfocused before draft persistence check");
            seedInput.text="987654321";
            await Task.Delay(400);
            if(seedInput.isFocused) throw new Exception("SeedInput unexpectedly gained focus during draft persistence check");
            if(seedInput.text!="987654321") throw new Exception("Unfocused seed draft changed before Apply Seed");
            if(Int(controller,"seed")!=1234) throw new Exception("Unapplied seed draft changed controller seed");

            applySeed.onClick.Invoke();
            int seed=Int(controller,"seed");
            if(seed!=987654321) throw new Exception("Apply Seed click did not update controller seed; got "+seed);

            var languageCounts=new List<object>();
            foreach(int language in new[]{1,2,0}) {
                Button languageButton=NamedButton("SetLanguage_"+language,root);
                languageButton.onClick.Invoke();
                int active=ValidateLanguage(localization,language,english,chinese,japanese);
                int overrideValue=Int(localization,"languageOverride");
                if(overrideValue!=language) throw new Exception("languageOverride expected "+language+", got "+overrideValue);
                languageCounts.Add(new{value=language,activeTmp=active});
            }

            Canvas mainCanvas=canvases.OrderBy(c=>Mathf.Abs(c.transform.position.x)).First();
            string front=Capture(camera,mainCanvas,"ui-main-front.png",false);
            string oblique=Capture(camera,mainCanvas,"ui-main-oblique.png",true);
            var screenshots=new List<string>{front,oblique};
            var teachingCanvas=canvases.Where(c=>c!=mainCanvas).OrderBy(c=>Mathf.Abs(c.transform.position.x-7f)).FirstOrDefault();
            if(teachingCanvas!=null) screenshots.Add(Capture(camera,teachingCanvas,"ui-teaching-front.png",false));
            var report=new {
                passed=true,
                buttonsRaycast=raycastResults.Count,
                canvases=canvasResults,
                localPlayerId=localPlayer.playerId,
                authorityIdAfterRequest=authorityId,
                classAfterClick=digit,
                seedAfterClick=seed,
                languages=languageCounts,
                screenshots=screenshots,
                sceneSaved=false,
                raycasts=raycastResults
            };
            WriteReport("ui-playmode.json",report);
            return report;
        } finally {
            foreach(var entry in previousCameras) if(entry.Key!=null) entry.Key.worldCamera=entry.Value;
            camera.transform.SetPositionAndRotation(previousPosition,previousRotation);
            camera.fieldOfView=previousFov;
            if(temporaryEventSystem!=null) Object.Destroy(temporaryEventSystem);
        }
    }
}
