Shader "DiffusionLab/SDSampler" {
 Properties {
  _State("Latent state",2D)="black"{} _Conditional("Conditional epsilon",2D)="black"{} _Unconditional("Unconditional epsilon",2D)="black"{}
  _RGB("Decoded VAE",2D)="black"{} _Mode("0 noise 1 CFG 2 x0 3 DDIM 4 latent preview 5 RGB",Int)=0
  _SeedLo("Seed lower 16",Int)=1234 _SeedHi("Seed upper 16",Int)=0 _LatentSize("Native latent size",Int)=32
  _Alpha("Alpha cumulative",Float)=1 _NextAlpha("Previous alpha cumulative",Float)=1 _Guidance("CFG scale",Float)=7.5
  _Timestep("Training timestep",Int)=1 _RGBShift("RGB memory row shift",Int)=10 _RGBSize("RGB size",Int)=256
  _SourceImage("Camera photograph",2D)="black"{} _SourceLatent("Photo latent",2D)="black"{} _SourceAspect("Photograph aspect",Float)=1
 }
 SubShader { Cull Off ZWrite Off ZTest Always
 Pass { CGPROGRAM
 #pragma target 5.0
 #pragma vertex vert_img
 #pragma fragment frag
 #include "UnityCG.cginc"
 Texture2D<float4> _State,_Conditional,_Unconditional,_RGB;
 sampler2D _SourceImage;Texture2D<float4> _SourceLatent;float _SourceAspect;
 int _Mode,_SeedLo,_SeedHi,_LatentSize,_RGBShift,_RGBSize,_Timestep;float _Alpha,_NextAlpha,_Guidance;
 uint hash32(uint a){a^=a>>16;a*=0x7feb352d;a^=a>>15;a*=0x846ca68b;a^=a>>16;return a;}
 float normal(uint id){uint seed=(uint)_SeedLo|((uint)_SeedHi<<16);float u=((hash32(seed^hash32(id*2+0x9e3779b9))>>8)+.5)/16777216.;float v=((hash32(seed^hash32(id*2+1+0x9e3779b9))>>8)+.5)/16777216.;return sqrt(-2*log(max(u,1e-8)))*cos(6.28318530718*v);}
 int3 latentAddr(uint index){return int3(index&63u,index>>6,0);}
 float4 frag(v2f_img i):SV_Target {
  uint2 p=(uint2)i.pos.xy;uint at=p.y*64+p.x;
  if(_Mode==10){uint index=p.y*1024+p.x;return _RGB.Load(int3(index&((1u<<_RGBShift)-1u),index>>_RGBShift,0));}
  if(_Mode==11||_Mode==12){
   uint index=p.y*1024+p.x;uint2 xy=uint2(index%_RGBSize,index/_RGBSize);
   float2 uv=_Mode==12?i.uv:float2((xy.x+.5)/_RGBSize,1-(xy.y+.5)/_RGBSize);
   if(_SourceAspect>1)uv.x=(uv.x-.5)/_SourceAspect+.5;else uv.y=(uv.y-.5)*_SourceAspect+.5;
   float3 rgb=tex2D(_SourceImage,uv).rgb;
   if(_Mode==12)return float4(rgb,1);
   #ifndef UNITY_COLORSPACE_GAMMA
   rgb=LinearToGammaSpace(rgb); // The VAE was trained on sRGB image values.
   #endif
   return index<(uint)(_RGBSize*_RGBSize)?float4(rgb*2-1,0):0;
  }
  if(_Mode==8)return float4(0,0,0,1);
  if(_Mode==6){float4 v=0;[unroll]for(int j=0;j<4;j++){int c=(int)p.x*4+j;float f=exp(-9.21034037197618*(c%160)/160.0)*_Timestep;v[j]=c<320?(c<160?cos(f):sin(f)):0;}return f16tof32(f32tof16(v));}
  if(_Mode==7){if(at>=(uint)(_LatentSize*_LatentSize))return 0;return _RGB.Load(int3(at&((1u<<_RGBShift)-1u),at>>_RGBShift,0));}
  if(_Mode==9){if(at>=(uint)(_LatentSize*_LatentSize))return 0;return sqrt(_Alpha)*_SourceLatent.Load(latentAddr(at))+sqrt(1-_Alpha)*float4(normal(at*4),normal(at*4+1),normal(at*4+2),normal(at*4+3));}
  if(_Mode>=4){
   uint2 xy=min((uint2)(float2(i.uv.x,1-i.uv.y)*_RGBSize),(uint)(_RGBSize-1));
   if(_Mode==5){uint index=xy.y*_RGBSize+xy.x;float3 v=saturate(_RGB.Load(int3(index&((1u<<_RGBShift)-1u),index>>_RGBShift,0)).rgb*.5+.5);
    #ifndef UNITY_COLORSPACE_GAMMA
    v=GammaToLinearSpace(v); // Display RTs are linear; the camera performs the sRGB transfer.
    #endif
    return float4(v,1);}
   uint2 lp=min((uint2)(float2(i.uv.x,1-i.uv.y)*_LatentSize),(uint)(_LatentSize-1));float4 z=_State.Load(latentAddr(lp.y*_LatentSize+lp.x));
   // A labelled linear latent preview, never presented as VAE decoding.
   float3 rgb=.5+float3(dot(z,float4(.298,.187,-.158,-.184)),dot(z,float4(.207,.286,.189,-.271)),dot(z,float4(.208,.173,.264,-.473)));
   return float4(saturate(rgb),1);
  }
  if(at>=(uint)(_LatentSize*_LatentSize))return 0;
  if(_Mode==0)return float4(normal(at*4),normal(at*4+1),normal(at*4+2),normal(at*4+3));
  float4 s=_State.Load(latentAddr(at)),c=_Conditional.Load(latentAddr(at)),u=_Unconditional.Load(latentAddr(at));
  float4 eps=u+_Guidance*(c-u);if(_Mode==1)return eps;
  float4 x0=(s-sqrt(1-_Alpha)*eps)/sqrt(_Alpha);if(_Mode==2)return x0;
  return sqrt(_NextAlpha)*x0+sqrt(1-_NextAlpha)*eps;
 }
 ENDCG }
 }
}
