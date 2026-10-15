#!/usr/bin/env python3
"""Export the localized SD anime prompt catalog and CPU CLIP embeddings."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
from typing import Any


DEFAULT_SOURCE = Path("/home/chiro/tmp/vrc-diffusion/sd-anime/fixed-diffusers")
DEFAULT_OUTPUT = Path("/home/chiro/tmp/vrc-diffusion/sd-extensions/conditions")
BYTES_PER_EMBEDDING = 77 * 768 * 4


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--overwrite", action="store_true")
    return parser.parse_args()


def sha256_bytes(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def load_modules() -> tuple[Any, Any]:
    # Prevent optional accelerator backends from being discovered before the
    # shared encoder imports torch. The encoder itself explicitly builds and
    # runs CLIP on torch.device("cpu").
    os.environ["CUDA_VISIBLE_DEVICES"] = ""
    os.environ["HIP_VISIBLE_DEVICES"] = ""
    os.environ["ROCR_VISIBLE_DEVICES"] = ""
    os.environ["ASCEND_VISIBLE_DEVICES"] = ""
    os.environ["NPU_VISIBLE_DEVICES"] = ""
    os.environ["TORCH_DEVICE_BACKEND_AUTOLOAD"] = "0"

    try:
        from . import export_sd_conditions as encoder
        from . import sd_prompt_catalog as catalog
    except ImportError:
        import export_sd_conditions as encoder
        import sd_prompt_catalog as catalog
    return encoder, catalog


def check_legacy_prefix(encoder: Any, catalog: Any) -> None:
    original = encoder.CONDITIONS
    if len(original) != 5:
        raise ValueError(f"Expected the existing negative plus four prompts, got {len(original)}")
    for index, (old, new) in enumerate(zip(original, catalog.PROMPT_CATALOG[:5])):
        if old["label"] != new["label"] or old["prompt"] != new["prompt"]:
            raise ValueError(
                f"Catalog entry {index} must preserve the existing condition label and prompt"
            )


def write_outputs(
    records: list[dict[str, Any]],
    runtime: dict[str, Any],
    source_provenance: dict[str, Any],
    output: Path,
    overwrite: bool,
    catalog: Any,
) -> None:
    output.mkdir(parents=True, exist_ok=True)
    destinations = [output / f"embedding-{record['index']}.bytes" for record in records]
    catalog_path = output / "catalog.json"
    existing = [path for path in [*destinations, catalog_path] if path.exists()]
    if existing and not overwrite:
        raise FileExistsError(
            "Output already exists; pass --overwrite to replace: "
            + ", ".join(str(path) for path in existing)
        )

    items: list[dict[str, Any]] = []
    for record, preset, destination in zip(records, catalog.PROMPT_CATALOG, destinations):
        payload = record["payload"]
        if len(payload) != BYTES_PER_EMBEDDING:
            raise ValueError(
                f"{destination.name} must contain {BYTES_PER_EMBEDDING} bytes, got {len(payload)}"
            )
        if record["index"] != preset["index"] or record["prompt"] != preset["prompt"]:
            raise ValueError(f"Encoder output drifted from catalog entry {preset['index']}")
        temp = destination.with_name(destination.name + ".tmp")
        temp.write_bytes(payload)
        os.replace(temp, destination)
        items.append(
            {
                "index": record["index"],
                "label": record["label"],
                "category": preset["category"],
                "name": preset["name"],
                "prompt": record["prompt"],
                "sha256": sha256_bytes(payload),
                "tokenCountBeforeTruncation": record["tokenCountBeforeTruncation"],
                "tokenCountAfterTruncation": record["tokenCountAfterTruncation"],
                "truncated": record["truncated"],
                "truncatedTokenCount": record["truncatedTokenCount"],
                "nonPaddingTokenCount": record["nonPaddingTokenCount"],
                "path": destination.name,
                "byteLength": destination.stat().st_size,
            }
        )

    document = {
        "schemaVersion": 1,
        "catalogId": catalog.CATALOG_ID,
        "catalogVersion": catalog.CATALOG_VERSION,
        "format": catalog.FORMAT_DESCRIPTION,
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
            "maxLength": 77,
        },
        "embedding": {
            "shape": [77, 768],
            "dtype": "float32",
            "endianness": "little",
            "layout": "row-major [token, hidden]",
            "bytesPerEmbedding": BYTES_PER_EMBEDDING,
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
    temp_catalog = catalog_path.with_name(catalog_path.name + ".tmp")
    temp_catalog.write_text(
        json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    os.replace(temp_catalog, catalog_path)


def main() -> None:
    args = parse_args()
    encoder, catalog = load_modules()
    catalog.validate_catalog()
    check_legacy_prefix(encoder, catalog)

    # Preserve the existing exporter implementation and provenance checks;
    # supply it the complete catalog as its ordered condition list.
    encoder.CONDITIONS = [
        {"label": preset["label"], "prompt": preset["prompt"]}
        for preset in catalog.PROMPT_CATALOG
    ]
    records, runtime, source_provenance = encoder.encode_conditions(args.source, args.threads)
    if runtime.get("device") != "cpu" or runtime.get("torchCudaBuild") is not None:
        raise RuntimeError(f"Expected a CPU-only PyTorch runtime, got {runtime}")
    write_outputs(
        records,
        runtime,
        source_provenance,
        args.output,
        args.overwrite,
        catalog,
    )
    print(
        json.dumps(
            {
                "output": str(args.output),
                "device": runtime["device"],
                "source": source_provenance,
                "count": len(records),
                "catalog": str(args.output / "catalog.json"),
                "embeddings": [
                    {
                        "index": record["index"],
                        "label": record["label"],
                        "sha256": record["sha256"],
                        "byteLength": len(record["payload"]),
                    }
                    for record in records
                ],
            },
            ensure_ascii=False,
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
