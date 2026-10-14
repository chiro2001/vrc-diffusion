Shader "DiffusionLab/SDMemoryDisplay" {
 Properties { _MainTex("NHWC4 tensor",2D)="black"{} _Shift("Tensor row shift",Int)=10 _H("Logical height",Int)=1 _W("Logical width",Int)=1 _Channels("Logical channels",Int)=4 _FirstChannel("First displayed channel",Int)=0 _Range("Signed range",Float)=3 _Packed("Encoded INT8",Int)=0 }
 SubShader { Tags { "RenderType"="Opaque" } Cull Off
 Pass { CGPROGRAM
 #pragma target 5.0
 #pragma vertex vert_img
 #pragma fragment frag
 #include "UnityCG.cginc"
 Texture2D<float4> _MainTex;int _Shift,_H,_W,_Channels,_FirstChannel,_Packed;float _Range;
 float4 frag(v2f_img i):SV_Target {
  float2 grid=float2(i.uv.x,1-i.uv.y)*4;int ch=_FirstChannel+(int)floor(grid.x)+(int)floor(grid.y)*4;
  if(ch>=_Channels)return float4(.03,.04,.06,1);
  uint2 p=min((uint2)(frac(grid)*float2(_W,_H)),uint2(_W-1,_H-1));uint at=(p.y*_W+p.x)*((_Channels+3)/4)+(ch>>2);
  float v=_MainTex.Load(int3(at&((1u<<_Shift)-1u),at>>_Shift,0))[ch&3];
  if(_Packed!=0)v=(floor(v*255+.5)-128)/127.;else v/=max(_Range,1e-8);
  return float4(v>=0?lerp(float3(.025,.035,.05),float3(1,.59,.15),saturate(v)):lerp(float3(.025,.035,.05),float3(.15,.68,1),saturate(-v)),1);
 }
 ENDCG }
 }
}
