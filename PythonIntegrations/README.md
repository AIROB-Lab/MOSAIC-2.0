# Python integrations

This directory contains only the Python source needed by retained public
integrations:

- `scripts/Umap/umap_bridge.py` supports the supervised UMAP block.
- `wulpus-package/src/wulpus` is the Apache-2.0-licensed Wulpus package.
- `pythonnet-runtime` and `wulpus-runtime` define reproducible Poetry
  environments for the corresponding integrations.

Experimental neural-network scripts, training data, pretrained weights, and
checkpoints are intentionally not distributed. MOSAIC's Python predictor blocks
accept user-supplied modules and model paths, but those artifacts must be
created or licensed independently and kept outside Git.

## Runtime configuration

Install the applicable Poetry environment, then set absolute paths before
starting MOSAIC:

| Variable | Meaning |
|---|---|
| `MOSAIC_PYTHON_DLL` | Python 3.12 shared library (`python312.dll`, `libpython3.12.so`, or equivalent) |
| `MOSAIC_PYTHON_SITE_PACKAGES` | `site-packages` directory for `pythonnet-runtime` |
| `MOSAIC_PYTHON_SCRIPTS` | Absolute path to this repository's `PythonIntegrations/scripts` directory |
| `MOSAIC_WULPUS_PYTHON_DLL` | Python 3.9 shared library used by Wulpus |
| `MOSAIC_WULPUS_SITE_PACKAGES` | `site-packages` directory for `wulpus-runtime` |

`config.json` and `configWulpus.json` expand these variables at runtime and
report a clear error when a required variable or directory is missing. A
single MOSAIC process can host only one Python runtime version, so Wulpus and
the Python 3.12 integrations must run in separate processes.
