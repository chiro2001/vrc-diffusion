using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharpEditor;
using Object=UnityEngine.Object;
public static class PolishSDLayout {
    public static object Apply(){
        if(EditorApplication.isPlaying)throw new System.Exception("EditMode only");var c=Object.FindObjectOfType<SDController>();c.vaePreviews=true;
        c.algorithmText.rectTransform.anchoredPosition=new Vector2(365,-210);c.algorithmText.rectTransform.sizeDelta=new Vector2(730,148);c.algorithmText.fontSize=14;
        var theory=GameObject.Find("SDPrinciples").GetComponent<Text>();theory.rectTransform.anchoredPosition=new Vector2(-390,-219);theory.rectTransform.sizeDelta=new Vector2(740,124);theory.fontSize=14;
        GameObject.Find("HistoryExplanation").GetComponent<Text>().text="NOISE → REAL VAE PREVIEWS → FINAL IMAGE";
        UdonSharpEditorUtility.CopyProxyToUdon(c);EditorUtility.SetDirty(c);AssetDatabase.SaveAssets();var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);return new{vaePreviews=true,layoutAdjusted=true};
    }
}
