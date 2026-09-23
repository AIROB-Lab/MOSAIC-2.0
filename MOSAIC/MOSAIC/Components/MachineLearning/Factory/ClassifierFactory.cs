using System;
using MOSAIC.Components.MachineLearning.Classifier;
using MOSAIC.Components.MachineLearning.Interfaces;
using MOSAIC.MachineLearning.Classification;

namespace MOSAIC.Components.MachineLearning.Factory;

/// <summary>
/// Available classifier types.
/// </summary>
public enum ClassifierType
{
    /// <summary>
    /// Online Softmax Classifier (linear).
    /// Fast, simple, works well for linearly separable classes.
    /// </summary>
    Softmax,

    /// <summary>
    /// Softmax Classifier with Random Fourier Features.
    /// Captures nonlinear patterns via RBF kernel approximation.
    /// </summary>
    SoftmaxRFF,

    /// <summary>
    /// Fisher Linear Discriminant Analysis (batch).
    /// Finds optimal linear projection maximising class separation.
    /// </summary>
    LDA,

    /// <summary>
    /// k-Nearest Neighbors (lazy learner).
    /// Non-parametric, captures arbitrary decision boundaries.
    /// </summary>
    KNN,

    /// <summary>
    /// Random Forest (batch ensemble).
    /// Robust, handles nonlinear boundaries, resistant to overfitting.
    /// </summary>
    RandomForest,

    /// <summary>
    /// Linear SVM with SGD on multi-class hinge loss.
    /// Good margin-based separation for linearly separable classes.
    /// </summary>
    LinearSVM,

    /// <summary>
    /// Simple nearest-centroid classifier with rejection threshold.
    /// Baseline classifier, fast, minimal computation.
    /// </summary>
    Threshold
}

/// <summary>
/// Configuration for classifier creation.
/// </summary>
public sealed record ClassifierConfig
{
    /// <summary>
    /// Learning rate for SGD-based classifiers (Softmax, SVM). Default: 0.01.
    /// </summary>
    public double LearningRate { get; init; } = 0.01;

    /// <summary>
    /// L2 regularization parameter (λ). Default: 0.001.
    /// </summary>
    public double Lambda { get; init; } = 0.001;

    /// <summary>
    /// RBF kernel bandwidth (σ). Only used for RFF. Default: 1.0.
    /// </summary>
    public double Sigma { get; init; } = 1.0;

    /// <summary>
    /// Number of random Fourier features (D). Only used for RFF. Default: 300.
    /// </summary>
    public int FeatureDim { get; init; } = 300;

    /// <summary>
    /// Random seed. Null = random. Set for reproducibility.
    /// </summary>
    public int? Seed { get; init; } = null;

    // ── kNN-specific ─────────────────────────────────────────────

    /// <summary>
    /// Number of neighbors for kNN. Default: 5.
    /// </summary>
    public int K { get; init; } = 5;

    /// <summary>
    /// Weight kNN votes by inverse distance. Default: true.
    /// </summary>
    public bool WeightByDistance { get; init; } = true;

    /// <summary>
    /// Maximum stored kNN samples (0 = unlimited). Default: 10000.
    /// </summary>
    public int MaxSamples { get; init; } = 10000;

    // ── Random Forest-specific ───────────────────────────────────

    /// <summary>
    /// Number of trees in the forest. Default: 100.
    /// </summary>
    public int NumTrees { get; init; } = 100;

    /// <summary>
    /// Maximum tree depth. Default: 15.
    /// </summary>
    public int MaxDepth { get; init; } = 15;

    /// <summary>
    /// Minimum samples per leaf node. Default: 2.
    /// </summary>
    public int MinSamplesLeaf { get; init; } = 2;

    // ── LDA-specific ─────────────────────────────────────────────

    /// <summary>
    /// Regularization added to within-class scatter diagonal (LDA). Default: 1e-6.
    /// </summary>
    public double LdaRegularization { get; init; } = 1e-6;

    // ── Threshold-specific ───────────────────────────────────────

    /// <summary>
    /// Rejection threshold for the Threshold classifier. Default: 0.0 (no rejection).
    /// </summary>
    public double ConfidenceThreshold { get; init; } = 0.0;

