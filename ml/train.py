"""Train without torchvision; MNIST IDX download, compact conditional U-Net, EMA."""
import argparse, gzip, json, time, urllib.request
from pathlib import Path
import numpy as np
import torch
from torch.nn import functional as F
from model import DigitDenoiser,TIMESTEPS,alpha_schedule,sample

p=argparse.ArgumentParser()
p.add_argument('--iterations',type=int,default=18000)
p.add_argument('--batch',type=int,default=128)
p.add_argument('--device',default='cuda')
p.add_argument('--output',default='Training')
p.add_argument('--resume',default=None)
p.add_argument('--threads',type=int,default=4)
p.add_argument('--pause-ms',type=float,default=20,help='Yield between iterations to leave GPU time for interactive applications')
a=p.parse_args()
torch.set_num_threads(a.threads)
torch.manual_seed(42)
out=Path(a.output);out.mkdir(parents=True,exist_ok=True)

def idx(name,offset,shape):
    path=out/name
    if not path.exists(): urllib.request.urlretrieve('https://storage.googleapis.com/cvdf-datasets/mnist/'+name,path)
    return np.frombuffer(gzip.decompress(path.read_bytes()),dtype=np.uint8,offset=offset).copy().reshape(shape)
images=torch.from_numpy(idx('train-images-idx3-ubyte.gz',16,(60000,1,28,28))).float().to(a.device)/127.5-1
images=F.pad(images,(2,2,2,2),value=-1)
labels=torch.from_numpy(idx('train-labels-idx1-ubyte.gz',8,(60000,))).long().to(a.device)
model=DigitDenoiser(normalize=True,classes=11).to(a.device)
if a.resume:
    previous=torch.load(a.resume,map_location='cpu',weights_only=True)['model']
    if previous['class_embedding.weight'].shape[0]==10:
        old=previous['class_embedding.weight'];previous['class_embedding.weight']=torch.cat([old,old.mean(0,keepdim=True)],0)
    model.load_state_dict(previous)
ema=DigitDenoiser(normalize=True,classes=11).to(a.device);ema.load_state_dict(model.state_dict());ema.eval()
optimizer=torch.optim.AdamW(model.parameters(),lr=0.0003,weight_decay=0.00001)
alpha=alpha_schedule(a.device)
start=time.time()
for it in range(1,a.iterations+1):
    inds=torch.randint(len(images),(a.batch,),device=a.device)
    clean=images[inds];y=labels[inds]
    y=torch.where(torch.rand(a.batch,device=a.device)<.1,torch.full_like(y,10),y)
    t=torch.randint(TIMESTEPS,(a.batch,),device=a.device)
    noise=torch.randn_like(clean)
    ac=alpha[t][:,None,None,None]
    noisy=ac.sqrt()*clean+(1-ac).sqrt()*noise
    loss=F.mse_loss(model(noisy,t,y),noise)
    optimizer.zero_grad(set_to_none=True);loss.backward()
    torch.nn.utils.clip_grad_norm_(model.parameters(),1.)
    optimizer.step()
    decay=min(.999, (1+it)/(10+it))
    with torch.no_grad():
        for ep,mp in zip(ema.parameters(),model.parameters()): ep.lerp_(mp,1-decay)
        for eb,mb in zip(ema.buffers(),model.buffers()): eb.copy_(mb)
    if a.pause_ms>0: time.sleep(a.pause_ms/1000)
    if it%100==0:
        entry={'iteration':it,'loss':float(loss),'seconds':round(time.time()-start,2)}
        print(json.dumps(entry),flush=True)
        (out/'progress.json').write_text(json.dumps(entry))
    if it%2000==0 or it==a.iterations:
        torch.save({'model':ema.cpu().state_dict(),'iteration':it,'loss':float(loss),'normalize':True,'classes':11},out/'mnist.pt')
        ema.to(a.device)
        from PIL import Image
        ys=torch.arange(10,device=a.device).repeat(4)
        result,hist=sample(ema,ys,seed=31415,steps=32,device=a.device)
        ar=((result[:,0].cpu().numpy()+1)*127.5).clip(0,255).astype('uint8')
        grid=ar.reshape(4,10,32,32).transpose(0,2,1,3).reshape(128,320)
        Image.fromarray(grid).resize((960,384)).save(out/f'samples-{it:06d}.png')
        if a.device=='cuda': torch.cuda.empty_cache()
print('Training complete',flush=True)
