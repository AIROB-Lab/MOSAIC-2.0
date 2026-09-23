# Block Catalogue

Find a block by its job, then open its page for input/output shapes, parameter positions,
and startup requirements. Availability depends on the platform and build options.

## Choose by task

| I want to… | Start with |
|---|---|
| Try MOSAIC without hardware | [Clock](blocks/clock.md) → [Sin Generator](blocks/sin-generator.md) → [Function](blocks/function.md) |
| Prepare signals | [Filter](blocks/filter.md), [Crop](blocks/crop.md), [Resampler](blocks/resampler.md) |
| Extract features from windows | [Sliding Window](blocks/sliding-window.md) → [Metric Extractor](blocks/metric-extractor.md) |
| Combine or select channels | [Joiner](blocks/joiner.md), [Projector](blocks/projector.md), [Channel Selector](blocks/channel-selector.md) |
| Capture examples for training | [Trigger](blocks/trigger.md) and [Trigger Buffer](blocks/trigger-buffer.md) |
| Predict or classify | [Batch Predictor](blocks/batch-predictor.md), [Classifier](blocks/classifier.md) |
| Exchange data | [UDP Client](blocks/udp-client.md), [Serial Sender](blocks/serial-sender.md), [LSL](blocks/lsl.md) |

## Read a block page

**Type** lists accepted configuration keys. **Consumes** and **Publishes** describe payload
types and shapes; connecting two nodes does not automatically convert them. **Parameters**
are positional and describe model-loading defaults. Some palette dialogs expose fewer fields
or choose different defaults. Follow the [configuration guide](configuration.md) when editing JSON.

Windows, macOS and iOS run MOSAIC, but vendor device blocks can require a specific platform,
SDK or Python environment. Read requirements before choosing hardware. A registered factory
type may also be absent from the palette; its page identifies that distinction where applicable.

<label for="catalogue-filter">Find a block by name or purpose</label>
<input id="catalogue-filter" type="search" placeholder="For example: filter, ultrasound, UDP" />
<p id="catalogue-filter-status" role="status" aria-live="polite"></p>

## Analytics

| Block | Purpose |
|---|---|
| <span id="metric-extractor"></span>[Metric Extractor](blocks/metric-extractor.md) | Collapses a windowed matrix to one scalar amplitude/feature value per channel. |
| <span id="online-ica"></span>[Online ICA](blocks/online-ica.md) | Streaming Independent Component Analysis — incrementally learns an unmixing matrix that separates mixed channels (EMG crosstalk, motor-unit sources) into k independent components. |
| <span id="online-lda"></span>[Online LDA](blocks/online-lda.md) | Streaming Linear Discriminant Analysis — maintains per-class means and within/between-class scatter online, projecting into a k-dim subspace that maximises class separation. |
| <span id="online-pca"></span>[Online PCA](blocks/online-pca.md) | Streaming Principal Component Analysis — incrementally tracks the top k principal directions (Oja/Sanger update with periodic QR re-orthonormalisation) and projects each sample onto them. |
| <span id="supervised-ica"></span>[Supervised ICA](blocks/supervised-ica.md) | Online ICA subclass that additionally stashes each projection into labelled clusters for the scatter-plot card. |
| <span id="supervised-pca"></span>[Supervised PCA](blocks/supervised-pca.md) | Online PCA subclass that additionally stashes each projection into a colour-coded, labelled cluster set for the scatter-plot card. |
| <span id="supervised-umap"></span>[Supervised UMAP](blocks/supervised-umap.md) | Label-guided non-linear dimensionality reduction via umap-learn over pythonnet. |

## Devices

