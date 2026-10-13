Shader "DiffusionLab/Convolution" {
 Properties { _Input("Input",2D)="black"{} _Skip("Skip",2D)="black"{} _Weights("Weights",2D)="black"{} _InSize("InSize",Float)=32 _InChannels("InChannels",Float)=1 _PrimaryChannels("PrimaryChannels",Float)=1 _OutSize("OutSize",Float)=32 _OutChannels("OutChannels",Float)=16 _Stride("Stride",Float)=1 _Upsample("Upsample",Float)=1 _WeightOffset("WeightOffset",Float)=0 _BiasOffset("BiasOffset",Float)=0 _ConditionOffset("ConditionOffset",Float)=-1 _WeightWidth("WeightWidth",Float)=1024 _Timesteps("Timesteps",Float)=256 _Class("Class",Float)=3 _Timestep("Timestep",Float)=128 _Activation("Activation",Float)=1 }
 SubShader { Cull Off ZWrite Off ZTest Always
 Pass { CGPROGRAM
 #pragma vertex vert_img
 #pragma fragment frag
 #pragma target 5.0
 #include "UnityCG.cginc"
 Texture2D<float> _Input,_Skip,_Weights;
 int _InSize,_InChannels,_PrimaryChannels,_OutSize,_OutChannels,_Stride,_Upsample;
 int _WeightOffset,_BiasOffset,_ConditionOffset,_WeightWidth,_Timesteps,_Class,_Timestep,_Activation;
 float weight(int index) { return _Weights.Load(int3(index%_WeightWidth,index/_WeightWidth,0)); }
 float primary(int2 p,int c) {
   // Padding is applied in the upsampled domain, as in interpolate -> Conv2d.
   int domain=_InSize*_Upsample;
   if(any(p<0)||any(p>=domain)) return 0;
   p/=_Upsample;
   return _Input.Load(int3(p.x+c*_InSize,p.y,0));
 }
 float secondary(int2 p,int c) {
   if(any(p<0)||any(p>=_OutSize)) return 0;
   return _Skip.Load(int3(p.x+c*_OutSize,p.y,0));
 }
 float4 frag(v2f_img i):SV_Target {
   int2 pos=int2(i.pos.xy);
   int oc=pos.x/_OutSize;
   int2 xy=int2(pos.x%_OutSize,pos.y);
   float sum=weight(_BiasOffset+oc);
   [loop] for(int ic=0;ic<_InChannels;ic++) {
     [unroll] for(int ky=0;ky<3;ky++) {
       [unroll] for(int kx=0;kx<3;kx++) {
         int2 p=xy*_Stride+int2(kx-1,ky-1);
         float v=ic<_PrimaryChannels?primary(p,ic):secondary(p,ic-_PrimaryChannels);
         int w=_WeightOffset+((oc*_InChannels+ic)*3+ky)*3+kx;
         sum+=v*weight(w);
       }
     }
   }
   if(_ConditionOffset>=0) sum+=weight(_ConditionOffset+(_Class*_Timesteps+_Timestep)*_OutChannels+oc);
   if(_Activation!=0) sum=sum/(1+exp(-sum));
   return float4(sum,0,0,1);
 }
 ENDCG }
 }
}
