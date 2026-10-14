Shader "DiffusionLab/Probe" {
 Properties { _Before("Before",2D)="black"{} _Conditional("Conditional",2D)="black"{} _Null("Null",2D)="black"{} _Guided("Guided",2D)="black"{} _Clean("Clean",2D)="black"{} _After("After",2D)="black"{} _Memory("Memory",2D)="black"{} _X("X",Float)=14 _Y("Y",Float)=14 _Size("Size",Float)=28 _Channel("Channel",Float)=0 _MemorySize("Memory size",Float)=28 }
 SubShader { Cull Off ZWrite Off ZTest Always
 Pass { CGPROGRAM
 #pragma vertex vert_img
 #pragma fragment frag
 #pragma target 5.0
 #include "UnityCG.cginc"
 Texture2D<float> _Before,_Conditional,_Null,_Guided,_Clean,_After,_Memory;
 int _X,_Y,_Size,_Channel,_MemorySize;
 float4 frag(v2f_img i):SV_Target {
   int n=(int)i.pos.x;int3 p=int3(clamp(_X,0,_Size-1),clamp(_Y,0,_Size-1),0);float v=0;
   if(n==0)v=_Before.Load(p);
   if(n==1)v=_Conditional.Load(p);
   if(n==2)v=_Null.Load(p);
   if(n==3)v=_Guided.Load(p);
   if(n==4)v=_Clean.Load(p);
   if(n==5)v=_After.Load(p);
   if(n==6)v=_Memory.Load(int3(_Channel*_MemorySize+min(_MemorySize-1,p.x*_MemorySize/_Size),min(_MemorySize-1,p.y*_MemorySize/_Size),0));
   return float4(v,0,0,1);
 }
 ENDCG }
 }
}
