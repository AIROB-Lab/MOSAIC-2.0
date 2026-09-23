using System;
using System.Collections.Generic;
using MOSAIC.Components.Interfaces;
using MOSAIC.Diagnostics;
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
using JsonModel = MOSAIC.Components.Basics.JsonModel;
using Resampler = MOSAIC.Models.SignalProcessing.Resampler;
using SinGenerator = MOSAIC.Models.Streaming.SinGenerator;
using Matrix2Vector = MOSAIC.Models.FlowControl.Matrix2Vector;
#if !MOSAIC_MOBILE
using PyPredictor = MOSAIC.Models.MachineLearning.PyPredictor;
#endif
using MuoviSingleProbe = MOSAIC.Models.Devices.MuoviSingleProbe;

namespace MOSAIC.Components.Factory;

/// <summary>
/// Default implementation of <see cref="IBlockFactory"/> that maps block type strings
/// to concrete block instances using a normalized key lookup.
/// </summary>
/// <remarks>
/// <para>
/// The factory accepts a <see cref="JsonModel"/> whose <see cref="JsonModel.Type"/> property
/// is normalized (trimmed and lowercased) and matched against a registry of known block types.
/// Each match delegates to the corresponding static <c>ConfigureInput</c> method on the
/// target model class.
/// </para>
/// <para>
/// Multiple aliases (short name, fully-qualified name, legacy name) are supported for
/// backward compatibility.
/// </para>
/// </remarks>
/// <param name="sp">
/// The application's <see cref="IServiceProvider"/> passed to every block's
/// <c>ConfigureInput</c> method for dependency resolution.
/// </param>
public sealed class BlockFactory(IServiceProvider sp) : IBlockFactory
{
    /// <summary>
    /// Creates a block instance from the supplied <see cref="JsonModel"/> definition.
    /// </summary>
    /// <param name="m">The JSON model containing the block type and configuration parameters.</param>
    /// <returns>The newly created block instance.</returns>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <see cref="JsonModel.Type"/> does not match any known block type.
    /// </exception>
    public object Create(JsonModel m)
    {
        var block = CreateCore(m)();

        // The single place every block passes through, so recording is wired once here instead of
        // being re-plumbed in each ConfigureInput.
        if (block is MOSAIC.Components.Basics.BaseBlock baseBlock)
        {
            // Order matters: AttachRecording reads whether a dumper is already open to decide the
            // switch's starting state, so the Path has to have been honoured by then.
            baseBlock.InitDumper(sp, m.Path);
            baseBlock.AttachRecording(sp.GetService(typeof(IRecordingDestination)) as IRecordingDestination);
        }

        return block;
    }

