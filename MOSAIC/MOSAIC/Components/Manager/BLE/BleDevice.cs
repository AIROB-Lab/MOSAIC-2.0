using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MOSAIC.Components.Manager.BLE;

public class BleDevice
{
    public string Name { get; set; }
    public IBlePeripheral Peripheral { get; }
    public List<byte[]> SampleBuffer = [];
    private static readonly object _lockSampleBuffer = new();
    private int _numberOfChannels;
    private readonly TaskCompletionSource _firstSample = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes only after a valid notification has supplied at least one sample.</summary>
    public Task WaitForFirstSampleAsync(TimeSpan timeout, CancellationToken ct = default)
        => _firstSample.Task.WaitAsync(timeout, ct);

    public BleDevice(string name, IBlePeripheral peripheral, int sampleLength = 0)
    {
        Name = name;
        Peripheral = peripheral;
        _numberOfChannels = sampleLength;
    }

    public void SetSampleLength(int sampleLength)
    {
        _numberOfChannels = sampleLength;
    }

    public byte[]? DequeueBuffer()
    {
        lock (_lockSampleBuffer)
        {
            if (SampleBuffer.Count == 0)
                return null;

            byte[] newByteSample = SampleBuffer[0];
            SampleBuffer.RemoveAt(0);
            return newByteSample;
        }
    }

    public int ShortenBuffer(int maxDelay)
    {
        lock (_lockSampleBuffer)
        {
            int numOfRemoval = 0;
            if (SampleBuffer.Count > maxDelay)
            {
                numOfRemoval = SampleBuffer.Count - maxDelay;
                SampleBuffer.RemoveRange(0, numOfRemoval);
                Console.WriteLine($"Buffer was cut short: {numOfRemoval} samples");
            }

            return numOfRemoval;
        }
    }

    public void HandleCharacteristicValue(byte[] characteristicVal)
    {
        if (characteristicVal.Length == 0)
        {
            Debug.WriteLine("Characteristic payload is empty. Ignoring.");
            return;
        }

        if (_numberOfChannels <= 0)
        {
            throw new InvalidOperationException(
                $"Sample length is not set (got {_numberOfChannels}). Call SetSampleLength() before streaming.");
        }

        int count = characteristicVal.Length;
        if (count % _numberOfChannels != 0)
        {
            throw new InvalidOperationException(
                $"Your sample length {_numberOfChannels} does not match the streamed data length {count}");
        }

        int numberSamples = count / _numberOfChannels;

        lock (_lockSampleBuffer)
        {
            for (int i = 0; i < numberSamples; i++)
            {
                var newSample = new Span<byte>(characteristicVal, i * _numberOfChannels, _numberOfChannels).ToArray();
                SampleBuffer.Add(newSample);
            }
        }

        if (_firstSample.TrySetResult())
            Console.WriteLine($"[BLE] First samples received from {Name}: {numberSamples} x {_numberOfChannels} bytes.");
    }
}
