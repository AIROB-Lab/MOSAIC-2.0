using System;

namespace MOSAIC.Components.Basics;

/// <summary>
/// A fixed-size circular (ring) buffer that overwrites the oldest elements once full.
/// </summary>
/// <remarks>
/// <para>
/// Elements are appended sequentially via <see cref="Add"/>. Once the buffer reaches capacity,
/// new elements overwrite the oldest entries. <see cref="First"/> always returns the oldest
/// element and <see cref="Last"/> the most recently added one.
/// </para>
/// <para>
/// This structure is not thread-safe. If concurrent access is required, the caller must
/// synchronize externally.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of elements stored in the buffer.</typeparam>
public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private int _currentIdx;
    private bool _firstRound = true;

    /// <summary>
    /// Gets the fixed capacity of the buffer (set at construction time).
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets the number of valid elements currently in the buffer.
    /// </summary>
    /// <remarks>
    /// Returns the count of elements added so far while filling for the first time.
    /// Once the buffer has wrapped at least once, this always equals <see cref="Capacity"/>.
    /// </remarks>
    public int Count => _firstRound ? _currentIdx : _buffer.Length;

    /// <summary>
    /// Gets a value indicating whether the buffer has wrapped at least once
    /// (i.e., <see cref="Count"/> equals <see cref="Capacity"/>).
    /// </summary>
    public bool IsFull => !_firstRound;

    /// <summary>
    /// Gets the oldest element in the buffer.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the buffer is empty.</exception>
    public T First
    {
        get
        {
            if (_firstRound && _currentIdx == 0)
                throw new InvalidOperationException("The buffer is empty.");

            return _buffer[_firstRound ? 0 : _currentIdx];
        }
    }

    /// <summary>
    /// Gets the most recently added element in the buffer.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the buffer is empty.</exception>
    public T Last
    {
        get
        {
            if (_firstRound && _currentIdx == 0)
                throw new InvalidOperationException("The buffer is empty.");

            return _buffer[_currentIdx > 0 ? _currentIdx - 1 : _buffer.Length - 1];
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CircularBuffer{T}"/> class with the specified capacity.
    /// </summary>
    /// <param name="capacity">The fixed number of elements the buffer can hold. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than or equal to zero.</exception>
    public CircularBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = new T[capacity];
    }

    /// <summary>
    /// Appends a value to the buffer, overwriting the oldest element if the buffer is full.
    /// </summary>
    /// <param name="value">The value to append.</param>
    public void Add(T value)
    {
        _buffer[_currentIdx++] = value;
        if (_currentIdx == _buffer.Length)
        {
            _firstRound = false;
            _currentIdx = 0;
        }
    }

    /// <summary>
    /// Resets the buffer to its initial empty state.
    /// </summary>
    /// <remarks>
    /// Existing array slots are not cleared; they will be overwritten by subsequent <see cref="Add"/> calls.
    /// If <typeparamref name="T"/> is a reference type and deterministic release of old references is needed,
    /// use <see cref="Clear"/> instead.
    /// </remarks>
    public void Reset()
    {
        _currentIdx = 0;
        _firstRound = true;
    }

    /// <summary>
    /// Resets the buffer to its initial empty state and zeroes all array slots.
    /// </summary>
    /// <remarks>
    /// Use this instead of <see cref="Reset"/> when <typeparamref name="T"/> is a reference type,
    /// and you want to release references for garbage collection immediately.
    /// </remarks>
    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _currentIdx = 0;
        _firstRound = true;
    }
}