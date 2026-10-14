using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDGraphExecutor : UdonSharpBehaviour {
    public SDGraphData graph;
    public Texture latent,condition,timeEmbedding;
    public bool finished=true;
    public int node,substage,tileOffset,keyOffset,blitCount;
    public int lastCompletedNode=-1;
    public RenderTexture lastMemory;
    private Material material;
    private RenderTexture convolutionPrevious,attStatsPrevious,attAccumPrevious;
    private int currentChunks;
    private Texture Resolve(int id) {
        if(id==-1)return latent;if(id==-2)return condition;if(id==-3)return timeEmbedding;
        if(id<0)return null;return graph.memory[graph.memoryIndex[id]];
    }
    private int Shift(int id) {
        if(id==-1)return 6; // Latent external textures are padded to 64 texels wide.
        if(id==-2)return 10;
        if(id==-3)return 7;
        if(id<0)return 0;return graph.shifts[id];
    }
    public void Begin() {node=0;substage=0;tileOffset=0;keyOffset=0;finished=false;lastCompletedNode=-1;blitCount=0;}
    private void BindNode() {
        material=graph.operators[node];
        if(graph.op[node]==0){material.SetInt("_WeightHeight",material.GetTexture("_Weights").height);material.SetInt("_WeightLayout",graph.weightLayout);}
        material.SetTexture("_A",Resolve(graph.inputA[node]));material.SetInt("_AShift",Shift(graph.inputA[node]));
        material.SetTexture("_B",Resolve(graph.inputB[node]));material.SetInt("_BShift",Shift(graph.inputB[node]));
        material.SetTexture("_C",Resolve(graph.inputC[node]));material.SetInt("_CShift",Shift(graph.inputC[node]));
        material.SetInt("_OutputShift",graph.shifts[node]);material.SetTexture("_Partial",graph.partial);material.SetInt("_PartialShift",graph.partialShift);
        material.SetInt("_OutCount",graph.counts[node]);
    }
    private void Draw(RenderTexture target,int pass,int shift) {
        material.SetInt("_OutputShift",shift);VRCGraphics.Blit(null,target,material,pass);blitCount++;
    }
    private void Done() {
        lastCompletedNode=node;lastMemory=graph.memory[graph.memoryIndex[node]];node++;substage=0;tileOffset=0;keyOffset=0;
        if(node>=graph.operators.Length)finished=true;
    }
    public void Compute() {
        if(finished||graph==null)return;
        if(substage==0&&tileOffset==0&&keyOffset==0)BindNode();int op=graph.op[node];RenderTexture output=graph.memory[graph.memoryIndex[node]];
        if(op==0){
            if(substage==0){
                int count=graph.inH[node]*graph.inW[node]*((graph.inChannels[node]+3)/4);
                currentChunks=(count+255)/256;material.SetInt("_Count",count);material.SetInt("_Chunks",currentChunks);
                Draw(graph.partial,0,graph.partialShift);substage=1;return;
            }
            if(substage==1){material.SetInt("_Chunks",currentChunks);Draw(graph.scale,1,0);substage=2;return;}
            material.SetTexture("_Stats",graph.scale);material.SetInt("_StatsShift",0);
            if(substage==2){Draw(graph.quantized,2,graph.quantizedShift);substage=3;convolutionPrevious=output;return;}
            material.SetTexture("_Packed",graph.quantized);material.SetInt("_PackedShift",graph.quantizedShift);
            material.SetInt("_Offset",tileOffset);material.SetInt("_TileCount",8192);
            if(substage==3){Draw(graph.tile,3,graph.tileShift);substage=4;return;}
            if(substage==4){
                RenderTexture target=convolutionPrevious==output?graph.scratch:output;
                int previousShift=convolutionPrevious==output?graph.shifts[node]:graph.scratchShift;
                int targetShift=target==output?graph.shifts[node]:graph.scratchShift;
                material.SetTexture("_Previous",convolutionPrevious);material.SetInt("_PreviousShift",previousShift);
                material.SetTexture("_Tile",graph.tile);material.SetInt("_TileShift",graph.tileShift);
                Draw(target,4,targetShift);convolutionPrevious=target;tileOffset+=8192;
                if(tileOffset<graph.counts[node])substage=3;
                else if(target==output)Done();else substage=5;
                return;
            }
            material.SetTexture("_Previous",convolutionPrevious);material.SetInt("_PreviousShift",graph.scratchShift);material.SetInt("_TileCount",0);Draw(output,4,graph.shifts[node]);Done();return;
        }
        if(op==1){
            int channels=graph.inChannels[node],g=graph.groups[node];
            int length=g==0?channels:graph.inH[node]*graph.inW[node]*channels/g;
            currentChunks=(length+255)/256;material.SetInt("_Chunks",currentChunks);
            if(substage==0){Draw(graph.partial,5,graph.partialShift);substage=1;return;}
            if(substage==1){Draw(graph.stats,6,graph.statsShift);substage=2;return;}
            material.SetTexture("_Stats",graph.stats);material.SetInt("_StatsShift",graph.statsShift);
            Draw(output,7,graph.shifts[node]);Done();return;
        }
        if(op==6){
            material.SetInt("_KeyStart",keyOffset);material.SetInt("_KeyBlock",64);material.SetInt("_FirstBlock",keyOffset==0?1:0);
            material.SetTexture("_Scores",graph.attentionScores);material.SetInt("_ScoreShift",graph.scoreShift);
            if(substage==0){Draw(graph.attentionScores,9,graph.scoreShift);substage=1;return;}
            if(substage==1){
                RenderTexture target=attStatsPrevious==graph.attentionStatsA?graph.attentionStatsB:graph.attentionStatsA;
                material.SetTexture("_OldStats",attStatsPrevious);material.SetInt("_OldStatsShift",graph.attentionStatsShift);
                Draw(target,10,graph.attentionStatsShift);attStatsPrevious=target;substage=2;return;
            }
            material.SetTexture("_Stats",attStatsPrevious);material.SetInt("_StatsShift",graph.attentionStatsShift);
            if(substage==2){
                RenderTexture target=attAccumPrevious==graph.attentionAccumA?graph.attentionAccumB:graph.attentionAccumA;
                material.SetTexture("_Previous",attAccumPrevious);material.SetInt("_PreviousShift",graph.attentionAccumShift);
                Draw(target,11,graph.attentionAccumShift);attAccumPrevious=target;keyOffset+=64;
                substage=keyOffset<graph.keyRows[node]?0:3;return;
            }
            material.SetTexture("_A",attAccumPrevious);material.SetInt("_AShift",graph.attentionAccumShift);
            Draw(output,12,graph.shifts[node]);Done();return;
        }
        Draw(output,8,graph.shifts[node]);Done();
    }
    public RenderTexture Output() {return graph.memory[graph.memoryIndex[graph.outputNode]];}
}
