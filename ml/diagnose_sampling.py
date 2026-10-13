"""Compare sampler choices on one unchanged checkpoint, to diagnose generation quality."""
import argparse,json
from pathlib import Path
import numpy as np
import torch
from PIL import Image
from model import DigitDenoiser,alpha_schedule,TIMESTEPS
p=argparse.ArgumentParser();p.add_argument('--checkpoint',default='Training/mnist.pt');p.add_argument('--output',default='Training/sampler-diagnosis.png');a=p.parse_args()
torch.set_num_threads(2)
checkpoint=torch.load(a.checkpoint,map_location='cpu',weights_only=True);m=DigitDenoiser(normalize=checkpoint.get('normalize',False),classes=checkpoint.get('classes',10)).cuda();m.load_state_dict(checkpoint['model']);m.eval()
s=alpha_schedule('cuda');y=torch.arange(10,device='cuda');initial=torch.randn(10,1,32,32,device='cuda',generator=torch.Generator(device='cuda').manual_seed(31415))
configs=[('current 32',torch.linspace(255,0,32).round().long(),True),('leading 32',torch.arange(32).flip(0)*8,True),('original eps 32',torch.linspace(255,0,32).round().long(),False),('full 256',torch.arange(256).flip(0),True),('leading no clip',torch.arange(32).flip(0)*8,None),('average CFG 5',torch.arange(32).flip(0)*8,None)]
rows=[]
with torch.no_grad():
 for name,ts,correct in configs:
  x=initial.clone()
  for j,t in enumerate(ts):
   t=int(t);eps=m(x,torch.full((10,),t,device='cuda',dtype=torch.long),y)
   if name=='average CFG 5':
    marginal=m(x.repeat_interleave(10,dim=0),torch.full((100,),t,device='cuda',dtype=torch.long),torch.arange(10,device='cuda').repeat(10)).reshape(10,10,1,32,32).mean(1)
    eps=marginal+5*(eps-marginal)
   ac=s[t];x0=(x-(1-ac).sqrt()*eps)/ac.sqrt()
   if correct is not None:x0=x0.clamp(-1,1)
   if j+1<len(ts):
    ap=s[int(ts[j+1])];e=(x-ac.sqrt()*x0)/(1-ac).sqrt() if correct is True else eps
    x=ap.sqrt()*x0+(1-ap).sqrt()*e
   else:x=x0
  rows.append(((x[:,0].cpu().numpy()+1)*127.5).clip(0,255).astype('uint8'))
  print(name,flush=True)
grid=np.stack(rows).transpose(0,2,1,3).reshape(len(rows)*32,320)
Image.fromarray(grid).resize((960,len(rows)*96)).save(a.output)
print(json.dumps({'iteration':checkpoint['iteration'],'rows':[c[0] for c in configs],'output':a.output}),flush=True)
