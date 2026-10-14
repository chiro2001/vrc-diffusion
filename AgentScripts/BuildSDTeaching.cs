using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharpEditor;
using Object=UnityEngine.Object;
public static class BuildSDTeaching {
    static GameObject Rect(Transform parent,string name,float x,float y,float w,float h,params Type[] components){
        var g=new GameObject(name,new[]{typeof(RectTransform),typeof(CanvasRenderer)}.Concat(components).ToArray());g.transform.SetParent(parent,false);g.layer=17;
        var r=g.GetComponent<RectTransform>();r.anchoredPosition=new Vector2(x,y);r.sizeDelta=new Vector2(w,h);return g;
    }
    public static object Apply(){
        if(EditorApplication.isPlaying)throw new Exception("EditMode only");var c=Object.FindObjectOfType<SDController>();var canvas=GameObject.Find("SDInterface").transform;
        if(canvas.Find("SDStructure")!=null)throw new Exception("Structure already made");
        var memory=c.memoryText.rectTransform;memory.anchoredPosition=new Vector2(355,-63);memory.sizeDelta=new Vector2(750,130);c.memoryText.fontSize=16;
        c.algorithmText.rectTransform.anchoredPosition=new Vector2(365,-232);c.algorithmText.rectTransform.sizeDelta=new Vector2(730,205);c.algorithmText.fontSize=17;
        var root=Rect(canvas,"SDStructure",355,72,700,145);var teacher=root.AddUdonSharpComponent<SDTeaching>();teacher.controller=c;
        string[] labels={"TIME","IN","DOWN 1","DOWN 2","DOWN 3","DOWN 4","MID","UP 1","UP 2","UP 3","UP 4","OUT","VAE"};
        teacher.prefixes=new[]{"time_embedding","conv_in","down_blocks.0","down_blocks.1","down_blocks.2","down_blocks.3","mid_block","up_blocks.0","up_blocks.1","up_blocks.2","up_blocks.3","conv_norm_out","decoder"};teacher.cards=new Image[labels.Length];
        var font=AssetDatabase.LoadAssetAtPath<Font>("Assets/DiffusionLab/Localization/FontSources/IBMPlexMono-Regular.ttf");
        for(int i=0;i<labels.Length;i++){
            var card=Rect(root.transform,labels[i],-272+(i%5)*136,48-(i/5)*48,128,40,typeof(Image));teacher.cards[i]=card.GetComponent<Image>();teacher.cards[i].color=new Color(.06f,.12f,.17f);teacher.cards[i].raycastTarget=false;
            var text=Rect(card.transform,"Label",0,0,123,37,typeof(Text)).GetComponent<Text>();text.font=font;text.text=labels[i];text.fontSize=17;text.color=new Color(.8f,.94f,1);text.alignment=TextAnchor.MiddleCenter;text.raycastTarget=false;
        }
        var theory=Rect(canvas,"SDPrinciples",-390,-221,740,112,typeof(Text)).GetComponent<Text>();theory.font=font;theory.fontSize=16;theory.alignment=TextAnchor.MiddleLeft;theory.color=new Color(.77f,.88f,.94f);theory.raycastTarget=false;
        theory.text="Noise and image share one latent space.\nU-Net predicts noise conditioned on CLIP.\nCFG combines prompt and negative predictions.\nDDIM removes noise step by step.\nVAE maps 4 latent channels into RGB.\nINT8 bytes + INT32 MAC; norms and softmax use float.";
        var floating=canvas.Find("RealMemory").GetComponent<RectTransform>();floating.anchoredPosition=new Vector2(185,385);floating.sizeDelta=new Vector2(300,300);
        string shared="Assets/DiffusionLab/SD/Shared/";
        c.quantDebugMemory=AssetDatabase.LoadAssetAtPath<RenderTexture>(shared+"QuantMemoryView.renderTexture");if(c.quantDebugMemory==null){c.quantDebugMemory=new RenderTexture(320,320,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);AssetDatabase.CreateAsset(c.quantDebugMemory,shared+"QuantMemoryView.renderTexture");}
        c.quantCaptureMaterial=AssetDatabase.LoadAssetAtPath<Material>(shared+"QuantMemoryCapture.mat");if(c.quantCaptureMaterial==null){c.quantCaptureMaterial=new Material(Shader.Find("DiffusionLab/SDMemoryDisplay"));AssetDatabase.CreateAsset(c.quantCaptureMaterial,shared+"QuantMemoryCapture.mat");}
        var q=Rect(canvas,"INT8Memory",535,385,300,300,typeof(RawImage)).GetComponent<RawImage>();q.texture=c.quantDebugMemory;q.raycastTarget=false;
        foreach(var pair in new[]{new {x=185f,text="FP16 OUTPUT"},new {x=535f,text="ACTUAL INT8 INPUT"}}){var t=Rect(canvas,pair.text,pair.x,560,330,38,typeof(Text)).GetComponent<Text>();t.font=font;t.fontSize=19;t.text=pair.text;t.alignment=TextAnchor.MiddleCenter;t.color=new Color(.8f,.94f,1);t.raycastTarget=false;}
        c.modelLicense=AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/DiffusionLab/SD/ModelLicense.txt");
        UdonSharpEditorUtility.CopyProxyToUdon(teacher);UdonSharpEditorUtility.CopyProxyToUdon(c);EditorUtility.SetDirty(teacher);EditorUtility.SetDirty(c);AssetDatabase.SaveAssets();var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);return new{cards=13,actualExecutorDrivesHighlight=true};
    }
}
