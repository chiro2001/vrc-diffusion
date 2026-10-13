Shader "DiffusionLab/Sampling" {
 Properties { _State("State",2D)="black"{} _Noise("Predicted noise",2D)="black"{} _SeedLo("Seed low 16",Float)=1234 _SeedHi("Seed high 16",Float)=0 _Mode("Mode",Float)=0 _Alpha("Alpha",Float)=1 _NextAlpha("Next alpha",Float)=1 _Guidance("Guidance",Float)=1 _UnconditionedNoise("Unconditioned noise",2D)="black"{} }
 SubShader { Cull Off ZWrite Off ZTest Always
 Pass { CGPROGRAM
 #pragma vertex vert_img
 #pragma fragment frag
 #pragma target 5.0
 #include "UnityCG.cginc"
 Texture2D<float> _State,_Noise,_UnconditionedNoise;
 int _SeedLo,_SeedHi,_Mode;
 float _Alpha,_NextAlpha,_Guidance;
 uint hash(uint x) { x^=x>>16;x*=0x7feb352du;x^=x>>15;x*=0x846ca68bu;return x^(x>>16); }
 float randomUniform(uint x) { return ((hash(x)&0xffffffu)+1.0)/16777217.0; }
 float4 frag(v2f_img i):SV_Target {
   int2 p=int2(i.pos.xy);
   if(_Mode==0) {
     uint fullSeed=(uint)_SeedLo|((uint)_SeedHi<<16);
     uint key=fullSeed^((uint)p.x*0x9e3779b9u)^((uint)p.y*0x85ebca6bu);
     float n=sqrt(-2*log(randomUniform(key)))*cos(6.28318530718*randomUniform(key^0x68bc21ebu));
     return float4(n,0,0,1);
   }
   if(_Mode==3) return 0;
   float x=_State.Load(int3(p,0));
   float eps=_Noise.Load(int3(p,0));
   if(_Guidance>1.0001) {
     float base=_UnconditionedNoise.Load(int3(p,0));
     eps=base+_Guidance*(eps-base);
   }
   if(_Mode==5) return float4(eps,0,0,1);
   float x0=clamp((x-sqrt(1-_Alpha)*eps)/sqrt(_Alpha),-1,1);
   if(_Mode==2) return float4(x0,0,0,1);
   float corrected=(x-sqrt(_Alpha)*x0)/sqrt(1-_Alpha);
   float next=sqrt(_NextAlpha)*x0+sqrt(1-_NextAlpha)*corrected;
   return float4(next,0,0,1);
 }
 ENDCG }
 }
}
