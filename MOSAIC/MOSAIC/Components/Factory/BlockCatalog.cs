using System;
using System.Collections.Generic;
using System.Linq;
using MOSAIC.Models;
using MOSAIC.Models.Analytics;
using MOSAIC.Models.Devices;
using MOSAIC.Models.FlowControl;
using MOSAIC.Models.Learning;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Models.Streaming;
using MOSAIC.Models.Tests;
using Buffer = MOSAIC.Models.FlowControl.Buffer;
using ControlAlgorithm = MOSAIC.Models.FlowControl.ControlAlgorithm;
using FFT = MOSAIC.Models.SignalProcessing.FFT;
using Filter = MOSAIC.Models.SignalProcessing.Filter;
using Resampler = MOSAIC.Models.SignalProcessing.Resampler;
using SinGenerator = MOSAIC.Models.Streaming.SinGenerator;
using Matrix2Vector = MOSAIC.Models.FlowControl.Matrix2Vector;
#if !MOSAIC_MOBILE
using PyPredictor = MOSAIC.Models.MachineLearning.PyPredictor;
#endif
using MuoviSingleProbe = MOSAIC.Models.Devices.MuoviSingleProbe;

namespace MOSAIC.Components.Factory;

public enum BlockCategory { Analytics, Devices, FlowControl, MachineLearning, SignalProcessing, Streaming, Tests }

public enum ParamKind { Int, Double, String, Bool, Enum, FilePath }

/// <param name="VisibleWhen">Name of a controlling (enum) param; when set, this param is only shown in
/// the dialog while that param's value is one of <paramref name="VisibleWhenValues"/>. Purely a UI
/// concern — the block still reads params positionally, so hidden params retain their positions and
/// are emitted with their current values, initially populated from the defaults.</param>
public sealed record ParamHint(
    string Name, ParamKind Kind, object? Default = null, string[]? Choices = null,
    string? VisibleWhen = null, string[]? VisibleWhenValues = null);

/// <summary>One registered block type, with the display metadata and parameter hints the GUI needs.</summary>
/// <remarks>Carries the CLR <see cref="Type"/> (compile-checked) so <see cref="BlockConstraints"/> can
/// reflect input arity off it.</remarks>
public sealed record BlockDescriptor(
    string TypeKey, Type BlockType, string DisplayName, BlockCategory Category,
    string Description, IReadOnlyList<ParamHint> Params)
{
    /// <summary>
    /// Whether the block needs no inputs and can originate a stream. Derived from the block class
    /// via <see cref="BlockConstraints"/> (reflection) — never hand-set, so it can't drift.
    /// </summary>
    public bool CanBeSource => BlockConstraints.CanBeSource(BlockType);
}

/// <summary>
/// Static catalog of every block type registered in <see cref="BlockFactory"/>,
/// with display metadata and parameter hints for the GUI palette and config builder.
/// </summary>
public static class BlockCatalog
{
    private static readonly ParamHint[] _none = [];

    public static IReadOnlyList<BlockDescriptor> All { get; } = Build();

    public static IReadOnlyDictionary<string, BlockDescriptor> ByKey { get; } =
        All.ToDictionary(d => d.TypeKey, d => d, StringComparer.OrdinalIgnoreCase);

