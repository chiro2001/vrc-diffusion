#!/usr/bin/env python3
"""Export fixed CLIP conditions for Counterfeit-V3.0 on CPU only."""

from __future__ import annotations

import argparse
import gc
import hashlib
import json
import os
from pathlib import Path
from typing import Any


REVISION = "f79f307806896050ac376e46d8081d221b8aa93a"
FIX_CHECKPOINT_REVISION = "d8ce63b3eb8e9102e54b3fcc07f9cb7a28ef30fc"
DEFAULT_SOURCE = Path(
    "/home/chiro/tmp/vrc-diffusion/sd-anime"
    f"/diffusers-{REVISION}"
)
CONVERTED_SOURCE = Path("/home/chiro/tmp/vrc-diffusion/sd-anime/fixed-diffusers")
DEFAULT_OUTPUT = Path("/home/chiro/tmp/vrc-diffusion/sd-anime/conditions")
TEXT_ENCODER_RELATIVE = Path("text_encoder/model.fp16.safetensors")
TEXT_ENCODER_SHA256 = (
    "77f96716795855aa244f5b25ff7fdb3eabc248e2f509ada0f61efbf1ad04ea2c"
)
FIX_CHECKPOINT_SHA256 = (
    "a54c944e4c04e9d9ca43468ef5b90ea0408bb6264829a4980de79a768df7179f"
)
MAX_LENGTH = 77
HIDDEN_SIZE = 768
POSITIVE_PROMPT_PREFIX = "masterpiece, best quality, anime illustration, "

CONDITIONS = [
    {
        "label": "negative_common",
        "prompt": (
            "low quality, worst quality, low resolution, blurry, jpeg artifacts, "
            "bad anatomy, deformed anatomy, malformed hands, extra fingers, "
            "missing fingers, fused fingers, extra limbs, text, watermark, logo, "
            "nsfw, nude, nudity, explicit, pornographic, sexual content"
        ),
    },
    {
        "label": "anime_portrait_adult",
        "prompt": (
            POSITIVE_PROMPT_PREFIX +
            "A calm portrait of an adult anime woman with short dark hair, wearing "
            "a blue cardigan, gentle expression, soft window light, simple background."
        ),
    },
    {
        "label": "spring_coast",
        "prompt": (
            POSITIVE_PROMPT_PREFIX +
            "A peaceful coastal village at sunrise, pastel houses beside a quiet sea, "
            "distant mountains, soft clouds, detailed anime landscape."
        ),
    },
    {
        "label": "autumn_cafe",
        "prompt": (
            POSITIVE_PROMPT_PREFIX +
            "A small street cafe in autumn, falling leaves, warm light in the "
            "windows, wet pavement, a cozy anime illustration."
        ),
    },
    {
        "label": "garden_cat",
        "prompt": (
            POSITIVE_PROMPT_PREFIX +
            "A white cat resting on a windowsill and looking into a green garden "
            "in spring, soft morning light, a peaceful illustrated scene."
        ),
    },
]


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--overwrite", action="store_true")
    return parser.parse_args()


