#!/usr/bin/env python3
"""
kg_eval.py — the KG-search retrieval-QUALITY eval harness (ADR 0135 KG-search F3-lift amendment, Slice 1d;
THE "verify" for the multilingual claim).

The de-risk spike measured LATENCY at scale (the earlier repository issue #1368). This measures retrieval QUALITY across
languages: per-language recall@k + nDCG@k over a small hand-labeled multilingual fixture, using the REAL
BGE-M3 dense embedder (the same model kg_embed.py runs), with an OPTIONAL bge-reranker-v2-m3 rescore stage.

It answers the question the design pins on: does the all-MIT BGE-M3 stack actually retrieve the right
property-management documents when the corpus + query are in English, Spanish, Chinese — and cross-lingual
(an English query over a Spanish/Chinese corpus, the multilingual model's headline property)?

Protocol mirrors the production retrieval shape (minus the security clip, which is orthogonal to retrieval
quality and proven in the .NET suite): embed every doc + query, rank docs by cosine similarity to the query,
score recall@k + nDCG@k against the ground-truth relevant set. The binary-quantization the index uses is a
SCALE optimization measured by the spike; this harness reports the FLOAT32 cosine ranking (the retrieval
ceiling) so the quality numbers are the model's, not the quantizer's (a quant-vs-float delta is a separate
scale study).

Usage:
  HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 \
    <venv>/bin/python3 kg_eval.py --fixture kg_eval_fixture.json --k 3 [--rerank] [--json]

Output: a per-language table of recall@k + nDCG@k (+ a macro-average row), human-readable or --json.
"""

import argparse
import json
import math
import os
import sys
from pathlib import Path

from operational_environment import assert_no_legacy_operational_variables

assert_no_legacy_operational_variables()

EMBED_REPO = os.environ.get("CAPABILITY_HOST_KG_EMBED_REPO", "BAAI/bge-m3")
RERANK_REPO = os.environ.get("CAPABILITY_HOST_KG_RERANK_REPO", "BAAI/bge-reranker-v2-m3")


def _ensure_imports():
    extra_site = os.environ.get("CAPABILITY_HOST_KG_EMBED_VENV_SITE")
    if extra_site and Path(extra_site).is_dir() and extra_site not in sys.path:
        sys.path.insert(0, extra_site)


def embed_texts(texts):
    """BGE-M3 dense embeddings (CLS pool + L2-normalize), float32. Mirrors kg_embed.py's embed()."""
    _ensure_imports()
    import torch
    from transformers import AutoModel, AutoTokenizer

    tok = AutoTokenizer.from_pretrained(EMBED_REPO)
    model = AutoModel.from_pretrained(EMBED_REPO, torch_dtype=torch.float32)
    model.eval()

    vectors = []
    with torch.no_grad():
        batch_size = 16
        for start in range(0, len(texts), batch_size):
            chunk = texts[start : start + batch_size]
            enc = tok(chunk, padding=True, truncation=True, max_length=512, return_tensors="pt")
            out = model(**enc)
            cls = out.last_hidden_state[:, 0]
            normed = torch.nn.functional.normalize(cls, p=2, dim=1)
            vectors.extend(row.tolist() for row in normed)
    return vectors


def rerank_scores(query, documents):
    """bge-reranker-v2-m3 cross-encoder scores for [query, doc] pairs. Mirrors kg_embed.py's rerank()."""
    _ensure_imports()
    import torch
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    tok = AutoTokenizer.from_pretrained(RERANK_REPO)
    model = AutoModelForSequenceClassification.from_pretrained(RERANK_REPO, torch_dtype=torch.float32)
    model.eval()

    scores = []
    with torch.no_grad():
        batch_size = 16
        for start in range(0, len(documents), batch_size):
            chunk = documents[start : start + batch_size]
            pairs = [[query, doc] for doc in chunk]
            enc = tok(pairs, padding=True, truncation=True, max_length=512, return_tensors="pt")
            logits = model(**enc).logits.view(-1).float()
            scores.extend(float(s) for s in logits.tolist())
    return scores


def cosine(a, b):
    # Both are L2-normalized unit vectors, so cosine == dot product.
    return sum(x * y for x, y in zip(a, b))


def dcg(relevances):
    # Discounted cumulative gain over the ranked relevance flags (binary gain).
    return sum(rel / math.log2(i + 2) for i, rel in enumerate(relevances))