    /// <summary>
    /// Whether <paramref name="type"/> names a block this factory can build, decided without building
    /// one. The check resolves the same switch <see cref="Create"/> uses, so it cannot drift from it.
    /// </summary>
    /// <param name="type">A <see cref="JsonModel.Type"/> value, as written by a saved graph.</param>
    /// <returns><see langword="true"/> when a saved block of this type would load.</returns>
    public bool CanCreate(string type)
    {
        try
        {
            CreateCore(new JsonModel { Type = type });
            return true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Maps a normalized type key to the constructor call for that block, without running it. See
    /// <see cref="Create"/> for the wrapper that runs it, and <see cref="CanCreate"/> for the probe
    /// that only needs to know the key is known.
    /// </summary>
    public Type? GetBlockType(string type)
    {
        try { return ResolveRegistration(new JsonModel { Type = type }).Type; }
        catch (KeyNotFoundException) { return null; }
    }

    private Func<object> CreateCore(JsonModel m) => ResolveRegistration(m).Create;

    private (Type Type, Func<object> Create) ResolveRegistration(JsonModel m) =>
        NormalizeKey(m.Type) switch
        {
            "metricextractor"
                => (typeof(MetricsExtractor), () => MetricsExtractor.ConfigureInput(sp, m)),

            "onlineica" or "mosaic.models.onlineica"
                => (typeof(OnlineICA), () => OnlineICA.ConfigureInput(sp, m)),

            "onlinelda" or "mosaic.models.onlinelda"
                => (typeof(OnlineLDA), () => OnlineLDA.ConfigureInput(sp, m)),

            "onlinepca" or "mosaic.models.onlinepca"
                => (typeof(OnlinePCA), () => OnlinePCA.ConfigureInput(sp, m)),

            "supervisedica" or "mosaic.models.supervisedica"
                => (typeof(SupervisedICA), () => SupervisedICA.ConfigureInput(sp, m)),

            "supervisedpca" or "mosaic.models.supervisedpca"
                => (typeof(SupervisedPCA), () => SupervisedPCA.ConfigureInput(sp, m)),
            
#if !MOSAIC_MOBILE
            "supervisedumap" or "umap"
                => (typeof(SupervisedUMAP), () => SupervisedUMAP.ConfigureInput(sp, m)),
#endif

            "bodyrig"
                => (typeof(Models.Devices.BodyRig), () => Models.Devices.BodyRig.ConfigureInput(sp, m)),

            "commandsender"
                => (typeof(CommandSender), () => CommandSender.ConfigureInput(sp, m)),

#if ENABLE_DELSYS
            "delsys"
                => (typeof(Delsys), () => Delsys.ConfigureInput(sp, m)),
#endif

            // "dlr_adcbt" is the iM-Blocks type name; kept so old pipeline configs still load.
            "dlradcbt" or "dlr_adcbt" or "dlradc"
                => (typeof(DlrAdcBt), () => DlrAdcBt.ConfigureInput(sp, m)),

            "hanneshand" or "mosaic.models.hanneshand"
                => (typeof(HannesHand), () => HannesHand.ConfigureInput(sp, m)),

            // "mc_daq" is the iM-Blocks type name; kept so old pipeline configs still load.
#if !MOSAIC_MOBILE
            "mccdaq" or "mc_daq" or "mcdaq"
                => (typeof(MccDaqBoard), () => MccDaqBoard.ConfigureInput(sp, m)),
#endif

            "muovi"
                => (typeof(Muovi), () => Muovi.ConfigureInput(sp, m)),

            "muovisingleprobe"
                => (typeof(MuoviSingleProbe), () => MuoviSingleProbe.ConfigureInput(sp, m)),

            "myo" or "mosaic.models.myo"
                => (typeof(Myo), () => Myo.ConfigureInput(sp, m)),
            
            "quattrocento" or "quattrocentoblock"
                => (typeof(Quattrocento), () => Quattrocento.ConfigureInput(sp, m)),
            
            "sifi"
                => (typeof(SiFi), () => SiFi.ConfigureInput(sp, m)),

            "stimulus"
                => (typeof(Stimulus), () => Stimulus.ConfigureInput(sp, m)),
            
#if !MOSAIC_MOBILE
            "wulpus" or "wulpuspython"
                => (typeof(Wulpus), () => Wulpus.ConfigureInput(sp, m)),
#endif

            // ── FlowControl ─────────────────────────────────────────────
            "buffer" or "mosaic.models.buffer" or "imblocks.blocks.flowcontrol.buffer"
                => (typeof(Buffer), () => Buffer.ConfigureInput(sp, m)),

            "channelselector"
                or "mosaic.models.flowcontrol.channelselector"
                or "imblocks.blocks.flowcontrol.channelselector"
                => (typeof(ChannelSelector), () => ChannelSelector.ConfigureInput(sp, m)),

            "controlalgorithm"
                => (typeof(ControlAlgorithm), () => ControlAlgorithm.ConfigureInput(sp, m)),

            "function" or "mosaic.models.function"
                => (typeof(Function), () => Function.ConfigureInput(sp, m)),

            "joiner" or "mosaic.models.joiner"
                => (typeof(Joiner), () => Joiner.ConfigureInput(sp, m)),
            
            "manualcontrol"
                => (typeof(ManualControl), () => ManualControl.ConfigureInput(sp, m)),
            
            "matrix2vector" or "mosaic.models.matrix2vector"
                => (typeof(Matrix2Vector), () => Matrix2Vector.ConfigureInput(sp, m)),

            "meanaveragevalue"
                or "mosaic.models.flowcontrol.meanaveragevalue"
                or "imblocks.blocks.flowcontrol.meanaveragevalue"
                => (typeof(MeanAverageValue), () => MeanAverageValue.ConfigureInput(sp, m)),

            "projector"
                or "mosaic.models.flowcontrol.projector"
                or "imblocks.blocks.flowcontrol.projector"
                => (typeof(Projector), () => Projector.ConfigureInput(sp, m)),

            "selector"
                or "mosaic.models.flowcontrol.selector"
                or "imblocks.blocks.flowcontrol.selector"
                => (typeof(Models.FlowControl.Selector), () => Models.FlowControl.Selector.ConfigureInput(sp, m)),

            "slidingwindow" or "mosaic.models.slidingwindow"
                => (typeof(SlidingWindow), () => SlidingWindow.ConfigureInput(sp, m)),

            "slopesignchanges"
                or "mosaic.models.flowcontrol.slopesignchanges"
                or "imblocks.blocks.flowcontrol.slopesignchanges"
                => (typeof(SlopeSignChanges), () => SlopeSignChanges.ConfigureInput(sp, m)),

            "stimtrigger"
                or "mosaic.models.flowcontrol.stimtrigger"
                or "imblocks.blocks.flowcontrol.stimtrigger"
                => (typeof(StimTrigger), () => StimTrigger.ConfigureInput(sp, m)),
            
            "streamtrigger" or "mosaic.models.streamtrigger"
                => (typeof(StreamTrigger), () => StreamTrigger.ConfigureInput(sp, m)),

            "switch"
                or "mosaic.models.flowcontrol.switch"
                or "imblocks.blocks.flowcontrol.switch"
                => (typeof(Switch), () => Switch.ConfigureInput(sp, m)),

            "trigger"
                or "mosaic.models.flowcontrol.trigger"
                or "imblocks.blocks.flowcontrol.trigger"
                => (typeof(Trigger), () => Trigger.ConfigureInput(sp, m)),
            
            "triggerbuffer" or "mosaic.models.triggerbuffer"
                => (typeof(TriggerBuffer), () => TriggerBuffer.ConfigureInput(sp, m)),
            
            "waveformlength"
                or "mosaic.models.flowcontrol.waveformlength"
                or "imblocks.blocks.flowcontrol.waveformlength"
                => (typeof(WaveformLength), () => WaveformLength.ConfigureInput(sp, m)),

            "zerocrossings"
                or "mosaic.models.flowcontrol.zerocrossings"
                or "imblocks.blocks.flowcontrol.zerocrossings"
                => (typeof(ZeroCrossings), () => ZeroCrossings.ConfigureInput(sp, m)),

            "batchpredictor" or "batchpredictorblock"
                => (typeof(BatchPredictor), () => BatchPredictor.ConfigureInput(sp, m)),
            
            "classifier" or "classifierblock"
                => (typeof(ClassifierBlock), () => ClassifierBlock.ConfigureInput(sp, m)),
            
            "hybridpredictor" or "hybridpredictorblock"
                => (typeof(HybridPredictorBlock), () => HybridPredictorBlock.ConfigureInput(sp, m)),

            "incrementalpredictor" or "incrementalpredictorblock"
                => (typeof(IncrementalPredictor), () => IncrementalPredictor.ConfigureInput(sp, m)),
#if !MOSAIC_MOBILE
            "pypredictor" 
                => (typeof(PyPredictor), () => PyPredictor.ConfigureInput(sp, m)),
#endif
            
#if !MOSAIC_MOBILE
            "pypredictorregression"
                => (typeof(PyPredictorRegression), () => PyPredictorRegression.ConfigureInput(sp, m)),
#endif
            
#if !MOSAIC_MOBILE
            "usprediction" 
                => (typeof(UltrasoundClassifier), () => UltrasoundClassifier.ConfigureInput(sp, m)),
#endif

            "adaptivefilterblock" or "adaptivefilter" or "mosaic.models.adaptivefilterblock"
                => (typeof(AdaptiveFilterBlock), () => AdaptiveFilterBlock.ConfigureInput(sp, m)),
            
            "crop"
                => (typeof(Crop), () => Crop.ConfigureInput(sp, m)),
            
            "depthfilter"
                => (typeof(DepthFilter), () => DepthFilter.ConfigureInput(sp, m)),

            "fft"
                => (typeof(FFT), () => FFT.ConfigureInput(sp, m)),

            "filterblock" or "mosaic.models.filterblock" or "filter"
                => (typeof(Filter), () => Filter.ConfigureInput(sp, m)),

            "resampler"
                => (typeof(Resampler), () => Resampler.ConfigureInput(sp, m)),

            "negate"
                => (typeof(Negate), () => Negate.ConfigureInput(sp, m)),

            "crosscorrelation"
                => (typeof(CrossCorrelation), () => CrossCorrelation.ConfigureInput(sp, m)),

            "blenderarm"
                => (typeof(BlenderArm), () => BlenderArm.ConfigureInput(sp, m)),

            "clockblock" or "mosaic.models.clockblock" or "clock"
                => (typeof(ClockBlock), () => ClockBlock.ConfigureInput(sp, m)),

#if !MOSAIC_MOBILE
            "lsl" or "lslblock"
                => (typeof(Models.Streaming.LSL), () => Models.Streaming.LSL.ConfigureInput(sp, m)),
#endif
            
            "mockultrasound"
                => (typeof(MockUltrasoundSource), () => MockUltrasoundSource.ConfigureInput(sp, m)),
            
            "ros" or "rosblock"
                => (typeof(ROS), () => ROS.ConfigureInput(sp, m)),

            "serialsender" or "serial"
                => (typeof(SerialSender), () => SerialSender.ConfigureInput(sp, m)),

            "singenerator" or "mosaic.models.singenerator"
                => (typeof(SinGenerator), () => SinGenerator.ConfigureInput(sp, m)),

            "udpclient"
                => (typeof(UDPClient), () => UDPClient.ConfigureInput(sp, m)),

            "udpcontrolclient" or "udpcontrol"
                => (typeof(UDPControlClient), () => UDPControlClient.ConfigureInput(sp, m)),

            "udpstreamlinedsender"
                => (typeof(UdpStreamlinedSender), () => UdpStreamlinedSender.ConfigureInput(sp, m)),

            "tac"
                => (typeof(TAC), () => TAC.ConfigureInput(sp, m)),

            _ => throw UnknownBlockType(m)
        };

    /// <summary>Reports an unrecognised block type, then produces the exception for it.</summary>
    /// <param name="m">The model whose <see cref="JsonModel.Type"/> matched no case.</param>
    /// <returns>The exception the caller throws.</returns>
    private static KeyNotFoundException UnknownBlockType(JsonModel m)
    {
        Log.Error("BlockFactory", $"Unknown block type '{m.Type}' (normalized '{NormalizeKey(m.Type)}') for block '{m.Name}'.");
        return new KeyNotFoundException($"Unknown block type '{m.Type}'.");
    }

    /// <summary>Reports a block that was created but is of the wrong type, then produces the exception.</summary>
    /// <param name="m">The model that was created.</param>
    /// <param name="requested">The type the caller asked for.</param>
    /// <returns>The exception the caller throws.</returns>
    private static InvalidCastException NotAssignable(JsonModel m, Type requested)
    {
        Log.Error("BlockFactory", $"The block '{m.Type}' (name '{m.Name}') is not assignable to {requested.Name}.");
        return new InvalidCastException($"The block '{m.Type}' is not assignable to {requested.Name}.");
    }

    /// <summary>
    /// Creates a block instance and casts it to the requested type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The expected type of the block. Must be a reference type.</typeparam>
    /// <param name="m">The JSON model containing the block type and configuration parameters.</param>
    /// <returns>The created block instance cast to <typeparamref name="T"/>.</returns>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <see cref="JsonModel.Type"/> does not match any known block type.
    /// </exception>
    /// <exception cref="InvalidCastException">
    /// Thrown when the created block is not assignable to <typeparamref name="T"/>.
    /// </exception>
    public T Create<T>(JsonModel m) where T : class =>
        Create(m) as T
        ?? throw NotAssignable(m, typeof(T));

    /// <summary>
    /// Normalizes a block type key by trimming whitespace and converting to lowercase.
    /// </summary>
    /// <param name="key">The raw block type string.</param>
    /// <returns>The normalized, lowercase key.</returns>
    private static string NormalizeKey(string key) => key.Trim().ToLowerInvariant();
}
