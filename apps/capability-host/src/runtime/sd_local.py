#!/usr/bin/env python3
"""
sd_local.py — Minimal Stable Diffusion 1.5 generator using diffusers (CPU).

The Capability CPU image-FLOOR generator (ADR 0123 §S6). It is VENDORED into the capability
runtime dir and staged into a per-Invoke work dir by `cpu-image-floor-runtime.ts`,
which spawns it CONFINED by the OS-native S7 sandbox. No webserver required.

DECOUPLED FROM ANY ONE INSTALL (2026-06-18): the model path is configurable via
`--model` OR the `CAPABILITY_HOST_IMAGE_MODEL` env var, defaulting to the A1111/Forge layout
only as a fallback. The interpreter that runs this script is expected to ALREADY
have torch + diffusers importable (the capability runtime points it at the configured
venv's python, spawned with `-s -E`). An OPTIONAL extra site-packages dir can be
injected via `CAPABILITY_HOST_IMAGE_VENV_SITE` for an interpreter whose ML deps live
elsewhere — injected ONLY when the dir exists (no hard-coded A1111 path).

Usage (standalone test):
  python3 sd_local.py --prompt "test prompt" --out /tmp/test.png
  CAPABILITY_HOST_IMAGE_MODEL=/path/to/model.safetensors python3 sd_local.py --prompt … --out …
"""

import argparse
import os
import sys
from pathlib import Path

from operational_environment import assert_no_legacy_operational_variables

assert_no_legacy_operational_variables()

# The model path: env override → CLI default. NOT hard-coded to one install; the
# A1111/Forge layout is only the fallback so existing dev hosts keep working.
_DEFAULT_MODEL = Path(
    os.environ.get(
        "CAPABILITY_HOST_IMAGE_MODEL",
        str(Path.home() / "stable-diffusion-webui/models/Stable-diffusion/v1-5-pruned-emaonly.safetensors"),
    )
)


def _ensure_imports():
    # The capability runtime spawns the configured venv's python (torch/diffusers already
    # on sys.path), so normally NOTHING is injected here. An operator whose ML deps
    # live in a separate site-packages dir can point CAPABILITY_HOST_IMAGE_VENV_SITE at it; it
    # is injected ONLY when it actually exists (no hard-coded A1111 python3.9 path).
    extra_site = os.environ.get("CAPABILITY_HOST_IMAGE_VENV_SITE")
    if extra_site and Path(extra_site).is_dir() and extra_site not in sys.path:
        sys.path.insert(0, extra_site)


def generate(
    positive: str,
    negative: str,
    dest: Path,
    width: int = 512,
    height: int = 512,
    steps: int = 20,
    cfg_scale: float = 7.0,
    seed: int = -1,
    model_path: Path = _DEFAULT_MODEL,
    ref_image: "Path | None" = None,
    ref_scale: float = 0.6,
) -> None:
    _ensure_imports()

    import torch
    from diffusers import StableDiffusionPipeline

    if not model_path.exists():
        raise FileNotFoundError(f"Model not found: {model_path}")

    print(f"         [diffusers] loading model from {model_path.name}…", flush=True)
    pipe = StableDiffusionPipeline.from_single_file(
        str(model_path),
        torch_dtype=torch.float32,
        safety_checker=None,
    )
    pipe = pipe.to("cpu")

    pil_ref = None
    if ref_image is not None:
        from PIL import Image
        print(f"         [diffusers] loading IP-Adapter (h94/IP-Adapter, ip-adapter_sd15.bin)…", flush=True)
        # load_ip_adapter installs IPAdapterAttnProcessor on every UNet cross-attn
        # layer.  enable_attention_slicing() must NOT be called afterwards because
        # it overwrites those processors with the sliced variant, which does not
        # handle the (text_embeds, ip_image_embeds) tuple that IP-Adapter passes
        # as encoder_hidden_states — causing an AttributeError at inference time.
        # Attention slicing is skipped when IP-Adapter is active; the memory
        # saving is not needed for the CPU-256x256 path and correctness wins.
        pipe.load_ip_adapter("h94/IP-Adapter", subfolder="models", weight_name="ip-adapter_sd15.bin")
        pipe.set_ip_adapter_scale(ref_scale)
        pil_ref = Image.open(ref_image).convert("RGB")
        print(f"         [diffusers] IP-Adapter scale={ref_scale}, ref={ref_image.name}", flush=True)
    else:
        pipe.enable_attention_slicing()

    generator = None
    if seed >= 0:
        generator = torch.Generator("cpu").manual_seed(seed)

    print(f"         [diffusers] generating {width}×{height} {steps} steps…", flush=True)
    call_kwargs: dict = dict(
        prompt=positive,
        negative_prompt=negative,
        width=width,
        height=height,
        num_inference_steps=steps,
        guidance_scale=cfg_scale,
        generator=generator,
    )
    if pil_ref is not None:
        call_kwargs["ip_adapter_image"] = pil_ref

    result = pipe(**call_kwargs)

    image = result.images[0]
    image.save(str(dest))


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--prompt", required=True)
    p.add_argument("--negative", default="")
    p.add_argument("--out", required=True, type=Path)
    p.add_argument("--width", type=int, default=512)
    p.add_argument("--height", type=int, default=512)
    p.add_argument("--steps", type=int, default=20)
    p.add_argument("--cfg", type=float, default=7.0)
    p.add_argument("--seed", type=int, default=-1)
    p.add_argument("--model", type=Path, default=_DEFAULT_MODEL)
    p.add_argument("--ref-image", type=Path, default=None, dest="ref_image",
                   help="Path to reference image for IP-Adapter identity lock")
    p.add_argument("--ref-scale", type=float, default=0.6, dest="ref_scale",
                   help="IP-Adapter conditioning scale (0.0–1.0, default 0.6)")
    args = p.parse_args()

    generate(
        args.prompt, args.negative, args.out,
        args.width, args.height, args.steps, args.cfg, args.seed,
        model_path=args.model,
        ref_image=args.ref_image,
        ref_scale=args.ref_scale,
    )
    print(f"Saved: {args.out}")
