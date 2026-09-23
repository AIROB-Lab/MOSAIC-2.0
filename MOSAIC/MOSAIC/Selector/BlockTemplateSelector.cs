using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MOSAIC.Models;
using MOSAIC.Models.Analytics;
using MOSAIC.Models.Devices;
using MOSAIC.Models.FlowControl;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Models.Streaming;
using MOSAIC.Models.Tests;
using MOSAIC.ViewModels;
using MOSAIC.ViewModels.Analytics;
using MOSAIC.ViewModels.Devices;
using MOSAIC.ViewModels.FlowControl;
using MOSAIC.ViewModels.Graph;
using MOSAIC.ViewModels.MachineLearning;
using MOSAIC.ViewModels.SignalProcessing;
using MOSAIC.ViewModels.Streaming;
using MOSAIC.ViewModels.Tests;
using MOSAIC.Views;
using MOSAIC.Views.Cards;
using MOSAIC.Views.Cards.Analytics;
using MOSAIC.Views.Cards.Devices;
using MOSAIC.Views.Cards.Devices.Muovi;
using MOSAIC.Views.Cards.FlowControl;
using MOSAIC.Views.Cards.MachineLearning;
using MOSAIC.Views.Cards.SignalProcessing;
using MOSAIC.Views.Cards.Streaming;
using MOSAIC.Views.Cards.Tests;
using BufferCardView = MOSAIC.Views.Cards.FlowControl.BufferCardView;
using BufferViewModel = MOSAIC.ViewModels.FlowControl.BufferViewModel;
using ControlAlgorithm = MOSAIC.Models.FlowControl.ControlAlgorithm;
using ControlAlgorithmCardView = MOSAIC.Views.Cards.FlowControl.ControlAlgorithmCardView;
using FFT = MOSAIC.Models.SignalProcessing.FFT;
using FFTViewModel = MOSAIC.ViewModels.SignalProcessing.FFTViewModel;
using Filter = MOSAIC.Models.SignalProcessing.Filter;
using FilterViewModel = MOSAIC.ViewModels.SignalProcessing.FilterViewModel;
using Matrix2Vector = MOSAIC.Models.FlowControl.Matrix2Vector;
using MuoviSingleProbeViewModel = MOSAIC.ViewModels.Devices.Muovi.MuoviSingleProbeViewModel;
#if !MOSAIC_MOBILE
using PyPredictor = MOSAIC.Models.MachineLearning.PyPredictor;
#endif
#if !MOSAIC_MOBILE
using PyPredictorViewModel = MOSAIC.ViewModels.MachineLearning.PyPredictorViewModel;
#endif
using Resampler = MOSAIC.Models.SignalProcessing.Resampler;
using ResamplerViewModel = MOSAIC.ViewModels.SignalProcessing.ResamplerViewModel;
using SinGenerator = MOSAIC.Models.Streaming.SinGenerator;
using Stimulus = MOSAIC.Models.Devices.Stimulus;
using StimulusViewModel = MOSAIC.ViewModels.Devices.StimulusViewModel;

namespace MOSAIC.Selector;

public sealed class BlockTemplateSelector : IDataTemplate
{
    public bool Match(object? data) => data is not null;

    public Control Build(object? data)
    {
        var card = CreateCard(data);
        if (card is PopoutCardBase popout)
            popout.OwnerBlock = data as MOSAIC.Components.Basics.BaseBlock;
        return card;
    }

