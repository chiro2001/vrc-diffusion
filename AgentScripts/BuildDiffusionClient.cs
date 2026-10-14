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
        throw new InvalidOperationException("Automatic VRChat client launch is disabled by project policy. Use Unity PlayMode validation.");
    }
    public static object Issues() {
        var panel=EditorWindow.GetWindow<VRCSdkControlPanel>();var descriptor=Object.FindObjectOfType<VRC.SDK3.Components.VRCSceneDescriptor>();
        return new{world=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(panel.GetGuiErrorsOrIssuesForItem(descriptor),i=>i.issueText)),project=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(panel.GetGuiErrorsOrIssuesForItem(panel),i=>i.issueText))};
    }
}
