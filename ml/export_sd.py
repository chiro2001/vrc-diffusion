"""Stream SD1.x affine weights into lossless linear RGBA8 INT8 textures.
Only Conv/Linear tensors used by the explicit graph are exported. No full model load.
"""
import argparse,hashlib,json,math,shutil
from pathlib import Path
import numpy as np
from PIL import Image
from safetensors import safe_open
from sd_graph import build_graphs

def round_away(x): return np.sign(x)*np.floor(np.abs(x)+.5)
def export(source,output,sizes=(256,384,512)):
    source=Path(source);output=Path(output);output.mkdir(parents=True,exist_ok=True)
    graphs={str(size):build_graphs(source,size) for size in sizes}
    weights={};total=0;stats=[]
    for part in ('unet','vae'):
        nodes=graphs[str(sizes[0])][part]['nodes']
        names=sorted({n['weight'] for n in nodes if 'weight' in n})
        file=next((source/part).glob('*.safetensors'))
        with safe_open(file,framework='np',device='cpu') as f:
            keys=set(f.keys())
            for name in names:
                key=part+'.'+name;stem=key.replace('.','_');w=f.get_tensor(name+'.weight').astype(np.float32)
                b=f.get_tensor(name+'.bias').astype(np.float32) if name+'.bias' in keys else np.zeros(w.shape[0],np.float32)
                if w.ndim==1:
                    param=np.stack([w,b,np.zeros_like(w),np.zeros_like(w)],axis=-1)
                    entry=dict(parameterFile=stem+'.bytes',channels=len(w),kind='norm')
                else:
                    if w.ndim==2:w=w[:,:,None,None]
                    oc,ic,kh,kw=w.shape;assert kh==kw
                    scale=np.maximum(np.abs(w).reshape(oc,-1).max(1)/127,1e-12)
                    q=np.clip(round_away(w/scale[:,None,None,None]),-127,127).astype(np.int16)
                    padded=np.zeros((oc,kh,kw,math.ceil(ic/4)*4),np.int16)
                    padded[:,:,:,:ic]=q.transpose(0,2,3,1)
                    pixels=(padded.reshape(oc,kh,kw,-1,4).transpose(1,2,3,0,4).reshape(-1,4)+128).astype(np.uint8)
                    width=min(2048,1<<max(0,(len(pixels)-1).bit_length()))
                    height=math.ceil(len(pixels)/width)
                    rgba=np.full((height*width,4),128,np.uint8);rgba[:len(pixels)]=pixels
                    png=output/(stem+'.png');Image.fromarray(rgba.reshape(height,width,4)).save(png,compress_level=3)
                    param=np.stack([scale,b,np.zeros_like(b),np.zeros_like(b)],axis=-1)
                    error=(q.astype(np.float32)*scale[:,None,None,None]-w)
                    entry=dict(parameterFile=stem+'.bytes',weightFile=png.name,channels=oc,
                               inputChannels=ic,kernel=kh,width=width,height=height,
                               sha256=hashlib.sha256(png.read_bytes()).hexdigest(),kind='w8a8')
                    total+=oc*ic*kh*kw
                    stats.append(dict(name=key,maxAbs=float(np.abs(error).max()),rmse=float(np.sqrt(np.mean(error**2)))))
                (output/entry['parameterFile']).write_bytes(param.astype('<f4').tobytes())
                weights[key]=entry
        print(json.dumps(dict(part=part,exported=len(names),parameters=total)),flush=True)
    scheduler=json.loads((source/'scheduler/scheduler_config.json').read_text())
    betas=np.linspace(math.sqrt(scheduler['beta_start']),math.sqrt(scheduler['beta_end']),scheduler['num_train_timesteps'],dtype=np.float32)**2
    alpha=np.cumprod(1-betas,dtype=np.float32)
    spec=dict(formatVersion=1,name='Counterfeit-V3.0 W8A8',source='gsdf/Counterfeit-V3.0',
              revision=json.loads((source/'conversion.json').read_text())['revision'] if (source/'conversion.json').exists() else 'f79f307806896050ac376e46d8081d221b8aa93a',license='CreativeML Open RAIL-M',
              conversion=json.loads((source/'conversion.json').read_text()) if (source/'conversion.json').exists() else None,
              layout='NHWC4',weightLayout='KY_KX_IC4_OC_LANES',activationQuantization='dynamic symmetric per tensor maxabs; ties away from zero; [-127,127]',
              accumulation='int32',weights=weights,graphs=graphs,alphaCumprod=alpha.tolist(),
              scheduler=scheduler,latentScale=.18215,parameterCount=total)
    (output/'model.json').write_text(json.dumps(spec,separators=(',',':')))
    (output/'quantization-stats.json').write_text(json.dumps(stats,indent=2))
    print(json.dumps(dict(parameters=total,weightTextures=sum(v['kind']=='w8a8' for v in weights.values()),files=len(list(output.iterdir())))),flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--source',required=True);p.add_argument('--output',required=True);a=p.parse_args();export(a.source,a.output)
