# DEV: fetch the open evaluation corpora (docs/REQUIREMENTS.md R5) from where their authors publish
# them, pinned by size and MD5 (what Zenodo and the pinned commit give), into a cache outside the
# repo. Nothing is redistributed; each corpus keeps its license (listed here and printed on fetch).
#   python3 tools/eval/corpora.py [name ...]      (no name: the default set)
#   DZ_EVAL_DATA overrides the cache (default ~/.cache/dissonancia/eval)
import hashlib, os, sys, urllib.request, zipfile

DATA = os.environ.get("DZ_EVAL_DATA", os.path.expanduser("~/.cache/dissonancia/eval"))
Z = "https://zenodo.org/records/{}/files/{}?download=1"
RWC_ANN = "2b84581b0c4c80514aadf7e9025a309c91e02cc2"

CORPORA = {
    "guitarset": ("CC BY 4.0 (Xi et al., ISMIR 2018)", [
        (Z.format(3371780, "annotation.zip"), "annotation.zip", 39132574, "b39b78e63d3446f2e54ddb7a54df9b10"),
        (Z.format(3371780, "audio_mono-mic.zip"), "audio_mono-mic.zip", 656927981, "275966d6610ac34999b58426beb119c3"),
    ]),
    "rwc-pop": ("CC BY-NC 4.0 (RWC Music Database, Goto et al.; audio and annotations) - non-commercial use only", [
        (Z.format(18656623, "RWC-P.zip"), "RWC-P.zip", 4071840278, "960a11a2d7fb603ad0dae8428f53d4f0"),
        (f"https://github.com/rwc-music/rwc-annotations/archive/{RWC_ANN}.zip", "rwc-annotations.zip", None, "40f17835554467f66de60602f72f5686"),
    ]),
    "vocadito": ("CC BY 4.0 (Bittner et al., 2021)", [
        (Z.format(5578807, "vocadito.zip"), "vocadito.zip", 58492257, "dea40fd18f14d899643c4ba221b33a46"),
    ]),
    "dagstuhl": ("CC BY-NC 4.0 (Rosenzweig et al., TISMIR 2020) - non-commercial use only", [
        (Z.format(4618287, "DagstuhlChoirSet_V1.2.3.zip"), "DagstuhlChoirSet_V1.2.3.zip", 5105460311, "82b95faa634d0c9fc05c81e0868f0217"),
    ]),
}
DEFAULT = ["guitarset", "rwc-pop", "vocadito"]

def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 22), b""): h.update(b)
    return h.hexdigest()

def fetch(name):
    lic, files = CORPORA[name]
    root = os.path.join(DATA, name)
    os.makedirs(root, exist_ok=True)
    print(f"{name}: {lic}")
    for url, fname, size, digest in files:
        path = os.path.join(root, fname)
        done = os.path.join(root, fname + ".ok")
        if os.path.exists(done): continue
        if not (os.path.exists(path) and (size is None or os.path.getsize(path) == size)):
            print(f"  downloading {fname}" + (f" ({size / 1e6:.0f} MB)" if size else ""), flush=True)
            tmp = path + ".part"
            urllib.request.urlretrieve(url, tmp)
            os.replace(tmp, path)
        if size is not None and os.path.getsize(path) != size: sys.exit(f"  {fname}: size {os.path.getsize(path)} != {size}")
        if md5(path) != digest: sys.exit(f"  {fname}: MD5 mismatch, refused")
        print(f"  extracting {fname}", flush=True)
        with zipfile.ZipFile(path) as z: z.extractall(root)
        open(done, "w").close()
        os.remove(path)   # the extracted files are what we keep
    return root

if __name__ == "__main__":
    for n in sys.argv[1:] or DEFAULT:
        if n not in CORPORA: sys.exit(f"unknown corpus {n}; known: {', '.join(CORPORA)}")
        fetch(n)
    print("ok:", DATA)
