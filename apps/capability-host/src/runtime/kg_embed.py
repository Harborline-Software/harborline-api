#!/usr/bin/env python3
"""
kg_embed.py — the KG-search on-device embedding + rerank worker (ADR 0123
amendment 2026-06-24 §S6; ADR 0135 F3-lift Slice 1 vector tier).

THE G-4 WORKER. This script reads DECRYPTED record text (the embedding input) and
runs a local CPU model over it — BGE-M3 (embeddings) or bge-reranker-v2-m3
(rerank). It is spawned CONFINED by the OS-native S7 sandbox by
`kg-embedding-runtime.ts`: no keychain/credential/seed/DEK reach, filesystem
confined to its work dir, NO network egress, no second-binary exec. Its ONLY
output is a float vector (embeddings) or a list of scores (rerank) written to the
declared output file in the write-only work dir — it CANNOT exfiltrate the
plaintext it sees, because the confinement leaves it nothing to exfiltrate WITH (a
no-tools, no-egress, credential-fenced CPU model can only emit numbers).

It is VENDORED into the capability runtime dir and staged into a per-Invoke work dir by
the runtime (same discipline as `sd_local.py`). It loads the models OFFLINE from
the local HF cache (no network — consistent with the no-egress sandbox).

Protocol (process boundary — JSON in a file, JSON out a file, never stdin/argv for
the plaintext so a confined process need not read from a pipe):
  python3 kg_embed.py --job <job.json> --out <result.json>
  job.json: {"task":"embed","texts":[...],"dimension":1024}
         OR {"task":"rerank","query":"...","documents":[...],"topK":20}
  result.json: {"task":"embed","vectors":[[...],...],"dimension":1024,"model":...}
            OR {"task":"rerank","scored":[{"index":i,"score":s},...],"model":...}

Usage (standalone):
  python3 kg_embed.py --job /tmp/job.json --out /tmp/result.json
"""

import argparse
import json
import os
import sys
from pathlib import Path

from operational_environment import assert_no_legacy_operational_variables

assert_no_legacy_operational_variables()

# The cached HF model repos (offline). The runtime passes these via the job so the
# worker never resolves a default install path; these are the fallbacks for a
# standalone run.
EMBED_REPO = os.environ.get("CAPABILITY_HOST_KG_EMBED_REPO", "BAAI/bge-m3")
RERANK_REPO = os.environ.get("CAPABILITY_HOST_KG_RERANK_REPO", "BAAI/bge-reranker-v2-m3")


def _ensure_imports():
    # The runtime spawns the configured venv's python (torch/transformers already
    # importable). An operator whose ML deps live elsewhere points
    # CAPABILITY_HOST_KG_EMBED_VENV_SITE at that site-packages dir; injected ONLY if it exists.
    extra_site = os.environ.get("CAPABILITY_HOST_KG_EMBED_VENV_SITE")
    if extra_site and Path(extra_site).is_dir() and extra_site not in sys.path:
        sys.path.insert(0, extra_site)


def embed(texts, dimension):
    """BGE-M3 dense embeddings — CLS pooling + L2-normalize, 1024-dim."""
    _ensure_imports()
    import torch
    from transformers import AutoModel, AutoTokenizer

    tok = AutoTokenizer.from_pretrained(EMBED_REPO)
    model = AutoModel.from_pretrained(EMBED_REPO, torch_dtype=torch.float32)
    model.eval()

    vectors = []
    with torch.no_grad():
        # Batch in chunks to keep memory bounded (the spike batched at 32).
        batch_size = 32
        for start in range(0, len(texts), batch_size):
            chunk = texts[start : start + batch_size]
            enc = tok(
                chunk,
                padding=True,
                truncation=True,
                max_length=512,
                return_tensors="pt",
            )
            out = model(**enc)
            # BGE-M3 dense = the CLS token (index 0) of the last hidden state.
            cls = out.last_hidden_state[:, 0]
            normed = torch.nn.functional.normalize(cls, p=2, dim=1)
            for row in normed:
                v = row.tolist()
                if len(v) != dimension:
                    raise ValueError(
                        f"embedding dim {len(v)} != declared {dimension} (model mismatch)"
                    )
                vectors.append(v)
    return vectors


def rerank(query, documents, top_k):
    """bge-reranker-v2-m3 cross-encoder — score [query, doc] pairs, sort desc."""
    _ensure_imports()
    import torch
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    tok = AutoTokenizer.from_pretrained(RERANK_REPO)
    model = AutoModelForSequenceClassification.from_pretrained(
        RERANK_REPO, torch_dtype=torch.float32
    )
    model.eval()

    scored = []
    with torch.no_grad():
        batch_size = 16
        for start in range(0, len(documents), batch_size):
            chunk = documents[start : start + batch_size]
            pairs = [[query, doc] for doc in chunk]
            enc = tok(
                pairs,
                padding=True,
                truncation=True,
                max_length=512,
                return_tensors="pt",
            )
            logits = model(**enc).logits.view(-1).float()
            for i, score in enumerate(logits.tolist()):
                scored.append({"index": start + i, "score": float(score)})

    scored.sort(key=lambda s: s["score"], reverse=True)
    if top_k is not None and top_k >= 0:
        scored = scored[:top_k]
    return scored


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--job", required=True, type=Path, help="job spec JSON file")
    p.add_argument("--out", required=True, type=Path, help="result JSON output file")
    args = p.parse_args()

    job = json.loads(args.job.read_text())
    task = job.get("task")

    if task == "embed":
        dimension = int(job.get("dimension", 1024))
        vectors = embed(list(job["texts"]), dimension)
        result = {
            "task": "embed",
            "vectors": vectors,
            "dimension": dimension,
            "model": "bge-m3",
            "modelVersion": "1.0",
        }
    elif task == "rerank":
        top_k = job.get("topK")
        scored = rerank(str(job["query"]), list(job["documents"]), top_k)
        result = {
            "task": "rerank",
            "scored": scored,
            "model": "bge-reranker-v2-m3",
            "modelVersion": "1.0",
        }
    else:
        raise ValueError(f"unknown task: {task!r}")

    args.out.write_text(json.dumps(result))
    print(f"Wrote: {args.out}")


if __name__ == "__main__":
    main()
