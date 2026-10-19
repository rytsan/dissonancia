#!/usr/bin/env python3
"""DEV-ONLY: the authors' YourMT3+ in Python on one file — the reference the native port
(app/YourMt3.cs) is checked against (tools/yourmt3/check-native). Not shipped.

Usage: dz_yourmt3.py <root> <input.wav> <output.json>
  root  the install made by setup.sh (amt/src, amt/logs, model_helper.py, venv)

Writes {"model": ..., "notes": [{"start", "end", "midi", "velocity", "program", "instrument", "drum"}]}
with times in seconds of the input file. Prints "stage <name>" lines for the app's status.
Model: YPTF.MoE+Multi (noPS), Chang et al. 2024, "YourMT3+: Multi-instrument Music Transcription
with Enhanced Transformer Architectures and Cross-dataset Stem Augmentation" (Apache-2.0).
"""
import json
import os
import sys

root, src, dst = sys.argv[1], os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3])
os.chdir(root)   # the checkpoints are found under amt/logs, relative to here
sys.path.insert(0, os.path.join(root, "amt", "src"))
sys.path.insert(0, root)

import torch
import torchaudio
from model_helper import load_model_checkpoint
from utils.audio import slice_padded_array
from utils.event2note import merge_zipped_note_events_and_ties_to_notes
from utils.note2event import mix_notes

MODEL = "YPTF.MoE+Multi (noPS)"
cuda = torch.cuda.is_available()
args = ["mc13_256_g4_all_v7_mt3f_sqr_rms_moe_wf4_n8k2_silu_rope_rp_b36_nops@last.ckpt", "-p", "2024",
        "-tk", "mc13_full_plus_256", "-dec", "multi-t5", "-nl", "26", "-enc", "perceiver-tf", "-sqr", "1",
        "-ff", "moe", "-wf", "4", "-nmoe", "8", "-kmoe", "2", "-act", "silu", "-epe", "rope", "-rp", "1",
        "-ac", "spec", "-hop", "300", "-atc", "1", "-pr", "16" if cuda else "32"]

print("stage loading", flush=True)
model = load_model_checkpoint(args=args, device="cpu")
device = torch.device("cuda" if cuda else "cpu")
model.to(device)

print("stage audio", flush=True)
audio, sr = torchaudio.load(src)
audio = torch.mean(audio, dim=0).unsqueeze(0)
audio = torchaudio.functional.resample(audio, sr, model.audio_cfg["sample_rate"])
frames = model.audio_cfg["input_frames"]
segments = slice_padded_array(audio, frames, frames)
segments = torch.from_numpy(segments.astype("float32")).to(device).unsqueeze(1)

print(f"stage inference {segments.shape[0]} segments on {'cuda' if cuda else 'cpu'}", flush=True)
with torch.no_grad():
    tokens, _ = model.inference_file(bsz=8, audio_segments=segments)

print("stage notes", flush=True)
starts = [frames * i / model.audio_cfg["sample_rate"] for i in range(segments.shape[0])]
per_channel = []
for ch in range(model.task_manager.num_decoding_channels):
    zipped, _, _ = model.task_manager.detokenize_list_batches([a[:, ch, :] for a in tokens], starts, return_events=True)
    notes, _ = merge_zipped_note_events_and_ties_to_notes(zipped)
    per_channel.append(notes)
inverse = model.midi_output_inverse_vocab
out = []
for n in mix_notes(per_channel):
    program, name = (0, "Drums") if n.is_drum else inverse.get(n.program, (n.program, f"program {n.program}"))
    out.append({"start": round(float(n.onset), 4), "end": round(float(n.offset), 4), "midi": int(n.pitch),
                "velocity": int(n.velocity), "program": int(program), "instrument": name, "drum": bool(n.is_drum)})
with open(dst, "w") as f:
    json.dump({"model": MODEL, "notes": out, "segments": [seg.tolist() for arr in tokens for seg in arr]}, f)   # tokens: dev reference
print(f"stage done {len(out)} notes", flush=True)
