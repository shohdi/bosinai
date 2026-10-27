"""Download pinned artifacts, verify hashes, and unpack runtime locally."""
import hashlib
import json
import urllib.request
import zipfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
manifest = json.loads((ROOT/'config/artifacts.json').read_text())

def download(url,path,digest):
    def valid():
        if not path.exists(): return False
        with path.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest()==digest
    if valid():
        print('Verified',path.name); return
    path.parent.mkdir(exist_ok=True)
    temp = path.with_suffix(path.suffix+'.partial')
    print('Downloading',path.name,flush=True)
    with urllib.request.urlopen(url,timeout=120) as source, temp.open('wb') as dest:
        while data:=source.read(8*1024*1024): dest.write(data)
    with temp.open('rb') as f:
        if hashlib.file_digest(f,'sha256').hexdigest()!=digest: raise RuntimeError('Checksum mismatch')
    temp.replace(path)

tag = manifest['runtime_release']
for name,digest in [(f'llama-{tag}-bin-win-cuda-12.4-x64.zip',manifest['runtime_sha256']),('cudart-llama-bin-win-cuda-12.4-x64.zip',manifest['cudart_sha256'])]:
    path = ROOT/'runtime'/name
    download(f'https://github.com/PrismML-Eng/llama.cpp/releases/download/{tag}/{name}',path,digest)
    dest = (ROOT/'runtime/cuda').resolve()
    with zipfile.ZipFile(path) as archive:
        for member in archive.namelist():
            if not (dest/member).resolve().is_relative_to(dest): raise RuntimeError('Unsafe archive path')
        archive.extractall(dest)
download('https://huggingface.co/'+manifest['model_repository']+'/resolve/main/'+manifest['model_filename'],ROOT/'models'/manifest['model_filename'],manifest['model_sha256'])
print('Artifacts ready')
