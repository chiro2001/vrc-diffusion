Shader "DiffusionLab/W8A8" {
 Properties {
  _PackedInput("INT8 activation bytes (q+128)",2D)="gray"{}
  _PackedWeights("INT8 weight bytes (q+128)",2D)="gray"{}
  _FloatInput("Floating activations",2D)="black"{}
  _FloatWeights("Floating weights",2D)="black"{}
  _Parameters("Per output channel: weight scale, real bias",2D)="white"{}
  _Accumulator("INT32 accumulator",2D)="black"{}
  _InWidth("Input width",Int)=1 _InHeight("Input height",Int)=1
  _InChannels("Input channels",Int)=4 _OutChannels("Output channels",Int)=4
  _OutWidth("Output width",Int)=1 _OutHeight("Output height",Int)=1
  _InputTexWidth("Input texture width",Int)=1 _WeightTexWidth("Weight texture width",Int)=1
  _OutputTexWidth("Output texture width",Int)=1 _ParameterTexWidth("Parameter texture width",Int)=1
  _ReadbackTexWidth("INT32 byte readback texture width",Int)=4
  _Kernel("Kernel",Int)=1 _Stride("Stride",Int)=1 _Padding("Padding",Int)=0
  _InputScale("Input scale",Float)=1 _OutputScale("Output scale",Float)=1
  _Activation("Activation: 0 none, 1 ReLU",Int)=0
 }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  CGINCLUDE
  #include "UnityCG.cginc"
  Texture2D<float4> _PackedInput,_PackedWeights,_FloatInput,_FloatWeights,_Parameters;
  Texture2D<int4> _Accumulator;
  int _InWidth,_InHeight,_InChannels,_OutChannels,_OutWidth,_OutHeight;
  int _InputTexWidth,_WeightTexWidth,_OutputTexWidth,_ParameterTexWidth,_ReadbackTexWidth;
  int _Kernel,_Stride,_Padding,_Activation;
  float _InputScale,_OutputScale;
  int inGroups(){return (_InChannels+3)>>2;}
  int outGroups(){return (_OutChannels+3)>>2;}
  int3 address(int index,int width){uint n=(uint)index,w=(uint)width;return int3(n%w,n/w,0);}
  int linearPixel(v2f_img i){int2 p=int2(i.pos.xy);return p.y*_OutputTexWidth+p.x;}
  // Linear textures have no sRGB, mipmaps or compression. Load has no filtering.
  int4 bytesToInt(float4 value){return (int4)floor(value*255.0+0.5)-128;}
  int4 maskInput(int4 a,int group){
   int c=group*4;
   return int4(c<_InChannels?a.x:0,c+1<_InChannels?a.y:0,c+2<_InChannels?a.z:0,c+3<_InChannels?a.w:0);
  }
  float4 maskFloat(float4 a,int group){
   int c=group*4;
   return float4(c<_InChannels?a.x:0,c+1<_InChannels?a.y:0,c+2<_InChannels?a.z:0,c+3<_InChannels?a.w:0);
  }
  int4 activationInt(int2 p,int group){
   if(any(p<0)||p.x>=_InWidth||p.y>=_InHeight)return 0;
   return maskInput(bytesToInt(_PackedInput.Load(address((p.y*_InWidth+p.x)*inGroups()+group,_InputTexWidth))),group);
  }
  float4 activationFloat(int2 p,int group){
   if(any(p<0)||p.x>=_InWidth||p.y>=_InHeight)return 0;
   return maskFloat(_FloatInput.Load(address((p.y*_InWidth+p.x)*inGroups()+group,_InputTexWidth)),group);
  }
  int weightIndex(int oc,int ky,int kx,int group){return ((oc*_Kernel+ky)*_Kernel+kx)*inGroups()+group;}
  int4 weightInt(int oc,int ky,int kx,int group){
   if(oc>=_OutChannels)return 0;
   return maskInput(bytesToInt(_PackedWeights.Load(address(weightIndex(oc,ky,kx,group),_WeightTexWidth))),group);
  }
  float4 weightFloat(int oc,int ky,int kx,int group){
   if(oc>=_OutChannels)return 0;
   return maskFloat(_FloatWeights.Load(address(weightIndex(oc,ky,kx,group),_WeightTexWidth)),group);
  }
  // SM5 fallback: genuine integer multiply/add, not an emulated floating dot.
  // It does not claim a DP4A/Tensor Core instruction. Host rejects INT32 overflow.
  int integerDot4(int4 a,int4 b){int4 product=a*b;return product.x+product.y+product.z+product.w;}
  int4 convolveInt(int index){
   int group=(uint)index%(uint)outGroups(),spatial=(uint)index/(uint)outGroups();
   if(spatial>=_OutWidth*_OutHeight)return 0;
   int2 p=int2((uint)spatial%(uint)_OutWidth,(uint)spatial/(uint)_OutWidth);int oc=group*4;int4 sum=0;
   [loop]for(int ky=0;ky<_Kernel;ky++)[loop]for(int kx=0;kx<_Kernel;kx++){
    int2 q=p*_Stride+int2(kx,ky)-_Padding;
    [loop]for(int g=0;g<inGroups();g++){
     int4 a=activationInt(q,g);
     sum.x+=integerDot4(a,weightInt(oc,ky,kx,g));
     sum.y+=integerDot4(a,weightInt(oc+1,ky,kx,g));
     sum.z+=integerDot4(a,weightInt(oc+2,ky,kx,g));
     sum.w+=integerDot4(a,weightInt(oc+3,ky,kx,g));
    }
   }
   return sum;
  }
  float4 convolveFloat(int index,bool quantizedWeights){
   int group=(uint)index%(uint)outGroups(),spatial=(uint)index/(uint)outGroups();
   if(spatial>=_OutWidth*_OutHeight)return 0;
   int2 p=int2((uint)spatial%(uint)_OutWidth,(uint)spatial/(uint)_OutWidth);int oc=group*4;float4 sum=0;
   [loop]for(int ky=0;ky<_Kernel;ky++)[loop]for(int kx=0;kx<_Kernel;kx++){
    int2 q=p*_Stride+int2(kx,ky)-_Padding;
    [loop]for(int g=0;g<inGroups();g++){
     float4 a=activationFloat(q,g);
     float4 b0=quantizedWeights?(float4)weightInt(oc,ky,kx,g):weightFloat(oc,ky,kx,g);
     float4 b1=quantizedWeights?(float4)weightInt(oc+1,ky,kx,g):weightFloat(oc+1,ky,kx,g);
     float4 b2=quantizedWeights?(float4)weightInt(oc+2,ky,kx,g):weightFloat(oc+2,ky,kx,g);
     float4 b3=quantizedWeights?(float4)weightInt(oc+3,ky,kx,g):weightFloat(oc+3,ky,kx,g);
     sum+=float4(dot(a,b0),dot(a,b1),dot(a,b2),dot(a,b3));
    }
   }
   return sum;
  }
  float finishChannel(float value,int oc,float inputScale,bool scaleWeight){
   if(oc>=_OutChannels)return 0;
   float4 param=_Parameters.Load(address(oc,_ParameterTexWidth));
   float result=value*inputScale*(scaleWeight?param.x:1)+param.y;
   return _Activation==1?max(0,result):result;
  }
  float4 finish(float4 value,int index,float inputScale,bool scaleWeight){
   if(index>=_OutWidth*_OutHeight*outGroups())return 0;
   int oc=((uint)index%(uint)outGroups())*4;
   return float4(finishChannel(value.x,oc,inputScale,scaleWeight),finishChannel(value.y,oc+1,inputScale,scaleWeight),finishChannel(value.z,oc+2,inputScale,scaleWeight),finishChannel(value.w,oc+3,inputScale,scaleWeight));
  }
  float4 encode(float4 value,float scale){
   float4 magnitude=floor(abs(value/scale)+0.5);
   float4 q=clamp(sign(value)*magnitude,-127,127);
   return (q+128)/255.0;
  }
  int4 fragInt(v2f_img i):SV_Target{return convolveInt(linearPixel(i));}
  float4 fragDequant(v2f_img i):SV_Target{int n=linearPixel(i);return finish((float4)convolveInt(n),n,_InputScale,true);}
  float4 fragQuant(v2f_img i):SV_Target{int n=linearPixel(i);return encode(finish((float4)convolveInt(n),n,_InputScale,true),_OutputScale);}
  float4 fragFloat(v2f_img i):SV_Target{int n=linearPixel(i);return finish(convolveFloat(n,false),n,1,false);}
  float4 fragW8Float(v2f_img i):SV_Target{int n=linearPixel(i);return finish(convolveFloat(n,true),n,1,true);}
  float4 fragPack(v2f_img i):SV_Target{
   int n=linearPixel(i);if(n>=_InWidth*_InHeight*inGroups())return 128.0/255.0;
   return encode(maskFloat(_FloatInput.Load(address(n,_InputTexWidth)),(uint)n%(uint)inGroups()),_InputScale);
  }
  float4 fragAccumulatorFloat(v2f_img i):SV_Target{int n=linearPixel(i);return finish((float4)_Accumulator.Load(address(n,_OutputTexWidth)),n,_InputScale,true);}
  float4 fragAccumulatorQuant(v2f_img i):SV_Target{int n=linearPixel(i);return encode(finish((float4)_Accumulator.Load(address(n,_OutputTexWidth)),n,_InputScale,true),_OutputScale);}
  float4 fragByteRoundtrip(v2f_img i):SV_Target{return (bytesToInt(_PackedInput.Load(int3(int2(i.pos.xy),0)))+128)/255.0;}
  // Unity 2022 D3D11 cannot AsyncGPUReadback RGBA32_SInt directly. Encode each
  // accumulator lane's exact two's-complement bits into one RGBA8 pixel instead.
  float4 fragIntegerBytes(v2f_img i):SV_Target{
   int2 p=int2(i.pos.xy);uint index=(uint)(p.y*_ReadbackTexWidth+p.x);
   int4 lanes=_Accumulator.Load(address((int)(index>>2),_OutputTexWidth));
   uint bits=asuint(lanes[index&3u]);
   return float4(bits&255u,(bits>>8)&255u,(bits>>16)&255u,(bits>>24)&255u)/255.0;
  }
  ENDCG
  Pass { Name "INT32_ACCUMULATOR" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragInt
  ENDCG }
  Pass { Name "W8A8_FLOAT_OUTPUT" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragDequant
  ENDCG }
  Pass { Name "W8A8_REQUANTIZE" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragQuant
  ENDCG }
  Pass { Name "FP32_REFERENCE" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragFloat
  ENDCG }
  Pass { Name "W8_FLOAT_REFERENCE" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragW8Float
  ENDCG }
  Pass { Name "QUANTIZE_ACTIVATIONS" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragPack
  ENDCG }
  Pass { Name "DEQUANTIZE_ACCUMULATOR" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragAccumulatorFloat
  ENDCG }
  Pass { Name "REQUANTIZE_ACCUMULATOR" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragAccumulatorQuant
  ENDCG }
  Pass { Name "BYTE_ROUNDTRIP" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragByteRoundtrip
  ENDCG }
  Pass { Name "INT32_BYTE_READBACK" CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment fragIntegerBytes
  ENDCG }
 }
 Fallback Off
}
