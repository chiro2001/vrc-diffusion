"""Evaluate actual map seed noise across all classes. No training on the game PC."""
import argparse,json,time
from pathlib import Path
import numpy as np
import torch
from PIL import Image,ImageDraw
from tea_model import load_model,sample
from seed_noise import noise
p=argparse.ArgumentParser();p.add_argument('--checkpoint',required=True);p.add_argument('--output',required=True);p.add_argument('--device',default='npu:0');p.add_argument('--steps',type=int,nargs='+',default=[32,64,128]);a=p.parse_args()
if a.device.startswith('npu'):
 import torch_npu
 torch.npu.set_device(a.device)
torch.set_num_threads(4)
out=Path(a.output);out.mkdir(parents=True,exist_ok=True)
m,ckpt=load_model(a.checkpoint,a.device)
seeds=[1,42,1234,31415,271828,20261014,987654320,987654321,999999999,8675309]
y=torch.arange(10,device=a.device).repeat(len(seeds))
x=torch.from_numpy(np.stack([noise(s,28) for s in seeds for _ in range(10)])[:,None]).to(a.device)
reports=[]
for steps in a.steps:
 start=time.time()
 final,hist=sample(m,x,y,steps,3.,ckpt['alphabar_t'].to(a.device))
 if a.device.startswith('npu'):torch.npu.synchronize()
 raw=final.cpu().numpy();np.save(out/f'samples-{steps}.npy',raw)
 canvas=Image.new('RGB',(10*96+85,len(seeds)*96+35),'#14212d');d=ImageDraw.Draw(canvas)
 for c in range(10):d.text((85+c*96+40,8),str(c),fill='white')
 for row,seed in enumerate(seeds):
  d.text((2,35+row*96+42),str(seed),fill='white')
  for c in range(10):
   im=Image.fromarray((raw[row*10+c,0].clip(0,1)*255).astype('uint8')).resize((84,84),Image.Resampling.NEAREST)
   canvas.paste(im,(85+c*96+6,35+row*96+6))
 canvas.save(out/f'quality-{steps}.png')
 occupancy=(raw[:,0]>.4).mean((1,2))
 reports.append(dict(steps=steps,seconds=time.time()-start,seeds=seeds,samples=len(y),finite=bool(np.isfinite(raw).all()),meanOccupancy=float(occupancy.mean()),extremeOccupancyCount=int(((occupancy<.02)|(occupancy>.5)).sum()),range=[float(raw.min()),float(raw.max())]))
 (out/'evaluation.json').write_text(json.dumps(reports,indent=2));print(json.dumps(reports[-1]),flush=True)
