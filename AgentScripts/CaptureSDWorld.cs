using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using Object=UnityEngine.Object;
public static class CaptureSDWorld {
    public static object Capture(){
        string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"tmp/vrc-diffusion/sd-anime/playmode");Directory.CreateDirectory(dir);
        var go=new GameObject("Temporary SD capture camera");var camera=go.AddComponent<Camera>();var rt=new RenderTexture(1400,1400,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);var active=RenderTexture.active;
        try{
            camera.transform.position=new Vector3(-7,2.8f,-3.3f);camera.transform.LookAt(new Vector3(-7,2.8f,3.14f));camera.fieldOfView=52;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.018f,.025f,.04f);camera.targetTexture=rt;camera.Render();
            RenderTexture.active=rt;var tex=new Texture2D(1400,1400,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1400,1400),0,0);tex.Apply();string path=Path.Combine(dir,"world-panel.png");File.WriteAllBytes(path,tex.EncodeToPNG());Object.DestroyImmediate(tex);return new{path};
        }finally{RenderTexture.active=active;camera.targetTexture=null;rt.Release();Object.DestroyImmediate(rt);Object.DestroyImmediate(go);}
    }
}