def resolve_source_provenance(
    source: Path, weights_path: Path
) -> tuple[dict[str, Any], str]:
    source = source.resolve(strict=True)
    original_source = DEFAULT_SOURCE.resolve()
    converted_source = CONVERTED_SOURCE.resolve()

    if source == original_source:
        manifest_path = source.parent / "manifest.json"
        if not manifest_path.exists():
            raise FileNotFoundError(manifest_path)
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest.get("repoId") != "gsdf/Counterfeit-V3.0":
            raise ValueError(f"Unexpected source model in {manifest_path}")
        if manifest.get("pinnedDiffusersRevision") != REVISION:
            raise ValueError(f"Unexpected pinned revision in {manifest_path}")

        source_weight_hash = sha256_file(weights_path)
        if source_weight_hash != TEXT_ENCODER_SHA256:
            raise ValueError(
                "Pinned CLIP weights SHA-256 mismatch: "
                f"expected {TEXT_ENCODER_SHA256}, got {source_weight_hash}"
            )
        return (
            {
                "repoId": "gsdf/Counterfeit-V3.0",
                "revision": REVISION,
                "component": "text_encoder",
                "weights": str(TEXT_ENCODER_RELATIVE),
                "weightsSha256": source_weight_hash,
            },
            source_weight_hash,
        )

    if source == converted_source:
        conversion_path = source / "conversion.json"
        if not conversion_path.exists():
            raise FileNotFoundError(conversion_path)
        conversion = json.loads(conversion_path.read_text(encoding="utf-8"))
        expected_fields = {
            "repoId": "gsdf/Counterfeit-V3.0",
            "revision": FIX_CHECKPOINT_REVISION,
            "checkpoint": "Counterfeit-V3.0_fix_fp16.safetensors",
            "checkpointSHA256": FIX_CHECKPOINT_SHA256,
            "configurationSourceRevision": REVISION,
        }
        mismatches = {
            key: (expected, conversion.get(key))
            for key, expected in expected_fields.items()
            if conversion.get(key) != expected
        }
        if mismatches:
            raise ValueError(f"Unexpected converted checkpoint provenance: {mismatches}")
        conversion_description = conversion.get("conversion")
        if not isinstance(conversion_description, str) or not conversion_description.strip():
            raise ValueError(f"Missing conversion method in {conversion_path}")

        checkpoint_path = source.parent / conversion["checkpoint"]
        if not checkpoint_path.exists():
            raise FileNotFoundError(checkpoint_path)
        checkpoint_hash = sha256_file(checkpoint_path)
        if checkpoint_hash != conversion["checkpointSHA256"]:
            raise ValueError(
                "Converted checkpoint SHA-256 mismatch: "
                f"expected {conversion['checkpointSHA256']}, got {checkpoint_hash}"
            )

        # The converted text encoder has a new file hash. Record its actual SHA
        # rather than comparing it with the separately published f79 component.
        source_weight_hash = sha256_file(weights_path)
        return (
            {
                "repoId": conversion["repoId"],
                "revision": conversion["revision"],
                "checkpoint": conversion["checkpoint"],
                "checkpointSha256": checkpoint_hash,
                "conversion": conversion_description,
                "configurationSourceRevision": conversion[
                    "configurationSourceRevision"
                ],
                "component": "text_encoder",
                "weights": str(TEXT_ENCODER_RELATIVE),
                "weightsSha256": source_weight_hash,
            },
            source_weight_hash,
        )

    raise ValueError(
        "Unsupported source directory. Use the pinned Diffusers directory "
        f"{original_source} or the converted checkpoint directory {converted_source}."
    )


