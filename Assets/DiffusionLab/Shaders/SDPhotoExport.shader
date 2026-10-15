Shader "DiffusionLab/SDPhotoExport" {
 Properties {
  _MainTex("Generated image",2D)="black"{}
  _Enabled("Photo export enabled",Float)=1
  _StationCenter("Camera zone center",Vector)=(0,0,0,0)
  _Radius("Camera zone radius",Float)=.65
 }
 SubShader {
  Tags {"Queue"="Overlay+100" "RenderType"="Transparent" "IgnoreProjector"="True"}
  Pass {
   Cull Off ZTest Always ZWrite Off
   CGPROGRAM
   #pragma target 3.0
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex;float4 _MainTex_TexelSize,_StationCenter;
   float _Enabled,_Radius,_VRChatCameraMode,_VRChatMirrorMode;
   struct v2f {float4 pos:SV_POSITION;float2 uv:TEXCOORD0;float enabled:TEXCOORD1;};
   v2f vert(appdata_img v){
    v2f o;o.uv=v.texcoord;if(_ProjectionParams.x<0)o.uv.y=1-o.uv.y;
    o.enabled=_Enabled>.5&&_VRChatCameraMode>0&&_VRChatMirrorMode==0&&distance(_WorldSpaceCameraPos,_StationCenter.xyz)<_Radius?1:0;
    o.pos=float4(v.vertex.xy*2,UNITY_NEAR_CLIP_VALUE,1);return o;
   }
   fixed4 frag(v2f i):SV_Target {
    clip(i.enabled-.5);
    // Preserve the entire square output with letterboxing on non-square photos.
    float aspect=_ScreenParams.x/_ScreenParams.y;float2 uv=i.uv;
    if(aspect>1)uv.x=(uv.x-.5)*aspect+.5;else uv.y=(uv.y-.5)/aspect+.5;
    if(any(uv<0)||any(uv>1))return fixed4(0,0,0,1);
    return fixed4(tex2D(_MainTex,uv).rgb,1);
   }
   ENDCG
  }
 }
}
