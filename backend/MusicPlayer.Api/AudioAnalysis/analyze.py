import json
import os
import sys
import subprocess

import numpy as np
import torch
from transformers import ClapModel, ClapProcessor


MODEL = "laion/larger_clap_music_and_speech"
REVISION = "195c3a3e68faebb3e2088b9a79e79b43ddbda76b"
GENRES = [
    "ambient", "blues", "classical", "country", "dance", "disco",
    "electronic", "folk", "funk", "hip hop", "jazz", "metal", "pop",
    "punk", "reggae", "rock", "soul", "techno",
]


def main():
    torch.set_num_threads(min(4, os.cpu_count() or 1))
    model = ClapModel.from_pretrained(MODEL, revision=REVISION).eval()
    processor = ClapProcessor.from_pretrained(MODEL, revision=REVISION)
    with torch.inference_mode():
        texts = processor(text=[f"This is a {genre} music recording." for genre in GENRES],
                          padding=True, return_tensors="pt")
        text_features = torch.nn.functional.normalize(model.get_text_features(**texts), dim=-1)

    for line in sys.stdin:
        try:
            request = json.loads(line)
            start = max(0, request["duration"] / 2 - 5)
            result = subprocess.run(
                ["ffmpeg", "-nostdin", "-v", "error", "-ss", str(start), "-i", request["path"],
                 "-t", "10", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"],
                check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60,
            )
            samples = np.frombuffer(result.stdout, dtype="<f4")
            if samples.size < 4800 or not np.isfinite(samples).all() or np.max(np.abs(samples)) < 1e-6:
                raise ValueError("Audio is empty, silent, or invalid")
            inputs = processor(audios=samples, sampling_rate=48000, return_tensors="pt")
            with torch.inference_mode():
                features = torch.nn.functional.normalize(model.get_audio_features(**inputs), dim=-1)
                scores = (features @ text_features.T)[0]
            response = {"embedding": features[0].tolist(), "genre": GENRES[int(scores.argmax())]}
        except (ValueError, KeyError, subprocess.SubprocessError) as error:
            response = {"error": str(error)}
        print(json.dumps(response, allow_nan=False), flush=True)


if __name__ == "__main__":
    main()