def encode_conditions(
    source: Path, threads: int
) -> tuple[list[dict[str, Any]], dict[str, Any], dict[str, Any]]:
    # Set visibility before importing torch. The output tensors and model are also
    # explicitly placed on CPU below; no CUDA/NPU device is requested.
    os.environ["CUDA_VISIBLE_DEVICES"] = ""
    os.environ["HIP_VISIBLE_DEVICES"] = ""
    os.environ["ROCR_VISIBLE_DEVICES"] = ""

    import numpy as np
    import safetensors
    import torch
    import transformers
    from safetensors.torch import load_file
    from transformers import CLIPTextModel, CLIPTextConfig, CLIPTokenizer

    if threads < 1:
        raise ValueError("--threads must be positive")
    torch.set_num_threads(threads)
    torch.set_num_interop_threads(1)
    device = torch.device("cpu")

    source = source.resolve(strict=True)

    config_path = source / "text_encoder/config.json"
    weights_path = source / TEXT_ENCODER_RELATIVE
    tokenizer_path = source / "tokenizer"
    for required in (config_path, weights_path, tokenizer_path / "vocab.json", tokenizer_path / "merges.txt"):
        if not required.exists():
            raise FileNotFoundError(required)

    source_provenance, source_hash = resolve_source_provenance(source, weights_path)

    config = CLIPTextConfig.from_pretrained(
        str(config_path.parent), local_files_only=True
    )
    if config.max_position_embeddings != MAX_LENGTH or config.hidden_size != HIDDEN_SIZE:
        raise ValueError(
            "Unexpected CLIP shape: "
            f"max_position_embeddings={config.max_position_embeddings}, "
            f"hidden_size={config.hidden_size}"
        )

    model = CLIPTextModel(config).to(device=device, dtype=torch.float32).eval()
    state_dict = load_file(str(weights_path), device="cpu")
    model_keys = set(model.state_dict())
    if set(state_dict) != model_keys:
        # Transformers versions and checkpoint converters differ on whether the
        # CLIPTextModel base module is prefixed with `text_model.`. Map keys only
        # when the target model has the corresponding counterpart. position_ids
        # is a generated, non-persistent buffer, not a learned weight.
        remapped: dict[str, torch.Tensor] = {}
        for key, value in state_dict.items():
            if key in model_keys:
                new_key = key
            elif key.endswith("embeddings.position_ids"):
                continue
            elif key.startswith("text_model.") and key[len("text_model.") :] in model_keys:
                new_key = key[len("text_model.") :]
            elif f"text_model.{key}" in model_keys:
                new_key = f"text_model.{key}"
            else:
                new_key = key
            if new_key in remapped:
                raise ValueError(f"Duplicate CLIP state key after remapping: {new_key}")
            remapped[new_key] = value
        if set(remapped) != model_keys:
            missing = sorted(model_keys - set(remapped))
            unexpected = sorted(set(remapped) - model_keys)
            raise ValueError(
                "CLIP state dictionary does not match this Transformers version; "
                f"missing={missing[:8]}, unexpected={unexpected[:8]}"
            )
        state_dict = remapped
    model.load_state_dict(state_dict, strict=True)
    del state_dict
    gc.collect()

    parameter_dtypes = {str(parameter.dtype) for parameter in model.parameters()}
    if parameter_dtypes != {"torch.float32"}:
        raise TypeError(f"Expected FP32 CPU model parameters, got {parameter_dtypes}")

    tokenizer = CLIPTokenizer.from_pretrained(
        str(tokenizer_path), local_files_only=True
    )
    if tokenizer.model_max_length != MAX_LENGTH:
        raise ValueError(f"Unexpected tokenizer length: {tokenizer.model_max_length}")

    input_rows: list[torch.Tensor] = []
    token_stats: list[dict[str, int | bool]] = []
    pad_token_id = tokenizer.pad_token_id
    for item in CONDITIONS:
        prompt = item["prompt"]
        full_ids = tokenizer(
            prompt,
            add_special_tokens=True,
            truncation=False,
            padding=False,
        )["input_ids"]
        clipped_ids = tokenizer(
            prompt,
            add_special_tokens=True,
            truncation=True,
            max_length=MAX_LENGTH,
            padding=False,
        )["input_ids"]
        if len(full_ids) > MAX_LENGTH:
            raise ValueError(
                f"Prompt {item['label']} exceeds the {MAX_LENGTH}-token limit: "
                f"{len(full_ids)} tokens before truncation"
            )
        padded = tokenizer(
            prompt,
            add_special_tokens=True,
            truncation=True,
            max_length=MAX_LENGTH,
            padding="max_length",
            return_tensors="pt",
        )["input_ids"][0]
        if padded.shape != (MAX_LENGTH,):
            raise ValueError(f"Tokenizer produced shape {tuple(padded.shape)}")
        input_rows.append(padded)
        token_stats.append(
            {
                "tokenCountBeforeTruncation": len(full_ids),
                "tokenCountAfterTruncation": len(clipped_ids),
                "truncated": len(full_ids) > len(clipped_ids),
                "truncatedTokenCount": max(0, len(full_ids) - len(clipped_ids)),
                "nonPaddingTokenCount": int((padded != pad_token_id).sum().item()),
            }
        )

    input_ids = torch.stack(input_rows).to(device=device)
    with torch.inference_mode():
        output = model(input_ids=input_ids, return_dict=True)
        hidden = output.last_hidden_state

    if tuple(hidden.shape) != (len(CONDITIONS), MAX_LENGTH, HIDDEN_SIZE):
        raise ValueError(f"Unexpected CLIP output shape: {tuple(hidden.shape)}")
    if hidden.dtype != torch.float32 or hidden.device.type != "cpu":
        raise TypeError(f"Expected FP32 CPU output, got {hidden.dtype} on {hidden.device}")

    arrays = hidden.detach().numpy().astype(np.dtype("<f4"), copy=False)
    if not arrays.flags.c_contiguous:
        arrays = np.ascontiguousarray(arrays, dtype=np.dtype("<f4"))

    records: list[dict[str, Any]] = []
    for index, item in enumerate(CONDITIONS):
        array = arrays[index]
        if tuple(array.shape) != (MAX_LENGTH, HIDDEN_SIZE):
            raise ValueError(f"Unexpected per-prompt shape: {tuple(array.shape)}")
        if array.dtype.str != "<f4":
            raise TypeError(f"Expected little-endian float32 output, got {array.dtype.str}")
        payload = array.tobytes(order="C")
        expected_bytes = MAX_LENGTH * HIDDEN_SIZE * 4
        if len(payload) != expected_bytes:
            raise ValueError(f"Expected {expected_bytes} bytes, got {len(payload)}")
        records.append(
            {
                "index": index,
                "label": item["label"],
                "prompt": item["prompt"],
                "payload": payload,
                "sha256": hashlib.sha256(payload).hexdigest(),
                **token_stats[index],
            }
        )

    runtime = {
        "device": "cpu",
        "torchVersion": torch.__version__,
        "torchCudaBuild": torch.version.cuda,
        "transformersVersion": transformers.__version__,
        "safetensorsVersion": safetensors.__version__,
        "threads": threads,
        "sourceWeightSha256": source_hash,
    }
    return records, runtime, source_provenance


