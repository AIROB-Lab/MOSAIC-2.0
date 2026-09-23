using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace MOSAIC.Components.Devices.HannesHand;

/// <summary>
/// Wrapper around SerialPort for the Rehab Dongle.
/// Based on Silicon Labs BGX Bluetooth Xpress protocol.
/// </summary>
internal sealed class RehabDongleClient : IDisposable
{
    private readonly SerialPort _serialPort = new()
    {
        BaudRate = 115200,
        DataBits = 8,
        Parity = Parity.None,
        StopBits = StopBits.One,
        Handshake = Handshake.None,
        ReadTimeout = 500,  // Increased timeout
        WriteTimeout = 500,
        NewLine = "\r\n"    // Dongle expects \r\n
    };

    public bool IsOpen => _serialPort.IsOpen;

    public void Open(string portName)
    {
        if (_serialPort.IsOpen) 
        {
            Debug.WriteLine($"[RehabDongle] Port already open, closing first...");
            _serialPort.Close();
            Thread.Sleep(100);
        }
        
        _serialPort.PortName = portName;
        _serialPort.Open();
        
        _serialPort.DiscardInBuffer();
        _serialPort.DiscardOutBuffer();
        
        Debug.WriteLine($"[RehabDongle] Opened {portName}");
    }

    /// <summary>
    /// Enter dongle command mode by sending "$$$" (no line ending!)
    /// </summary>
    public void EnterDongleCommandMode()
    {
        Debug.WriteLine($"[RehabDongle] Sending '$$$' (command mode)...");
        // Important: NO line ending after $$$
        _serialPort.Write("$$$");
    }

    /// <summary>
    /// Send a command with \r\n line ending.
    /// </summary>
    public void WriteLine(string cmd)
    {
        Debug.WriteLine($"[RehabDongle] Sending: '{cmd}\\r\\n'");
        _serialPort.Write(cmd + "\r\n");
    }

    /// <summary>
    /// Send raw bytes.
    /// </summary>
    public void WriteRaw(byte[] buffer)
    {
        Debug.WriteLine($"[RehabDongle] Sending {buffer.Length} raw bytes");
        _serialPort.Write(buffer, 0, buffer.Length);
    }

    /// <summary>
    /// Clear input buffer.
    /// </summary>
    public void ClearInput()
    {
        try
        {
            _serialPort.DiscardInBuffer();
            // Also read any pending data
            if (_serialPort.BytesToRead > 0)
            {
                var discard = _serialPort.ReadExisting();
                Debug.WriteLine($"[RehabDongle] Cleared {discard.Length} chars from buffer");
            }
        }
        catch { }
    }

    /// <summary>
    /// Read all available data as a string.
    /// </summary>
    public string ReadExisting()
    {
        try
        {
            return _serialPort.ReadExisting();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Read all lines until timeout.
    /// </summary>
    public string[] ReadAllLinesUntilTimeout()
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        
        try
        {
            // Give device time to respond
            Thread.Sleep(50);
            
            while (_serialPort.BytesToRead > 0 || list.Count == 0)
            {
                try
                {
                    var line = _serialPort.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                    {
                        list.Add(line);
                        Debug.WriteLine($"[RehabDongle] Read line: '{line.TrimEnd()}'");
                    }
                }
                catch (TimeoutException)
                {
                    // No more data
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            // Normal - no data available
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RehabDongle] ReadAllLines error: {ex.Message}");
        }

        return list.ToArray();
    }

    /// <summary>
    /// Read with longer timeout for scan operations.
    /// </summary>
    public string[] ReadAllLinesWithTimeout(int timeoutMs)
    {
        var list = new List<string>();
        var oldTimeout = _serialPort.ReadTimeout;
        
        try
        {
            _serialPort.ReadTimeout = timeoutMs;
            
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var line = _serialPort.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                    {
                        list.Add(line);
                        Debug.WriteLine($"[RehabDongle] Read: '{line.TrimEnd()}'");
                    }
                }
                catch (TimeoutException)
                {
                    if (list.Count > 0)
                        break; // Got some data, probably done
                }
            }
        }
        finally
        {
            _serialPort.ReadTimeout = oldTimeout;
        }

        return list.ToArray();
    }

    public void Dispose()
    {
        try
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                _serialPort.Close();
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[RehabDongle] Dispose error: {e.Message}");
        }
    }
}