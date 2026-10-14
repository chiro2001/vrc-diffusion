"""Export TeaPearce pretrained MNIST to an explicit fragment-shader operator graph."""
import argparse,hashlib,json
from pathlib import Path
import numpy as np
import torch
from torch.nn import functional as F
from tea_model import load_model,sample

p=argparse.ArgumentParser();p.add_argument('--checkpoint',required=True);p.add_argument('--output',required=True);a=p.parse_args()
torch.set_num_threads(4)
m,checkpoint=load_model(a.checkpoint);out=Path(a.output);out.mkdir(parents=True,exist_ok=True)
parts=[];nodes=[];offset=0
x=torch.randn(1,1,28,28,generator=torch.Generator().manual_seed(1234));values=[]

def append(t):
    global offset
    z=np.asarray(t.detach().cpu() if isinstance(t,torch.Tensor) else t,dtype='<f4').reshape(-1)
    at=offset;parts.append(z);offset+=len(z);return at

def emit(name,op,v,primary=-1,skip=-1,**kwargs):
    entry=dict(name=name,op=op,primary=primary,skip=skip,size=v.shape[-1],outputChannels=v.shape[1],
               inputSize=28 if primary<0 else values[primary].shape[-1],primaryChannels=1 if primary<0 else values[primary].shape[1],
               inputChannels=1 if primary<0 else values[primary].shape[1],weightOffset=-1,biasOffset=-1,conditionOffset=-1,scaleOffset=-1,kernel=3,stride=1,padding=1,activation=0,residualScale=1.)
    entry.update(kwargs);nodes.append(entry);values.append(v);return len(nodes)-1

def value(i):return x if i<0 else values[i]

def conv(name,seq,primary,skip=-1,transpose=False,context=None):
    cv=seq[0] if isinstance(seq,torch.nn.Sequential) else seq
    inp=value(primary)
    if context:
        ce,te=context
        c=-F.one_hot(torch.arange(10),10).float();c=torch.cat([c,torch.zeros(1,10)])
        ct=ce(c)
        ts=te(torch.arange(401).float()/400)
        lutScale=ct[:,None,:].expand(11,401,-1)
        lutBias=ts[None,:,:].expand(11,401,-1)
        inp=inp*ct[3].view(1,-1,1,1)+ts[128].view(1,-1,1,1)
        scaleOffset=append(lutScale);conditionOffset=append(lutBias)
    else:scaleOffset=conditionOffset=-1
    if skip>=0:inp=torch.cat([inp,value(skip)],1)
    w=cv.weight.detach();b=cv.bias.detach()
    activation=0
    if isinstance(seq,torch.nn.Sequential) and len(seq)>1 and isinstance(seq[1],torch.nn.BatchNorm2d):
        norm=seq[1];scale=norm.weight/torch.sqrt(norm.running_var+norm.eps)
        w=w*scale[:,None,None,None];b=(b-norm.running_mean)*scale+norm.bias;activation=2
    if transpose:
        v=F.conv_transpose2d(inp,cv.weight,cv.bias,stride=cv.stride,padding=cv.padding)
        w=w.transpose(0,1)
    else:v=F.conv2d(inp,w,b,stride=cv.stride,padding=cv.padding)
    if activation==2:v=F.gelu(v)
    return emit(name,1 if transpose else 0,v,primary,skip,inputChannels=inp.shape[1],kernel=cv.kernel_size[0],stride=cv.stride[0],padding=cv.padding[0],weightOffset=append(w),biasOffset=append(b),activation=activation,conditionOffset=conditionOffset,scaleOffset=scaleOffset)

def block(name,b,primary):
    i=conv(name+'.conv1',b.conv1,primary);j=conv(name+'.conv2',b.conv2,i)
    if b.is_res:
        res=primary if b.same_channels else i
        j=emit(name+'.residual',2,(value(res)+value(j))/1.414,j,res,residualScale=1/1.414)
    return j

