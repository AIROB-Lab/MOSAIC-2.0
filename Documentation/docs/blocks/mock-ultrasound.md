# Mock Ultrasound

[Block Catalogue](../block-catalogue.md) / Streaming

Synthetic ultrasound frame source for exercising the classifier/trigger-buffer pipeline with no hardware, emitting deterministic class-conditional grayscale patterns plus Gaussian noise.

## Input and output

| Property | Value |
|---|---|
| **Type** | `mockultrasound` |
| **Inputs** | exactly 1 (normally the Clock) |
| **Consumes** | dispatch by message type: `Vector<double>` → cached as activation weights, no frame emitted; anything else (e.g. the clock's `double`) → emit one frame |
| **Publishes** | `Matrix<double>` of FrameHeight × FrameWidth |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | NumClasses | int | 5 | Number of class templates (clamped ≥1) |
| 1 | FrameHeight | int | 224 | Rows (≥1) |
| 2 | FrameWidth | int | 224 | Columns (≥1) |
| 3 | NoiseLevel | double | 0.05 | Std-dev of additive Gaussian noise; negatives clamped to 0 |
| 4 | WeightedMode | string/bool | *(off)* | `weighted`, `weighted_mode`, `blend`, `true`, `1`, or JSON `true` enable blending; surrounding whitespace is trimmed, any other value means label mode |

## Requirements and use

Use a compatible trigger/class input to generate the class-conditioned patterns expected by the training workflow. This provides synthetic images for development without ultrasound hardware.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.MockUltrasoundSource). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
