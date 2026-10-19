#!/usr/bin/env python3
"""DEV-ONLY: greedy decoding with the exported ONNX graphs must give the PyTorch reference tokens.
Usage: python check_onnx.py <out_dir>   (the folder export_onnx.py wrote)"""
import json, sys
import numpy as np
import onnxruntime as ort

d = sys.argv[1]
meta, ref = json.load(open(f"{d}/meta.json")), json.load(open(f"{d}/reference.json"))
opt = ort.SessionOptions()
enc_s, first_s, next_s = (ort.InferenceSession(f"{d}/{n}.onnx", opt, providers=["CPUExecutionProvider"]) for n in ("encoder", "decoder_first", "decoder_next"))
spec = np.array(ref["spec"], dtype=np.float32)[None]
enc = enc_s.run(None, {"spec": spec})[0]
K, eos, pad = meta["channels"], 1, 0
ids = np.zeros((1, K, 1), dtype=np.int64)
logits, self_kv, cross_kv = first_s.run(None, {"ids": ids, "enc": enc})
pred = logits.argmax(-1)                                   # (1, K, 1)
out, alive = [pred], np.ones((1, K, 1), dtype=np.int64)
nxt = pred
for _ in range(meta["max_tokens"] - 1):
    logits, self_kv = next_s.run(None, {"ids": nxt, "enc": enc, "self_kv": self_kv, "cross_kv": cross_kv})
    p = logits.argmax(-1)
    nxt = p.copy()
    p = p * alive + pad * (1 - alive)
    out.append(p)
    alive = alive * (p != eos)
    if alive.max() == 0:
        break
tokens = np.concatenate(out, -1)[0]
want = np.array(ref["tokens"])
n = min(tokens.shape[-1], want.shape[-1])
same = (tokens[:, :n] == want[:, :n]).mean()
print(f"tokens {tokens.shape} reference {want.shape}  agreement over {n} steps: {same:.4f}")
print("non-pad per channel:", [(int((c > 1).sum())) for c in tokens])
