"""Export the existing checkpoint's VAE encoder and CLIP for world-local inference."""
import argparse,hashlib,json,math
from pathlib import Path
import numpy as np
from PIL import Image
from safetensors import safe_open
from sd_graph import Graph,shapes_from_file
from export_sd import round_away
from export_sd_conditions import resolve_source_provenance

def main():
    p=argparse.ArgumentParser();p.add_argument('--source',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    source=a.source;out=a.output;out.mkdir(parents=True,exist_ok=True)
    clipfile=next((source/'text_encoder').glob('*.safetensors'))
    provenance,_=resolve_source_provenance(source,clipfile)
    graphs={str(s):{} for s in (256,384,512)};weights={}
    for part,component,method in [('encoder','vae','encoder'),('clip','text_encoder','clip')]:
        file=next((source/component).glob('*.safetensors'));shapes=shapes_from_file(file)
        # The converted VAE keeps original query/key/value attention names.
        for size in graphs:
            g=Graph(shapes,int(size));z=getattr(g,method)()
            graphs[size][part]={'nodes':g.nodes,'output':z.node,'externalShift':7 if part=='clip' else 10}
        nodes=graphs['256'][part]['nodes'];names=sorted({n['weight'] for n in nodes if 'weight' in n})
        with safe_open(file,framework='np',device='cpu') as f:
            keys=set(f.keys())
            for name in names:
                key=part+'.'+name;stem=key.replace('.','_');w=f.get_tensor(name+'.weight').astype(np.float32)
                b=f.get_tensor(name+'.bias').astype(np.float32) if name+'.bias' in keys else np.zeros(w.shape[0],np.float32)
                if w.ndim==1:
                    param=np.stack([w,b,np.zeros_like(w),np.zeros_like(w)],-1)
                    entry=dict(parameterFile=stem+'.bytes',channels=len(w),kind='norm')
                else:
                    if w.ndim==2:w=w[:,:,None,None]
                    oc,ic,kh,kw=w.shape;scale=np.maximum(np.abs(w).reshape(oc,-1).max(1)/127,1e-12)
                    q=np.clip(round_away(w/scale[:,None,None,None]),-127,127).astype(np.int16)
                    padded=np.zeros((oc,kh,kw,math.ceil(ic/4)*4),np.int16);padded[:,:,:,:ic]=q.transpose(0,2,3,1)
                    pixels=(padded.reshape(oc,kh,kw,-1,4).transpose(1,2,3,0,4).reshape(-1,4)+128).astype(np.uint8)
                    width=min(2048,1<<max(0,(len(pixels)-1).bit_length()));height=math.ceil(len(pixels)/width)
                    rgba=np.full((height*width,4),128,np.uint8);rgba[:len(pixels)]=pixels
                    dest=out/(stem+'.png');Image.fromarray(rgba.reshape(height,width,4)).save(dest,compress_level=3)
                    param=np.stack([scale,b,np.zeros_like(b),np.zeros_like(b)],-1)
                    entry=dict(parameterFile=stem+'.bytes',weightFile=dest.name,channels=oc,inputChannels=ic,kernel=kh,width=width,height=height,sha256=hashlib.sha256(dest.read_bytes()).hexdigest(),kind='w8a8')
                (out/entry['parameterFile']).write_bytes(param.astype('<f4').tobytes());weights[key]=entry
            if part=='clip':
                for name in ('token_embedding','position_embedding'):
                    array=f.get_tensor('text_model.embeddings.'+name+'.weight').astype('<f2')
                    flat=array.reshape(-1,4);width=2048 if name=='token_embedding' else 1024;height=math.ceil(len(flat)/width)
                    padded=np.zeros((width*height,4),'<f2');padded[:len(flat)]=flat
                    (out/(name+'.bytes')).write_bytes(padded.tobytes())
                    weights[name]=dict(kind='embedding',file=name+'.bytes',width=width,height=height,shape=list(array.shape))
        print(json.dumps({'part':part,'weights':len(names),'nodes':len(nodes)}),flush=True)
    vocab=json.loads((source/'tokenizer/vocab.json').read_text())
    merges=[line for line in (source/'tokenizer/merges.txt').read_text().splitlines() if line and not line.startswith('#')]
    (out/'vocab.json').write_text(json.dumps(vocab,ensure_ascii=False,separators=(',',':')))
    (out/'merges.json').write_text(json.dumps({line:i for i,line in enumerate(merges)},ensure_ascii=False,separators=(',',':')))
    from transformers.models.clip.tokenization_clip import bytes_to_unicode
    (out/'byte-encoder.txt').write_text(''.join(bytes_to_unicode()[i] for i in range(256)))
    spec=dict(formatVersion=1,source=provenance,weightLayout='KY_KX_IC4_OC_LANES',weights=weights,graphs=graphs)
    (out/'extensions.json').write_text(json.dumps(spec,separators=(',',':')))
    print(json.dumps({'output':str(out),'files':len(list(out.iterdir()))}),flush=True)

if __name__=='__main__':main()