| Block | Purpose |
|---|---|
| <span id="bodyrig"></span>[BodyRig](blocks/bodyrig.md) | DLR BodyRig inertial motion capture. |
| <span id="command-sender"></span>[Command Sender](blocks/command-sender.md) | Fire-and-forget UDP command sender for ESP-based IMU nodes. |
| <span id="dlr-adc-bt"></span>[DLR ADC/BT](blocks/dlr-adc-bt.md) | Acquires analogue samples over a Bluetooth serial port. |
| <span id="delsys"></span>[Delsys](blocks/delsys.md) | Delsys Trigno RF wireless EMG source. |
| <span id="hannes-hand"></span>[Hannes Hand](blocks/hannes-hand.md) | Sink driving the Hannes prosthetic hand over a BLE serial dongle using the EMGEM protocol. |
| <span id="mcc-daq"></span>[MCC DAQ](blocks/mcc-daq.md) | Acquires analogue inputs through the Measurement Computing Universal Library. |
| <span id="muovi"></span>[Muovi](blocks/muovi.md) | Streams HD-sEMG / EEG (optionally plus IMU quaternions) from up to 16 OT Bioelettronica Muovi probes attached to a SyncStation, over an outbound TCP connection to `192.168.76.1:54320`. |
| <span id="muovi-single-probe"></span>[Muovi Single Probe](blocks/muovi-single-probe.md) | The same decode as Muovi, but for one probe connecting directly over Wi-Fi with no SyncStation: this block is the TCP *server*, binding `0.0.0.0:54321` and waiting for the probe to dial in. |
| <span id="myo-armband"></span>[Myo Armband](blocks/myo-armband.md) | Tick-driven BLE source for the Thalmic Myo armband: dequeues one BLE EMG sample per tick and decodes 8 signed bytes through a 256-entry LUT. |
| <span id="quattrocento"></span>[Quattrocento](blocks/quattrocento.md) | Streams multichannel EMG/EEG/AUX from an OT Bioelettronica Quattrocento amplifier over TCP (default `169.254.1.10:23456`), decoding the device frame down to only the inputs the user enabled. |
| <span id="sifi"></span>[SiFi](blocks/sifi.md) | Source for SiFi Labs BLE biosignal wearables (BioArmband / BioPoint), driven by an external `sifibridge` child process whose JSON stdout is parsed into EMG/IMU/ECG/EDA/PPG streams. |
| <span id="stimulus"></span>[Stimulus](blocks/stimulus.md) | Automated cue generator: walks a list of target activation vectors through a timed FSM (rest → rise → hold → capture → fall → next task) and publishes the current state plus interpolated target each tick, so downstream trainers know when to record. |
| <span id="wulpus"></span>[WULPUS](blocks/wulpus.md) | Bridges the Wulpus wearable ultra-low-power ultrasound dongle into the pipeline through its Python package (pythonnet), publishing A-mode acquisitions or assembled B-mode frames. |

## Flow Control

