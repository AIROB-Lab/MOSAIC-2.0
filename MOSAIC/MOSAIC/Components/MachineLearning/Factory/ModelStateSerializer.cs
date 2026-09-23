using System;
using System.IO;
using System.Text.Json;
using MOSAIC.Components.MachineLearning.Interfaces;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Components.MachineLearning.Factory
{
    /// <summary>
    /// Serializable format for RidgeState.
    /// </summary>
    internal sealed class RidgeStateDto
    {
        public double[][] Ainv { get; set; } = Array.Empty<double[]>();
        public double[][] B { get; set; } = Array.Empty<double[]>();
    }

    /// <summary>
    /// Utilities for saving and loading model states.
    /// </summary>
    public static class ModelStateSerializer
    {
        /// <summary>
        /// Saves a RidgeState to a JSON file.
        /// </summary>
        public static void Save(RidgeState state, string path)
        {
            var dto = new RidgeStateDto
            {
                Ainv = MatrixToArray(state.Ainv),
                B = MatrixToArray(state.B)
            };

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Loads a RidgeState from a JSON file.
        /// </summary>
        public static RidgeState Load(string path)
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RidgeStateDto>(json)
                ?? throw new InvalidDataException("Failed to deserialize state");

            return new RidgeState
            {
                Ainv = ArrayToMatrix(dto.Ainv),
                B = ArrayToMatrix(dto.B)
            };
        }

        /// <summary>
        /// Saves a RidgeState to a simple CSV format (for compatibility with old code).
        /// </summary>
        public static void SaveCsv(RidgeState state, string path)
        {
            using var writer = new StreamWriter(path);

            for (int i = 0; i < state.Ainv.RowCount; i++)
            {
                var row = state.Ainv.Row(i);
                writer.WriteLine(string.Join(",", row));
            }

            for (int i = 0; i < state.B.RowCount; i++)
            {
                var row = state.B.Row(i);
                writer.WriteLine(string.Join(",", row));
            }
        }

        /// <summary>
        /// Loads a RidgeState from CSV format.
        /// </summary>
        /// <param name="path">Path to the CSV file.</param>
        /// <param name="inputDim">Input dimension (to know where Ainv ends and B begins).</param>
        public static RidgeState LoadCsv(string path, int inputDim)
        {
            var lines = File.ReadAllLines(path);

            var ainvRows = new double[inputDim][];
            var bRows = new double[inputDim][];

            for (int i = 0; i < inputDim; i++)
            {
                ainvRows[i] = Array.ConvertAll(lines[i].Split(','), double.Parse);
            }

            for (int i = 0; i < inputDim; i++)
            {
                bRows[i] = Array.ConvertAll(lines[inputDim + i].Split(','), double.Parse);
            }

            return new RidgeState
            {
                Ainv = Matrix.Build.DenseOfRowArrays(ainvRows),
                B = Matrix.Build.DenseOfRowArrays(bRows)
            };
        }

        private static double[][] MatrixToArray(Matrix m)
        {
            var result = new double[m.RowCount][];
            for (int i = 0; i < m.RowCount; i++)
                result[i] = m.Row(i).ToArray();
            return result;
        }

        private static Matrix ArrayToMatrix(double[][] arr)
        {
            return Matrix.Build.DenseOfRowArrays(arr);
        }
    }
}
