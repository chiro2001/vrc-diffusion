using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

// Fonts are bound in the editor: switching locales is local and needs no font mutation in Udon.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DiffusionLocalization : UdonSharpBehaviour {
    public int languageIndex=1;
    public int languageOverride=-1;
    public Text[] sources;
    public TextMeshProUGUI[] english,chinese,japanese;
    public string[] englishPhrases,chinesePhrases,japanesePhrases;
    public Image[] languageButtons;
    private float nextSync;
    private void Start() { SetLanguage(); }
    private int Resolve(string lang) {
        if(lang==null) return 0;
        if(lang.StartsWith("zh")) return 1;
        if(lang.StartsWith("ja")) return 2;
        return 0;
    }
    public override void OnLanguageChanged(string language) {
        if(languageOverride<0) {languageIndex=Resolve(language);Refresh();}
    }
    public void SetLanguage() {
        languageIndex=languageOverride<0?Resolve(VRCPlayerApi.GetCurrentLanguage()):Mathf.Clamp(languageOverride,0,2);
        Refresh();
    }
    private void Update() {if(Time.time>=nextSync) {nextSync=Time.time+.2f;Refresh();}}
    public string Translate(string value) {
        if(languageIndex==0||value==null) return value;
        string[] translated=languageIndex==1?chinesePhrases:japanesePhrases;
        for(int i=0;i<englishPhrases.Length;i++) value=value.Replace(englishPhrases[i],translated[i]);
        return value;
    }
    public void Refresh() {
        if(sources==null) return;
        for(int i=0;i<sources.Length;i++) {
            english[i].gameObject.SetActive(languageIndex==0);
            chinese[i].gameObject.SetActive(languageIndex==1);
            japanese[i].gameObject.SetActive(languageIndex==2);
            var target=languageIndex==0?english[i]:languageIndex==1?chinese[i]:japanese[i];
            string text=Translate(sources[i].text);
            if(target.text!=text) target.text=text;
        }
        if(languageButtons!=null) for(int i=0;i<languageButtons.Length;i++)
            languageButtons[i].color=(i-1==languageOverride)?new Color(.08f,.46f,.46f):new Color(.07f,.19f,.26f);
    }
}
