using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

// Preparing text and photo uses the same bounded, world-local Shader executor.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDInputs : UdonSharpBehaviour {
    public SDController controller;
    public SDTokenizer tokenizer;
    public SDGraphData textGraph;
    public SDGraphData[] encoderGraphs;
    public RenderTexture positiveCondition,negativeCondition,photo;
    public RenderTexture[] packedPhotos;
    public Texture2D photoLatentTexture;
    public InputField positiveInput,negativeInput;
    public RawImage photoView;
    public Texture photoSource;
    public string[] presetPrompts;
    public string defaultNegative;
    public bool hasPhoto;
    public string notice="";
    public int preparation;
    private bool textNegative,photoChanged;
    private int cachedSize=-1,runEpoch;
    private float[] readback;
    public bool NeedsPhotoEncoding(){return controller.usePhoto&&hasPhoto&&(photoChanged||cachedSize!=controller.unetGraphs[controller.resolution].nativeSize);}
    public void ReleaseResolution(int index){if(encoderGraphs!=null&&index>=0&&index<encoderGraphs.Length){encoderGraphs[index].ReleaseMemory();packedPhotos[index].Release();}}
    public bool CanStart(){
        if(!controller.usePhoto)return true;
        int size=controller.unetGraphs[controller.resolution].nativeSize;
        if(hasPhoto||controller.photoSize==size&&controller.photoBytes!=null&&controller.photoBytes.Length==size*size/8)return true;
        notice="Take a photo first (or disable PHOTO INIT).";return false;
    }
    public void CapturePhoto(){
        if(!controller.HasControl()||controller.phase==1||photoSource==null)return;
        Material m=controller.sampler;m.SetTexture("_SourceImage",photoSource);m.SetFloat("_SourceAspect",photoSource.width/(float)photoSource.height);m.SetInt("_Mode",12);
        VRCGraphics.Blit(null,photo,m);hasPhoto=true;photoChanged=true;notice="Photo captured: center square crop. Generate to encode.";
        if(photoView!=null)photoView.texture=photo;
    }
    public void Begin(){
        runEpoch=controller.epoch;preparation=0;
        if(controller.customPrompt){
            textNegative=true;BeginText(controller.negativePrompt);return;
        }
        BeginPhoto();
    }
    private void BeginText(string prompt){
        if(!tokenizer.Encode(prompt)){notice="CLIP tokenizer could not initialize.";controller.algorithmStage=0;return;}
        if(tokenizer.truncated)notice="Prompt truncated to 75 content tokens.";
        preparation=textNegative?1:2;controller.algorithmStage=7;
        var e=controller.executor;e.graph=textGraph;e.latent=tokenizer.tokenTexture;e.Begin();
    }
    private void CopyText(RenderTexture destination){
        Material m=controller.sampler;m.SetInt("_Mode",10);m.SetTexture("_RGB",controller.executor.Output());m.SetInt("_RGBShift",textGraph.shifts[textGraph.outputNode]);
        VRCGraphics.Blit(null,destination,m);
    }
    private void BeginPhoto(){
        if(!controller.usePhoto){preparation=0;controller.InputsReady();return;}
        int size=controller.unetGraphs[controller.resolution].nativeSize;
        if(controller.HasControl()&&hasPhoto&&(photoChanged||cachedSize!=size)){
            preparation=3;controller.algorithmStage=9;var e=controller.executor;e.graph=encoderGraphs[controller.resolution];
            Material m=controller.sampler;m.SetTexture("_SourceImage",photo);m.SetFloat("_SourceAspect",1);m.SetInt("_Mode",11);m.SetInt("_RGBSize",size);
            VRCGraphics.Blit(null,packedPhotos[controller.resolution],m);e.latent=packedPhotos[controller.resolution];e.Begin();return;
        }
        if(controller.photoSize==size&&controller.photoBytes!=null&&controller.photoBytes.Length==size*size/8){LoadPhotoLatent();return;}
        preparation=4;controller.algorithmStage=10;notice="Waiting for the controller's encoded photo.";
    }
    private void LoadPhotoLatent(){
        int count=controller.photoSize*controller.photoSize/64;Color[] pixels=new Color[4096];byte[] bytes=controller.photoBytes;
        for(int i=0;i<count;i++){
            Color p=Color.clear;for(int c=0;c<4;c++){int at=(i*4+c)*2;int q=(bytes[at]|bytes[at+1]<<8)-32768;p[c]=q*controller.photoScale;}
            pixels[i]=p;
        }
        photoLatentTexture.SetPixels(pixels);photoLatentTexture.Apply(false,false);preparation=0;
        controller.InputsReady();
    }
    public void Advance(){
        var e=controller.executor;
        if(preparation==4){
            int size=controller.unetGraphs[controller.resolution].nativeSize;
            if(controller.photoSize==size&&controller.photoBytes!=null&&controller.photoBytes.Length==size*size/8)LoadPhotoLatent();return;
        }
        if(preparation==5)return;
        if(!e.finished){e.Compute();return;}
        if(preparation==1){CopyText(negativeCondition);textNegative=false;BeginText(controller.positivePrompt);return;}
        if(preparation==2){CopyText(positiveCondition);textGraph.ReleaseMemory();BeginPhoto();return;}
        if(preparation==3){
            preparation=5;controller.algorithmStage=10;
            RenderTexture output=e.Output();readback=new float[output.width*output.height*4];VRCAsyncGPUReadback.Request(output,0,TextureFormat.RGBAFloat,this);
        }
    }
    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request){
        if(runEpoch!=controller.epoch||!controller.HasControl())return;
        if(request.hasError||!request.TryGetData(readback)){notice="Photo latent GPU readback failed; retake photo.";preparation=0;controller.algorithmStage=0;return;}
        int size=controller.unetGraphs[controller.resolution].nativeSize,count=size*size/64;
        float max=0;for(int i=0;i<count;i++)for(int c=0;c<4;c++)max=Mathf.Max(max,Mathf.Abs(readback[i*8+c]*.18215f));
        controller.photoScale=Mathf.Max(max/32767f,1e-12f);byte[] bytes=new byte[count*8];
        for(int i=0;i<count;i++)for(int c=0;c<4;c++){
            int q=Mathf.Clamp(Mathf.RoundToInt(readback[i*8+c]*.18215f/controller.photoScale),-32767,32767)+32768;int at=(i*4+c)*2;
            bytes[at]=(byte)(q&255);bytes[at+1]=(byte)(q>>8);
        }
        controller.photoBytes=bytes;controller.photoSize=size;cachedSize=size;photoChanged=false;encoderGraphs[controller.resolution].ReleaseMemory();packedPhotos[controller.resolution].Release();
        controller.PublishInputs();notice="Photo latent ready; shared as 16-bit values.";LoadPhotoLatent();
    }
    public void ApplyPrompt(){
        if(!controller.CanEditConfiguration())return;
        controller.positivePrompt=positiveInput.text;controller.negativePrompt=negativeInput.text;controller.customPrompt=true;controller.ConfigurationChanged();
        notice="Custom prompt: local W8A16 CLIP encoding on Generate.";
    }
    public void LoadPresetText(){
        if(positiveInput!=null)positiveInput.text=presetPrompts[controller.preset];
        if(negativeInput!=null)negativeInput.text=defaultNegative;
    }
}
