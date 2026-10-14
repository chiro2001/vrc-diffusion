using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using VRC.Udon;
using Object=UnityEngine.Object;

public static class ValidateW8A8 {
    const string Root="Assets/DiffusionLab/W8A8Validation";
    const string Reports="Reports/DiffusionLab/W8A8";
    const int SafeReduction=133144; // floor(INT32_MAX / (127*127)); bias is applied in float.
    public static Task benchmarkTask;

    public class Spec {
        public string name;
        public int width,height,inputChannels,outputChannels,kernel=1,stride=1,padding,activation;
        public float inputScale=.03125f,outputScale=.125f;
        public int OutWidth {get{return (width+2*padding-kernel)/stride+1;}}
        public int OutHeight {get{return (height+2*padding-kernel)/stride+1;}}
        public int InputGroups {get{return (inputChannels+3)/4;}}
        public int OutputGroups {get{return (outputChannels+3)/4;}}
    }
    public class Fixture:IDisposable {
        public Spec spec;
        public int[] input,weights;
        public float[] scales,bias;
        public Texture2D packedInput,packedWeights,floatInput,floatWeights,halfInput,halfWeights,parameters;
        public Material material;
        public RenderTexture accumulator,floating,quantized;
        public int outputTexWidth,outputTexHeight;
        public void Dispose(){foreach(var a in Objects())if(a!=null)Object.DestroyImmediate(a);}
        public Object[] Objects(){return new Object[]{packedInput,packedWeights,floatInput,floatWeights,halfInput,halfWeights,parameters,material,accumulator,floating,quantized};}
    }
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Guard(Spec s){
        Require(s.width>0&&s.height>0&&s.inputChannels>0&&s.outputChannels>0&&s.kernel>0&&s.stride>0&&s.padding>=0,"Invalid shape");
        Require((long)s.kernel*s.kernel*s.inputChannels<=SafeReduction,"INT32 overflow risk: reduction must be <= "+SafeReduction);
        Require(s.OutWidth>0&&s.OutHeight>0&&s.inputScale>0&&s.outputScale>0,"Invalid output shape/scale");
    }
    static int Width(int count){return Math.Max(1,Math.Min(257,count));}
    static int Quantize(float value,float scale){return Math.Max(-127,Math.Min(127,(int)(Math.Sign(value)*(float)Math.Floor(Math.Abs(value/scale)+.5f))));}
    static Texture2D Bytes(int width,int height,byte[] raw){
        var t=new Texture2D(width,height,TextureFormat.RGBA32,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
        t.LoadRawTextureData(raw);t.Apply(false,false);return t;
    }
    static Texture2D Floats(int width,int height,float[] values,bool half=false){
        var t=new Texture2D(width,height,half?TextureFormat.RGBAHalf:TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
        var c=new Color[width*height];for(int i=0;i<c.Length;i++)c[i]=new Color(values[i*4],values[i*4+1],values[i*4+2],values[i*4+3]);
        t.SetPixels(c);t.Apply(false,false);return t;
    }
    static RenderTexture RT(int w,int h,RenderTextureFormat format){
        Require(SystemInfo.SupportsRenderTextureFormat(format),"Unsupported RT "+format);
        var r=new RenderTexture(w,h,0,format,RenderTextureReadWrite.Linear){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,useMipMap=false,autoGenerateMips=false};
        Require(r.Create(),"RT creation failed "+format);return r;
    }
    static Fixture Make(Spec s,int seed=123){
        Guard(s);var f=new Fixture{spec=s};var rng=new System.Random(seed);
        f.input=Enumerable.Range(0,s.width*s.height*s.inputChannels).Select(i=>rng.Next(-127,128)).ToArray();
        f.weights=Enumerable.Range(0,s.outputChannels*s.kernel*s.kernel*s.inputChannels).Select(i=>rng.Next(-127,128)).ToArray();
        f.scales=Enumerable.Range(0,s.outputChannels).Select(c=>.015625f*(1<<(c%3))).ToArray();
        f.bias=Enumerable.Range(0,s.outputChannels).Select(c=>(c%5-2)*.25f).ToArray();
        BuildTextures(f);return f;
    }
    static void BuildTextures(Fixture f){
        var s=f.spec;int ac=s.width*s.height*s.InputGroups,bc=s.outputChannels*s.kernel*s.kernel*s.InputGroups;
        int aw=Width(ac),ah=(ac+aw-1)/aw,bw=Width(bc),bh=(bc+bw-1)/bw;
        var a=new byte[aw*ah*4];var b=new byte[bw*bh*4];var af=new float[a.Length];var bf=new float[b.Length];
        // All unused bytes contain -128 deliberately: kernels must mask channel and texture tails.
        for(int p=0;p<s.width*s.height;p++)for(int c=0;c<s.inputChannels;c++){
            int at=(p*s.InputGroups+c/4)*4+c%4;int q=f.input[p*s.inputChannels+c];a[at]=(byte)(q+128);af[at]=q*s.inputScale;
        }
        for(int oc=0;oc<s.outputChannels;oc++)for(int k=0;k<s.kernel*s.kernel;k++)for(int c=0;c<s.inputChannels;c++){
            int at=((oc*s.kernel*s.kernel+k)*s.InputGroups+c/4)*4+c%4;
            int q=f.weights[(oc*s.kernel*s.kernel+k)*s.inputChannels+c];b[at]=(byte)(q+128);bf[at]=q*f.scales[oc];
        }
        int pw=Width(s.outputChannels),ph=(s.outputChannels+pw-1)/pw;var parameters=new float[pw*ph*4];
        for(int c=0;c<s.outputChannels;c++){parameters[c*4]=f.scales[c];parameters[c*4+1]=f.bias[c];}
        f.packedInput=Bytes(aw,ah,a);f.packedWeights=Bytes(bw,bh,b);f.floatInput=Floats(aw,ah,af);f.floatWeights=Floats(bw,bh,bf);
        f.halfInput=Floats(aw,ah,af,true);f.halfWeights=Floats(bw,bh,bf,true);f.parameters=Floats(pw,ph,parameters);
        int ocnt=s.OutWidth*s.OutHeight*s.OutputGroups;f.outputTexWidth=Width(ocnt);f.outputTexHeight=(ocnt+f.outputTexWidth-1)/f.outputTexWidth;
        f.accumulator=RT(f.outputTexWidth,f.outputTexHeight,RenderTextureFormat.ARGBInt);
        f.floating=RT(f.outputTexWidth,f.outputTexHeight,RenderTextureFormat.ARGBFloat);
        f.quantized=RT(f.outputTexWidth,f.outputTexHeight,RenderTextureFormat.ARGB32);
        var shader=Shader.Find("DiffusionLab/W8A8");Require(shader!=null&&shader.isSupported,"W8A8 shader unavailable");
        f.material=new Material(shader);Configure(f);
    }
    static void Rebuild(Fixture f){
        foreach(var a in f.Objects())if(a!=null)Object.DestroyImmediate(a);
        BuildTextures(f);
    }
    static void Configure(Fixture f){
        var s=f.spec;var m=f.material;
        m.SetTexture("_PackedInput",f.packedInput);m.SetTexture("_PackedWeights",f.packedWeights);m.SetTexture("_FloatInput",f.floatInput);m.SetTexture("_FloatWeights",f.floatWeights);
        m.SetTexture("_Parameters",f.parameters);m.SetTexture("_Accumulator",f.accumulator);
        m.SetInt("_InWidth",s.width);m.SetInt("_InHeight",s.height);m.SetInt("_InChannels",s.inputChannels);m.SetInt("_OutChannels",s.outputChannels);
        m.SetInt("_OutWidth",s.OutWidth);m.SetInt("_OutHeight",s.OutHeight);m.SetInt("_InputTexWidth",f.packedInput.width);m.SetInt("_WeightTexWidth",f.packedWeights.width);
        m.SetInt("_OutputTexWidth",f.outputTexWidth);m.SetInt("_ParameterTexWidth",f.parameters.width);m.SetInt("_Kernel",s.kernel);m.SetInt("_Stride",s.stride);m.SetInt("_Padding",s.padding);m.SetInt("_Activation",s.activation);
        m.SetInt("_ReadbackTexWidth",f.outputTexWidth*4);
        m.SetFloat("_InputScale",s.inputScale);m.SetFloat("_OutputScale",s.outputScale);
    }
    static int[] Oracle(Fixture f){
        var s=f.spec;var result=new int[f.outputTexWidth*f.outputTexHeight*4];
        // Scalar logical NHWC convolution: deliberately independent of the GPU's RGBA packing.
        for(int y=0;y<s.OutHeight;y++)for(int x=0;x<s.OutWidth;x++)for(int oc=0;oc<s.outputChannels;oc++){
            long sum=0;
            for(int ky=0;ky<s.kernel;ky++)for(int kx=0;kx<s.kernel;kx++){
                int ix=x*s.stride+kx-s.padding,iy=y*s.stride+ky-s.padding;
                if(ix<0||iy<0||ix>=s.width||iy>=s.height)continue;
                for(int ic=0;ic<s.inputChannels;ic++)sum+=(long)f.input[(iy*s.width+ix)*s.inputChannels+ic]*f.weights[((oc*s.kernel+ky)*s.kernel+kx)*s.inputChannels+ic];
            }
            Require(sum>=int.MinValue&&sum<=int.MaxValue,"CPU accumulation overflow");
            result[((y*s.OutWidth+x)*s.OutputGroups+oc/4)*4+oc%4]=(int)sum;
        }
        return result;
    }
    static float[] Real(Fixture f,int[] acc){
        var s=f.spec;var result=new float[acc.Length];
        for(int p=0;p<s.OutWidth*s.OutHeight;p++)for(int c=0;c<s.outputChannels;c++){
            int at=(p*s.OutputGroups+c/4)*4+c%4;float v=acc[at]*s.inputScale*f.scales[c]+f.bias[c];result[at]=s.activation==1?Math.Max(0,v):v;
        }
        return result;
    }
    static byte[] Encoded(Fixture f,int[] acc){return Real(f,acc).Select(v=>(byte)(Quantize(v,f.spec.outputScale)+128)).ToArray();}
    static async Task<T[]> Read<T>(RenderTexture rt)where T:struct{
        if(typeof(T)==typeof(int)&&rt.format==RenderTextureFormat.ARGBInt){
            var encoded=RT(rt.width*4,rt.height,RenderTextureFormat.ARGB32);var m=new Material(Shader.Find("DiffusionLab/W8A8"));
            try{
                m.SetTexture("_Accumulator",rt);m.SetInt("_OutputTexWidth",rt.width);m.SetInt("_ReadbackTexWidth",encoded.width);
                Graphics.Blit(null,encoded,m,9);var bytes=await Read<byte>(encoded);var ints=new int[bytes.Length/4];Buffer.BlockCopy(bytes,0,ints,0,bytes.Length);return (T[])(object)ints;
            }finally{Object.DestroyImmediate(encoded);Object.DestroyImmediate(m);}
        }
        var request=AsyncGPUReadback.Request(rt,0);
        // Isolated EditMode fixtures need an explicit fence: an unfocused Editor
        // does not reliably pump AsyncGPUReadback between run_script callbacks.
        request.WaitForCompletion();
        Require(!request.hasError,"GPU readback error: "+rt.format);return request.GetData<T>().ToArray();
    }
    static Task NextFrame(){
        EditorApplication.QueuePlayerLoopUpdate();return Task.Delay(20);
    }
    static void Equal<T>(T[] actual,T[] expected,string name){
        Require(actual.Length==expected.Length,name+" length mismatch "+actual.Length+" / "+expected.Length);
        for(int i=0;i<actual.Length;i++)Require(EqualityComparer<T>.Default.Equals(actual[i],expected[i]),name+" mismatch at "+i+": "+actual[i]+" != "+expected[i]);
    }
    static float Close(float[] actual,float[] expected,string name,float limit=.0001f){
        Require(actual.Length==expected.Length,name+" length mismatch");float max=0;
        for(int i=0;i<actual.Length;i++){Require(!float.IsNaN(actual[i])&&!float.IsInfinity(actual[i]),name+" nonfinite");max=Math.Max(max,Math.Abs(actual[i]-expected[i]));}
        Require(max<=limit,name+" maxAbs="+max);return max;
    }
    static void Save(string name,object value){Directory.CreateDirectory(Reports);File.WriteAllText(Reports+"/"+name,JsonConvert.SerializeObject(value,Formatting.Indented));}
    public static object ShaderStatus(){
        var s=Shader.Find("DiffusionLab/W8A8");Require(s!=null,"Missing shader");var errors=ShaderUtil.GetShaderMessages(s).Select(e=>new{e.message,e.line,severity=e.severity.ToString()}).ToArray();
        return new{supported=s.isSupported,passes=s.passCount,errors};
    }
    public static async Task<object> Numeric(){
        Save("numeric.json",new{status="running",completed=0});
        try{return await NumericInner();}
        catch(Exception e){Save("numeric.json",new{status="failed",error=e.ToString()});throw;}
    }
    static async Task<object> NumericInner(){
        Require(!EditorApplication.isPlaying,"Run numeric fixtures in EditMode");var cases=new[]{
            new Spec{name="linear-signed-tail",width=3,height=1,inputChannels=5,outputChannels=7},
            new Spec{name="conv-nonsquare-padding",width=5,height=4,inputChannels=5,outputChannels=7,kernel=3,padding=1},
            new Spec{name="conv-stride2",width=5,height=4,inputChannels=9,outputChannels=5,kernel=3,stride=2,padding=1,activation=1},
            new Spec{name="linear-stride2",width=6,height=3,inputChannels=17,outputChannels=9,stride=2},
            new Spec{name="kernel-larger-than-input",width=1,height=1,inputChannels=3,outputChannels=2,kernel=3,padding=1},
            new Spec{name="zero-input",width=2,height=2,inputChannels=4,outputChannels=4},
            new Spec{name="integer-above-float24",width=1,height=1,inputChannels=2049,outputChannels=1},
            new Spec{name="negative-integer-above-float24",width=1,height=1,inputChannels=2049,outputChannels=1}
            ,new Spec{name="int32-max-safe",width=1,height=1,inputChannels=SafeReduction,outputChannels=1}
            ,new Spec{name="negative-int32-max-safe",width=1,height=1,inputChannels=SafeReduction,outputChannels=1}
        };
        var results=new List<object>();
        foreach(var s in cases)using(var f=Make(s)){
            if(s.name=="zero-input"){Array.Clear(f.input,0,f.input.Length);Rebuild(f);}
            bool large=s.name.Contains("above-float24")||s.name.Contains("max-safe");
            if(large){for(int j=0;j<f.input.Length;j++)f.input[j]=127;for(int j=0;j<f.weights.Length;j++)f.weights[j]=s.name.StartsWith("negative")?-127:127;Rebuild(f);}
            int[] expected=Oracle(f);Graphics.Blit(null,f.accumulator,f.material,0);Equal(await Read<int>(f.accumulator),expected,s.name+" INT32");
            var real=Real(f,expected);Graphics.Blit(null,f.floating,f.material,1);float error=Close(await Read<float>(f.floating),real,s.name+" dequant");
            Graphics.Blit(null,f.quantized,f.material,2);Equal(await Read<byte>(f.quantized),Encoded(f,expected),s.name+" fused requant");
            Graphics.Blit(null,f.quantized,f.material,7);Equal(await Read<byte>(f.quantized),Encoded(f,expected),s.name+" accumulator requant");
            Graphics.Blit(null,f.floating,f.material,6);Close(await Read<float>(f.floating),real,s.name+" accumulator dequant");
            if(!large){
                Graphics.Blit(null,f.floating,f.material,3);Close(await Read<float>(f.floating),real,s.name+" FP32");
                Graphics.Blit(null,f.floating,f.material,4);Close(await Read<float>(f.floating),real,s.name+" W8 float");
            }
            results.Add(new{name=s.name,shape=s,int32Exact=true,maxAbsDequant=error,minAccumulator=expected.Min(),maxAccumulator=expected.Max()});
            Save("numeric.json",new{status="running",completed=results.Count,results});
        }
        using(var f=Make(new Spec{name="output-requant-ties",width=1,height=1,inputChannels=1,outputChannels=10,inputScale=1,outputScale=2})){
            f.input[0]=1;int[] values={-1,1,-3,3,-5,5,-127,127,127,-127};
            for(int c=0;c<10;c++){f.weights[c]=values[c];f.scales[c]=c<8?1:4;f.bias[c]=0;}Rebuild(f);
            var expected=Oracle(f);Graphics.Blit(null,f.accumulator,f.material,0);
            Graphics.Blit(null,f.quantized,f.material,2);Equal(await Read<byte>(f.quantized),Encoded(f,expected),"output requant ties and saturation fused");
            Graphics.Blit(null,f.quantized,f.material,7);Equal(await Read<byte>(f.quantized),Encoded(f,expected),"output requant ties and saturation staged");
        }
        using(var f=Make(new Spec{name="activation-pack-ties",width=1,height=1,inputChannels=13,outputChannels=1,inputScale=1})){
            float[] values={-.5f,.5f,-1.5f,1.5f,-2.5f,2.5f,-128,128,0,-127,127,.49f,-.49f};
            var raw=new float[f.floatInput.width*f.floatInput.height*4];Array.Copy(values,raw,values.Length);var t=Floats(f.floatInput.width,f.floatInput.height,raw);
            try{f.material.SetTexture("_FloatInput",t);f.material.SetInt("_OutputTexWidth",t.width);var packed=RT(t.width,t.height,RenderTextureFormat.ARGB32);
                try{Graphics.Blit(null,packed,f.material,5);Equal(await Read<byte>(packed),raw.Select(v=>(byte)(Quantize(v,1)+128)).ToArray(),"activation pack ties-away saturation");}
                finally{Object.DestroyImmediate(packed);}}
            finally{Object.DestroyImmediate(t);}
        }
        var bytes=Enumerable.Range(0,256).Select(x=>(byte)x).ToArray();var texture=Bytes(64,1,bytes);var target=RT(64,1,RenderTextureFormat.ARGB32);var mat=new Material(Shader.Find("DiffusionLab/W8A8"));
        try{mat.SetTexture("_PackedInput",texture);Graphics.Blit(null,target,mat,8);Equal(await Read<byte>(target),bytes,"all 256 UNorm byte codes");}
        finally{Object.DestroyImmediate(texture);Object.DestroyImmediate(target);Object.DestroyImmediate(mat);}
        bool rejected=false;try{Guard(new Spec{width=1,height=1,inputChannels=SafeReduction+1,outputChannels=1});}catch(Exception){rejected=true;}Require(rejected,"Overflow guard failed");
        var report=new{status="completed",passed=true,device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),arithmetic="signed INT8 operands, scalar/vector INT32 multiply-add (SM5), no native DP4A claim",int32Readback="bit-exact little-endian RGBA8 transport, no float conversion",rounding="nearest, ties away from zero",requantTiesAndSaturation=true,byteRoundtrip=256,overflowReductionLimit=SafeReduction,overflowRejected=true,cases=results};Save("numeric.json",report);return report;
    }

    static void Folder(string p){if(AssetDatabase.IsValidFolder(p))return;int i=p.LastIndexOf('/');Folder(p.Substring(0,i));AssetDatabase.CreateFolder(p.Substring(0,i),p.Substring(i+1));}
    static T Persist<T>(T value,string path)where T:Object{Require(AssetDatabase.LoadMainAssetAtPath(path)==null,"Validation asset already exists: "+path);AssetDatabase.CreateAsset(value,path);return value;}
    static void PersistFixture(Fixture f,string prefix){
        Object[] assets={f.packedInput,f.packedWeights,f.floatInput,f.floatWeights,f.halfInput,f.halfWeights,f.parameters};
        string[] names={"Input","Weights","FloatInput","FloatWeights","HalfInput","HalfWeights","Parameters"};
        for(int i=0;i<assets.Length;i++)Persist(assets[i],Root+"/"+prefix+names[i]+".asset");
        Persist(f.accumulator,Root+"/"+prefix+"Accumulator.renderTexture");Persist(f.quantized,Root+"/"+prefix+"Quantized.renderTexture");Persist(f.floating,Root+"/"+prefix+"Output.renderTexture");Persist(f.material,Root+"/"+prefix+".mat");
    }
    public static object BuildPlayFixture(){
        Require(!EditorApplication.isPlaying,"EditMode required");
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();Require(!scene.isDirty,"Preserve unsaved main scene before fixture build");Folder(Root);
        Require(AssetDatabase.LoadMainAssetAtPath(Root+"/Runner.prefab")==null,"Fixture already built; do not overwrite");
        var a=Make(new Spec{name="play-conv",width=5,height=4,inputChannels=5,outputChannels=7,kernel=3,padding=1,activation=1});
        var b=Make(new Spec{name="play-linear",width=5,height=4,inputChannels=7,outputChannels=5,inputScale=a.spec.outputScale,outputScale=.25f},321);
        var aExpected=Oracle(a);var encoded=Encoded(a,aExpected);
        for(int p=0;p<b.spec.width*b.spec.height;p++)for(int c=0;c<b.spec.inputChannels;c++)b.input[p*b.spec.inputChannels+c]=encoded[(p*a.spec.OutputGroups+c/4)*4+c%4]-128;
        Rebuild(b);var bExpected=Oracle(b);
        PersistFixture(a,"First");PersistFixture(b,"Second");
        var readback=Persist(RT(b.accumulator.width*4,b.accumulator.height,RenderTextureFormat.ARGB32),Root+"/IntegerReadback.renderTexture");
        var source=AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/DiffusionLab/Scripts/W8A8ValidationRunner.cs");
        var program=Persist(ScriptableObject.CreateInstance<UdonSharpProgramAsset>(),Root+"/W8A8ValidationRunner.asset");program.sourceCsScript=source;EditorUtility.SetDirty(program);AssetDatabase.SaveAssets();
        Save("play-expected.json",new{first=aExpected,second=bExpected,output=Real(b,bExpected),requant=encoded});
        UdonSharpProgramAsset.CompileAllCsPrograms(true,true);
        return new{prepared=true,finish="Run FinishPlayFixture after asynchronous Udon compilation",mainSceneDirty=scene.isDirty};
    }
    public static object CompilePlayFixture(){
        Require(!EditorApplication.isPlaying,"EditMode required");
        UdonSharpProgramAsset.CompileAllCsPrograms(true,true);return new{requested=true};
    }
    public static object FinishPlayFixture(){
        Require(!EditorApplication.isPlaying,"EditMode required");
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();Require(!scene.isDirty,"Preserve main scene edits");
        Require(!UdonSharpProgramAsset.AnyUdonSharpScriptHasError(),"Udon compile failed");
        Require(AssetDatabase.LoadMainAssetAtPath(Root+"/Runner.prefab")==null,"Fixture already built");
        // Recover deterministic oracle if preparation was interrupted before it was written.
        if(!File.Exists(Reports+"/play-expected.json"))using(var a=Make(new Spec{name="play-conv",width=5,height=4,inputChannels=5,outputChannels=7,kernel=3,padding=1,activation=1}))using(var b=Make(new Spec{name="play-linear",width=5,height=4,inputChannels=7,outputChannels=5,inputScale=.125f,outputScale=.25f},321)){
            var first=Oracle(a);var encoded=Encoded(a,first);
            for(int p=0;p<b.spec.width*b.spec.height;p++)for(int c=0;c<b.spec.inputChannels;c++)b.input[p*b.spec.inputChannels+c]=encoded[(p*a.spec.OutputGroups+c/4)*4+c%4]-128;
            var second=Oracle(b);Save("play-expected.json",new{first,second,output=Real(b,second),requant=encoded});
        }
        var temporary=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
        try{
            var g=new GameObject("W8A8ValidationRunner");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(g,temporary);
            var r=g.AddUdonSharpComponent<W8A8ValidationRunner>();
            r.first=AssetDatabase.LoadAssetAtPath<Material>(Root+"/First.mat");r.second=AssetDatabase.LoadAssetAtPath<Material>(Root+"/Second.mat");
            r.firstAccumulator=AssetDatabase.LoadAssetAtPath<RenderTexture>(Root+"/FirstAccumulator.renderTexture");r.quantized=AssetDatabase.LoadAssetAtPath<RenderTexture>(Root+"/FirstQuantized.renderTexture");
            r.secondAccumulator=AssetDatabase.LoadAssetAtPath<RenderTexture>(Root+"/SecondAccumulator.renderTexture");r.output=AssetDatabase.LoadAssetAtPath<RenderTexture>(Root+"/SecondOutput.renderTexture");r.integerReadback=AssetDatabase.LoadAssetAtPath<RenderTexture>(Root+"/IntegerReadback.renderTexture");
            r.integerBytes=new byte[r.integerReadback.width*r.integerReadback.height*4];UdonSharpEditorUtility.CopyProxyToUdon(r);
            PrefabUtility.SaveAsPrefabAsset(g,Root+"/Runner.prefab");
        }finally{EditorSceneManager.CloseScene(temporary,true);UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);}
        AssetDatabase.SaveAssets();
        return new{created=true,prefab=Root+"/Runner.prefab",mainSceneDirty=scene.isDirty};
    }
    public static async Task<object> Play(){
        Require(EditorApplication.isPlaying,"PlayMode required");
        var g=GameObject.Find("W8A8ValidationRunner");Require(g!=null,"Load validation scene before entering PlayMode");
        try{
            var b=g.GetComponent<UdonBehaviour>();Require(b!=null,"Missing Udon backing behaviour");
            var initializedDeadline=DateTime.UtcNow.AddSeconds(5);
            while(b.GetProgramVariable("runs")==null){Require(DateTime.UtcNow<initializedDeadline,"Runtime Udon VM did not initialize");await NextFrame();}
            b.SendCustomEvent("Execute");var deadline=DateTime.UtcNow.AddSeconds(20);
            while(!(bool)b.GetProgramVariable("readReady")){Require(DateTime.UtcNow<deadline,"VRC readback timeout");await NextFrame();}
            Require(!(bool)b.GetProgramVariable("readError"),"VRC byte[] integer readback failed");
            var expected=JObject.Parse(File.ReadAllText(Reports+"/play-expected.json"));
            Equal(await Read<int>((RenderTexture)b.GetProgramVariable("firstAccumulator")),expected["first"].ToObject<int[]>(),"Play VRC first integer conv");
            Equal(await Read<byte>((RenderTexture)b.GetProgramVariable("quantized")),expected["requant"].ToObject<byte[]>(),"Play VRC requantized activations");
            var raw=(byte[])b.GetProgramVariable("integerBytes");var integers=new int[raw.Length/4];Buffer.BlockCopy(raw,0,integers,0,raw.Length);
            Equal(integers,expected["second"].ToObject<int[]>(),"Play Udon VRCAsyncGPUReadback byte[] INT32");
            float error=Close(await Read<float>((RenderTexture)b.GetProgramVariable("output")),expected["output"].ToObject<float[]>(),"Play VRC output");
            var result=new{passed=true,source="real UdonSharp backing behaviour + VRCGraphics.Blit + VRCAsyncGPUReadback byte[]",runs=b.GetProgramVariable("runs"),firstInt32Exact=true,requantBytesExact=true,secondInt32Exact=true,floatMaxAbs=error};Save("playmode.json",result);return result;
        }finally{ /* The authored validation scene is closed after leaving PlayMode. */ }
    }
    public static object OpenPlayScene(){
        Require(!EditorApplication.isPlaying,"EditMode required");
        var main=UnityEngine.SceneManagement.SceneManager.GetActiveScene();Require(!main.isDirty,"Preserve main scene edits");
        string path=Root+"/Validation.unity";
        UnityEngine.SceneManagement.Scene validation;
        if(AssetDatabase.LoadMainAssetAtPath(path)==null){
            validation=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var g=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Runner.prefab"),validation);g.name="W8A8ValidationRunner";
            Require(EditorSceneManager.SaveScene(validation,path),"Could not save validation scene");
        }else validation=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
        UnityEngine.SceneManagement.SceneManager.SetActiveScene(main);return new{opened=true,path,mainSceneDirty=main.isDirty};
    }
    public static object ClosePlayScene(){
        Require(!EditorApplication.isPlaying,"Leave PlayMode first");
        var s=UnityEngine.SceneManagement.SceneManager.GetSceneByPath(Root+"/Validation.unity");
        if(s.IsValid()&&s.isLoaded)EditorSceneManager.CloseScene(s,true);return new{closed=true};
    }
    public static object SnapshotPaths(){
        Require(!EditorApplication.isPlaying,"Leave PlayMode before snapshot");
        var paths=new HashSet<string>(AssetDatabase.GetAllAssetPaths().Where(p=>p.StartsWith(Root+"/")&&File.Exists(p)));
        foreach(string p in AssetDatabase.GetDependencies(new[]{Root+"/Runner.prefab",Root+"/Validation.unity"},true))if(p.StartsWith("Assets/")&&File.Exists(p))paths.Add(p);
        foreach(string p in paths.ToArray())if(File.Exists(p+".meta"))paths.Add(p+".meta");
        paths.Add(Root+".meta");return paths.OrderBy(p=>p).ToArray();
    }
    public static object RestoreValidationSideEffects(int virtualVoices,int realVoices,string noiseGuid,string nullNoiseGuid){
        Require(!EditorApplication.isPlaying,"Leave PlayMode first");
        // Values captured before this validation run: ClientSim changes the voice
        // budget on entering PlayMode, and shared materials retain runtime bindings.
        var audio=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);
        audio.FindProperty("m_VirtualVoiceCount").intValue=virtualVoices;audio.FindProperty("m_RealVoiceCount").intValue=realVoices;audio.ApplyModifiedProperties();
        var sampler=AssetDatabase.LoadAssetAtPath<Material>("Assets/DiffusionLab/Materials/Sampler.mat");
        sampler.SetTexture("_Noise",AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(noiseGuid)));
        sampler.SetTexture("_UnconditionedNoise",AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(nullNoiseGuid)));EditorUtility.SetDirty(sampler);
        var second=AssetDatabase.LoadAssetAtPath<Material>(Root+"/Second.mat");second.SetTexture("_PackedInput",AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/SecondInput.asset"));EditorUtility.SetDirty(second);
        AssetDatabase.SaveAssets();return new{restored=true};
    }

    public static object StartBenchmark(){
        return StartBenchmarkCore(false);
    }
    public static object StartProfiledBenchmark(){return StartBenchmarkCore(true);}
    static object StartBenchmarkCore(bool enableGpuProfiler){
        Require(!EditorApplication.isPlaying,"Use EditMode for controlled isolated kernel benchmark");
        Directory.CreateDirectory(Reports);
        var singleRunLock=new FileStream(Reports+"/benchmark.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
        benchmarkTask=Benchmark(singleRunLock,enableGpuProfiler);return new{started=true,enableGpuProfiler,report=Reports+"/benchmark.json"};
    }
    public static object BenchmarkStatus(){return JObject.Parse(File.ReadAllText(Reports+"/benchmark.json"));}
    static async Task Benchmark(FileStream singleRunLock,bool enableGpuProfiler){
        var results=new List<object>();Save("benchmark.json",new{status="running",completed=0});
        bool profilerWasEnabled=Profiler.enabled,gpuAreaWasEnabled=Profiler.GetAreaEnabled(ProfilerArea.GPU);
        try{
            if(enableGpuProfiler&&SystemInfo.supportsGpuRecorder){Profiler.SetAreaEnabled(ProfilerArea.GPU,true);Profiler.enabled=true;}
            foreach(var s in new[]{
                new Spec{name="mnist-conv-28-c128",width=28,height=28,inputChannels=128,outputChannels=128,kernel=3,padding=1},
                new Spec{name="sd-channel-conv-16-c320",width=16,height=16,inputChannels=320,outputChannels=320,kernel=3,padding=1},
                new Spec{name="linear-64-tokens-c320",width=8,height=8,inputChannels=320,outputChannels=320}
            })using(var f=Make(s)){
                float[] floatReference=null;
                foreach(string route in new[]{"FP32-storage-FP32-math","FP16-storage-FP32-math","W8-A16-storage-FP32-math","W8A8-INT32-math"}){
                    bool w8a8=route=="W8A8-INT32-math",w8float=route.StartsWith("W8-A16"),half=route.StartsWith("FP16")||w8float;
                    f.material.SetTexture("_FloatInput",half?f.halfInput:f.floatInput);f.material.SetTexture("_FloatWeights",half?f.halfWeights:f.floatWeights);
                    int pass=w8a8?1:w8float?4:3;
                    Graphics.Blit(null,f.floating,f.material,pass);var warmup=await Read<float>(f.floating);
                    if(floatReference==null)floatReference=warmup;
                    float warmupError=Close(warmup,floatReference,"benchmark warmup "+route,.001f);
                    var wall=new List<double>();var gpu=new List<double>();
                    for(int sample=0;sample<5;sample++){
                        string marker="W8A8."+Guid.NewGuid().ToString("N");var sampler=CustomSampler.Create(marker,true);var recorder=sampler.GetRecorder();recorder.enabled=true;
                        var cmd=new CommandBuffer{name=marker};
                        try{
                            cmd.BeginSample(sampler);cmd.Blit(null,f.floating,f.material,pass);cmd.EndSample(sampler);
                            var sw=Stopwatch.StartNew();Graphics.ExecuteCommandBuffer(cmd);await Read<float>(f.floating);sw.Stop();wall.Add(sw.Elapsed.TotalMilliseconds);
                            // GPU Recorder is delayed by several rendered frames; never call wall time GPU time.
                            for(int frame=0;enableGpuProfiler&&SystemInfo.supportsGpuRecorder&&recorder.isValid&&frame<12;frame++){
                                await NextFrame();
                                if(SystemInfo.supportsGpuRecorder&&recorder.isValid&&recorder.gpuSampleBlockCount>0&&recorder.gpuElapsedNanoseconds>0){gpu.Add(recorder.gpuElapsedNanoseconds/1e6/recorder.gpuSampleBlockCount);break;}
                            }
                        }finally{recorder.enabled=false;cmd.Release();}
                    }
                    results.Add(new{name=s.name,shape=s,route,pass,warmupMaxAbs=warmupError,logicalMACs=(long)s.OutWidth*s.OutHeight*s.outputChannels*s.inputChannels*s.kernel*s.kernel,gpuMilliseconds=gpu.ToArray(),gpuMedianMs=gpu.Count>0?(double?)Median(gpu):null,dispatchAndReadbackMilliseconds=wall.ToArray(),dispatchAndReadbackMedianMs=Median(wall),outputFormat=f.floating.graphicsFormat.ToString()});
                    Save("benchmark.json",new{status="running",completed=results.Count,results});
                }
            }
            Save("benchmark.json",new{status="completed",passed=true,device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),nativeInt8Dot=false,gpuRecorderSupported=SystemInfo.supportsGpuRecorder,gpuProfilingRequested=enableGpuProfiler,notes="Resident inputs, identical logical shapes and RGBA32F outputs, 1 warmup + 5 samples. Wall time includes dispatch/readback. Excludes activation quantization, normalization and full diffusion. Possible concurrent game GPU load; no VRChat client launched. GPU timestamps requested only by explicit profiled benchmark.",results});
        }catch(Exception e){Save("benchmark.json",new{status="failed",error=e.ToString(),results});UnityEngine.Debug.LogException(e);}
        finally{Profiler.enabled=profilerWasEnabled;Profiler.SetAreaEnabled(ProfilerArea.GPU,gpuAreaWasEnabled);singleRunLock.Dispose();}
    }
    static double Median(List<double> values){var a=values.OrderBy(x=>x).ToArray();return a[a.Length/2];}
}
