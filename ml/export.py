"""Export raw float32 weights and all timestep/class biases; Unity imports via Pipeline."""
import argparse,json,hashlib
from pathlib import Path
import numpy as np
import torch
from model import DigitDenoiser,LAYERS,TIMESTEPS,alpha_schedule,sample
p=argparse.ArgumentParser();p.add_argument('--checkpoint',required=True);p.add_argument('--output',default='Assets/DiffusionLab/Data');a=p.parse_args()
out=Path(a.output);out.mkdir(parents=True,exist_ok=True)
checkpoint=torch.load(a.checkpoint,map_location='cpu',weights_only=True);m=DigitDenoiser(normalize=checkpoint.get('normalize',False),classes=checkpoint.get('classes',10));m.load_state_dict(checkpoint['model']);m.eval()
parts=[];offset=0;spec=[]
def append(arr):
    global offset
    flat=np.asarray(arr,dtype='<f4').reshape(-1);p=offset;parts.append(flat);offset+=len(flat);return p
with torch.no_grad():
    # LUT indexing: class * T + timestep, then output channel.
    t=torch.arange(TIMESTEPS).repeat(m.classes);y=torch.arange(m.classes).repeat_interleave(TIMESTEPS)
    conditions=m.conditioning(t,y)
    for i,(ci,co,size,stride,up,skip) in enumerate(LAYERS):
        w=m.convs[i].weight.numpy();b=m.convs[i].bias.numpy()
        if isinstance(m.norms[i],torch.nn.BatchNorm2d):
            n=m.norms[i];scale=(n.weight/torch.sqrt(n.running_var+n.eps)).numpy()
            w=w*scale[:,None,None,None]
            b=(b-n.running_mean.numpy())*scale+n.bias.numpy()
        spec.append({'index':i,'inputChannels':ci,'outputChannels':co,'size':size,'stride':stride,'upsample':up,'skip':skip,
                     'weightOffset':append(w),'biasOffset':append(b),
                     'conditionOffset':append(conditions[i].numpy()) if i<len(conditions) else -1})
    packed=np.concatenate(parts)
    (out/'weights.bytes').write_bytes(packed.tobytes())
    data={'formatVersion':1,'name':'MNIST-Conditional-Pixel-DDPM','trainingIteration':checkpoint['iteration'],
          'modelVersion':int.from_bytes(hashlib.sha256(packed.tobytes()).digest()[:4],'little')&0x7fffffff,'normalizationFolded':checkpoint.get('normalize',False),'classifierFreeGuidance':m.classes>10,'guidanceDefault':3.,'timestepSpacing':'leading' if m.classes>10 else 'linspace','timesteps':TIMESTEPS,'size':32,'weightCount':len(packed),'textureWidth':1024,
          'alphaCumprod':alpha_schedule().tolist(),'layers':spec}
    (out/'model.json').write_text(json.dumps(data,indent=2))
    # Portable numeric fixture, independent of GPU RNG.
    x=torch.randn(1,1,32,32,generator=torch.Generator().manual_seed(1234))
    states=m(x,torch.tensor([128]),torch.tensor([3]),return_layers=True)
    (out/'reference-input.bytes').write_bytes(x.numpy().astype('<f4').tobytes())
    for i,state in enumerate(states): (out/f'reference-layer-{i:02d}.bytes').write_bytes(state.numpy().astype('<f4').tobytes())
    final,hist=sample(m,torch.tensor([3]),steps=32,initial=x)
    (out/'reference-final.bytes').write_bytes(final.numpy().astype('<f4').tobytes())
    for j,state in enumerate(hist): (out/f'reference-step-{j:02d}.bytes').write_bytes(state.numpy().astype('<f4').tobytes())
print(json.dumps({'weights':len(packed),'bytes':len(packed)*4,'parameters':sum(p.numel() for p in m.parameters()),'iteration':checkpoint['iteration']}))
