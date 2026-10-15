using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDTeaching : UdonSharpBehaviour {
    public SDController controller;
    public Image[] cards;
    public string[] prefixes;
    private float nextUpdate;
    private int lastNode=-1,lastStage=-1;
    private void Update(){
        if(Time.realtimeSinceStartup<nextUpdate)return;nextUpdate=Time.realtimeSinceStartup+.2f;
        int stage=controller.algorithmStage;var executor=controller.executor;
        if(!controller.details||!controller.localInference||controller.phase==2||executor.graph==null){for(int i=0;i<cards.Length;i++)cards[i].color=new Color(.06f,.12f,.17f);lastNode=-1;return;}
        int node=Mathf.Clamp(executor.node,0,executor.graph.nodeNames.Length-1);
        if(node==lastNode&&stage==lastStage)return;lastNode=node;lastStage=stage;
        string name=executor.graph.nodeNames[node];bool vae=stage==5||stage==9;
        for(int i=0;i<cards.Length;i++){
            bool active=(stage>0&&stage<6||stage==9)&&(i==12?vae:(!vae&&name.StartsWith(prefixes[i])));
            cards[i].color=active?new Color(.03f,.53f,.6f):new Color(.06f,.12f,.17f);
        }
    }
}
