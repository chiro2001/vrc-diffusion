using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDPerformance : UdonSharpBehaviour {
    public SDController controller;
    public Text settingsText,progressText;
    public Image progressFill;
    public bool exportEnabled=true;
    public float progress,passesPerSecond;
    private float nextRefresh,lastSample;
    private int lastPasses;
    public void ResetProgress(){progress=0;passesPerSecond=0;lastSample=Time.realtimeSinceStartup;lastPasses=controller.executor.totalBlits;}
    public void ToggleExport(){exportEnabled=!exportEnabled;if(controller.photoExport!=null)controller.photoExport.SetFloat("_Enabled",exportEnabled?1:0);Refresh();}
    private void Update(){if(Time.realtimeSinceStartup>=nextRefresh){nextRefresh=Time.realtimeSinceStartup+.5f;Refresh();}}
    public void Refresh(){
        var c=controller;var e=c.executor;
        float now=Time.realtimeSinceStartup,dt=now-lastSample;int delta=e.totalBlits-lastPasses;
        if(dt>.1f){float instant=delta/dt;passesPerSecond=delta>0?Mathf.Lerp(passesPerSecond,instant,.4f):0;lastSample=now;lastPasses=e.totalBlits;}
        int stage=c.algorithmStage;
        float graph=e.graph==null?0:Mathf.Clamp01(e.blitCount/(float)Mathf.Max(1,e.graph.estimatedBlits));
        int effective=Mathf.Max(1,c.totalSteps-c.firstStep),done=Mathf.Max(0,c.localStep-c.firstStep);
        float fraction=stage==1?graph*.45f:stage==2?.45f+graph*.45f:stage==3?.9f:stage==4?.95f:0;
        float value=c.phase==0?0:stage==6?1:stage==7?.025f*graph:stage==9?.025f+.025f*graph:stage==10?.049f:stage==5&&c.localStep>=c.totalSteps?.95f+.05f*graph:.05f+.9f*Mathf.Clamp01((done+fraction)/effective);
        progress=c.phase==0?0:Mathf.Max(progress,value);
        if(progressFill!=null)progressFill.fillAmount=progress;
        float rate=c.activeSeconds>0?done/c.activeSeconds:0;
        if(progressText!=null)progressText.text="PROGRESS "+(progress*100).ToString("F1")+"% | "+done+" / "+effective+" denoise steps\n"+
            "Active "+c.activeSeconds.ToString("F1")+" s | "+rate.ToString("F3")+" steps/s | "+(rate>0?(1/rate).ToString("F1"):"--")+" s/step | "+passesPerSecond.ToString("F0")+" Blits submitted/s\n"+
            "Stage: "+(stage==7?"CLIP text encoding":stage==9?"VAE photo encoding":stage==10?"Photo synchronization":stage==5?"VAE decoding":stage==6?"Complete":"Denoising")+" | estimate by completed work";
        if(settingsText!=null)settingsText.text="LOCAL PERFORMANCE / PREVIEWS\n"+
            "Tensor memory + network highlight: "+(c.details?"ON":"OFF")+" | Intermediate VAE previews: "+(c.vaePreviews?"ON":"OFF")+"\n"+
            "Local inference: "+(c.localInference?"ON":"OFF")+" | Camera export: "+(exportEnabled?"ON":"OFF")+"\n"+
            "Submission: up to "+c.passesPerFrame+" passes/frame | "+c.frameBudgetMs.ToString("F1")+" ms CPU/frame\n"+
            "PREVIEWS COST PERFORMANCE. Memory capture adds GPU passes; each VAE preview runs a full decoder.\n"+
            "For fastest generation turn BOTH previews OFF. Final VAE decoding is always required.\n"+
            "PHOTO INIT: "+(c.usePhoto?"ON":"OFF")+" | Denoise strength "+c.imageStrength.ToString("F2")+" | "+(c.customPrompt?"CUSTOM TEXT":"PRESET")+"\n"+
            (c.inputs==null?"":c.inputs.notice);
    }
}
