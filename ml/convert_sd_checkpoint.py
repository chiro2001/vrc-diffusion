"""Convert the verified original Counterfeit checkpoint locally, without network lookup."""
import argparse,gc,json,hashlib,shutil
from pathlib import Path
from safetensors import safe_open
from safetensors.torch import save_file
from diffusers.loaders.single_file_utils import convert_ldm_unet_checkpoint,convert_ldm_vae_checkpoint

class LazyCheckpoint:
    def __init__(self,f):self.f=f;self.available=set(f.keys())
    def keys(self):return self.available
    def get(self,k,default=None):return self.f.get_tensor(k) if k in self.available else default

p=argparse.ArgumentParser();p.add_argument('--checkpoint',required=True);p.add_argument('--config-source',required=True);p.add_argument('--output',required=True);a=p.parse_args()
source=Path(a.config_source);out=Path(a.output);out.mkdir(parents=True,exist_ok=True)
with safe_open(a.checkpoint,framework='pt',device='cpu') as f:
    cp=LazyCheckpoint(f)
    for part,convert in [('unet',convert_ldm_unet_checkpoint),('vae',convert_ldm_vae_checkpoint)]:
        dst=out/part;dst.mkdir(exist_ok=True);cfg=json.loads((source/part/'config.json').read_text())
        state=convert(cp,cfg)
        if part=='vae':
            # Legacy attention names are understood by diffusers and by the explicit exporter.
            for key in list(state):
                old=key
                for modern,legacy in [('.to_q.','.query.'),('.to_k.','.key.'),('.to_v.','.value.'),('.to_out.0.','.proj_attn.')]:old=old.replace(modern,legacy)
                if old!=key:state[old]=state.pop(key)
        expected_source=source/part/'diffusion_pytorch_model.fp16.safetensors'
        with safe_open(str(expected_source),framework='pt') as original:
            keys=set(original.keys());assert keys==set(state),(part,'keys differ',keys-set(state),set(state)-keys)
            for k,t in state.items():assert tuple(t.shape)==tuple(original.get_slice(k).get_shape()),(part,k,t.shape)
        save_file({k:v.contiguous().half() for k,v in state.items()},str(dst/'diffusion_pytorch_model.fp16.safetensors'))
        (dst/'config.json').write_text(json.dumps(cfg,indent=2));print(json.dumps(dict(part=part,tensors=len(state))),flush=True)
        del state;gc.collect()
    dst=out/'text_encoder';dst.mkdir(exist_ok=True);prefix='cond_stage_model.transformer.'
    state={k.removeprefix(prefix):f.get_tensor(k).half().contiguous() for k in f.keys() if k.startswith(prefix)}
    assert len(state)>190,len(state)
    save_file(state,str(dst/'model.fp16.safetensors'));shutil.copyfile(source/'text_encoder/config.json',dst/'config.json')
    print(json.dumps(dict(part='text_encoder',tensors=len(state))),flush=True)
for part in ('tokenizer','scheduler'):shutil.copytree(source/part,out/part,dirs_exist_ok=True)
shutil.copyfile(source/'model_index.json',out/'model_index.json')
manifest=dict(repoId='gsdf/Counterfeit-V3.0',revision='d8ce63b3eb8e9102e54b3fcc07f9cb7a28ef30fc',checkpoint=Path(a.checkpoint).name,checkpointSHA256=hashlib.file_digest(open(a.checkpoint,'rb'),'sha256').hexdigest(),conversion='diffusers 0.35.2 convert_ldm_unet_checkpoint / convert_ldm_vae_checkpoint; no EMA; exact key/shape comparison',configurationSourceRevision='f79f307806896050ac376e46d8081d221b8aa93a')
(out/'conversion.json').write_text(json.dumps(manifest,indent=2))
