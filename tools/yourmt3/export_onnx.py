#!/usr/bin/env python3
"""DEV-ONLY: export YourMT3+ (YPTF.MoE+Multi, noPS) to ONNX for the app's native transcriber.

Usage: <root>/venv/bin/python export_onnx.py <root> <out_dir> [reference.wav [start_seconds]]
  root     the dev install made by setup.sh (torch, the authors' code and checkpoint)
  out_dir  gets encoder.onnx, decoder_first.onnx, decoder_next.onnx, meta.json
           (+ reference.json: spectrogram input and greedy tokens of the first segment, to
           check the C# port against)

Users never run this: the app ships the ONNX files as checksum-pinned downloads and runs
them with onnxruntime. The spectrogram (log-magnitude STFT) is computed in C#, so the
encoder graph starts at the spectrogram.
"""
import json
import os
import sys

root, out = sys.argv[1], os.path.abspath(sys.argv[2])
ref_wav = os.path.abspath(sys.argv[3]) if len(sys.argv) > 3 else None
ref_start = float(sys.argv[4]) if len(sys.argv) > 4 else 0.0
os.makedirs(out, exist_ok=True)
os.chdir(root)
sys.path.insert(0, os.path.join(root, "amt", "src"))
sys.path.insert(0, root)

import torch
from torch import nn
from model_helper import load_model_checkpoint

args = ["mc13_256_g4_all_v7_mt3f_sqr_rms_moe_wf4_n8k2_silu_rope_rp_b36_nops@last.ckpt", "-p", "2024",
        "-tk", "mc13_full_plus_256", "-dec", "multi-t5", "-nl", "26", "-enc", "perceiver-tf", "-sqr", "1",
        "-ff", "moe", "-wf", "4", "-nmoe", "8", "-kmoe", "2", "-act", "silu", "-epe", "rope", "-rp", "1",
        "-ac", "spec", "-hop", "300", "-atc", "1", "-pr", "32"]
model = load_model_checkpoint(args=args, device="cpu").eval()
K = model.decoder.num_channels


class Encoder(nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, spec):                       # (B, T, F) log-magnitude, min-max normalized
        x = self.m.pre_encoder(spec)
        h = self.m.encoder(inputs_embeds=x)["last_hidden_state"]
        return self.m.pre_decoder(h)               # (B, K, T', D)


def flat(past):   # tuple of L (self_k, self_v, cross_k, cross_v) -> two stacked tensors
    return (torch.stack([torch.stack([p[0], p[1]]) for p in past]),
            torch.stack([torch.stack([p[2], p[3]]) for p in past]))


class DecoderFirst(nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, ids, enc):                   # ids (B, K, 1) int64; enc (B, K, T', D)
        hs, past = self.m.decoder(inputs_embeds=self.m.embed_tokens(ids), encoder_hidden_states=enc, return_dict=False)
        self_kv, cross_kv = flat(past)
        return self.m.lm_head(hs), self_kv, cross_kv


class DecoderNext(nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, ids, enc, self_kv, cross_kv):
        past = tuple((self_kv[i, 0], self_kv[i, 1], cross_kv[i, 0], cross_kv[i, 1]) for i in range(self_kv.shape[0]))
        hs, new = self.m.decoder(inputs_embeds=self.m.embed_tokens(ids), encoder_hidden_states=enc, past_key_values=past, return_dict=False)
        return self.m.lm_head(hs), flat(new)[0]