| Block | Purpose |
|---|---|
| <span id="buffer"></span>[Buffer](blocks/buffer.md) | Start/stop capture accumulator for batch training. |
| <span id="channel-selector"></span>[Channel Selector](blocks/channel-selector.md) | Per-channel enable mask driven from the UI. |
| <span id="control-algorithm"></span>[Control Algorithm](blocks/control-algorithm.md) | Maps a prediction vector onto prosthesis degrees-of-actuation via a runtime-swappable `IControlAlgorithmStrategy`. |
| <span id="joiner"></span>[Joiner](blocks/joiner.md) | Fan-in: concatenates the most recent value from every input into one flat vector, emitted once per arrival from a designated timer input. |
| <span id="manual-control"></span>[Manual Control](blocks/manual-control.md) | UI sliders as a degrees-of-actuation source. |
| <span id="matrix--vector"></span>[Matrix → Vector](blocks/matrix--vector.md) | Replays a buffered [timesteps × channels] frame one row per clock tick, turning windowed output back into a continuous sample-by-sample stream. |
| <span id="mean-average-value"></span>[Mean Average Value](blocks/mean-average-value.md) | Fixed single-feature extractor: MAV = (1/N)·Σ\|x[i]\| per channel over a window. |
| <span id="projector"></span>[Projector](blocks/projector.md) | Keeps only the listed channel indices, producing a reduced-dimension output. |
| <span id="selector"></span>[Selector](blocks/selector.md) | Unwraps a (label, payload) tuple and forwards only the payload, so downstream blocks expecting a bare Vector/Matrix can consume tagged output unmodified. |
| <span id="sliding-window"></span>[Sliding Window](blocks/sliding-window.md) | Accumulates incoming rows and publishes a BufferSize × Channels matrix every Stride rows, giving downstream feature blocks the overlapping windows they need. |
| <span id="slope-sign-changes"></span>[Slope Sign Changes](blocks/slope-sign-changes.md) | Counts slope sign reversals per channel per window — a standard EMG time-domain feature. |
| <span id="stim-trigger"></span>[Stim Trigger](blocks/stim-trigger.md) | Adapter converting a Stimulus block's FSM state tuples into the Buffer start/stop protocol, so a scripted stimulus schedule can drive capture without user clicks. |
| <span id="stream-trigger"></span>[Stream Trigger](blocks/stream-trigger.md) | Proxies a continuously-varying Vector source (dataglove, BodyRig, SinGenerator) into a Trigger Buffer as per-frame regression targets, republishing whatever arrived most recently. |
| <span id="switch"></span>[Switch](blocks/switch.md) | A/B selector — forwards whatever the currently active one of two upstreams publishes and drops the other. |
| <span id="trigger"></span>[Trigger](blocks/trigger.md) | Manual label source for supervised training. |
| <span id="trigger-buffer"></span>[Trigger Buffer](blocks/trigger-buffer.md) | Captures labelled training segments, pairing every data frame with whichever target the trigger published most recently — one storage shape for both classification and regression. |
| <span id="waveform-length"></span>[Waveform Length](blocks/waveform-length.md) | Fixed single-feature extractor: WL = Σ\|x[n] − x[n−1]\| per channel per window, capturing amplitude and frequency content together. |
| <span id="zero-crossings"></span>[Zero Crossings](blocks/zero-crossings.md) | Counts sign changes through zero per channel per window — a cheap proxy for dominant frequency content. |

## Machine Learning

| Block | Purpose |
|---|---|
| <span id="batch-predictor"></span>[Batch Predictor](blocks/batch-predictor.md) | Multi-output regression fit in one shot over every labelled segment held by an upstream Trigger Buffer, then predicting continuously. |
| <span id="classifier"></span>[Classifier](blocks/classifier.md) | Multi-class gesture classifier with two training modes: batch retraining from a Trigger Buffer, or per-sample incremental updates driven live by a Trigger. |
| <span id="hybrid-predictor"></span>[Hybrid Predictor](blocks/hybrid-predictor.md) | Trains on discrete gesture labels like a classifier but outputs a continuous control vector — the probability-weighted blend of the per-class target vectors defined by the Trigger. |
| <span id="incremental-predictor"></span>[Incremental Predictor](blocks/incremental-predictor.md) | Online multi-output regression: updates on every sample while a Trigger holds a target vector, and predicts continuously otherwise. |
| <span id="python-predictor"></span>[Python Predictor](blocks/python-predictor.md) | Classification delegated to a Python model over pythonnet — the block ships windows of data into a user-supplied Python `Model` class and republishes its probability output. |
| <span id="python-predictor-regression"></span>[Python Predictor (Regression)](blocks/python-predictor-regression.md) | Continuous multi-output regression delegated to a PyTorch model over pythonnet — windows of multichannel EMG in, a smoothed joint/DOF activation vector out. |
| <span id="ultrasound-classifier"></span>[Ultrasound Classifier](blocks/ultrasound-classifier.md) | ResNet-18 over pythonnet: one B-mode ultrasound frame in, a softmax gesture-probability vector out. |

## Signal Processing