    private static Control CreateCard(object? data) => data switch
    {
        // ── Group node → collapsible section with nested member cards ──
        VertexGroupViewModel group => new GroupCardView { DataContext = group },

        // ── All block types ──
        ClockBlock m => new ClockCardView { DataContext = m.GetOrCreateOwned(() => new ClockViewModel(m)) },
        SinGenerator => new SinGeneratorCardView(),
        Buffer buffer => new BufferCardView { DataContext = buffer.GetOrCreateOwned(() => new BufferViewModel(buffer)) },
        Function function => new FunctionCardView { DataContext = function.GetOrCreateOwned(() => new FunctionViewModel(function)) },
        SlidingWindow slidingWindow => new SlidingWindowCardView { DataContext = slidingWindow.GetOrCreateOwned(() => new SlidingWindowViewModel(slidingWindow)) },
        SupervisedPCA supervisedPca => new SupervisedPCAView { DataContext = supervisedPca.GetOrCreateOwned(() => new SupervisedPCAViewModel(supervisedPca)) },
        OnlinePCA onlinePCA => new OnlinePCAView { DataContext = onlinePCA.GetOrCreateOwned(() => new OnlinePCAViewModel(onlinePCA)) },
        HannesHand hannesHand => new HannesHandCardView { DataContext = hannesHand.GetOrCreateOwned(() => new HannesHandViewModel(hannesHand)) },
        ChannelSelector channelSelector => new ChannelSelectorCardView { DataContext = channelSelector.GetOrCreateOwned(() => new ChannelSelectorViewModel(channelSelector)) },
        MeanAverageValue meanAverageValue => new MeanAverageValueCardView { DataContext = meanAverageValue.GetOrCreateOwned(() => new MeanAverageValueViewModel(meanAverageValue)) },
        Projector projector => new ProjectorCardView { DataContext = projector.GetOrCreateOwned(() => new ProjectorViewModel(projector)) },
        Models.FlowControl.Selector selector => new SelectorCardView { DataContext = selector.GetOrCreateOwned(() => new SelectorViewModel(selector)) },
        SlopeSignChanges slopeSignChanges => new SlopeSignChangesCardView { DataContext = slopeSignChanges.GetOrCreateOwned(() => new SlopeSignChangesViewModel(slopeSignChanges)) },
        StimTrigger stimTrigger => new StimTriggerCardView { DataContext = stimTrigger.GetOrCreateOwned(() => new StimTriggerViewModel(stimTrigger)) },
        Switch sSwitch => new SwitchCardView { DataContext = sSwitch.GetOrCreateOwned(() => new SwitchViewModel(sSwitch)) },
        Trigger trigger => new TriggerCardView { DataContext = trigger.GetOrCreateOwned(() => new TriggerViewModel(trigger)) },
        WaveformLength waveform => new WaveformLengthCardView { DataContext = waveform.GetOrCreateOwned(() => new WaveformLengthViewModel(waveform)) },
        ZeroCrossings zeroCrossings => new ZeroCrossingsCardView { DataContext = zeroCrossings.GetOrCreateOwned(() => new ZeroCrossingsViewModel(zeroCrossings)) },
        Joiner joiner => new JoinerCardView { DataContext = joiner.GetOrCreateOwned(() => new JoinerViewModel(joiner)) },
        Resampler resampler => new ResamplerCardView { DataContext = resampler.GetOrCreateOwned(() => new ResamplerViewModel(resampler)) },
        CrossCorrelation xcorr => new CrossCorrelationCardView { DataContext = xcorr.GetOrCreateOwned(() => new CrossCorrelationViewModel(xcorr)) },
        SupervisedICA supervisedIca => new SupervisedICAView { DataContext = supervisedIca.GetOrCreateOwned(() => new SupervisedICAViewModel(supervisedIca)) },
        OnlineICA onlineIca => new OnlineICAView { DataContext = onlineIca.GetOrCreateOwned(() => new OnlineICAViewModel(onlineIca)) },
        OnlineLDA onlineLda => new OnlineLDAView { DataContext = onlineLda.GetOrCreateOwned(() => new OnlineLDAViewModel(onlineLda)) },
        MetricsExtractor metricsExtractor => new MetricsExtractorCardView { DataContext = metricsExtractor.GetOrCreateOwned(() => new MetricsExtractorViewModel(metricsExtractor)) },
        ControlAlgorithm controlAlgorithm => new ControlAlgorithmCardView { DataContext = controlAlgorithm.GetOrCreateOwned(() => new ControlAlgorithmViewModel(controlAlgorithm)) },
        IncrementalPredictor incrementalPredictorBlock => new IncrementalPredictorView { DataContext = incrementalPredictorBlock.GetOrCreateOwned(() => new IncrementalPredictorViewModel(incrementalPredictorBlock)) },
        Filter filterBlock => new FilterCardView { DataContext = filterBlock.GetOrCreateOwned(() => new FilterViewModel(filterBlock)) },
        Myo myo => new MyoCardView { DataContext = myo.GetOrCreateOwned(() => new MyoViewModel(myo)) },
        Muovi muoviBlock => new MuoviCardView{ DataContext = muoviBlock.GetOrCreateOwned(() => new MuoviViewModel(muoviBlock)) },
        MuoviSingleProbe muoviSingleProbe => new MuoviSingleProbeCardView{DataContext = muoviSingleProbe.GetOrCreateOwned(() => new MuoviSingleProbeViewModel(muoviSingleProbe)) },
#if ENABLE_DELSYS
        Delsys delsys => new DelsysCardView{ DataContext = delsys.GetOrCreateOwned(() => new ViewModels.Devices.DelsysViewModel(delsys))},
#endif
        SerialSender serialSender => new SerialSenderCardView { DataContext = serialSender.GetOrCreateOwned(() => new SerialSenderViewModel(serialSender)) },
        UDPClient udpClient => new UDPClientCardView { DataContext = udpClient.GetOrCreateOwned(() => new UDPClientViewModel(udpClient)) },
        UDPControlClient udpControlClient => new UDPControlClientCardView { DataContext = udpControlClient.GetOrCreateOwned(() => new UDPControlClientViewModel(udpControlClient)) },
        CommandSender commandSender => new CommandSenderCardView { DataContext = commandSender.GetOrCreateOwned(() => new CommandSenderViewModel(commandSender)) },
        BodyRig bodyRig => new BodyRigCardView { DataContext = bodyRig.GetOrCreateOwned(() => new BodyRigViewModel(bodyRig)) },
        DlrAdcBt dlrAdcBt => new DlrAdcBtCardView { DataContext = dlrAdcBt.GetOrCreateOwned(() => new DlrAdcBtViewModel(dlrAdcBt)) },
#if !MOSAIC_MOBILE
        MccDaqBoard mccDaq => new MccDaqBoardCardView { DataContext = mccDaq.GetOrCreateOwned(() => new MccDaqBoardViewModel(mccDaq)) },
#endif
        FFT fft => new FFTCardView() {DataContext = fft.GetOrCreateOwned(() => new FFTViewModel(fft))},
        UdpStreamlinedSender udp => new UdpStreamlinedSenderCardView() {DataContext = udp.GetOrCreateOwned(() => new UdpStreamlinedSenderViewModel(udp))},
        BlenderArm blenderArm => new BlenderArmCardView { DataContext = blenderArm.GetOrCreateOwned(() => new BlenderArmViewModel(blenderArm)) },
        Stimulus stimulus => new StimulusCardView { DataContext = stimulus.GetOrCreateOwned(() => new StimulusViewModel(stimulus)) },
        TAC tac => new TACCardView() {DataContext = tac.GetOrCreateOwned(() => new TACViewModel(tac))},
        SiFi sifi => new SiFiCardView() {DataContext = sifi.GetOrCreateOwned(() => new SifiViewModel(sifi)) },
#if !MOSAIC_MOBILE
        PyPredictor pyPredictor => new PyPredictorCardView() {DataContext = pyPredictor.GetOrCreateOwned(() => new PyPredictorViewModel(pyPredictor))},
#endif
#if !MOSAIC_MOBILE
        PyPredictorRegression pyPredictorRegression => new PyPredictorRegressionCardView() {DataContext = pyPredictorRegression.GetOrCreateOwned(() => new PyPredictorRegressionViewModel(pyPredictorRegression))},
#endif
        TriggerBuffer triggerBuffer => new TriggerBufferCardView { DataContext = triggerBuffer.GetOrCreateOwned(() => new TriggerBufferViewModel(triggerBuffer)) },
        ROS ros => new ROSCardView { DataContext = ros.GetOrCreateOwned(() => new ROSViewModel(ros)) },
#if !MOSAIC_MOBILE
        Models.Streaming.LSL lsl => new LSLCardView() {DataContext = lsl.GetOrCreateOwned(() => new LSLViewModel(lsl))},
#endif
#if !MOSAIC_MOBILE
        Wulpus wulpus => new WulpusCardView { DataContext = wulpus.GetOrCreateOwned(() => new WulpusViewModel(wulpus)) },
#endif
        DepthFilter depthFilter => new DepthFilterCardView { DataContext = depthFilter.GetOrCreateOwned(() => new DepthFilterViewModel(depthFilter)) },
        Crop crop => new CropCardView { DataContext = crop.GetOrCreateOwned(() => new CropViewModel(crop)) },
        Matrix2Vector matrix2Vector => new Matrix2VectorCardView { DataContext = matrix2Vector.GetOrCreateOwned(() => new Matrix2VectorViewModel(matrix2Vector)) },
        ClassifierBlock classifierBlock => new ClassifierCardView() {DataContext = classifierBlock.GetOrCreateOwned(() => new ClassifierBlockViewModel(classifierBlock))},
        Quattrocento quattrocento => new QuattrocentoCardView() {DataContext =  quattrocento.GetOrCreateOwned(() => new QuattrocentoViewModel(quattrocento)) },
#if !MOSAIC_MOBILE
        SupervisedUMAP umap => new SupervisedUMAPView() { DataContext = umap.GetOrCreateOwned(() => new SupervisedUMAPViewModel(umap)) },
#endif
        ManualControl manualControl => new ManualControlCardView { DataContext = manualControl.GetOrCreateOwned(() => new ManualControlViewModel(manualControl)) },
        BatchPredictor batchPredictor => new BatchPredictorView() {DataContext = batchPredictor.GetOrCreateOwned(() => new BatchPredictorViewModel(batchPredictor))},
#if !MOSAIC_MOBILE
        UltrasoundClassifier ultrasoundClassifier => new UltrasoundClassifierCardView() {DataContext = ultrasoundClassifier.GetOrCreateOwned(() => new UltrasoundClassifierViewModel(ultrasoundClassifier))},
#endif
        MockUltrasoundSource mockUltrasoundSource => new MockUltrasoundSourceCardView() {DataContext = mockUltrasoundSource.GetOrCreateOwned(() => new MockUltrasoundSourceViewModel(mockUltrasoundSource))},
        StreamTrigger streamTrigger => new StreamTriggerCardView { DataContext = streamTrigger.GetOrCreateOwned(() => new StreamTriggerViewModel(streamTrigger)) },


        _ => new TextBlock
        {
            Margin = new Thickness(12),
            Text = data?.GetType().Name ?? "Unknown item",
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        }
    };
}
