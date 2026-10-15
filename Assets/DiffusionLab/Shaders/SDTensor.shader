Shader "DiffusionLab/SDTensor" {
 Properties {
  _A("Float input A",2D)="black"{} _B("Float input B",2D)="black"{} _C("Float input C",2D)="black"{}
  _Packed("Quantized activation q+128",2D)="gray"{} _Weights("Quantized weights q+128",2D)="gray"{}
  _Params("Scale and bias / gamma and beta",2D)="white"{}
  _TokenEmbedding("CLIP token embedding FP16",2D)="black"{} _PositionEmbedding("CLIP position embedding FP16",2D)="black"{}
  _Causal("Causal attention mask",Int)=0
  _FloatActivation("W8A16 instead of W8A8",Int)=0
  _Partial("Partial reduction",2D)="black"{} _Stats("Reduction result",2D)="black"{}
  _Tile("Convolution output tile",2D)="black"{} _Previous("Previous output",2D)="black"{}
  _Scores("Attention tile scores",2D)="black"{} _OldStats("Online softmax previous stats",2D)="black"{}
  _AShift("A texture row shift",Int)=10 _BShift("B row shift",Int)=10 _CShift("C row shift",Int)=10
  _PackedShift("Packed row shift",Int)=10 _WeightLayout("0 OC-major 1 contiguous OC",Int)=0 _WeightHeight("PNG weight rows",Int)=1 _WeightShift("Weight row shift",Int)=10 _ParamShift("Parameter row shift",Int)=10
  _OutputShift("Output row shift",Int)=10 _PartialShift("Partial row shift",Int)=10 _StatsShift("Stats row shift",Int)=10
  _TileShift("Tile row shift",Int)=7 _PreviousShift("Previous row shift",Int)=10 _ScoreShift("Score row shift",Int)=10
  _OldStatsShift("Old stats row shift",Int)=10
  _InH("Input H",Int)=1 _InW("Input W",Int)=1 _InC("Input C",Int)=4
  _OutH("Output H",Int)=1 _OutW("Output W",Int)=1 _OutC("Output C",Int)=4
  _BH("B H",Int)=1 _BW("B W",Int)=1 _BC("B C",Int)=4
  _Kernel("Kernel",Int)=1 _Stride("Stride",Int)=1 _Padding("Padding",Int)=0
  _Groups("GroupNorm groups; 0 LayerNorm",Int)=32 _Epsilon("Norm epsilon",Float)=0.00001
  _Count("Input logical texel count",Int)=1 _OutCount("Output logical texel count",Int)=1
  _Chunks("Reduction chunks per group",Int)=1 _Offset("Tile start",Int)=0 _TileCount("Tile length",Int)=8192
  _Op("Elementwise op",Int)=2 _Activation("Activation 1 SiLU 2 GELU",Int)=1 _Multiplier("Scalar multiplier",Float)=1
  _Heads("Attention heads",Int)=8 _KeyRows("Attention keys",Int)=77 _KeyStart("Key block start",Int)=0
  _KeyBlock("Keys per block",Int)=64 _FirstBlock("First key block",Int)=1
 }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  CGINCLUDE
  #include "UnityCG.cginc"
  Texture2D<float4> _A,_B,_C,_Packed,_Weights,_Params,_Partial,_Stats,_Tile,_Previous,_Scores,_OldStats;
  Texture2D<float4> _TokenEmbedding,_PositionEmbedding;
  int _Causal,_FloatActivation;
  int _WeightHeight,_WeightLayout;
  int _AShift,_BShift,_CShift,_PackedShift,_WeightShift,_ParamShift,_OutputShift,_PartialShift,_StatsShift;
  int _TileShift,_PreviousShift,_ScoreShift,_OldStatsShift;
  int _InH,_InW,_InC,_OutH,_OutW,_OutC,_BH,_BW,_BC,_Kernel,_Stride,_Padding;
  int _Groups,_Count,_OutCount,_Chunks,_Offset,_TileCount,_Op,_Activation,_Heads,_KeyRows,_KeyStart,_KeyBlock,_FirstBlock;
  float _Epsilon,_Multiplier;
  int3 addr(uint n,int shift){return int3(n&((1u<<shift)-1u),n>>shift,0);}
  uint pixel(v2f_img i){uint2 p=(uint2)i.pos.xy;return (p.y<<_OutputShift)+p.x;}
  int groups(int c){return (c+3)>>2;}
  float4 roundHalf(float4 x){return f16tof32(f32tof16(x));}
  float lane(float4 x,int c){return x[c&3];}
  float getA(int row,int ch){return lane(_A.Load(addr(row*groups(_InC)+(ch>>2),_AShift)),ch);}
  float getB(int row,int ch){return lane(_B.Load(addr(row*groups(_BC)+(ch>>2),_BShift)),ch);}
  float getC(int row,int ch){return lane(_C.Load(addr(row*groups(_BC)+(ch>>2),_CShift)),ch);}
  float4 mask(float4 a,int ch,int count){return float4(ch<count?a.x:0,ch+1<count?a.y:0,ch+2<count?a.z:0,ch+3<count?a.w:0);}
  int4 unpack(float4 a){return (int4)floor(a*255+.5)-128;}
  int idot(int4 a,int4 b){int4 p=a*b;return p.x+p.y+p.z+p.w;}
  float gelu(float x){
   // Abramowitz-Stegun erf approximation, absolute error < 1.5e-7.
   float z=abs(x)*.7071067811865475,t=1/(1+.3275911*z);
   float er=1-(((((1.061405429*t-1.453152027)*t)+1.421413741)*t-.284496736)*t+.254829592)*t*exp(-z*z);
   return .5*x*(1+sign(x)*er);
  }
  float activate(float x){return _Activation==1?x/(1+exp(-x)):_Activation==3?x/(1+exp(-1.702*x)):gelu(x);}
  float4 maxPartial(v2f_img i):SV_Target {
   uint part=pixel(i);float m=0;
   [loop]for(uint j=0;j<256;j++){uint at=part*256+j;if(at<(uint)_Count){float4 a=abs(_A.Load(addr(at,_AShift)));m=max(m,max(max(a.x,a.y),max(a.z,a.w)));}}
   return float4(m,0,0,0);
  }
  float4 maxFinal(v2f_img i):SV_Target {
   float m=0;[loop]for(int j=0;j<_Chunks;j++)m=max(m,_Partial.Load(addr(j,_PartialShift)).x);
   return float4(max(m/127,1e-12),m,0,0);
  }
  float4 quantize(v2f_img i):SV_Target {
   uint p=pixel(i);if(p>=(uint)_Count)return 128.0/255;
   float s=_Stats.Load(int3(0,0,0)).x;float4 a=_A.Load(addr(p,_AShift));
   float4 q=clamp(sign(a)*floor(abs(a/s)+.5),-127,127);return (q+128)/255;
  }
  int4 loadActivation(int row,int group){return unpack(_Packed.Load(addr(row*groups(_InC)+group,_PackedShift)));}
  int4 loadWeight(int oc,int ky,int kx,int group){
   if(oc>=_OutC)return 0;
   int at=_WeightLayout==1?((ky*_Kernel+kx)*groups(_InC)+group)*_OutC+oc:((oc*_Kernel+ky)*_Kernel+kx)*groups(_InC)+group;int3 p=addr(at,_WeightShift);p.y=_WeightHeight-1-p.y;return unpack(_Weights.Load(p));
  }
  float finish(int sum,int oc,float s){if(oc>=_OutC)return 0;float4 p=_Params.Load(addr(oc,_ParamShift));return sum*(s*p.x)+p.y;}
  float4 convolution(v2f_img i):SV_Target {
   uint local=pixel(i),at=local+_Offset;if(local>=(uint)_TileCount||at>=(uint)_OutCount)return 0;
   int og=groups(_OutC),group=at%og,row=at/og,oc=group*4;
   int2 p=int2(row%_OutW,row/_OutW);int4 sum=0;float4 fsum=0;
   [loop]for(int ky=0;ky<_Kernel;ky++)[loop]for(int kx=0;kx<_Kernel;kx++){
    int2 q=p*_Stride+int2(kx,ky)-_Padding;
    if(q.x<0||q.y<0||q.x>=_InW||q.y>=_InH)continue;
    [loop]for(int g=0;g<groups(_InC);g++){
     int ch=g*4;
     if(_FloatActivation!=0){
      float4 a=_A.Load(addr((q.y*_InW+q.x)*groups(_InC)+g,_AShift));a=mask(a,ch,_InC);
      fsum.x+=dot(a,(float4)loadWeight(oc,ky,kx,g));fsum.y+=dot(a,(float4)loadWeight(oc+1,ky,kx,g));
      fsum.z+=dot(a,(float4)loadWeight(oc+2,ky,kx,g));fsum.w+=dot(a,(float4)loadWeight(oc+3,ky,kx,g));continue;
     }
     int4 a=loadActivation(q.y*_InW+q.x,g);
     if(ch+3>=_InC){a.x=ch<_InC?a.x:0;a.y=ch+1<_InC?a.y:0;a.z=ch+2<_InC?a.z:0;a.w=ch+3<_InC?a.w:0;}
     sum.x+=idot(a,loadWeight(oc,ky,kx,g));sum.y+=idot(a,loadWeight(oc+1,ky,kx,g));
     sum.z+=idot(a,loadWeight(oc+2,ky,kx,g));sum.w+=idot(a,loadWeight(oc+3,ky,kx,g));
    }
   }
   if(_FloatActivation!=0){float4 r=0;[unroll]for(int lane=0;lane<4;lane++){if(oc+lane<_OutC){float4 par=_Params.Load(addr(oc+lane,_ParamShift));r[lane]=fsum[lane]*par.x+par.y;}}return roundHalf(r);}
   float s=_Stats.Load(int3(0,0,0)).x;
   return roundHalf(float4(finish(sum.x,oc,s),finish(sum.y,oc+1,s),finish(sum.z,oc+2,s),finish(sum.w,oc+3,s)));
  }
  float4 scatter(v2f_img i):SV_Target {
   uint p=pixel(i);if(p>=(uint)_OutCount)return 0;
   if(p>=(uint)_Offset&&p<(uint)(_Offset+_TileCount))return _Tile.Load(addr(p-_Offset,_TileShift));
   return _Previous.Load(addr(p,_PreviousShift));
  }
  int normCount(){return _Groups==0?_InC:(_InH*_InW*_InC)/_Groups;}
  float normValue(int group,int local){
   if(_Groups==0)return getA(group,local);
   int gc=_InC/_Groups;int row=local/gc,ch=group*gc+local%gc;return getA(row,ch);
  }
  float4 normPartial(v2f_img i):SV_Target {
   uint at=pixel(i);int group=at/_Chunks,part=at%_Chunks;
   int numGroups=_Groups==0?_InH*_InW:_Groups;if(group>=numGroups)return 0;
   float sum=0,sq=0,count=0;int n=normCount();
   [loop]for(int j=0;j<256;j++){int local=part*256+j;if(local<n){float a=normValue(group,local);sum+=a;sq+=a*a;count++;}}
   return float4(sum,sq,count,0);
  }
  float4 normFinal(v2f_img i):SV_Target {
   uint group=pixel(i);int numGroups=_Groups==0?_InH*_InW:_Groups;if(group>=(uint)numGroups)return 0;
   float3 sum=0;[loop]for(int j=0;j<_Chunks;j++)sum+=_Partial.Load(addr(group*_Chunks+j,_PartialShift)).xyz;
   float mean=sum.x/sum.z;return float4(mean,rsqrt(max(0,sum.y/sum.z-mean*mean)+_Epsilon),0,0);
  }
  float normChannel(float a,int row,int ch){
   if(ch>=_InC)return 0;int group=_Groups==0?row:ch/(_InC/_Groups);
   float4 stat=_Stats.Load(addr(group,_StatsShift)),p=_Params.Load(addr(ch,_ParamShift));
   return (a-stat.x)*stat.y*p.x+p.y;
  }
  float4 normalize(v2f_img i):SV_Target {
   uint at=pixel(i);if(at>=(uint)_OutCount)return 0;
   int row=at/groups(_InC),ch=(at%groups(_InC))*4;float4 a=_A.Load(addr(at,_AShift));
   return roundHalf(float4(normChannel(a.x,row,ch),normChannel(a.y,row,ch+1),normChannel(a.z,row,ch+2),normChannel(a.w,row,ch+3)));
  }
  float4 elementwiseRaw(v2f_img i) {
   uint at=pixel(i);if(at>=(uint)_OutCount)return 0;
   int row=at/groups(_OutC),ch=(at%groups(_OutC))*4;float4 a=_A.Load(addr(row*groups(_InC)+(ch>>2),_AShift));
   if(_Op==9){int token=(int)_A.Load(addr(row,_AShift)).x;return _TokenEmbedding.Load(addr(token*192+(ch>>2),11))+_PositionEmbedding.Load(addr(row*192+(ch>>2),10));}
   if(_Op==2)return mask(float4(activate(a.x),activate(a.y),activate(a.z),activate(a.w)),ch,_OutC);
   if(_Op==3){int br=_BH*_BW==1?0:row;return mask(a+_B.Load(addr(br*groups(_BC)+(ch>>2),_BShift)),ch,_OutC);}
   if(_Op==4){
    float4 v=0;[unroll]for(int j=0;j<4;j++){int c=ch+j;v[j]=c<_InC?getA(row,c):(c<_OutC?getB(row,c-_InC):0);}return v;
   }
   if(_Op==5){int y=row/_OutW,x=row%_OutW;int ar=(y/2)*_InW+x/2;return _A.Load(addr(ar*groups(_InC)+(ch>>2),_AShift));}
   if(_Op==7){float4 r=0;[unroll]for(int j=0;j<4;j++){int c=ch+j;r[j]=c<_OutC?getA(row,c)*gelu(getA(row,c+_OutC)):0;}return r;}
   return mask(a*_Multiplier,ch,_OutC);
  }
  float4 elementwise(v2f_img i):SV_Target {return roundHalf(elementwiseRaw(i));}
  float score(int query,int head,int key){
   if(key>=_KeyRows||(_Causal!=0&&key>query))return -1e30;
   int d=_InC/_Heads;float s=0;
   [loop]for(int c=0;c<d;c+=4){int group=(head*d+c)>>2;float4 q=_A.Load(addr(query*groups(_InC)+group,_AShift));float4 k=_B.Load(addr(key*groups(_BC)+group,_BShift));s+=dot(q,k);}
   return s*rsqrt((float)d);
  }
  float4 scores(v2f_img i):SV_Target {
   uint at=pixel(i);int kg=(_KeyBlock+3)/4;int group=at/kg,key=(at%kg)*4+_KeyStart;
   int query=group/_Heads,head=group%_Heads;if(query>=_InH*_InW)return -1e30;
   return float4(score(query,head,key),score(query,head,key+1),score(query,head,key+2),score(query,head,key+3));
  }
  float4 attentionStats(v2f_img i):SV_Target {
   uint rowHead=pixel(i);if(rowHead>=(uint)(_InH*_InW*_Heads))return 0;
   int kg=(_KeyBlock+3)/4;float m=-1e30;
   [loop]for(int j=0;j<kg;j++){float4 s=_Scores.Load(addr(rowHead*kg+j,_ScoreShift));m=max(m,max(max(s.x,s.y),max(s.z,s.w)));}
   float l=0;[loop]for(int k=0;k<_KeyBlock;k++){if(_KeyStart+k<_KeyRows)l+=exp(lane(_Scores.Load(addr(rowHead*kg+(k>>2),_ScoreShift)),k)-m);}
   if(_FirstBlock!=0)return float4(m,l,0,m);
   float4 old=_OldStats.Load(addr(rowHead,_OldStatsShift));float nm=max(old.x,m);
   return float4(nm,exp(old.x-nm)*old.y+exp(m-nm)*l,exp(old.x-nm),m);
  }
  float4 attentionValue(v2f_img i):SV_Target {
   uint at=pixel(i);if(at>=(uint)_OutCount)return 0;
   int row=at/groups(_OutC),group=at%groups(_OutC),ch=group*4,d=_OutC/_Heads,head=ch/d;
   int rowHead=row*_Heads+head,kg=(_KeyBlock+3)/4;float4 stat=_Stats.Load(addr(rowHead,_StatsShift));
   float4 v=_FirstBlock!=0?0:_Previous.Load(addr(at,_PreviousShift))*stat.z;
   [loop]for(int k=0;k<_KeyBlock;k++){
    int key=_KeyStart+k;if(key>=_KeyRows)break;
    float s=lane(_Scores.Load(addr(rowHead*kg+(k>>2),_ScoreShift)),k);
    v+=exp(s-stat.x)*_C.Load(addr(key*groups(_BC)+group,_CShift));
   }
   return v;
  }
  float4 attentionFinish(v2f_img i):SV_Target {
   uint at=pixel(i);if(at>=(uint)_OutCount)return 0;
   int row=at/groups(_OutC),ch=(at%groups(_OutC))*4,d=_OutC/_Heads,head=ch/d;
   float l=_Stats.Load(addr(row*_Heads+head,_StatsShift)).y;
   return roundHalf(_A.Load(addr(at,_AShift))/max(l,1e-30));
  }
  ENDCG
  Pass { Name "maxPartial"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment maxPartial
   ENDCG
  }
  Pass { Name "maxFinal"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment maxFinal
   ENDCG
  }
  Pass { Name "quantize"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment quantize
   ENDCG
  }
  Pass { Name "convolution"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment convolution
   ENDCG
  }
  Pass { Name "scatter"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment scatter
   ENDCG
  }
  Pass { Name "normPartial"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment normPartial
   ENDCG
  }
  Pass { Name "normFinal"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment normFinal
   ENDCG
  }
  Pass { Name "normalize"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment normalize
   ENDCG
  }
  Pass { Name "elementwise"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment elementwise
   ENDCG
  }
  Pass { Name "scores"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment scores
   ENDCG
  }
  Pass { Name "attentionStats"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment attentionStats
   ENDCG
  }
  Pass { Name "attentionValue"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment attentionValue
   ENDCG
  }
  Pass { Name "attentionFinish"
   CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment attentionFinish
   ENDCG
  }
 }
}
