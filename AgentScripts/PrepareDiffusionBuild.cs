using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UdonSharpEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public static class PrepareDiffusionBuild {
    public static object RefineLayout() {
        if(EditorApplication.isPlaying)throw new Exception("Use EditMode");
        var world=GameObject.Find("DiffusionLabWorld");var canvas=world.transform.Find("Interface");
        var subtitle=canvas.Find("Subtitle").GetComponent<UnityEngine.UI.Text>();subtitle.rectTransform.anchoredPosition=new Vector2(0,510);subtitle.rectTransform.sizeDelta=new Vector2(1950,40);subtitle.fontSize=21;
        var status=canvas.Find("Status").GetComponent<UnityEngine.UI.Text>();status.rectTransform.anchoredPosition=new Vector2(-445,-80);status.rectTransform.sizeDelta=new Vector2(1100,70);status.fontSize=20;
        var config=canvas.Find("Config").GetComponent<UnityEngine.UI.Text>();config.rectTransform.anchoredPosition=new Vector2(450,-80);config.rectTransform.sizeDelta=new Vector2(1050,70);
        canvas.Find("Live memory").localPosition=new Vector3(365,195,1.25f);
        var scene=world.scene;EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);return new{refined=true};
    }
    public static object InteractiveDefaults() {
        if(EditorApplication.isPlaying)throw new Exception("Stop PlayMode first");
        var c=UnityEngine.Object.FindObjectOfType<DiffusionController>();
        if(c==null||c.gameObject.scene.path!="Assets/DiffusionLab/Scenes/DiffusionLab.unity")throw new Exception("Wrong world");
        c.validationRunOnStart=false;c.fixtureRun=false;c.phase=0;c.epoch=0;c.revision=0;
        c.authorityId=-1;c.grantedId=-1;c.digit=3;c.seed=1234;c.modelIndex=c.models!=null&&c.models.Length>1?1:0;c.outputIndex=0;c.totalSteps=c.models!=null&&c.models.Length>0?c.models[c.modelIndex].defaultSteps:32;c.completedSteps=0;
        c.localEpoch=-1;c.localStep=0;c.localLayer=0;c.validationPassed=false;c.validationMaxError=0;
        c.passesPerFrame=1;c.passInterval=.016f;c.lastComputedLayer=-1;c.lastAlgorithmStage=0;
        if(c.seedInput!=null)c.seedInput.text="1234";
        if(c.models!=null&&c.models.Length>0){c.models[0].displayName="Legacy MNIST 32 (comparison)";UdonSharpEditorUtility.CopyProxyToUdon(c.models[0]);EditorUtility.SetDirty(c.models[0]);}
        var label=c.transform.parent.Find("Interface/SelectModel_0/Label");if(label!=null)label.GetComponent<UnityEngine.UI.Text>().text="MODEL Legacy compare";
        if(c.teaching!=null){c.teaching.page=0;c.teaching.titleText.text=c.teaching.pageTitles[0];c.teaching.bodyText.text=c.teaching.pageBodies[0];UdonSharpEditorUtility.CopyProxyToUdon(c.teaching);EditorUtility.SetDirty(c.teaching);}
        UdonSharpEditorUtility.CopyProxyToUdon(c);EditorUtility.SetDirty(c);
        EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(c));
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(c.gameObject.scene);EditorSceneManager.SaveScene(c.gameObject.scene);
        return new{interactive=true,validationRunOnStart=c.validationRunOnStart,phase=c.phase,model=c.modelVersion};
    }

    public static object SaveGeneratedScene() {
        if(EditorApplication.isPlaying)throw new Exception("Stop PlayMode first");
        var scene=SceneManager.GetActiveScene();
        if(scene.path!="Assets/DiffusionLab/Scenes/DiffusionLab.unity")throw new Exception("Wrong scene");
        var roots=scene.GetRootGameObjects();
        if(roots.Length!=1||roots[0].name!="DiffusionLabWorld")throw new Exception("Scene contains additional roots; preserve their changes explicitly");
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"tmp","vrc-diffusion","scene-checkpoints");Directory.CreateDirectory(dir);
        var backup=Path.Combine(dir,"DiffusionLab-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".unity");
        // Keep the current unsaved compatibility/serialization changes before rebuilding our generated root.
        if(!EditorSceneManager.SaveScene(scene,backup,true))throw new Exception("Backup failed");
        AssetDatabase.SaveAssets();
        if(!EditorSceneManager.SaveScene(scene))throw new Exception("Scene save failed");
        return new{saved=true,scene=scene.path,backup};
    }
}
