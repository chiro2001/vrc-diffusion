"""Explicit SD1.x inference graph; independent of diffusers tracing internals.
Tensor layout: one batch, NHWC, four consecutive channels per RGBA texel.
"""
from dataclasses import dataclass

@dataclass(frozen=True)
class Tensor:
    node: int
    h: int
    w: int
    c: int

class Graph:
    def __init__(self, shapes, size=512):
        self.shapes=shapes; self.nodes=[]; self.size=size
        self.latent=Tensor(-1,size//8,size//8,4)
        self.condition=Tensor(-2,77,1,768)
        self.time=Tensor(-3,1,1,320)
    def emit(self,name,op,a,b=None,c=None,shape=None,**attrs):
        h,w,ch=shape or (a.h,a.w,a.c)
        n=dict(name=name,op=op,a=a.node,b=-99 if b is None else b.node,
               c=-99 if c is None else c.node,h=h,w=w,channels=ch,
               inH=a.h,inW=a.w,inChannels=a.c,
               bH=0 if b is None else b.h,bW=0 if b is None else b.w,
               bChannels=0 if b is None else b.c)
        n.update(attrs); self.nodes.append(n)
        return Tensor(len(self.nodes)-1,h,w,ch)
    def affine(self,name,a,stride=1,padding=None):
        shape=self.shapes[name+'.weight'];out,inc=shape[:2]
        k=shape[2] if len(shape)>2 else 1
        if inc!=a.c: raise ValueError((name,shape,a))
        p=k//2 if padding is None else padding
        return self.emit(name,0,a,shape=((a.h+2*p-k)//stride+1,(a.w+2*p-k)//stride+1,out),
                         weight=name,kernel=k,stride=stride,padding=p)
    def norm(self,name,a,groups=32,eps=1e-5):
        assert self.shapes[name+'.weight']==[a.c]
        return self.emit(name,1,a,weight=name,groups=groups,epsilon=eps)
    def act(self,name,a,kind=1): return self.emit(name,2,a,activation=kind)
    def add(self,name,a,b):
        assert a.c==b.c and ((a.h,a.w)==(b.h,b.w) or (b.h,b.w)==(1,1)),(name,a,b)
        return self.emit(name,3,a,b)
    def cat(self,name,a,b):
        assert (a.h,a.w)==(b.h,b.w)
        return self.emit(name,4,a,b,shape=(a.h,a.w,a.c+b.c))
    def resize(self,name,a): return self.emit(name,5,a,shape=(a.h*2,a.w*2,a.c))
    def attn(self,name,q,k,v,heads):
        assert q.c==k.c==v.c and q.c%heads==0
        return self.emit(name,6,q,k,v,heads=heads,keyRows=k.h*k.w)
    def resnet(self,name,a,time=None):
        z=self.act(name+'.silu1',self.norm(name+'.norm1',a,eps=1e-6 if name.startswith('decoder.') else 1e-5))
        z=self.affine(name+'.conv1',z)
        if time is not None:
            z=self.add(name+'.time_add',z,self.affine(name+'.time_emb_proj',time))
        z=self.act(name+'.silu2',self.norm(name+'.norm2',z,eps=1e-6 if name.startswith('decoder.') else 1e-5))
        z=self.affine(name+'.conv2',z)
        shortcut=self.affine(name+'.conv_shortcut',a) if name+'.conv_shortcut.weight' in self.shapes else a
        return self.add(name+'.residual',z,shortcut)
    def transformer(self,name,a):
        z=self.affine(name+'.proj_in',self.norm(name+'.norm',a,eps=1e-6))
        base=name+'.transformer_blocks.0'
        for i,context in [(1,None),(2,self.condition)]:
            b=base+f'.attn{i}'; n=self.norm(base+f'.norm{i}',z,groups=0)
            q=self.affine(b+'.to_q',n)
            k=self.affine(b+'.to_k',n if context is None else context)
            v=self.affine(b+'.to_v',n if context is None else context)
            att=self.attn(b+'.online_softmax',q,k,v,8)
            z=self.add(b+'.residual',self.affine(b+'.to_out.0',att),z)
        n=self.norm(base+'.norm3',z,groups=0)
        ff=self.affine(base+'.ff.net.0.proj',n)
        ff=self.emit(base+'.ff.geglu',7,ff,shape=(ff.h,ff.w,ff.c//2))
        z=self.add(base+'.ff.residual',self.affine(base+'.ff.net.2',ff),z)
        return self.add(name+'.residual',self.affine(name+'.proj_out',z),a)
    def unet(self):
        t=self.affine('time_embedding.linear_1',self.time)
        t=self.act('time_embedding.silu',t)
        t=self.affine('time_embedding.linear_2',t)
        t=self.act('time_embedding.resnet_silu',t)
        z=self.affine('conv_in',self.latent);skips=[z]
        for i in range(4):
            for j in range(2):
                z=self.resnet(f'down_blocks.{i}.resnets.{j}',z,t)
                if i<3:z=self.transformer(f'down_blocks.{i}.attentions.{j}',z)
                skips.append(z)
            if i<3:
                z=self.affine(f'down_blocks.{i}.downsamplers.0.conv',z,stride=2);skips.append(z)
        z=self.resnet('mid_block.resnets.0',z,t)
        z=self.transformer('mid_block.attentions.0',z)
        z=self.resnet('mid_block.resnets.1',z,t)
        for i in range(4):
            for j in range(3):
                z=self.cat(f'up_blocks.{i}.concat.{j}',z,skips.pop())
                z=self.resnet(f'up_blocks.{i}.resnets.{j}',z,t)
                if i>0:z=self.transformer(f'up_blocks.{i}.attentions.{j}',z)
            if i<3:z=self.affine(f'up_blocks.{i}.upsamplers.0.conv',self.resize(f'up_blocks.{i}.resize',z))
        assert not skips
        z=self.affine('conv_out',self.act('conv_norm_out.silu',self.norm('conv_norm_out',z)))
        return z
    def vae(self):
        z=self.emit('latent_scaling',8,self.latent,multiplier=1/.18215)
        z=self.affine('post_quant_conv',z)
        z=self.affine('decoder.conv_in',z)
        z=self.resnet('decoder.mid_block.resnets.0',z)
        n='decoder.mid_block.attentions.0'
        qn=self.norm(n+'.group_norm',z,eps=1e-6)
        q=self.affine(n+'.query',qn);k=self.affine(n+'.key',qn);v=self.affine(n+'.value',qn)
        z=self.add(n+'.residual',self.affine(n+'.proj_attn',self.attn(n+'.online_softmax',q,k,v,1)),z)
        z=self.resnet('decoder.mid_block.resnets.1',z)
        for i in range(4):
            for j in range(3): z=self.resnet(f'decoder.up_blocks.{i}.resnets.{j}',z)
            if i<3:z=self.affine(f'decoder.up_blocks.{i}.upsamplers.0.conv',self.resize(f'decoder.up_blocks.{i}.resize',z))
        z=self.affine('decoder.conv_out',self.act('decoder.conv_norm_out.silu',self.norm('decoder.conv_norm_out',z,eps=1e-6)))
        return z

def shapes_from_file(path):
    import json,struct
    with open(path,'rb') as f: header=json.loads(f.read(struct.unpack('<Q',f.read(8))[0]))
    return {k:v['shape'] for k,v in header.items() if k!='__metadata__'}

def build_graphs(source,size):
    from pathlib import Path
    source=Path(source);result={}
    for name in ('unet','vae'):
        file=next((source/name).glob('*.safetensors'))
        graph=Graph(shapes_from_file(file),size)
        output=graph.unet() if name=='unet' else graph.vae()
        result[name]=dict(nodes=graph.nodes,output=output.node)
    return result

if __name__=='__main__':
    import argparse,json
    p=argparse.ArgumentParser();p.add_argument('source');p.add_argument('--size',type=int,default=512);a=p.parse_args()
    graphs=build_graphs(a.source,a.size)
    print(json.dumps({k:dict(nodes=len(v['nodes']),affines=sum(n['op']==0 for n in v['nodes']),output=v['nodes'][-1]) for k,v in graphs.items()},indent=2))