def ndcg_at_k(ranked_ids, relevant_set, k):
    gains = [1.0 if rid in relevant_set else 0.0 for rid in ranked_ids[:k]]
    ideal = sorted(gains, reverse=True)
    # The ideal DCG must consider ALL relevant docs (up to k), even if they ranked below k.
    ideal_count = min(len(relevant_set), k)
    ideal = [1.0] * ideal_count + [0.0] * (k - ideal_count)
    idcg = dcg(ideal)
    return (dcg(gains) / idcg) if idcg > 0 else 0.0


def recall_at_k(ranked_ids, relevant_set, k):
    if not relevant_set:
        return 0.0
    hit = sum(1 for rid in ranked_ids[:k] if rid in relevant_set)
    return hit / len(relevant_set)


def evaluate_language(lang_data, k, use_rerank):
    doc_ids = list(lang_data["documents"].keys())
    doc_texts = [lang_data["documents"][d] for d in doc_ids]
    doc_vecs = embed_texts(doc_texts)

    per_query = []
    for q in lang_data["queries"]:
        query_text = q["query"]
        relevant = set(q["relevant"])
        qvec = embed_texts([query_text])[0]

        # Dense cosine ranking (the retrieval ceiling — float32, no quant).
        scored = sorted(
            ((cosine(qvec, dv), doc_ids[i]) for i, dv in enumerate(doc_vecs)),
            key=lambda t: t[0],
            reverse=True,
        )
        ranked = [rid for _, rid in scored]

        if use_rerank:
            # Rescore the dense top-(k*4) pool with the cross-encoder (the production rerank stage).
            pool_n = min(len(ranked), max(k * 4, k))
            pool_ids = ranked[:pool_n]
            pool_texts = [lang_data["documents"][rid] for rid in pool_ids]
            rscores = rerank_scores(query_text, pool_texts)
            reranked = [
                rid
                for _, rid in sorted(zip(rscores, pool_ids), key=lambda t: t[0], reverse=True)
            ]
            ranked = reranked + [rid for rid in ranked if rid not in set(reranked)]

        per_query.append(
            {
                "query": query_text,
                "recall_at_k": recall_at_k(ranked, relevant, k),
                "ndcg_at_k": ndcg_at_k(ranked, relevant, k),
                "top_k": ranked[:k],
                "relevant": sorted(relevant),
            }
        )

    recall = sum(r["recall_at_k"] for r in per_query) / len(per_query)
    ndcg = sum(r["ndcg_at_k"] for r in per_query) / len(per_query)
    return {"recall_at_k": recall, "ndcg_at_k": ndcg, "queries": per_query}


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--fixture", required=True, type=Path, help="multilingual eval fixture JSON")
    p.add_argument("--k", type=int, default=3, help="cutoff k for recall@k / nDCG@k")
    p.add_argument("--rerank", action="store_true", help="add the bge-reranker rescore stage")
    p.add_argument("--json", action="store_true", help="machine-readable JSON output")
    args = p.parse_args()

    fixture = json.loads(args.fixture.read_text())
    languages = fixture["languages"]

    results = {}
    for lang_code, lang_data in languages.items():
        results[lang_code] = evaluate_language(lang_data, args.k, args.rerank)

    macro_recall = sum(r["recall_at_k"] for r in results.values()) / len(results)
    macro_ndcg = sum(r["ndcg_at_k"] for r in results.values()) / len(results)

    report = {
        "model": EMBED_REPO,
        "reranker": RERANK_REPO if args.rerank else None,
        "k": args.k,
        "languages": {
            code: {"name": languages[code]["name"], "recall_at_k": r["recall_at_k"], "ndcg_at_k": r["ndcg_at_k"]}
            for code, r in results.items()
        },
        "macro_average": {"recall_at_k": macro_recall, "ndcg_at_k": macro_ndcg},
    }

    if args.json:
        report["detail"] = results
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return

    rer = f"  (+ rerank {RERANK_REPO})" if args.rerank else ""
    print(f"KG-search multilingual retrieval eval — {EMBED_REPO}{rer}  (k={args.k})")
    print(f"{'language':<28} {'recall@k':>10} {'nDCG@k':>10}")
    print("-" * 50)
    for code, r in results.items():
        print(f"{languages[code]['name']:<28} {r['recall_at_k']:>10.3f} {r['ndcg_at_k']:>10.3f}")
    print("-" * 50)
    print(f"{'MACRO AVERAGE':<28} {macro_recall:>10.3f} {macro_ndcg:>10.3f}")


if __name__ == "__main__":
    main()
