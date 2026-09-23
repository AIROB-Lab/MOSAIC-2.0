using System;
using MOSAIC.Components.Basics;

namespace MOSAIC.Components.Interfaces;

/// <summary>
/// Defines a factory that creates runtime block instances from a <see cref="JsonModel"/> configuration.
/// </summary>
/// <remarks>
/// <para>
/// Implementations typically use <see cref="JsonModel.Type"/> to resolve a concrete runtime type
/// (for example via reflection or dependency injection) and then apply configuration values such as
/// <see cref="JsonModel.Params"/>, <see cref="JsonModel.Inputs"/>, and <see cref="JsonModel.DesiredRate"/>.
/// </para>
/// <para>
/// The factory returns <see cref="object"/> to support heterogeneous pipelines. Use <see cref="Create{T}(JsonModel)"/>
/// when the expected block type is known.
/// </para>
/// </remarks>
public interface IBlockFactory
{
    /// <summary>Resolves a registered runtime type without opening devices. Custom factories may return null.</summary>
    Type? GetBlockType(string type) => null;

    /// <summary>
    /// Creates a block instance from the provided configuration model.
    /// </summary>
    /// <param name="m">Block configuration model.</param>
    /// <returns>The created block instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="m"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the configuration cannot be resolved to a concrete block type or the block cannot be constructed.
    /// </exception>
    object Create(JsonModel m);

    /// <summary>
    /// Creates a block instance from the provided configuration model and casts it to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Expected runtime type of the created block.</typeparam>
    /// <param name="m">Block configuration model.</param>
    /// <returns>The created block instance cast to <typeparamref name="T"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="m"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the configuration cannot be resolved to a concrete block type, the block cannot be constructed,
    /// or the created instance is not compatible with <typeparamref name="T"/>.
    /// </exception>
    T Create<T>(JsonModel m) where T : class;
}
