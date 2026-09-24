# MOSAIC 2.0

**MOSAIC 2.0** is a modular, open-source software suite for assistive intelligent control and real-time biosignal processing. It supports rapid prototyping of acquisition, signal-processing, machine-learning, visualization, recording, and control pipelines.

> **This repository contains MOSAIC 2.0.** The original MOSAIC repository remains available at [AIROB-Lab/MOSAIC](https://github.com/AIROB-Lab/MOSAIC).

[![Documentation](https://img.shields.io/badge/docs-MOSAIC%202.0-0969da)](https://airob-lab.github.io/MOSAIC-2.0/)
[![Documentation deployment](https://github.com/AIROB-Lab/MOSAIC-2.0/actions/workflows/documentation-pages.yml/badge.svg)](https://github.com/AIROB-Lab/MOSAIC-2.0/actions/workflows/documentation-pages.yml)

- 📚 [Documentation: setup, usage, examples, and tutorials](https://airob-lab.github.io/MOSAIC-2.0/)
- 🧩 [API reference](https://airob-lab.github.io/MOSAIC-2.0/api/index.html)

<p align="center">
  <img width="945" height="422" alt="528558647-1e289e24-67d0-43f3-a35a-7bae64ae9209" src="https://github.com/user-attachments/assets/56983525-201e-4dd4-9f18-dbc8db50a049" />
</p>


## Overview

MOSAIC experiments are assembled by connecting reusable **blocks** in a visual workbench. Source blocks acquire or generate data; downstream blocks filter, transform, classify, visualize, record, or transmit the results. Pipelines can branch so that the same stream is processed in several ways at once, and their configuration can be saved in a human-readable format for reuse.

MOSAIC is intended for researchers, students, and developers working with signals such as surface electromyography (sEMG), body motion, and ultrasound. A built-in signal generator and example pipelines allow the application to be explored without connecting hardware.

## Getting started

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), clone the repository, and run the desktop application.

### Windows

```powershell
git clone https://github.com/AIROB-Lab/MOSAIC-2.0.git
cd MOSAIC-2.0/MOSAIC
dotnet run --project MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-windows10.0.19041.0
```

### macOS

Install the .NET macOS workload, then run:

```bash
git clone https://github.com/AIROB-Lab/MOSAIC-2.0.git
cd MOSAIC-2.0/MOSAIC
dotnet run --project MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-macos
```

After MOSAIC opens, load the hardware-free example at `MOSAIC/Assets/Examples/SevenSteps/01_simple_example.json` and start the `Clock` block.

See [Getting Started](https://airob-lab.github.io/MOSAIC-2.0/docs/getting-started.html) for iOS, Android, optional hardware, Python integrations, and troubleshooting. The [first-pipeline tutorial](https://airob-lab.github.io/MOSAIC-2.0/docs/first-pipeline.html) provides a guided introduction to the workbench.

## Public-release scope

This repository does not include credentials, private experimental data, pretrained models, generated checkpoints, proprietary Delsys packages, `MccDaq.dll`, or the native Windows `lsl.dll` runtime. Authorised users can provide optional vendor dependencies locally as described in the [setup guide](https://airob-lab.github.io/MOSAIC-2.0/docs/getting-started.html#packages-and-optional-devices).

## Development and testing

Run the automated tests from the solution directory:

```powershell
cd MOSAIC
dotnet test MOSAIC.Tests/MOSAIC.Tests.csproj
```

For extension development, see [Creating a New Block](https://airob-lab.github.io/MOSAIC-2.0/docs/new-block-guide.html). Instructions for building the website are in [Building the Documentation](Documentation/docs/building-documentation.md).

## Citation and archival record

Please cite the version of MOSAIC used in your work. This repository includes machine-readable citation and archival metadata in [`CITATION.cff`](CITATION.cff) and [`.zenodo.json`](.zenodo.json). A version-specific Zenodo DOI and citation will be added after the first archived MOSAIC 2.0 release.

## Maintainers and licence

Developed and maintained by the **[Assistive Intelligent Robotics (AIROB) Lab](https://www.airob.tf.fau.de/)** at Friedrich-Alexander-Universität Erlangen-Nürnberg.

MOSAIC is released under the [MIT License](LICENSE). Third-party components and optional vendor dependencies remain subject to their own licences.
