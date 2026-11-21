"""Export the pinned Chinese-CLIP checkpoint and verify PyTorch/ONNX parity."""
import argparse
import hashlib
import json
import os
import sys
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch
from PIL import Image
from transformers import BertTokenizer, ChineseCLIPModel


MODEL_ID = "OFA-Sys/chinese-clip-vit-base-patch16"
REVISION = "36e679e65c2a2fead755ae21162091293ad37834"
CHECKPOINT_HASH = "7b7b583c210c867410bc6bdb8a55fe14eec62999e0a9ea31ff222dc501f9cfbe"
CONFIG_HASH = "97850313ab34f4fdeed3a7886b4db8b209a842a48d1cbda079abd071d12c7a2f"
VOCABULARY_HASH = "45bbac6b341c319adc98a532532882e91a9cefc0329aa57bac9ae761c27b291c"
MEAN = [0.48145466, 0.4578275, 0.40821073]
STD = [0.26862954, 0.26130258, 0.27577711]
SIZE = 224
TEXT_LENGTH = 52
DIMENSIONS = 512


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class ImageEncoder(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.vision = model.vision_model
        self.projection = model.visual_projection

    def forward(self, pixels):
        return self.projection(self.vision(pixel_values=pixels, return_dict=False)[1])


class TextEncoder(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.text = model.text_model
        self.projection = model.text_projection

    def forward(self, input_ids, attention_mask):
        states = self.text(input_ids=input_ids, attention_mask=attention_mask, return_dict=False)[0]
        return self.projection(states[:, 0, :])


def image_tensor(path):
    with Image.open(path) as image:
        pixels = np.asarray(image.convert("RGB").resize((SIZE, SIZE), Image.Resampling.BICUBIC), dtype=np.float32)
    pixels = (pixels / 255 - np.asarray(MEAN, dtype=np.float32)) / np.asarray(STD, dtype=np.float32)
    return pixels.transpose(2, 0, 1)[None]


def export(module, values, names, output):
    # The exporter restores the wrapper's training state recursively after tracing.
    module.eval()
    with torch.inference_mode():
        torch.onnx.export(module, values, str(output), input_names=names, output_names=["embedding"],
                          opset_version=17, dynamo=False)
        expected = module(*values).numpy()
    onnx.checker.check_model(str(output))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    session = ort.InferenceSession(str(output), options, providers=["CPUExecutionProvider"])
    inputs = {name: value.numpy() for name, value in zip(names, values)}
    actual = session.run(["embedding"], inputs)[0]
    np.testing.assert_allclose(actual, expected, rtol=1e-4, atol=3e-4)
    return expected[0], float(np.max(np.abs(actual - expected)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Directory with local pinned checkpoint, config and vocabulary")
    parser.add_argument("output", type=Path)
    parser.add_argument("--image", type=Path, help="Parity image. Defaults to the upstream festival.jpg")
    options = parser.parse_args()
    source, destination = options.source.resolve(), options.output.resolve()
    for filename, expected in (("pytorch_model.bin", CHECKPOINT_HASH), ("config.json", CONFIG_HASH),
                               ("vocab.txt", VOCABULARY_HASH)):
        if sha256(source / filename) != expected:
            raise ValueError(f"{filename} does not match the pinned revision.")
    config = json.loads((source / "config.json").read_text(encoding="utf-8"))
    if config["projection_dim"] != DIMENSIONS or config["vision_config"]["image_size"] != SIZE:
        raise ValueError("Unexpected checkpoint dimensions.")
    tokenizer = BertTokenizer(str(source / "vocab.txt"), do_lower_case=True)
    torch.set_num_threads(min(os.cpu_count() or 1, 4))
    model = ChineseCLIPModel.from_pretrained(str(source), local_files_only=True, weights_only=True,
                                            attn_implementation="eager").eval()
    sample = "青岛海边晚霞，Sunset 2026！"
    tokens = tokenizer(sample, return_tensors="pt", max_length=TEXT_LENGTH, padding="max_length", truncation=True)
    sample_image = options.image or source / "festival.jpg"
    image = image_tensor(sample_image)
    destination.mkdir(parents=True, exist_ok=True)

    # Keep a package incomplete until both graphs have passed parity checks.
    with torch.inference_mode():
        reference_image = model.get_image_features(torch.from_numpy(image)).numpy()[0]
        reference_text = model.get_text_features(input_ids=tokens.input_ids, attention_mask=tokens.attention_mask).numpy()[0]
    image_vector, image_error = export(ImageEncoder(model), (torch.from_numpy(image),), ["pixel_values"],
                                       destination / "image.onnx")
    text_vector, text_error = export(TextEncoder(model), (tokens.input_ids, tokens.attention_mask),
                                     ["input_ids", "attention_mask"], destination / "text.onnx")
    np.testing.assert_allclose(image_vector, reference_image, rtol=1e-5, atol=1e-5)
    np.testing.assert_allclose(text_vector, reference_text, rtol=1e-5, atol=1e-5)
    (destination / "vocab.txt").write_bytes((source / "vocab.txt").read_bytes())
    (destination / "festival.jpg").write_bytes(sample_image.read_bytes())
    metadata = {
        "model": MODEL_ID, "revision": REVISION, "opset": 17, "imageSize": SIZE, "textLength": TEXT_LENGTH,
        "dimensions": DIMENSIONS, "imageMean": MEAN, "imageStd": STD, "resize": "pillow-bicubic-rgb",
        "shapes": {"pixel_values": [1, 3, SIZE, SIZE], "input_ids": [1, TEXT_LENGTH],
                   "attention_mask": [1, TEXT_LENGTH], "embedding": [1, DIMENSIONS]},
        "sha256": {name: sha256(destination / name) for name in ("image.onnx", "text.onnx", "vocab.txt")}
    }
    comparison = {
        "sampleText": sample, "tokens": tokens.input_ids[0].tolist(), "image": image_vector.tolist(),
        "text": text_vector.tolist(), "maxAbsoluteError": {"image": image_error, "text": text_error}
    }
    (destination / "parity.json").write_text(json.dumps(comparison, ensure_ascii=False), encoding="utf-8")
    manifest = destination / "manifest.json"
    temporary = destination / "manifest.json.tmp"
    temporary.write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    temporary.replace(manifest)
    print(json.dumps({"manifest": str(manifest), "maxAbsoluteError": comparison["maxAbsoluteError"]}, ensure_ascii=True))


if __name__ == "__main__":
    sys.exit(main())