    /// <summary>
    /// Class label returned on rejection. Default: 0 (rest).
    /// </summary>
    public int RejectClass { get; init; } = 0;
}

/// <summary>
/// Factory for creating classifier instances.
/// </summary>
public static class ClassifierFactory
{
    /// <summary>
    /// Creates a classifier of the specified type.
    /// </summary>
    /// <param name="type">The type of classifier to create.</param>
    /// <param name="inputDim">Input dimension (number of features).</param>
    /// <param name="numClasses">Number of classes.</param>
    /// <param name="config">Configuration (uses defaults if null).</param>
    /// <returns>A new classifier instance.</returns>
    public static IClassifier Create(
        ClassifierType type,
        int inputDim,
        int numClasses,
        ClassifierConfig? config = null)
    {
        config ??= new ClassifierConfig();

        return type switch
        {
            ClassifierType.Softmax => new OnlineSoftmax(
                inputDim,
                numClasses,
                config.LearningRate,
                config.Lambda),

            ClassifierType.SoftmaxRFF => new RFFClassification(
                inputDim,
                numClasses,
                config.LearningRate,
                config.Lambda,
                config.Sigma,
                config.FeatureDim,
                config.Seed),

            ClassifierType.LDA => new LdaClassifier(
                inputDim,
                numClasses,
                config.LdaRegularization),

            ClassifierType.KNN => new KnnClassifier(
                inputDim,
                numClasses,
                config.K,
                config.WeightByDistance,
                config.MaxSamples),

            ClassifierType.RandomForest => new RandomForestClassifier(
                inputDim,
                numClasses,
                config.NumTrees,
                config.MaxDepth,
                config.MinSamplesLeaf,
                config.Seed),

            ClassifierType.LinearSVM => new LinearSvmClassifier(
                inputDim,
                numClasses,
                config.LearningRate,
                config.Lambda),

            ClassifierType.Threshold => new ThresholdClassifier(
                inputDim,
                numClasses,
                config.ConfidenceThreshold,
                config.RejectClass),

            _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown classifier type: {type}")
        };
    }

    /// <summary>
    /// Gets a description of a classifier type.
    /// </summary>
    public static string GetDescription(ClassifierType type) => type switch
    {
        ClassifierType.Softmax =>
            "Online Softmax using SGD. Fast and simple, best for linearly separable classes.",
        ClassifierType.SoftmaxRFF =>
            "Softmax with Random Fourier Features. Approximates RBF kernel for nonlinear boundaries.",
        ClassifierType.LDA =>
            "Fisher LDA (batch). Finds the linear projection that maximises between-class / within-class scatter ratio.",
        ClassifierType.KNN =>
            "k-Nearest Neighbors. Non-parametric lazy learner, captures arbitrary decision boundaries.",
        ClassifierType.RandomForest =>
            "Random Forest (batch). Ensemble of decision trees with bagging and random feature subsets.",
        ClassifierType.LinearSVM =>
            "Linear SVM with SGD on multi-class hinge loss. Good margin-based linear separation.",
        ClassifierType.Threshold =>
            "Nearest-centroid with rejection. Simplest possible classifier, useful as a baseline.",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets the display name for a classifier type.
    /// </summary>
    public static string GetDisplayName(ClassifierType type) => type switch
    {
        ClassifierType.Softmax => "Online Softmax",
        ClassifierType.SoftmaxRFF => "Online Softmax + RFF",
        ClassifierType.LDA => "Fisher LDA",
        ClassifierType.KNN => "k-NN",
        ClassifierType.RandomForest => "Random Forest",
        ClassifierType.LinearSVM => "Linear SVM",
        ClassifierType.Threshold => "Threshold (Centroid)",
        _ => type.ToString()
    };

    /// <summary>
    /// Returns whether the classifier type requires batch training
    /// (call <c>TrainBatch</c> before prediction) vs. online-only.
    /// </summary>
    public static bool IsBatchClassifier(ClassifierType type) => type switch
    {
        ClassifierType.LDA => true,
        ClassifierType.RandomForest => true,
        _ => false
    };
}