frames = model.audio_cfg["input_frames"]
with torch.no_grad():
    wav = torch.zeros(1, 1, frames)
    if ref_wav:
        import torchaudio
        a, sr = torchaudio.load(ref_wav)
        a = torchaudio.functional.resample(a.mean(0, keepdim=True), sr, model.audio_cfg["sample_rate"])
        a = a[:, int(ref_start * model.audio_cfg["sample_rate"]):]
        wav[0, 0, :min(frames, a.shape[1])] = a[0, :frames]
    spec = model.spectrogram(wav)
    tokens = model.inference(wav)[0].tolist()   # the reference, from the original sparse MoE

    # Export-friendly MoE: each expert on the tokens routed to it (NonZero + gather + index_add, all
    # dynamic in the graph), weighted by the renormalized top-k routing — the same sums as the
    # authors' block-sparse loop, whose per-expert token counts depend on the audio and would be
    # frozen into the graph by tracing. A dense version (every expert on every token) also works
    # but spends half the encoder's time on experts whose weight is zero.
    import torch.nn.functional as F
    from model.ff_layer import MixtralSparseMoeBlock

    def sparse_moe(self, h):
        b, s, d = h.shape
        x = h.reshape(-1, d)
        router_logits = self.gate(x)
        w = F.softmax(router_logits, dim=1, dtype=torch.float)
        top_w, sel = torch.topk(w, self.top_k, dim=-1)
        weights = torch.zeros_like(w).scatter(1, sel, top_w / top_w.sum(-1, keepdim=True)).to(x.dtype)
        y = torch.zeros_like(x)
        for e, expert in enumerate(self.experts):
            idx = torch.nonzero(weights[:, e] > 0).squeeze(1)
            y = y.index_add(0, idx, expert(x.index_select(0, idx)) * weights.index_select(0, idx)[:, e:e + 1])
        return y.reshape(b, s, d), router_logits

    MixtralSparseMoeBlock.forward = sparse_moe
    enc_m, first_m, next_m = Encoder(model), DecoderFirst(model), DecoderNext(model)
    enc = enc_m(spec)
    ids = torch.zeros(1, K, 1, dtype=torch.long)
    logits, self_kv, cross_kv = first_m(ids, enc)
    nxt = logits.argmax(-1)
    logits2, self_kv2 = next_m(nxt, enc, self_kv, cross_kv)

    opset = 17
    torch.onnx.export(enc_m, (spec,), f"{out}/encoder.onnx", input_names=["spec"], output_names=["enc"],
                      dynamic_axes={"spec": {0: "b"}, "enc": {0: "b"}}, opset_version=opset)
    torch.onnx.export(first_m, (ids, enc), f"{out}/decoder_first.onnx", input_names=["ids", "enc"],
                      output_names=["logits", "self_kv", "cross_kv"],
                      dynamic_axes={"ids": {0: "b"}, "enc": {0: "b"}, "logits": {0: "b"}, "self_kv": {2: "bk"}, "cross_kv": {2: "bk"}},
                      opset_version=opset)
    torch.onnx.export(next_m, (nxt, enc, self_kv, cross_kv), f"{out}/decoder_next.onnx",
                      input_names=["ids", "enc", "self_kv", "cross_kv"], output_names=["logits", "self_kv_out"],
                      dynamic_axes={"ids": {0: "b"}, "enc": {0: "b"}, "self_kv": {2: "bk", 4: "n"}, "cross_kv": {2: "bk"},
                                    "logits": {0: "b"}, "self_kv_out": {2: "bk", 4: "n1"}},
                      opset_version=opset)

    # Reference for the C# port: the spectrogram and the greedy tokens of this segment.
    a = model.audio_cfg
    meta = {"sample_rate": a["sample_rate"], "input_frames": frames, "n_fft": a["n_fft"], "hop_length": a["hop_length"],
            "eps": 1e-5, "channels": K, "max_tokens": int(model.max_total_token_length),
            "spec_shape": list(spec.shape), "enc_shape": list(enc.shape), "vocab": int(logits.shape[-1])}
    # What the C# detokenizer needs: token index -> (event type, value), ticks per second, and the
    # output instrument (GM program, name) of every program the model can emit.
    tok = model.task_manager.tokenizer
    meta["tps"] = tok.tps
    meta["events"] = [[tok.codec.decode_event_index(i).type, int(tok.codec.decode_event_index(i).value)] for i in range(meta["vocab"])]
    meta["programs"] = {str(k): [int(v[0]), v[1]] for k, v in model.midi_output_inverse_vocab.items()}
    json.dump(meta, open(f"{out}/meta.json", "w"), indent=1)
    json.dump({"wav": wav[0, 0].tolist(), "spec": spec[0].tolist(), "tokens": tokens}, open(f"{out}/reference.json", "w"))
print("exported", meta)
