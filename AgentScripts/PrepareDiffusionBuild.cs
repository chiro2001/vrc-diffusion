using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UdonSharpEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public static class PrepareDiffusionBuild {
    public static object InteractiveDefaults() {
        if(EditorApplication.isPlaying)throw new Exception("Stop PlayMode first");
        var c=UnityEngine.Object.FindObjectOfType<DiffusionController>();
        if(c==null||c.gameObject.scene.path!="Assets/DiffusionLab/Scenes/DiffusionLab.unity")throw new Exception("Wrong world");
        c.validationRunOnStart=false;c.fixtureRun=false;c.phase=0;c.epoch=0;c.revision=0;
        c.authorityId=-1;c.grantedId=-1;c.digit=3;c.seed=1234;c.totalSteps=32;c.completedSteps=0;
        c.localEpoch=-1;c.localStep=0;c.localLayer=0;c.validationPassed=false;c.validationMaxError=0;
        c.passesPerFrame=1;c.passInterval=.016f;
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
