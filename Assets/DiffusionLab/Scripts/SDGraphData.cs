using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDGraphData : UdonSharpBehaviour {
    public string displayName;
    public int nativeSize,weightLayout=1,externalShift=6,estimatedBlits=1;
    public bool floatActivation;
    public Material[] operators;
    public string[] nodeNames;
    public int[] op,inputA,inputB,inputC,memoryIndex,shifts,counts,inH,inW,inChannels,outChannels,groups,heads,keyRows;
    public RenderTexture[] memory;
    public int[] memoryShifts;
    public RenderTexture quantized,partial,stats,scale,tile,scratch,attentionScores,attentionStatsA,attentionStatsB,attentionAccumA,attentionAccumB;
    public int quantizedShift,partialShift,statsShift,tileShift,scratchShift,scoreShift,attentionStatsShift,attentionAccumShift;
    public int outputNode;
    public void ReleaseMemory() {
        for(int i=0;i<memory.Length;i++)memory[i].Release();
        quantized.Release();partial.Release();stats.Release();scale.Release();tile.Release();scratch.Release();
        attentionScores.Release();attentionStatsA.Release();attentionStatsB.Release();attentionAccumA.Release();attentionAccumB.Release();
    }
}
