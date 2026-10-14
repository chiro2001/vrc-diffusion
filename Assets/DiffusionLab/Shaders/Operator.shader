Shader "DiffusionLab/Operator" {
 Properties { _Input("Input",2D)="black"{} _Skip("Skip or statistics",2D)="black"{} _Weights("Weights",2D)="black"{}
 _Op("Op",Float)=0
 _InSize("InSize",Float)=28
 _InChannels("InChannels",Float)=1
 _PrimaryChannels("PrimaryChannels",Float)=1
 _OutSize("OutSize",Float)=28
 _OutChannels("OutChannels",Float)=128
 _SkipSize("SkipSize",Float)=28
 _Kernel("Kernel",Float)=3
 _Stride("Stride",Float)=1
 _Padding("Padding",Float)=1
 _WeightOffset("WeightOffset",Float)=0
 _BiasOffset("BiasOffset",Float)=0
 _WeightWidth("WeightWidth",Float)=2048
 _Activation("Activation",Float)=0
 _Groups("Groups",Float)=8
 _Class("Class",Float)=3
 _Timestep("Timestep",Float)=128
 _Timesteps("Timesteps",Float)=401
 _ConditionOffset("ConditionOffset",Float)=-1
 _ScaleOffset("ScaleOffset",Float)=-1
 _ResidualScale("Residual scale",Float)=1 _Epsilon("Epsilon",Float)=0.00001
 }
 SubShader { Cull Off ZWrite Off ZTest Always
 Pass { CGPROGRAM
 #pragma vertex vert_img
 #pragma fragment frag
 #pragma target 5.0
 #include "UnityCG.cginc"
 Texture2D<float> _Input,_Skip,_Weights;
 int _Op,_InSize,_InChannels,_PrimaryChannels,_OutSize,_OutChannels,_SkipSize;
 int _Kernel,_Stride,_Padding,_WeightOffset,_BiasOffset,_WeightWidth;
 int _Activation,_Groups,_Class,_Timestep,_Timesteps,_ConditionOffset,_ScaleOffset;
 float _ResidualScale,_Epsilon;
 float weight(int index) {return _Weights.Load(int3(index%_WeightWidth,index/_WeightWidth,0));}
 float primary(int c,int2 p) {return _Input.Load(int3(c*_InSize+p.x,p.y,0));}
 float inputValue(int c,int2 p) {
   if(c>=_PrimaryChannels)return _Skip.Load(int3((c-_PrimaryChannels)*_SkipSize+p.x,p.y,0));
   float v=primary(c,p);
   int at=(_Class*_Timesteps+_Timestep)*_PrimaryChannels+c;
   if(_ScaleOffset>=0)v*=weight(_ScaleOffset+at);
   if(_ConditionOffset>=0)v+=weight(_ConditionOffset+at);
   return v;
 }
 // erf approximation with absolute error below 1.5e-7 (not the tanh GELU approximation).
 float erfApprox(float x) {
   float s=x<0?-1:1;float a=abs(x);float t=1/(1+.3275911*a);
   float z=(((((1.061405429*t-1.453152027)*t)+1.421413741)*t-.284496736)*t+.254829592)*t;
   return s*(1-z*exp(-a*a));
 }
 float activate(float v) {
   if(_Activation==1)return v/(1+exp(-v));
   if(_Activation==2)return .5*v*(1+erfApprox(v*.7071067811865475));
   if(_Activation==3)return max(v,0);
   return v;
 }
 float4 frag(v2f_img i):SV_Target {
   int2 pixel=int2(i.pos.xy);int oc=pixel.x/_OutSize;int2 p=int2(pixel.x%_OutSize,pixel.y);
   float result=0;
   if(_Op<=1) {
     result=weight(_BiasOffset+oc);
     [loop]for(int ic=0;ic<_InChannels;ic++) {
       [loop]for(int ky=0;ky<_Kernel;ky++)[loop]for(int kx=0;kx<_Kernel;kx++) {
         int2 q;
         if(_Op==0)q=p*_Stride+int2(kx,ky)-_Padding;
         else {
           int2 numerator=p+_Padding-int2(kx,ky);
           if(any(numerator<0)||any(numerator%_Stride!=0))continue;
           q=numerator/_Stride;
         }
         if(any(q<0)||any(q>=_InSize))continue;
         int at=_WeightOffset+((oc*_InChannels+ic)*_Kernel+ky)*_Kernel+kx;
         result+=inputValue(ic,q)*weight(at);
       }
     }
     result=activate(result);
   } else if(_Op==2) {
     result=(primary(oc,p)+_Skip.Load(int3(oc*_OutSize+p.x,p.y,0)))*_ResidualScale;
   } else if(_Op==3||_Op==4) {
     result=_Op==3?-3.402823e38:0;
     [loop]for(int y=0;y<_Kernel;y++)[loop]for(int x=0;x<_Kernel;x++) {
       float v=primary(oc,p*_Stride+int2(x,y));
       if(_Op==3)result=max(result,v);else result+=v;
     }
     if(_Op==4)result=activate(result/(_Kernel*_Kernel));
   } else if(_Op==5) {
     int group=oc%_Groups;int cpg=_PrimaryChannels/_Groups;
     float sum=0,square=0;int n=cpg*_InSize*_InSize;
     [loop]for(int c=0;c<cpg;c++)[loop]for(int y=0;y<_InSize;y++)[loop]for(int x=0;x<_InSize;x++) {
       float v=primary(group*cpg+c,int2(x,y));sum+=v;square+=v*v;
     }
     float mean=sum/n;result=oc<_Groups?mean:max(0,square/n-mean*mean);
   } else if(_Op==6) {
     int group=oc/(_PrimaryChannels/_Groups);
     float mean=_Skip.Load(int3(group,0,0));float variance=_Skip.Load(int3(group+_Groups,0,0));
     result=(primary(oc,p)-mean)*rsqrt(variance+_Epsilon);
     result=activate(result*weight(_WeightOffset+oc)+weight(_BiasOffset+oc));
   }
   return float4(result,0,0,1);
 }
 ENDCG }
 }
}
