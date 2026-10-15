"""Copy allowlisted native extension data via E: staging; never overwrite Unity assets/meta."""
import hashlib,json,shutil,subprocess,sys,tarfile,uuid
from pathlib import Path
from unity_remote import run_pwsh,pwsh_literal,DEFAULT_HOST,DEFAULT_PROJECT

def ps(script):
    r=run_pwsh(script,capture_output=True)
    if r.returncode:raise RuntimeError(r.stderr.decode('utf-8',errors='replace'))
    return r.stdout.decode('utf-8-sig').strip()

def main():
    root=Path(__file__).resolve().parents[1];source=Path.home()/'tmp/vrc-diffusion/sd-extensions';entries=[]
    for sub,destination in [('model','Extensions'),('conditions','conditions')]:
        for p in sorted((source/sub).iterdir()):
            if p.suffix not in ('.json','.bytes','.txt','.png') or not p.is_file() or p.is_symlink():raise RuntimeError(str(p))
            rel='Assets/DiffusionLab/Data/SD/'+destination+'/'+p.name
            local=root/rel;local.parent.mkdir(parents=True,exist_ok=True)
            if local.exists() and local.read_bytes()!=p.read_bytes():
                # Existing conditions retain all old filenames; only new catalog/embeddings are supplied.
                raise RuntimeError('Unexpected existing native data differs: '+rel)
            shutil.copyfile(p,local);entries.append((p,rel))
    inventory={rel:hashlib.sha256(p.read_bytes()).hexdigest() for p,rel in entries}
    tag='extensions-'+uuid.uuid4().hex;archive=source/(tag+'.tar');remote='E:/tmp/vrc-diffusion/'+archive.name
    with tarfile.open(archive,'w') as tar:
        for p,rel in entries:tar.add(p,arcname=rel,recursive=False)
    ps("New-Item -ItemType Directory -Force -Path 'E:/tmp/vrc-diffusion' | Out-Null")
    subprocess.run(['scp','-q',str(archive),DEFAULT_HOST+':'+remote],check=True)
    # The archive contains native input data only, never serialized assets or GUIDs.
    ps('& tar -xf '+pwsh_literal(remote)+' -C '+pwsh_literal(DEFAULT_PROJECT)+"; if($LASTEXITCODE -ne 0){throw 'Native data extraction failed'}")
    check=source/(tag+'.json');check.write_text(json.dumps(inventory))
    subprocess.run(['scp','-q',str(check),DEFAULT_HOST+':E:/tmp/vrc-diffusion/'+check.name],check=True)
    print(ps('$expected=Get-Content -Raw -LiteralPath '+pwsh_literal('E:/tmp/vrc-diffusion/'+check.name)+' | ConvertFrom-Json -AsHashtable; '+
        '$count=0; foreach($relative in $expected.Keys){$p=Join-Path '+pwsh_literal(DEFAULT_PROJECT)+' $relative; if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$relative]){throw "Hash mismatch: $relative"}; $count++}; '+
        '[pscustomobject]@{verified=$true;files=$count}|ConvertTo-Json'))
    ps('Remove-Item -LiteralPath '+pwsh_literal(remote)+','+pwsh_literal('E:/tmp/vrc-diffusion/'+check.name)+' -Force')
    archive.unlink();check.unlink()

if __name__=='__main__':main()
