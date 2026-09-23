using System;
using MOSAIC.Components.MachineLearning.Interfaces;
using MOSAIC.MachineLearning.Regression;

namespace MOSAIC.Components.MachineLearning.Factory
{
    /// <summary>
    /// Available regressor types.
    /// </summary>
    public enum RegressorType
    {
        /// <summary>
        /// Online Ridge Regression (linear).
        /// Fast, simple, works well for linear relationships.
        /// </summary>
        Ridge,

        /// <summary>
        /// Ridge Regression with Random Fourier Features.
        /// Captures nonlinear patterns via RBF kernel approximation.
        /// </summary>
        RidgeRFF,

        /// <summary>
        /// Recursive Least Squares (RLS) with forgetting factor.
        /// Adapts quickly to drift/non-stationarity.
        /// </summary>
        RecursiveLeastSquares,
    }

    /// <summary>
    /// Configuration for regressor creation.
    /// </summary>
    public sealed record RegressorConfig
    {
        /// <summary>
        /// Regularization parameter (λ). Default: 1.0.
        /// Higher values = stronger regularization = simpler model.
        /// </summary>
        public double Lambda { get; init; } = 1.0;

        /// <summary>
        /// RBF kernel bandwidth (σ). Only used for RFF. Default: 1.0.
        /// Controls how "local" the kernel is.
        /// </summary>
        public double Sigma { get; init; } = 1.0;

        /// <summary>
        /// Number of random Fourier features (D). Only used for RFF. Default: 300.
        /// Higher = better kernel approximation but slower.
        /// </summary>
        public int FeatureDim { get; init; } = 300;

        /// <summary>
        /// Random seed for RFF. Null = random. Set for reproducibility.
        /// </summary>
        public int? Seed { get; init; } = null;

        /// <summary>
        /// RLS initialization regularization (delta). P0 = (1/delta) I.
        /// Only used for RecursiveLeastSquares. Default: 1.0.
        /// </summary>
        public double Delta { get; init; } = 1.0;

        /// <summary>
        /// RLS forgetting factor ff in (0,1]. 1.0 = no forgetting.
        /// Only used for RecursiveLeastSquares. Default: 1.0.
        /// Typical for drift adaptation: 0.99..0.999
        /// </summary>
        public double ForgettingFactor { get; init; } = 1.0;
    }

    /// <summary>
    /// Factory for creating regressor instances.
    /// </summary>
    public static class RegressorFactory
    {
        /// <summary>
        /// Creates a regressor of the specified type.
        /// </summary>
        public static IRegressor Create(
            RegressorType type,
            int inputDim,
            int outputDim,
            RegressorConfig? config = null)
        {
            config ??= new RegressorConfig();

            return type switch
            {
                RegressorType.Ridge
                    => new RidgeRegression(inputDim, outputDim, config.Lambda),

                RegressorType.RidgeRFF
                    => new RFFRegression(
                        inputDim,
                        outputDim,
                        config.Lambda,
                        config.Sigma,
                        config.FeatureDim,
                        config.Seed),

                RegressorType.RecursiveLeastSquares
                    => new RecursiveLeastSquares(
                        inputDim,
                        outputDim,
                        config.Delta,
                        config.ForgettingFactor),

                _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown regressor type: {type}")
            };
        }

        /// <summary>
        /// Gets a description of a regressor type.
        /// </summary>
        public static string GetDescription(RegressorType type) => type switch
        {
            RegressorType.Ridge
                => "Online Ridge Regression using Sherman-Morrison updates. Fast and simple, best for linear relationships.",

            RegressorType.RidgeRFF
                => "Ridge Regression with Random Fourier Features. Approximates RBF kernel for nonlinear patterns.",

            RegressorType.RecursiveLeastSquares
                => "Recursive Least Squares (RLS) with optional forgetting factor. Very fast online adaptation; good for non-stationary signals (e.g., EMG drift).",

            _ => "Unknown"
        };

        /// <summary>
        /// Gets the display name for a regressor type.
        /// </summary>
        public static string GetDisplayName(RegressorType type) => type switch
        {
            RegressorType.Ridge => "Online Ridge",
            RegressorType.RidgeRFF => "Online Ridge + RFF",
            RegressorType.RecursiveLeastSquares => "Online RLS",
            _ => type.ToString()
        };
    }
}
