"""GPU reference for the explicit graph and Shader-style dynamic W8A8.
Run in the isolated reference environment; no training or global package changes.
"""
import argparse,json,time,math
from pathlib import Path
import torch
import torch.nn.functional as F
from sd_graph import build_graphs

def away(x):return x.sign()*torch.floor(x.abs()+.5)

class Executor:
    def __init__(self,module,graph,quantized=False,taps=()):
        self.state=module.state_dict();self.graph=graph;self.quantized=quantized
        for key,value in list(self.state.items()):
            for modern,old in [(".to_q.",".query."),(".to_k.",".key."),(".to_v.",".value."),(".to_out.0.",".proj_attn.")]:
                if "decoder.mid_block.attentions" in key and modern in key:self.state[key.replace(modern,old)]=value
        self.qweights={};self.taps=set(taps);self.saved={}
        self.last={}
        for i,n in enumerate(graph['nodes']):
            for k in ('a','b','c'):
                if n[k]>=0:self.last[n[k]]=i
    def weight(self,name):
        w=self.state[name+'.weight']
        if not self.quantized:return w
        if name not in self.qweights:
            wf=w.float();scale=wf.abs().flatten(1).amax(1).clamp_min(1e-10)/127
            q=away(wf/scale.reshape((-1,)+(1,)*(w.ndim-1))).clamp(-127,127).to(torch.int8)
            self.qweights[name]=(q,scale)
        return self.qweights[name]
    @torch.no_grad()
    def __call__(self,x,condition=None,timestep=1):
        freq=torch.exp(-math.log(10000)*torch.arange(160,device=x.device,dtype=torch.float32)/160)
        temb=torch.cat([torch.cos(freq*timestep),torch.sin(freq*timestep)]).reshape(1,320,1,1).to(x.dtype)
        vals={-1:x,-2:condition,-3:temb}
        for i,n in enumerate(self.graph['nodes']):
            z=vals[n['a']];b=vals.get(n['b']);op=n['op'];name=n.get('weight')
            if op==0:
                bias=self.state.get(name+'.bias');kernel=n['kernel']
                # Matrix rows are represented as [1,C,77,1] in this graph.
                if self.quantized:
                    scale=z.float().abs().amax().clamp_min(1e-10)/127
                    qa=away(z.float()/scale).clamp(-127,127)
                    qw,ws=self.weight(name)
                    if kernel==1 and qw.ndim==2:qw=qw[:,:,None,None]
                    accum=F.conv2d(qa,qw.float(),stride=n['stride'],padding=n['padding'])
                    v=accum*(scale*ws.reshape(1,-1,1,1))
                    if bias is not None:v=v+bias.float().reshape(1,-1,1,1)
                    v=v.half()
                else:
                    w=self.weight(name)
                    if w.ndim==2:w=w[:,:,None,None]
                    if z.device.type=='cpu':v=F.conv2d(z.float(),w.float(),None if bias is None else bias.float(),stride=n['stride'],padding=n['padding']).half()
                    else:v=F.conv2d(z,w,bias,stride=n['stride'],padding=n['padding'])
            elif op==1:
                gamma=self.state[name+'.weight'].float();beta=self.state[name+'.bias'].float()
                if n['groups']:
                    v=F.group_norm(z.float(),n['groups'],gamma,beta,n['epsilon']).half()
                else:
                    v=F.layer_norm(z.permute(0,2,3,1).float(),(z.shape[1],),gamma,beta,n['epsilon']).permute(0,3,1,2).half()
            elif op==2:v=F.silu(z.float()).half() if n['activation']==1 else F.gelu(z.float()).half()
            elif op==3:v=(z.float()+b.float()).half()
            elif op==4:v=torch.cat([z,b],1)
            elif op==5:v=F.interpolate(z,scale_factor=2,mode='nearest')
            elif op==6:
                heads=n['heads'];c=vals[n['c']];d=z.shape[1]//heads
                def seq(t):return t.reshape(1,heads,d,-1).transpose(2,3)
                # SDPA implementation uses stable tiled softmax and never saves N*N scores.
                v=F.scaled_dot_product_attention(seq(z),seq(b),seq(c)).transpose(2,3).reshape_as(z)
            elif op==7:
                a,g=z.chunk(2,1);v=(a.float()*F.gelu(g.float())).half()
            elif op==8:v=(z.float()*n['multiplier']).half()
            else:raise ValueError(op)
            if tuple(v.shape)!=(1,n['channels'],n['h'],n['w']):raise ValueError((i,n['name'],v.shape,n))
            vals[i]=v
            if i in self.taps:self.saved[i]=v.float().cpu()
            for k in ('a','b','c'):
                dep=n[k]
                if dep>=0 and self.last[dep]==i:vals.pop(dep,None)
        return vals[self.graph['output']]

def save_tensor(p,t):
    # Shader layout: flat NHWC including zero lanes when C is not a multiple of 4.
    a=t.detach().float().cpu().permute(0,2,3,1)
    if a.shape[-1]%4:a=F.pad(a,(0,4-a.shape[-1]%4))
    p.write_bytes(a.contiguous().numpy().astype('<f4').tobytes())