    /// <summary>Runtime class name (e.g. <c>"ClockBlock"</c>) → the block's friendly display name,
    /// so <c>AllowableBlocks</c> whitelists can be shown to users in readable form.</summary>
    public static IReadOnlyDictionary<string, string> DisplayNameByClass { get; } =
        All.GroupBy(d => d.BlockType.Name, StringComparer.Ordinal)
           .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.Ordinal);

    /// <summary>Runtime class name → its palette category, for tinting graph nodes by category.</summary>
    public static IReadOnlyDictionary<string, BlockCategory> CategoryByClass { get; } =
        All.GroupBy(d => d.BlockType.Name, StringComparer.Ordinal)
           .ToDictionary(g => g.Key, g => g.First().Category, StringComparer.Ordinal);

    private static BlockDescriptor[] Build() =>
    [
        new("metricextractor", typeof(MetricsExtractor), "Metric Extractor", BlockCategory.Analytics,
            "Computes a scalar feature metric (e.g. RMS, MAV) from each channel.",
            [new("Metric", ParamKind.Enum, "RMS",
                ["RMS", "MAV", "IEMG", "SSI", "VAR", "STD", "LOG", "PEAK", "P2P", "MEAN", "ZSCORE"])]),
        
        new("onlineica", typeof(OnlineICA), "Online ICA", BlockCategory.Analytics,
            "Online Independent Component Analysis — separates mixed signals into independent sources.",
            [new("Components",             ParamKind.Int,    2),
             new("LearningRate",           ParamKind.Double, 0.1),
             new("ReorthogonalizeEvery",   ParamKind.Int,    50),
             new("MinStableCount",         ParamKind.Int,    1000),
             new("ContrastFunction",       ParamKind.Int,    0),
             new("WarmupSamples",          ParamKind.Int,    500)]),

        new("onlinelda", typeof(OnlineLDA), "Online LDA", BlockCategory.Analytics,
            "Online Linear Discriminant Analysis — projects features into a discriminative subspace.",
            [new("Components",           ParamKind.Int,    3),
             new("LearningRate",         ParamKind.Double, 0.1),
             new("ReorthogonalizeEvery", ParamKind.Int,    100),
             new("MinStableCount",       ParamKind.Int,    500),
             new("Regularization",       ParamKind.Double, 1e-4)]),

        new("onlinepca", typeof(OnlinePCA), "Online PCA", BlockCategory.Analytics,
            "Online Principal Component Analysis — extracts principal components incrementally.",
            [new("Components",           ParamKind.Int,    3),
             new("LearningRate",         ParamKind.Double, 0.2),
             new("ReorthogonalizeEvery", ParamKind.Int,    100),
             new("MinStableCount",       ParamKind.Int,    500)]),

        new("supervisedica", typeof(SupervisedICA), "Supervised ICA", BlockCategory.Analytics,
            "Supervised ICA — driven by class labels from a Trigger block.",
            [new("Components",             ParamKind.Int,    2),
             new("LearningRate",           ParamKind.Double, 0.1),
             new("ReorthogonalizeEvery",   ParamKind.Int,    50),
             new("MinStableCount",         ParamKind.Int,    1000),
             new("ContrastFunction",       ParamKind.Int,    0),
             new("WarmupSamples",          ParamKind.Int,    500)]),

        new("supervisedpca", typeof(SupervisedPCA), "Supervised PCA", BlockCategory.Analytics,
            "Supervised PCA — label-guided dimensionality reduction.",
            [new("Components",           ParamKind.Int,    3),
             new("LearningRate",         ParamKind.Double, 0.2),
             new("ReorthogonalizeEvery", ParamKind.Int,    100),
             new("MinStableCount",       ParamKind.Int,    500)]),

#if !MOSAIC_MOBILE
        new("supervisedumap", typeof(SupervisedUMAP), "Supervised UMAP", BlockCategory.Analytics,
            "Supervised UMAP — non-linear dimensionality reduction guided by class labels.",
            [new("Components",  ParamKind.Int,    3),
             new("Neighbors",   ParamKind.Int,    15),
             new("MinDist",     ParamKind.Double, 0.1),
             new("RandomSeed",  ParamKind.Int,    42)]),
#endif

        new("bodyrig", typeof(Models.Devices.BodyRig), "BodyRig", BlockCategory.Devices,
            "BodyRig inertial motion capture device.",
            [new("Port",           ParamKind.String,   "COM5"),
             new("Channels",       ParamKind.Int,      10),
             new("CalibrationDir", ParamKind.FilePath, "")]),
        
        new("commandsender", typeof(CommandSender), "Command Sender", BlockCategory.Devices,
            "Sends ASCII stream-control commands (e.g. ROT18on) to the ESP nodes over UDP.",
            [new("Host", ParamKind.String, "192.168.0.255"),
             new("Port", ParamKind.Int,    11000)]),

#if ENABLE_DELSYS
        new("delsys", typeof(Delsys), "Delsys", BlockCategory.Devices,
            "Delsys Trigno wireless EMG system.",
            [new("DefaultModeIndex", ParamKind.Int,  0),
             new("SyncMode",         ParamKind.Enum, "async", ["async", "sync"])]),
#endif

        new("dlradcbt", typeof(DlrAdcBt), "DLR ADC/BT", BlockCategory.Devices,
            "DLR analogue-to-digital board over a Bluetooth serial port. Needs a Clock input.",
            [new("Port",     ParamKind.String, "COM7"),
             new("Channels", ParamKind.Int,    10),
             new("BaudRate", ParamKind.Int,    115200)]),

        new("hanneshand", typeof(HannesHand), "Hannes Hand", BlockCategory.Devices,
            "Hannes prosthetic hand controller.",
            _none),

#if !MOSAIC_MOBILE
        new("mccdaq", typeof(MccDaqBoard), "MCC DAQ", BlockCategory.Devices,
            "Measurement Computing DAQ analogue input via the Universal Library. Needs a Clock input.",
            [new("Channels",       ParamKind.String, "0;1;2;3;4;5;6;7"),
             new("ScansPerPacket", ParamKind.Int,    10),
             new("BoardNumber",    ParamKind.Int,    0),
             new("Range",          ParamKind.Enum,   "Bip10Volts", MccDaqBoard.KnownRanges),
             new("Scale",          ParamKind.Enum,   "volts", ["volts", "legacy"])]),
#endif

        new("muovi", typeof(Muovi), "Muovi", BlockCategory.Devices,
            "OT Bioelettronica Muovi EMG probe.",
            _none),

        new("muovisingleprobe", typeof(MuoviSingleProbe), "Muovi Single Probe", BlockCategory.Devices,
            "Single OT Bioelettronica Muovi probe.",
            _none),

        new("myo", typeof(Myo), "Myo Armband", BlockCategory.Devices,
            "Thalmic Labs Myo armband EMG device.",
            [new("DeviceName", ParamKind.String, "")]),

        new("quattrocento", typeof(Quattrocento), "Quattrocento", BlockCategory.Devices,
            "OT Bioelettronica Quattrocento 400-channel EMG amplifier.",
            _none),

        new("sifi", typeof(SiFi), "SiFi", BlockCategory.Devices,
            "SiFi Labs wearable EMG device.",
            _none),

        new("stimulus", typeof(Stimulus), "Stimulus", BlockCategory.Devices,
            "Delivers timed stimuli and captures responses.",
            [new("CaptureDuration", ParamKind.Double, 2.0),
             new("TaskSpec",        ParamKind.String, "task1:1;2;3")]),

#if !MOSAIC_MOBILE
        new("wulpus", typeof(Wulpus), "WULPUS", BlockCategory.Devices,
            "Wearable Ultralow-Power Ultrasound probe.",
            _none),
#endif

        new("buffer", typeof(Buffer), "Buffer", BlockCategory.FlowControl,
            "Collects labelled segments driven by a Trigger block for batch training.",
            _none),

        new("channelselector", typeof(ChannelSelector), "Channel Selector", BlockCategory.FlowControl,
            "Enables or disables individual channels in a vector/matrix stream.",
            _none),

        new("controlalgorithm", typeof(ControlAlgorithm), "Control Algorithm", BlockCategory.FlowControl,
            "Applies a named control strategy to the incoming signal.",
            [new("Algorithm", ParamKind.String, "algorithm:ProportionalControl")]),

        new("joiner", typeof(Joiner), "Joiner", BlockCategory.FlowControl,
            "Aligns and joins multiple input streams using a shared timer block.",
            [new("TimerBlock", ParamKind.String, "timer:Clock")]),

        new("manualcontrol", typeof(ManualControl), "Manual Control", BlockCategory.FlowControl,
            "UI sliders for manually controlling a prosthetic or actuator.",
            _none),

        new("matrix2vector", typeof(Matrix2Vector), "Matrix → Vector", BlockCategory.FlowControl,
            "Flattens the last row of a matrix to a vector, synchronized to a timer block.",
            [new("TimerBlock", ParamKind.String, "timer:Clock")]),

        new("meanaveragevalue", typeof(MeanAverageValue), "Mean Average Value", BlockCategory.FlowControl,
            "Running mean-absolute-value over all channels.",
            _none),

        new("projector", typeof(Projector), "Projector", BlockCategory.FlowControl,
            "Projects the input to a subset of channel indices.",
            [new("ChannelIndices", ParamKind.String, "0;1;2")]),

        new("selector", typeof(Models.FlowControl.Selector), "Selector", BlockCategory.FlowControl,
            "Selects a named output from a tuple emitted by an upstream block.",
            _none),

        new("slidingwindow", typeof(SlidingWindow), "Sliding Window", BlockCategory.FlowControl,
            "Buffers samples and fires a windowed matrix at each stride.",
            [new("BufferSize", ParamKind.Int,    SlidingWindow.DefaultBufferSize),
             new("Stride",     ParamKind.Int,    SlidingWindow.DefaultStride),
             new("Window",     ParamKind.Enum,   SlidingWindow.DefaultWindow.ToString(),
                 Enum.GetNames<SlidingWindow.WindowType>())]),

        new("slopesignchanges", typeof(SlopeSignChanges), "Slope Sign Changes", BlockCategory.FlowControl,
            "Counts sign changes of the signal slope per window.",
            [new("Threshold", ParamKind.Double, 0.0)]),

        new("stimtrigger", typeof(StimTrigger), "Stim Trigger", BlockCategory.FlowControl,
            "Forwards stimulation timing pulses from a Stimulus block.",
            _none),

        new("streamtrigger", typeof(StreamTrigger), "Stream Trigger", BlockCategory.FlowControl,
            "Fires named actions on incoming stream values to drive supervised training.",
            _none),

        new("switch", typeof(Switch), "Switch", BlockCategory.FlowControl,
            "Toggles between two input streams.",
            _none),

        new("trigger", typeof(Trigger), "Trigger", BlockCategory.FlowControl,
            "Fires named actions (label:values) to drive supervised training.",
            [new("Actions", ParamKind.String, "class1:1.0;2.0")]),

        new("triggerbuffer", typeof(TriggerBuffer), "Trigger Buffer", BlockCategory.FlowControl,
            "Accumulates labelled windows captured between Trigger start/stop signals.",
            _none),

        new("waveformlength", typeof(WaveformLength), "Waveform Length", BlockCategory.FlowControl,
            "Computes the sum of absolute first differences per channel.",
            _none),

        new("zerocrossings", typeof(ZeroCrossings), "Zero Crossings", BlockCategory.FlowControl,
            "Counts zero-crossing events above a threshold per window.",
            [new("Threshold", ParamKind.Double, 0.0)]),

        new("batchpredictor", typeof(BatchPredictor), "Batch Predictor", BlockCategory.MachineLearning,
            "Regression predictor retrained in batch on each new labelled segment.",
            [new("ModelType",   ParamKind.Enum,   "Ridge", ["Ridge", "RidgeRFF", "RecursiveLeastSquares"]),
             new("InputDim",    ParamKind.Int,    32),
             new("OutputDim",   ParamKind.Int,    3),
             new("Lambda",      ParamKind.Double, 1.0, VisibleWhen: "ModelType", VisibleWhenValues: ["Ridge", "RidgeRFF"]),
             new("Sigma",       ParamKind.Double, 1.0, VisibleWhen: "ModelType", VisibleWhenValues: ["RidgeRFF"]),
             new("FeatureDim",  ParamKind.Int,    300, VisibleWhen: "ModelType", VisibleWhenValues: ["RidgeRFF"])]),

        new("classifier", typeof(ClassifierBlock), "Classifier", BlockCategory.MachineLearning,
            "Multi-class classifier supporting both batch (Buffer) and incremental training.",
            [new("ModelType",    ParamKind.Enum, "LDA",
                 ["Softmax", "SoftmaxRFF", "LDA", "KNN", "RandomForest", "LinearSVM", "Threshold"]),
             new("InputDim",    ParamKind.Int,  32),
             new("NumClasses",  ParamKind.Int,  3),
             new("TrainingMode",ParamKind.Enum, "Buffer", ["Buffer", "Incremental"])]),

        new("hybridpredictor", typeof(HybridPredictorBlock), "Hybrid Predictor", BlockCategory.MachineLearning,
            "Classifier with real-time incremental updates and optional RFF kernel.",
            [new("ModelType",    ParamKind.Enum, "Softmax",
                 ["Softmax", "SoftmaxRFF", "LDA", "KNN", "RandomForest", "LinearSVM", "Threshold"]),
             new("InputDim",    ParamKind.Int,    32),
             new("LearningRate",ParamKind.Double, 0.01,  VisibleWhen: "ModelType", VisibleWhenValues: ["Softmax", "SoftmaxRFF", "LinearSVM"]),
             new("Lambda",      ParamKind.Double, 0.001, VisibleWhen: "ModelType", VisibleWhenValues: ["Softmax", "SoftmaxRFF", "LinearSVM"]),
             new("Sigma",       ParamKind.Double, 1.0,   VisibleWhen: "ModelType", VisibleWhenValues: ["SoftmaxRFF"]),
             new("FeatureDim",  ParamKind.Int,    300,   VisibleWhen: "ModelType", VisibleWhenValues: ["SoftmaxRFF"])]),

        new("incrementalpredictor", typeof(IncrementalPredictor), "Incremental Predictor", BlockCategory.MachineLearning,
            "Online regression predictor updated on every new sample.",
            [new("ModelType",   ParamKind.Enum,   "Ridge", ["Ridge", "RidgeRFF", "RecursiveLeastSquares"]),
             new("InputDim",    ParamKind.Int,    32),
             new("OutputDim",   ParamKind.Int,    3),
             new("Lambda",      ParamKind.Double, 1.0, VisibleWhen: "ModelType", VisibleWhenValues: ["Ridge", "RidgeRFF"]),
             new("Sigma",       ParamKind.Double, 1.0, VisibleWhen: "ModelType", VisibleWhenValues: ["RidgeRFF"]),
             new("FeatureDim",  ParamKind.Int,    300, VisibleWhen: "ModelType", VisibleWhenValues: ["RidgeRFF"])]),

#if !MOSAIC_MOBILE
        new("pypredictor", typeof(PyPredictor), "Python Predictor", BlockCategory.MachineLearning,
            "Classification model implemented in Python via pythonnet.",
            [new("ModulePath",  ParamKind.FilePath, ""),
             new("NumClasses",  ParamKind.Int,      3),
             new("RandomSeed",  ParamKind.Int,      42),
             new("SavePath",    ParamKind.String,   "")]),
#endif

#if !MOSAIC_MOBILE
        new("pypredictorregression", typeof(PyPredictorRegression), "Python Predictor (Regression)", BlockCategory.MachineLearning,
            "Regression model implemented in Python via pythonnet.",
            [new("ModulePath",    ParamKind.FilePath, ""),
             new("InputChannels", ParamKind.Int,      32),
             new("WindowLength",  ParamKind.Int,      200),
             new("SampleRate",    ParamKind.Int,      2000),
             new("RandomSeed",    ParamKind.Int,      42),
             new("NumOutputs",    ParamKind.Int,      8)]),
#endif

#if !MOSAIC_MOBILE
        new("usprediction", typeof(UltrasoundClassifier), "Ultrasound Classifier", BlockCategory.MachineLearning,
            "Classifies ultrasound frames into gesture/pose classes.",
            _none),
#endif

        new("adaptivefilterblock", typeof(AdaptiveFilterBlock), "Adaptive Filter", BlockCategory.SignalProcessing,
            "Adaptive EMG envelope filter combining magnitude, offset, and derivative.",
            [new("Alpha",      ParamKind.Double, 5.0),
             new("Offset",     ParamKind.Double, 0.0),
             new("Magnitude",  ParamKind.Double, -20.0),
             new("Derivative", ParamKind.Double, 10.0)]),

        new("crop", typeof(Crop), "Crop", BlockCategory.SignalProcessing,
            "Crops a depth/row slice from an incoming matrix.",
            [new("DepthIndex", ParamKind.Int, 0),
             new("DepthCount", ParamKind.Int, 1),
             new("RowIndex",   ParamKind.Int, 0),
             new("RowCount",   ParamKind.Int, 0)]),

        new("crosscorrelation", typeof(CrossCorrelation), "Cross Correlation", BlockCategory.SignalProcessing,
            "Correlates selected channels. Numeric MaxLag keeps the legacy curve output; Lags:min:max enables peak lag/rate output and optional channel/filter settings in JSON.",
            [new("MaxLag / Lags", ParamKind.String, "0"),
             new("BufferLen",     ParamKind.Int, 1000),
             new("ProcessEveryN", ParamKind.Int, 5)]),

        new("depthfilter", typeof(DepthFilter), "Depth Filter", BlockCategory.SignalProcessing,
            "Applies a frequency filter along the depth (time) axis of a matrix.",
            [new("FilterType",      ParamKind.Enum,   "Lowpass",  ["Lowpass", "Highpass", "Bandpass", "Bandstop"]),
             new("Implementation",  ParamKind.Enum,   "IIR",      ["IIR", "FIR"]),
             new("CutoffLow",       ParamKind.Double, 50.0),
             new("CutoffHigh",      ParamKind.Double, 150.0, VisibleWhen: "FilterType",     VisibleWhenValues: ["Bandpass", "Bandstop"]),
             new("Order",           ParamKind.Int,    4,     VisibleWhen: "Implementation", VisibleWhenValues: ["IIR"]),
             new("FirTaps",         ParamKind.Int,    64,    VisibleWhen: "Implementation", VisibleWhenValues: ["FIR"]),
             new("DepthSampleRate", ParamKind.Double, 1000.0)]),

        new("negate", typeof(Negate), "Negate", BlockCategory.SignalProcessing,
            "Flips the sign of every value. Minimal example block with no parameters.",
            _none),

        new("fft", typeof(FFT), "FFT", BlockCategory.SignalProcessing,
            "Computes the FFT magnitude spectrum from a windowed matrix input.",
            [new("WindowType",  ParamKind.Enum,   "Rectangular", ["Rectangular", "Hamming", "Hann", "Blackman"]),
             new("OutputMode",  ParamKind.Enum,   "Magnitude",   ["Magnitude", "MagnitudeNormalized", "Power", "DB"]),
             new("DbRef",       ParamKind.Double, 1.0),
             new("DbFloor",     ParamKind.Double, -120.0),
             new("SpectroDepth",ParamKind.Int,    200),
             new("SampleRate",  ParamKind.Double, 0.0)]),

        new("filterblock", typeof(Filter), "Filter", BlockCategory.SignalProcessing,
            "Applies an IIR or FIR frequency filter to each channel.",
            [new("FilterType",     ParamKind.Enum,   "Lowpass",  ["Lowpass", "Highpass", "Bandpass", "Bandstop"]),
             new("Implementation", ParamKind.Enum,   "IIR",      ["IIR", "FIR"]),
             new("CutoffLow",      ParamKind.Double, 50.0),
             new("CutoffHigh",     ParamKind.Double, 150.0, VisibleWhen: "FilterType",     VisibleWhenValues: ["Bandpass", "Bandstop"]),
             new("Order",          ParamKind.Int,    4,     VisibleWhen: "Implementation", VisibleWhenValues: ["IIR"]),
             new("FirTaps",        ParamKind.Int,    64,    VisibleWhen: "Implementation", VisibleWhenValues: ["FIR"])]),

        new("function", typeof(Function), "Function", BlockCategory.SignalProcessing,
            "Applies a per-element mathematical function to the signal.",
            [new("FunctionType", ParamKind.Enum,   "abs",
                 ["abs", "add", "multiply", "power", "clip", "threshold"]),
             new("Param1",      ParamKind.Double, 0.0, VisibleWhen: "FunctionType", VisibleWhenValues: ["add", "multiply", "power", "clip", "threshold"]),
             new("Param2",      ParamKind.Double, 0.0, VisibleWhen: "FunctionType", VisibleWhenValues: ["clip"])]),

        new("resampler", typeof(Resampler), "Resampler", BlockCategory.SignalProcessing,
            "Resamples the signal to a target sample rate.",
            [new("TargetRate", ParamKind.Double, 100.0)]),

        new("blenderarm", typeof(BlenderArm), "Blender Arm", BlockCategory.Streaming,
            "Sends prosthetic arm commands to a Blender visualization via UDP.",
            [new("HostIP", ParamKind.String, "127.0.0.1"),
             new("Port",   ParamKind.Int,    5005)]),

        new("clockblock", typeof(ClockBlock), "Clock", BlockCategory.Streaming,
            "Master timing clock — use DesiredRate to set the tick frequency in Hz.",
            _none),

#if !MOSAIC_MOBILE
        new("lsl", typeof(Models.Streaming.LSL), "LSL", BlockCategory.Streaming,
            "Lab Streaming Layer outlet or inlet for interoperability with other tools.",
            [new("Mode",       ParamKind.Enum,   "outlet",  ["outlet", "inlet"]),
             new("StreamName", ParamKind.String, "MOSAIC"),
             new("StreamType", ParamKind.String, "EMG"),
             new("SampleRate", ParamKind.Double, 2000.0),
             new("Channels",   ParamKind.Int,    0)]),
#endif

        new("mockultrasound", typeof(MockUltrasoundSource), "Mock Ultrasound", BlockCategory.Streaming,
            "Synthetic ultrasound frame source for testing without hardware.",
            _none),

        new("ros", typeof(ROS), "ROS", BlockCategory.Streaming,
            "ROS bridge — publishes or subscribes to a ROS topic via rosbridge.",
            [new("IP",          ParamKind.String, "localhost"),
             new("Action",      ParamKind.Enum,   "subscribe", ["subscribe", "publish"]),
             new("Topic",       ParamKind.String, "ros_topic"),
             new("MessageType", ParamKind.String, "std_msgs/Float64MultiArray"),
             new("Channels",    ParamKind.Int,    1, VisibleWhen: "Action", VisibleWhenValues: ["subscribe"])]),

        new("singenerator", typeof(SinGenerator), "Sin Generator", BlockCategory.Streaming,
            "Generates a multi-component sinusoidal test signal. Above one sample per tick it " +
            "publishes a matrix, reproducing a packetised device without hardware.",
            [new("Components",     ParamKind.Int,    1),
             new("Amplitude",      ParamKind.Double, 1.0),
             new("Frequency",      ParamKind.Double, 0.2),
             new("Phase",          ParamKind.Double, 0.0),
             new("PhaseStep",      ParamKind.Double, 10.0),
             new("ScansPerPacket", ParamKind.Int,    1)]),

        new("udpclient", typeof(UDPClient), "UDP Client", BlockCategory.Streaming,
            "Receives a vector stream over UDP.",
            [new("ReceivePort",   ParamKind.Int,    9000),
             new("Format",        ParamKind.Enum,   "double", ["double", "float", "uint16"]),
             new("RemoteHost",    ParamKind.String, ""),
             new("RemotePort",    ParamKind.Int,    0)]),

        new("udpstreamlinedsender", typeof(UdpStreamlinedSender), "UDP Sender", BlockCategory.Streaming,
            "Sends a vector stream over UDP to a remote host.",
            [new("HostIP", ParamKind.String, "127.0.0.1"),
             new("Port",   ParamKind.Int,    9001)]),

        new("tac", typeof(TAC), "TAC Test", BlockCategory.Tests,
            "Target Achievement Control test for prosthetic hand performance evaluation.",
            _none),
    ];
}