def pool(name,primary,average=False):
    inp=value(primary);v=F.gelu(F.avg_pool2d(inp,7)) if average else F.max_pool2d(inp,2)
    return emit(name,4 if average else 3,v,primary,kernel=7 if average else 2,padding=0,stride=7 if average else 2,activation=2 if average else 0)

def norm(name,n,primary):
    inp=value(primary);group=inp.reshape(1,n.num_groups,-1)
    mean=group.mean(-1);var=group.var(-1,unbiased=False)
    stats=torch.cat([mean,var],1)[:,:,None,None]
    s=emit(name+'.stats',5,stats,primary,groups=n.num_groups)
    v=F.relu(n(inp))
    return emit(name,6,v,primary,s,groups=n.num_groups,epsilon=n.eps,weightOffset=append(n.weight),biasOffset=append(n.bias),activation=3)

with torch.no_grad():
    initial=block('init',m.init_conv,-1)
    d1=pool('down1.pool',block('down1',m.down1.model[0],initial))
    d2=pool('down2.pool',block('down2',m.down2.model[0],d1))
    vec=pool('average',d2,True)
    up=norm('up0.norm',m.up0[1],conv('up0.transpose',m.up0[0],vec,transpose=True))
    for index,(module,skip,condition) in enumerate([(m.up1,d2,(m.contextembed1,m.timeembed1)),(m.up2,d1,(m.contextembed2,m.timeembed2))]):
        up=conv(f'up{index+1}.transpose',module.model[0],up,skip,True,condition)
        up=block(f'up{index+1}.block1',module.model[1],up)
        up=block(f'up{index+1}.block2',module.model[2],up)
    up=conv('out.conv1',m.out[0],up,initial)
    up=norm('out.norm',m.out[1],up)
    up=conv('out.final',m.out[3],up)
    expected=m(x,torch.tensor([3]),torch.tensor([128/400]),torch.tensor([0]))
    error=(value(up)-expected).abs().max().item()
    if error>.0001:raise RuntimeError(f'Graph differs from source: {error}')
    data=np.concatenate(parts);raw=data.tobytes()
    (out/'weights.bytes').write_bytes(raw)
    spec=dict(formatVersion=2,name='TeaPearce MNIST (pretrained)',source='https://github.com/TeaPearce/Conditional_Diffusion_MNIST',sourceRevision='657b49854bbf844caa5580177243ae607c79339f',license='MIT',checkpointSHA256=hashlib.sha256(Path(a.checkpoint).read_bytes()).hexdigest(),modelVersion=int.from_bytes(hashlib.sha256(raw).digest()[:4],'little')&0x7fffffff,weightCount=len(data),textureWidth=2048,size=28,timesteps=401,timestepMin=1,timestepMax=400,timestepSpacing='linspace',classifierFreeGuidance=True,guidanceDefault=3.,clipPrediction=False,valueMin=0.,valueMax=1.,alphaCumprod=checkpoint['alphabar_t'].tolist(),layers=nodes,graphReferenceMaxAbs=error)
    (out/'model.json').write_text(json.dumps(spec,indent=2))
    (out/'reference-input.bytes').write_bytes(x.numpy().astype('<f4').tobytes())
    for i,v in enumerate(values):(out/f'reference-layer-{i:02d}.bytes').write_bytes(v.numpy().astype('<f4').tobytes())
    final,hist=sample(m,x,torch.tensor([3]),64,schedule=checkpoint['alphabar_t'])
    for j,v in enumerate(hist):(out/f'reference-step-{j:02d}.bytes').write_bytes(v.numpy().astype('<f4').tobytes())
    (out/'reference-final.bytes').write_bytes(final.numpy().astype('<f4').tobytes())
    print(json.dumps(dict(nodes=len(nodes),bytes=len(raw),graphMaxAbs=error)),flush=True)