def write_outputs(
    records: list[dict[str, Any]],
    runtime: dict[str, Any],
    source_provenance: dict[str, Any],
    output: Path,
    overwrite: bool,
) -> None:
    output.mkdir(parents=True, exist_ok=True)
    destinations = [output / f"embedding-{record['index']}.bytes" for record in records]
    destinations.append(output / "conditions.json")
    existing = [path for path in destinations if path.exists()]
    if existing and not overwrite:
        raise FileExistsError(
            "Output already exists; pass --overwrite to replace: "
            + ", ".join(str(path) for path in existing)
        )

    items: list[dict[str, Any]] = []
    for record, destination in zip(records, destinations[:-1]):
        temp = destination.with_name(destination.name + ".tmp")
        temp.write_bytes(record["payload"])
        os.replace(temp, destination)
        items.append(
            {
                key: record[key]
                for key in (
                    "index",
                    "label",
                    "prompt",
                    "sha256",
                    "tokenCountBeforeTruncation",
                    "tokenCountAfterTruncation",
                    "truncated",
                    "truncatedTokenCount",
                    "nonPaddingTokenCount",
                )
            }
            | {
                "path": destination.name,
                "byteLength": destination.stat().st_size,
            }
        )

    document = {
        "source": source_provenance,
        "encoder": {
            "class": "CLIPTextModel",
            "output": "last_hidden_state",
            "clipSkip": "not applied",
            "sourceWeightDtype": "float16",
            "inferenceDtype": "float32",
            "device": "cpu",
            "attentionMaskPassed": False,
        },
        "tokenizer": {
            "class": "CLIPTokenizer",
            "addSpecialTokens": True,
            "padding": "max_length",
            "truncation": True,
            "maxLength": MAX_LENGTH,
        },
        "embedding": {
            "shape": [MAX_LENGTH, HIDDEN_SIZE],
            "dtype": "float32",
            "endianness": "little",
            "layout": "row-major [token, hidden]",
            "bytesPerEmbedding": MAX_LENGTH * HIDDEN_SIZE * 4,
            "rgbaTexelLanes": 4,
        },
        "runtime": runtime,
        "sharedNegativeCondition": {
            "index": 0,
            "path": items[0]["path"],
            "label": items[0]["label"],
            "sha256": items[0]["sha256"],
            "usedWith": [item["label"] for item in items[1:]],
        },
        "conditions": items,
    }
    json_path = destinations[-1]
    temp_json = json_path.with_name(json_path.name + ".tmp")
    temp_json.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temp_json, json_path)


def main() -> None:
    args = parse_args()
    records, runtime, source_provenance = encode_conditions(args.source, args.threads)
    write_outputs(records, runtime, source_provenance, args.output, args.overwrite)
    print(json.dumps({"output": str(args.output), "runtime": runtime, "conditions": [
        {key: value for key, value in record.items() if key != "payload"}
        for record in records
    ]}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
