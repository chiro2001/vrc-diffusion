"""CPU references for world-local CLIP and VAE photo initialization."""
import argparse,json
from pathlib import Path
import numpy as np
import torch
from transformers import CLIPTextModel,CLIPTokenizer
from diffusers import AutoencoderKL
from PIL import Image
from sd_graph import Graph,shapes_from_file
from sd_reference import Executor,save_tensor

def metric(a,b):
    d=a.float()-b.float()
    return dict(rmse=d.square().mean().sqrt().item(),maxAbs=d.abs().max().item(),referenceRMS=b.float().square().mean().sqrt().item())

@torch.no_grad()
def main():
    p=argparse.ArgumentParser();p.add_argument('--source',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
    torch.set_num_threads(4);source=a.source
    tok=CLIPTokenizer.from_pretrained(source/'tokenizer',local_files_only=True)
    text=CLIPTextModel.from_pretrained(source/'text_encoder',local_files_only=True,variant='fp16',torch_dtype=torch.float32).eval()
    g=Graph(shapes_from_file(next((source/'text_encoder').glob('*.safetensors'))),256);z=g.clip();graph=dict(nodes=g.nodes,output=z.node)
    prompt='masterpiece, best quality, anime illustration, a white cat, blue sky, cherry blossoms'
    ids=tok(prompt,padding='max_length',truncation=True,max_length=77,return_tensors='pt').input_ids
    x=ids[:,None,:,None].float();reference=text(ids).last_hidden_state.permute(0,2,1).unsqueeze(-1)
    result={'clip':{}};save_tensor(a.output/'clip-token-ids.bytes',x)
    (a.output/'clip-prompt.txt').write_text(prompt)
    for quantized in (False,True):
        executor=Executor(text,graph,quantized,taps=tuple(range(len(g.nodes))),activation_quantized=False);output=executor(x)
        result['clip']['w8a16' if quantized else 'mixedFloat']=metric(output,reference)
        if quantized:
            save_tensor(a.output/'clip-expected.bytes',output)
            for i in (0,1,5,7,12,60,120,145):save_tensor(a.output/f'clip-node-{i}.bytes',executor.saved[i])
    vae=AutoencoderKL.from_pretrained(source/'vae',local_files_only=True,variant='fp16',torch_dtype=torch.float32).eval()
    size=256;yy,xx=np.mgrid[:size,:size].astype(np.float32)
    pixels=np.stack([xx/(size-1),yy/(size-1),.5+.3*np.sin(xx*.05)*np.cos(yy*.05)],-1)
    Image.fromarray((pixels*255).round().astype('uint8')).save(a.output/'encoder-input.png')
    image=torch.from_numpy(pixels).permute(2,0,1)[None]*2-1
    g=Graph(shapes_from_file(next((source/'vae').glob('*.safetensors'))),size);z=g.encoder();graph=dict(nodes=g.nodes,output=z.node)
    reference=vae.quant_conv(vae.encoder(image));result['encoder']={}
    save_tensor(a.output/'encoder-input.bytes',image)
    for quantized in (False,True):
        executor=Executor(vae,graph,quantized,taps=tuple(range(len(g.nodes))));output=executor(image)
        result['encoder']['w8a8' if quantized else 'mixedFloat']=metric(output[:,:4],reference[:,:4])
        if quantized:
            save_tensor(a.output/'encoder-expected.bytes',output)
            for i in (0,4,10,20,40,60,86):save_tensor(a.output/f'encoder-node-{i}.bytes',executor.saved[i])
    (a.output/'reference.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2),flush=True)

if __name__=='__main__':main()
