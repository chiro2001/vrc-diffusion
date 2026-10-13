"""Small class-conditional pixel DDPM. Fixed graph intentionally maps to fragment passes."""
import math
import torch
from torch import nn
from torch.nn import functional as F

# (input channels, output channels, size, stride, upsample, skip layer)
LAYERS = [(1,16,32,1,1,-1),(16,16,32,1,1,-1),
          (16,32,16,2,1,-1),(32,32,16,1,1,-1),
          (32,64,8,2,1,-1),(64,64,8,1,1,-1),
          (64,32,16,1,2,-1),(64,32,16,1,1,3),
          (32,16,32,1,2,-1),(32,16,32,1,1,1),
          (16,1,32,1,1,-1)]
TIMESTEPS = 256

class DigitDenoiser(nn.Module):
    def __init__(self,normalize=False,classes=10):
        super().__init__()
        self.classes=classes
        self.class_embedding = nn.Embedding(classes,32)
        self.time_mlp = nn.Sequential(nn.Linear(32,64),nn.SiLU(),nn.Linear(64,64),nn.SiLU())
        self.convs = nn.ModuleList([nn.Conv2d(ci,co,3,stride=s,padding=1) for ci,co,_,s,_,_ in LAYERS])
        self.normalize=normalize
        self.norms=nn.ModuleList([nn.BatchNorm2d(co) if normalize and i<len(LAYERS)-1 else nn.Identity() for i,(_,co,_,_,_,_) in enumerate(LAYERS)])
        self.conditions = nn.ModuleList([nn.Linear(64,co) for _,co,_,_,_,_ in LAYERS[:-1]])
        nn.init.zeros_(self.convs[-1].weight)
        nn.init.zeros_(self.convs[-1].bias)

    def conditioning(self,t,y):
        frequencies = torch.exp(-math.log(10000)*torch.arange(16,device=t.device)/15)
        phase=t.float()[:,None]*frequencies[None,:]
        emb=torch.cat([phase.sin(),phase.cos()],1)+self.class_embedding(y)
        emb=self.time_mlp(emb)
        return [c(emb) for c in self.conditions]

    def forward(self,x,t,y,return_layers=False):
        conditions=self.conditioning(t,y)
        states=[]
        for i,(_,_,_,_,up,skip) in enumerate(LAYERS):
            if up>1: x=F.interpolate(x,scale_factor=up,mode='nearest')
            if skip>=0: x=torch.cat([x,states[skip]],1)
            x=self.norms[i](self.convs[i](x))
            if i<len(conditions): x=F.silu(x+conditions[i][:,:,None,None])
            states.append(x)
        return states if return_layers else x

def alpha_schedule(device='cpu'):
    t=torch.linspace(0,1,TIMESTEPS+1,device=device)
    a=torch.cos((t+.008)/1.008*math.pi/2).square()
    betas=(1-a[1:]/a[:-1]).clamp(.0001,.999)
    return torch.cumprod(1-betas,0)

@torch.no_grad()
def sample(model,y,seed=1234,steps=32,device='cpu',initial=None,guidance=3.):
    gen=torch.Generator(device=device).manual_seed(seed)
    x=torch.randn(len(y),1,32,32,generator=gen,device=device) if initial is None else initial.to(device).clone()
    schedule=alpha_schedule(device)
    times=(torch.arange(steps,device=device).flip(0)*(TIMESTEPS//steps)) if model.classes>10 else torch.linspace(TIMESTEPS-1,0,steps,device=device).round().long()
    history=[x.cpu()]
    for j,t in enumerate(times):
        tt=torch.full((len(y),),int(t),device=device,dtype=torch.long)
        eps=model(x,tt,y)
        if model.classes>10:
            base=model(x,tt,torch.full_like(y,10))
            eps=base+guidance*(eps-base)
        a=schedule[t]
        x0=((x-(1-a).sqrt()*eps)/a.sqrt()).clamp(-1,1)
        if j+1<len(times):
            ap=schedule[times[j+1]]
            # Clipped x0 with epsilon recomputed, matching shader DDIM eta=0.
            corrected=(x-a.sqrt()*x0)/(1-a).sqrt()
            x=ap.sqrt()*x0+(1-ap).sqrt()*corrected
        else: x=x0
        history.append(x.cpu())
    return x,history
