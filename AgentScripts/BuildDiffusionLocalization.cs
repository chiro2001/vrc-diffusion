using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TMPro;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Object=UnityEngine.Object;

public static class BuildDiffusionLocalization {
    const string Root="Assets/DiffusionLab/Localization";
    static void Folder(string p){if(AssetDatabase.IsValidFolder(p))return;int i=p.LastIndexOf('/');Folder(p.Substring(0,i));AssetDatabase.CreateFolder(p.Substring(0,i),p.Substring(i+1));}
    static TMP_FontAsset Font(string name,string source,string chars,TMP_FontAsset fallback=null) {
        string path=Root+"/Fonts/"+name+".asset";
        var f=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if(f==null) {
            var sf=AssetDatabase.LoadAssetAtPath<UnityEngine.Font>(Root+"/FontSources/"+source);
            f=TMP_FontAsset.CreateFontAsset(sf,40,6,GlyphRenderMode.SDFAA,2048,2048,AtlasPopulationMode.Dynamic,false);
            f.name=name;AssetDatabase.CreateAsset(f,path);
        }
        f.ReadFontAssetDefinition();
        string pending=new string(chars.Where(c=>!f.HasCharacter(c)).Distinct().ToArray());
        f.atlasPopulationMode=AtlasPopulationMode.Dynamic;
        f.TryAddCharacters(pending,out string missing);
        string unresolved=new string((missing??"").Where(c=>c!='\n'&&c!='\r'&&c!='\t'&&(fallback==null||!fallback.HasCharacter(c))).ToArray());
        if(unresolved.Length>0)throw new Exception("Missing font glyphs: "+name+" "+unresolved);
        f.atlasPopulationMode=AtlasPopulationMode.Static;
        if(fallback!=null)f.fallbackFontAssetTable=new System.Collections.Generic.List<TMP_FontAsset>{fallback};
        if(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(f.material)))AssetDatabase.AddObjectToAsset(f.material,path);
        foreach(var tex in f.atlasTextures)if(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(tex)))AssetDatabase.AddObjectToAsset(tex,path);
        EditorUtility.SetDirty(f);return f;
    }
    static TextMeshProUGUI Overlay(Text source,string name,TMP_FontAsset font,string text,bool active) {
        var old=source.transform.Find(name);if(old!=null)Object.DestroyImmediate(old.gameObject);
        var g=new GameObject(name,typeof(RectTransform),typeof(CanvasRenderer),typeof(TextMeshProUGUI));g.transform.SetParent(source.transform,false);g.layer=source.gameObject.layer;
        var rt=g.GetComponent<RectTransform>();rt.anchorMin=Vector2.zero;rt.anchorMax=Vector2.one;rt.offsetMin=rt.offsetMax=Vector2.zero;
        var tmp=g.GetComponent<TextMeshProUGUI>();tmp.font=font;tmp.text=text;tmp.color=source.color;tmp.fontSize=source.fontSize;
        tmp.alignment=source.alignment==TextAnchor.MiddleLeft?TextAlignmentOptions.MidlineLeft:TextAlignmentOptions.Center;tmp.raycastTarget=false;tmp.enableWordWrapping=true;
        tmp.enableAutoSizing=source.resizeTextForBestFit;tmp.fontSizeMin=source.resizeTextMinSize;tmp.fontSizeMax=source.fontSize;
        tmp.overflowMode=TextOverflowModes.Truncate;g.SetActive(active);source.enabled=false;return tmp;
    }
    public static async Task<object> Apply() {
        if(EditorApplication.isPlaying||UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)throw new Exception("Use saved scene in EditMode");
        if(Shader.Find("TextMeshPro/Distance Field")==null) {
            var pkg=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMP_FontAsset).Assembly);
            AssetDatabase.ImportPackage(Path.Combine(pkg.resolvedPath,"Package Resources/TMP Essential Resources.unitypackage"),false);
            var deadline=DateTime.UtcNow.AddSeconds(35);
            while(Shader.Find("TextMeshPro/Distance Field")==null&&DateTime.UtcNow<deadline)await Task.Delay(100);
            if(Shader.Find("TextMeshPro/Distance Field")==null)throw new Exception("TMP resource import incomplete");
        }
        Folder(Root+"/Fonts");
        var locale=Object.FindObjectOfType<DiffusionLocalization>();
        var phrases=JArray.Parse(File.ReadAllText(Root+"/catalog.json"));
        if(File.Exists(Root+"/teaching.json"))foreach(var page in JArray.Parse(File.ReadAllText(Root+"/teaching.json"))){
            phrases.Add(page["title"].DeepClone());phrases.Add(page["body"].DeepClone());
            foreach(var entry in ((JObject)page["phrases"]).Properties())phrases.Add(entry.Value.DeepClone());
        }
        phrases=new JArray(phrases.OrderByDescending(v=>((string)v[0]).Length));
        locale.englishPhrases=phrases.Select(v=>(string)v[0]).ToArray();
        locale.chinesePhrases=phrases.Select(v=>(string)v[1]).ToArray();
        locale.japanesePhrases=phrases.Select(v=>(string)v[2]).ToArray();
        var texts=locale.GetComponentsInChildren<Text>(true).Where(t=>t.GetComponentInParent<InputField>()==null).ToArray();
        string common=new string(Enumerable.Range(32,95).Select(i=>(char)i).ToArray())+"×±·—–简体中文日本語"+string.Join("",texts.Select(t=>t.text));
        string allGlyphs=common+string.Join("",locale.englishPhrases)+string.Join("",locale.chinesePhrases)+string.Join("",locale.japanesePhrases);
        var math=Font("Math-SDF","NotoSansMath-Regular.ttf","αεη̄√−→±×");
        var zh=Font("Chinese-SDF","NotoSansSC-Regular.ttf",allGlyphs,math);
        var en=Font("English-SDF","NotoSansJP-Regular.ttf",common+string.Join("",locale.englishPhrases),zh);
        var ja=Font("Japanese-SDF","NotoSansJP-Regular.ttf",common+string.Join("",locale.japanesePhrases),zh);
        locale.sources=texts;locale.english=new TextMeshProUGUI[texts.Length];locale.chinese=new TextMeshProUGUI[texts.Length];locale.japanese=new TextMeshProUGUI[texts.Length];
        for(int i=0;i<texts.Length;i++) {
            locale.english[i]=Overlay(texts[i],"English",en,texts[i].text,locale.languageIndex==0);
            string cn=texts[i].text,jp=cn;
            for(int j=0;j<locale.englishPhrases.Length;j++){cn=cn.Replace(locale.englishPhrases[j],locale.chinesePhrases[j]);jp=jp.Replace(locale.englishPhrases[j],locale.japanesePhrases[j]);}
            locale.chinese[i]=Overlay(texts[i],"Chinese",zh,cn,locale.languageIndex==1);
            locale.japanese[i]=Overlay(texts[i],"Japanese",ja,jp,locale.languageIndex==2);
        }
        UdonSharpEditorUtility.CopyProxyToUdon(locale);EditorUtility.SetDirty(locale);AssetDatabase.SaveAssets();
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        return new{localized=texts.Length,languages=3,staticFonts=true};
    }
}
