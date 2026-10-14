using System.Linq;
using UnityEngine;
using Object=UnityEngine.Object;
public static class AuditSDMemory {
    static RenderTexture[] Textures(SDGraphData g){return g.memory.Concat(new[]{g.quantized,g.partial,g.stats,g.scale,g.tile,g.scratch,g.attentionScores,g.attentionStatsA,g.attentionStatsB,g.attentionAccumA,g.attentionAccumB}).Distinct().ToArray();}
    static long Bytes(RenderTexture t){return (long)t.width*t.height*(t.format==RenderTextureFormat.ARGBFloat?16:t.format==RenderTextureFormat.ARGBHalf?8:4);}
    public static object Report(){var c=Object.FindObjectOfType<SDController>();return c.unetGraphs.Select((g,i)=>new{size=g.nativeSize,unetBytes=Textures(g).Sum(Bytes),vaeBytes=Textures(c.vaeGraphs[i]).Sum(Bytes),createdBytes=Textures(g).Concat(Textures(c.vaeGraphs[i])).Where(t=>t.IsCreated()).Sum(Bytes)}).ToArray();}
}
