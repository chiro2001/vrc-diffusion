using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using Object=UnityEngine.Object;

public static class BuildDiffusionClient {
    const string Report="Reports/DiffusionLab/client-build.json";
    static void Save(object result){Directory.CreateDirectory("Reports/DiffusionLab");File.WriteAllText(Report,JsonConvert.SerializeObject(result,Formatting.Indented));}
    public static object Start() {
        if(EditorApplication.isPlaying)throw new Exception("Stop PlayMode first");
        if(SceneManager.GetActiveScene().path!="Assets/DiffusionLab/Scenes/DiffusionLab.unity"||SceneManager.GetActiveScene().isDirty)throw new Exception("Open saved DiffusionLab scene");
        Save(new{status="starting",time=DateTime.UtcNow});
        _=Run();return new{started=true,report=Report};
    }
    static async Task Run() {
        var controller=Object.FindObjectOfType<DiffusionController>();
        bool previous=controller.validationRunOnStart;
        int clients=EditorPrefs.GetInt("numClients",1);bool noVR=EditorPrefs.GetBool("forceNoVR",true);
        string bundle=null;
        try {
            // The shipped scene remains interactive; only this smoke-test bundle auto-runs.
            controller.validationRunOnStart=true;
            UdonSharpEditorUtility.CopyProxyToUdon(controller);EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            EditorPrefs.SetInt("numClients",1);EditorPrefs.SetBool("forceNoVR",true);
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");await Task.Delay(2000);
            IVRCSdkWorldBuilderApi builder;
            if(!VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out builder))throw new Exception("World builder unavailable");
            builder.RegisterBuilder(EditorWindow.GetWindow<VRCSdkControlPanel>());
            string validation;
            if(!builder.IsValidBuilder(out validation))throw new Exception("Invalid SDK builder: "+validation);
            builder.Initialize();
            EventHandler<string> success=(sender,path)=>bundle=path;builder.OnSdkBuildSuccess+=success;
            try {await builder.BuildAndTest();}finally {builder.OnSdkBuildSuccess-=success;}
            Save(new{status="builtAndLaunched",bundlePath=bundle,modelVersion=controller.modelVersion,desktop=true,clients=1,time=DateTime.UtcNow});
        } catch(Exception e){Save(new{status="failed",error=e.ToString(),bundlePath=bundle,time=DateTime.UtcNow});Debug.LogError("DIFFUSION_CLIENT_BUILD_FAIL "+e.Message);}
        finally {
            EditorPrefs.SetInt("numClients",clients);EditorPrefs.SetBool("forceNoVR",noVR);
            if(controller!=null&&!EditorApplication.isPlaying) {
                controller.validationRunOnStart=previous;UdonSharpEditorUtility.CopyProxyToUdon(controller);EditorUtility.SetDirty(controller);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            }
        }
    }
    public static object Issues() {
        var panel=EditorWindow.GetWindow<VRCSdkControlPanel>();var descriptor=Object.FindObjectOfType<VRC.SDK3.Components.VRCSceneDescriptor>();
        return new{world=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(panel.GetGuiErrorsOrIssuesForItem(descriptor),i=>i.issueText)),project=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(panel.GetGuiErrorsOrIssuesForItem(panel),i=>i.issueText))};
    }
}