| Block | Purpose |
|---|---|
| <span id="adaptive-filter"></span>[Adaptive Filter](blocks/adaptive-filter.md) | First-order IIR low-pass whose cutoff is recomputed every sample as `fc = exp(Offset + Deriviate·\|dx_filt\| + Magnitude·\|x\|)`. |
| <span id="crop"></span>[Crop](blocks/crop.md) | Slices a sub-range out of a vector or matrix — trimming an A-mode line to a region of interest, discarding leading metadata samples, or keeping a subset of channels. |
| <span id="cross-correlation"></span>[Cross Correlation](blocks/cross-correlation.md) | Compares two channels to estimate their relative lag. |
| <span id="depth-filter"></span>[Depth Filter](blocks/depth-filter.md) | The same FIR/IIR designs as Filter, but applied **along each row** (the depth axis) with the state reset before every row, so frames are independent. |
| <span id="fft"></span>[FFT](blocks/fft.md) | Single-sided FFT spectrum per channel from a windowed matrix, scaled as magnitude, normalized amplitude, power, or dB, feeding a live SpectrogramMonitor. |
| <span id="filter"></span>[Filter](blocks/filter.md) | The general-purpose time-domain frequency filter: an independent IIR (Butterworth) or FIR instance per channel, state carried across samples, parameters adjustable at runtime. |
| <span id="function"></span>[Function](blocks/function.md) | Applies one element-wise mathematical map to every element of the stream — rectification, offset, scaling, power, clipping, or a soft threshold. |
| <span id="negate"></span>[Negate](blocks/negate.md) | Flips the sign of each channel in a vector. |
| <span id="resampler"></span>[Resampler](blocks/resampler.md) | Sample-and-hold rate converter. |

## Streaming

| Block | Purpose |
|---|---|
| <span id="blender-arm"></span>[Blender Arm](blocks/blender-arm.md) | Converts a control-algorithm actuation dictionary into a 9-element normalised command vector and ships it as an ASCII JSON array over UDP to the Blender-side Python script. |
| <span id="clock"></span>[Clock](blocks/clock.md) | Master timing source: a dedicated highest-priority background thread publishes a monotonic timestamp at `DesiredRate` Hz, driving every synthetic pipeline. |
| <span id="lsl"></span>[LSL](blocks/lsl.md) | A Lab Streaming Layer endpoint that is either an outlet (pushes pipeline data onto the network) or an inlet (pulls a resolved network stream in), selected by `Params[0]`. |
| <span id="mock-ultrasound"></span>[Mock Ultrasound](blocks/mock-ultrasound.md) | Synthetic ultrasound frame source for exercising the classifier/trigger-buffer pipeline with no hardware, emitting deterministic class-conditional grayscale patterns plus Gaussian noise. |
| <span id="ros"></span>[ROS](blocks/ros.md) | A rosbridge_suite bridge over WebSocket (`ws://IP:9090`): subscribes to a topic and publishes each message into the pipeline, or advertises a topic and forwards pipeline vectors/matrices out as `std_msgs` data arrays. |
| <span id="serial-sender"></span>[Serial Sender](blocks/serial-sender.md) | Writes formatted numeric samples to a serial port with a background writer. |
| <span id="sin-generator"></span>[Sin Generator](blocks/sin-generator.md) | Synthetic multi-channel sine source: on each clock tick it evaluates A·sin(2πft + φ₀ + i·Δφ) per channel, using a jitter-free internal sample counter rather than the incoming timestamp. |
| <span id="udp-client"></span>[UDP Client](blocks/udp-client.md) | Bidirectional UDP endpoint: binds a local receive port and publishes every parsed datagram downstream, and — if an upstream and a remote endpoint are configured — also serialises upstream vectors out. |
| <span id="udp-control-client"></span>[UDP Control Client](blocks/udp-control-client.md) | Sends control vectors with proportional and derivative gains and receives feedback. |
| <span id="udp-sender"></span>[UDP Sender](blocks/udp-sender.md) | Sends pipeline data to Unity's Streamlined Input Manager over UDP using its binary framing — count, value/data/subtype bytes, 8-byte timestamp, payload, UInt16 counter — choosing the serialisation by the upstream block's runtime class name. |

## Tests

| Block | Purpose |
|---|---|
| <span id="tac-test"></span>[TAC Test](blocks/tac-test.md) | Target Achievement Control test (Simon et al. 2011): scores how well a user drives a predictor onto Stimulus-supplied target postures. |

## Add a block

Follow [Build Your Own Block](new-block-guide.md), then [Catalogue Registration](block-catalogue-registration.md). Add a reference page alongside its model and palette entry.
