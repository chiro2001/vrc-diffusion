Shader "DiffusionLab/MemoryDisplay" {
 Properties { _MainTex("Memory",2D)="black"{} _Channels("Channels",Float)=1 _Range("Signed range",Float)=1 _Signed("Signed colors",Float)=0 _ValueMin("Value min",Float)=-1 _ValueMax("Value max",Float)=1 }
 SubShader { Tags { "Queue"="Geometry" "RenderType"="Opaque" } Cull Off ZWrite On
 Pass { CGPROGRAM
 #pragma vertex vert_img
 #pragma fragment frag
 #pragma target 3.0
 #include "UnityCG.cginc"
 sampler2D _MainTex;
 float _Channels,_Range,_Signed,_ValueMin,_ValueMax;
 float4 frag(v2f_img i):SV_Target {
   float cols=ceil(sqrt(_Channels));
   float rows=ceil(_Channels/cols);
   float2 grid=float2(i.uv.x,1-i.uv.y)*float2(cols,rows);
   float channel=floor(grid.x)+floor(grid.y)*cols;
   if(channel>=_Channels) return float4(.035,.04,.055,1);
   float2 uv=frac(grid);
   // Channel-major horizontal stripes are rearranged for human inspection only.
   uv.x=(channel+uv.x)/_Channels;
   float x=tex2D(_MainTex,uv).r;
   float v=clamp(x/max(_Range,.00001),-1,1);
   if(_Signed>.5) {
     float3 c= v>=0?lerp(float3(.04,.05,.07),float3(1,.57,.12),v):lerp(float3(.04,.05,.07),float3(.12,.65,1),-v);
     return float4(c,1);
   }
   return float4(saturate((x-_ValueMin)/max(_ValueMax-_ValueMin,.00001)).xxx,1);
 }
 ENDCG }
 }
}
