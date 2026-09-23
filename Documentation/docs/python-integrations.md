# Python Integrations

MOSAIC embeds Python through pythonnet for Supervised UMAP, WULPUS, and the
optional user-supplied predictor modules. Python is not required for the
hardware-free tutorials or the native .NET learning blocks.

## Choose the correct environment

| Integration | Environment | Included in this repository |
|---|---|---|
| Supervised UMAP | Python 3.12, `PythonIntegrations/pythonnet-runtime` | UMAP bridge and pinned dependencies |
| Python Predictor | Python 3.12, `PythonIntegrations/pythonnet-runtime` | C# adapter only; no model module or weights |
| Python Predictor (Regression) | Python 3.12 plus a compatible PyTorch installation | C# adapter only; no model module or weights |
| Ultrasound Classifier | Python 3.12 plus a compatible PyTorch installation | C# adapter only; no classifier module or weights |
| WULPUS | Python 3.9, `PythonIntegrations/wulpus-runtime` | Wulpus package source and pinned dependencies |

A MOSAIC process can load only one Python runtime. Because WULPUS currently uses
Python 3.9 and the other integrations use Python 3.12, run them in separate MOSAIC
processes. Do not add both Python configurations to one pipeline.

## Install a retained environment

Install [Poetry](https://python-poetry.org/docs/#installation) and the required
Python version. From the repository root, install the general runtime with:

```text
cd PythonIntegrations/pythonnet-runtime
poetry install
poetry env info --path
```

For WULPUS, use `PythonIntegrations/wulpus-runtime` instead. The final command prints
the virtual-environment directory; its `site-packages` directory is needed below.
The general lockfile intentionally does not install PyTorch. Users of either
PyTorch predictor must select and install a PyTorch build appropriate for their
operating system, CPU/GPU, and driver stack.

## Configure MOSAIC

Set absolute paths in the shell that launches MOSAIC:

| Variable | Value |
|---|---|
| `MOSAIC_PYTHON_DLL` | Python 3.12 shared library, such as `python312.dll` or `libpython3.12.so` |
| `MOSAIC_PYTHON_SITE_PACKAGES` | General Poetry environment's `site-packages` directory |
| `MOSAIC_PYTHON_SCRIPTS` | Repository's absolute `PythonIntegrations/scripts` directory |
| `MOSAIC_WULPUS_PYTHON_DLL` | Python 3.9 shared library |
| `MOSAIC_WULPUS_SITE_PACKAGES` | WULPUS Poetry environment's `site-packages` directory |

Use the first three variables for Supervised UMAP and the predictor blocks. Use
the last two for a WULPUS-only process. `config.json` and `configWulpus.json`
expand these values when Python starts and report unresolved variables or missing
directories as configuration errors.

Environment variables set only in a terminal are inherited only by programs
started from that terminal. Launch MOSAIC from the same shell or configure the
variables through your operating system.

## User-supplied predictor modules

`modulePath` is an importable Python module name, not a model filename. Put the
module on Python's `sys.path`; do not commit private training data, credentials,
or generated checkpoints. The public repository intentionally provides no
pretrained weights or experimental model implementations.

The adapters call these Python members dynamically:

- **Python Predictor:** `Model(num_classes=, random_seed=)`, `predict(array)`,
  `train_incremental(...)`, and `retrain_specific_classes(...)`. `predict` must
  return a tuple-like value whose second item is the class-probability array.
- **Python Predictor (Regression):** `Model(state_dict_path=, in_ch=, win_len=,
  fs=, random_seed=, num_outputs=, skip_preprocess=)`, a `device` attribute,
  `predict(array)`, `train_incremental(...)`, `configure_smoothing(...)`,
  `reset()`, `reset_to_checkpoint()`, and `reset_smoother()`.
- **Ultrasound Classifier:** `Model(state_dict_path=, num_classes=, in_h=, in_w=,
  learning_rate=, random_seed=, is_regression=)`, a `device` attribute,
  `predict(frame)`, `train_incremental(...)`, `reset()`,
  `reset_to_checkpoint()`, `save_state_to(path)`, and `load_state_from(path)`.

Python exceptions raised by these members are reported by the corresponding block.
Match array shapes, class ordering, preprocessing, and checkpoint format to the
adapter before using predictions in an experiment.

## Model-file safety

Treat model files as executable input. PyTorch checkpoints and Python pickle files
can execute code while loading. Load only artifacts you created yourself or obtained
from a trusted, authenticated source. Keep generated models outside Git; the public
`.gitignore` excludes common checkpoint formats.

Continue with [Supervised UMAP](blocks/supervised-umap.md),
[Python Predictor](blocks/python-predictor.md),
[Python Predictor (Regression)](blocks/python-predictor-regression.md),
[Ultrasound Classifier](blocks/ultrasound-classifier.md), or
[WULPUS](blocks/wulpus.md).
