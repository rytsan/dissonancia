#!/bin/sh
# DEV-ONLY: the authors' YourMT3+ in Python (torch + code + ~560 MB checkpoint), to export the ONNX
# graphs the app runs natively (export_onnx.py) and to produce references for the port (dz_yourmt3.py,
# check-native). Users never install this: the app runs the ONNX files with onnxruntime.
set -e
ROOT="${1:-$HOME/.config/Dissonancia/yourmt3-dev}"
SPACE=https://huggingface.co/spaces/mimbres/YourMT3/resolve/main
EXP=mc13_256_g4_all_v7_mt3f_sqr_rms_moe_wf4_n8k2_silu_rope_rp_b36_nops
mkdir -p "$ROOT" && cd "$ROOT"
python3 -m venv venv
venv/bin/pip install -q torch==2.5.1 torchaudio==2.5.1 --index-url https://download.pytorch.org/whl/cu124
venv/bin/pip install -q "lightning>=2.2.1" transformers==4.45.1 numpy==1.26.4 einops librosa deprecated mido mir_eval wandb python-dotenv onnx onnxruntime   # onnx: export; onnxruntime: checking the export
curl -s https://huggingface.co/api/spaces/mimbres/YourMT3 | venv/bin/python -c "import json,sys; [print(s['rfilename']) for s in json.load(sys.stdin)['siblings'] if s['rfilename'].startswith('amt/src/') and not s['rfilename'].startswith(('amt/src/extras','amt/src/tests')) and s['rfilename'].endswith('.py')]" > files.txt
while read f; do mkdir -p "$(dirname "$f")"; curl -sfL "$SPACE/$f" -o "$f"; done < files.txt
curl -sfL "$SPACE/model_helper.py" -o model_helper.py
mkdir -p "amt/logs/2024/$EXP/checkpoints"
curl -fL "$SPACE/amt/logs/2024/$EXP/checkpoints/last.ckpt" -o "amt/logs/2024/$EXP/checkpoints/last.ckpt"
echo "YourMT3+ installed in $ROOT"