@torch.no_grad()
def main():
    from diffusers import UNet2DConditionModel,AutoencoderKL,DDIMScheduler
    from PIL import Image
    import numpy as np
    p=argparse.ArgumentParser();p.add_argument('--source',required=True);p.add_argument('--conditions',required=True);p.add_argument('--output',required=True)
    p.add_argument('--size',type=int,default=256);p.add_argument('--steps',type=int,default=20);p.add_argument('--seed',type=int,default=1234)
    p.add_argument('--device',default='cuda',choices=['cuda','cpu']);p.add_argument('--threads',type=int,default=4);p.add_argument('--initial-bytes');p.add_argument('--only-check',action='store_true');p.add_argument('--preset',type=int,default=0);p.add_argument('--quantized',action='store_true');p.add_argument('--check-graph',action='store_true');a=p.parse_args()
    torch.backends.cuda.matmul.allow_tf32=False;torch.backends.cudnn.allow_tf32=False
    torch.set_grad_enabled(False);torch.set_num_threads(a.threads);device=a.device;out=Path(a.output);out.mkdir(parents=True,exist_ok=True)
    source=Path(a.source);graphs=build_graphs(source,a.size)
    def emb(index):
        raw=np.fromfile(Path(a.conditions)/f'embedding-{index}.bytes',dtype='<f4').reshape(77,768)
        return torch.from_numpy(raw).to(device,torch.float16).T.reshape(1,768,77,1)
    cond=emb(a.preset+1);negative=emb(0)
    unet=UNet2DConditionModel.from_pretrained(source/'unet',torch_dtype=torch.float16,variant='fp16',use_safetensors=True).to(device).eval()
    exe=Executor(unet,graphs['unet'],a.quantized,taps=(tuple(range(52))+(100,200,300,400,500,608)) if a.check_graph else ())
    g=torch.Generator(device=device).manual_seed(a.seed)
    x=torch.randn((1,4,a.size//8,a.size//8),generator=g,device=device,dtype=torch.float32).half()
    if a.initial_bytes:
        initial=np.fromfile(a.initial_bytes,dtype='<f4').reshape(1,a.size//8,a.size//8,4)
        x=torch.from_numpy(initial).permute(0,3,1,2).to(device,torch.float16)
    save_tensor(out/'initial.bytes',x)
    scheduler=DDIMScheduler.from_pretrained(source/'scheduler');scheduler.set_timesteps(a.steps)
    checks={}
    if a.check_graph:
        e=exe(x,cond,int(scheduler.timesteps[0]));base=unet(x,scheduler.timesteps[0],encoder_hidden_states=cond.flatten(2).transpose(1,2)).sample
        diff=e.float()-base.float();checks=dict(maxAbs=diff.abs().max().item(),rmse=diff.square().mean().sqrt().item(),referenceRMS=base.float().square().mean().sqrt().item())
        save_tensor(out/'graph-epsilon.bytes',e);save_tensor(out/'baseline-epsilon.bytes',base)
        for i,v in exe.saved.items():save_tensor(out/f'node-{i}.bytes',v)
        print(json.dumps(dict(graphCheck=checks,quantized=a.quantized)),flush=True)
        if not a.quantized and checks['rmse']>.025:raise RuntimeError(f'Explicit graph failed: {checks}')
        if a.only_check:return
    exe.taps.clear();exe.saved.clear()
    started=time.monotonic();history=[]
    for i,t in enumerate(scheduler.timesteps):
        n=exe(x,negative,int(t));c=exe(x,cond,int(t));eps=(n.float()+7.5*(c.float()-n.float())).half()
        x=scheduler.step(eps,t,x,eta=0).prev_sample.half()
        save_tensor(out/f'latent-{i:02d}.bytes',x)
        print(json.dumps(dict(step=i+1,timestep=int(t),seconds=time.monotonic()-started,latentRMS=x.float().square().mean().sqrt().item())),flush=True)
    del exe,unet;torch.cuda.empty_cache()
    vae=AutoencoderKL.from_pretrained(source/'vae',torch_dtype=torch.float16,variant='fp16',use_safetensors=True).to(device).eval()
    vexe=Executor(vae,graphs['vae'],a.quantized)
    rgb=vexe(x)
    if a.check_graph:
        baseline=vae.decode(x/.18215).sample;diff=rgb.float()-baseline.float()
        checks['vae']=dict(maxAbs=diff.abs().max().item(),rmse=diff.square().mean().sqrt().item())
        print(json.dumps(dict(vaeCheck=checks['vae'])),flush=True)
    if not torch.isfinite(rgb).all():raise RuntimeError('Non-finite VAE output')
    save_tensor(out/'rgb.bytes',rgb)
    pixels=((rgb.float().cpu()[0].permute(1,2,0)/2+.5).clamp(0,1)*255).round().byte().numpy()
    Image.fromarray(pixels).save(out/'final.png')
    (out/'result.json').write_text(json.dumps(dict(size=a.size,steps=a.steps,seed=a.seed,preset=a.preset,quantized=a.quantized,seconds=time.monotonic()-started,checks=checks),indent=2))

if __name__=='__main__':main()
