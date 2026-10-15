using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEditor;
using VRC.SDK3.Components;
using VRC.Udon;
using UdonSharpEditor;
using Object=UnityEngine.Object;
public static class TestSDUI {
    public static object Check(string canvasName="SDInterface",string boardName="SDBackboard"){
        var canvas=GameObject.Find(canvasName).GetComponent<Canvas>();var rect=canvas.GetComponent<RectTransform>();var box=canvas.GetComponent<BoxCollider>();
        if(canvas.renderMode!=RenderMode.WorldSpace||canvas.GetComponent<VRCUiShape>()==null||box==null||box.size.x<rect.rect.width||box.size.y<rect.rect.height)throw new Exception("Incomplete ray UI Canvas");
        var board=GameObject.Find(boardName).transform;
        var labels=canvas.GetComponentsInChildren<TMP_Text>(true);var input=canvas.GetComponentInChildren<InputField>();
        float distance=labels.Select(t=>Mathf.Abs(Vector3.Dot(board.forward,t.transform.position-board.position))).Append(Mathf.Abs(Vector3.Dot(board.forward,input.transform.position-board.position))).Max();
        if(distance>.02f||labels.Length==0||labels.Any(t=>t.raycastTarget))throw new Exception("Floating or missing TMP labels");
        var buttons=canvas.GetComponentsInChildren<Button>();var hits=new List<string>();var previous=canvas.worldCamera;
        var go=new GameObject("Temporary SD ray test");var cam=go.AddComponent<Camera>();var es=Object.FindObjectOfType<EventSystem>();GameObject ev=null;
        if(es==null){ev=new GameObject("Temporary SD events");es=ev.AddComponent<EventSystem>();}
        try{
            cam.transform.position=canvas.transform.position-canvas.transform.forward*12;cam.transform.LookAt(canvas.transform.position,canvas.transform.up);cam.fieldOfView=40;canvas.worldCamera=cam;
            Canvas.ForceUpdateCanvases();var ray=canvas.GetComponent<GraphicRaycaster>();if(ray==null)throw new Exception("Missing GraphicRaycaster");
            foreach(var button in buttons){
                if(button.gameObject.layer!=17||button.onClick.GetPersistentEventCount()!=1||!(button.onClick.GetPersistentTarget(0) is UdonBehaviour)||button.onClick.GetPersistentMethodName(0)!="SendCustomEvent")throw new Exception("Bad button event: "+button.name);
                var r=button.GetComponent<RectTransform>();var p=RectTransformUtility.WorldToScreenPoint(cam,r.TransformPoint(r.rect.center));var data=new PointerEventData(es){position=p};var found=new List<RaycastResult>();ray.Raycast(data,found);
                if(found.Count==0||!(found[0].gameObject==button.gameObject||found[0].gameObject.transform.IsChildOf(button.transform)))throw new Exception("Button ray blocked: "+button.name);
                hits.Add(button.name);
            }
            if(EditorApplication.isPlaying&&canvasName=="SDInterface"){
                var controller=UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectOfType<SDController>());
                bool details=(bool)controller.GetProgramVariable("details");buttons.Single(b=>b.name=="ToggleDetails0").onClick.Invoke();
                if((bool)controller.GetProgramVariable("details")==details)throw new Exception("Actual UI click did not reach Udon");buttons.Single(b=>b.name=="ToggleDetails0").onClick.Invoke();
                bool previews=(bool)controller.GetProgramVariable("vaePreviews");buttons.Single(b=>b.name=="VAEPreviews").onClick.Invoke();
                if((bool)controller.GetProgramVariable("vaePreviews")==previews)throw new Exception("Preview UI click did not reach Udon");buttons.Single(b=>b.name=="VAEPreviews").onClick.Invoke();
            }
        }finally{canvas.worldCamera=previous;Object.DestroyImmediate(go);if(ev!=null)Object.DestroyImmediate(ev);}
        var report=new{passed=true,buttons=buttons.Length,tmpLabels=labels.Length,maxPlaneDistanceMeters=distance,rayHits=hits,actualClick=EditorApplication.isPlaying};
        string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"tmp/vrc-diffusion/sd-anime/playmode");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"ui-"+canvasName+".json"),JsonConvert.SerializeObject(report,Formatting.Indented));return report;
    }
}
