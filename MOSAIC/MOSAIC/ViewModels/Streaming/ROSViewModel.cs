using System;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Streaming;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.ViewModels.Streaming;

/// <summary>
/// ViewModel for <see cref="MOSAIC.Models.Streaming.ROS"/>. Surfaces connection state, message
/// statistics, editable connection parameters, and a reconnect command.
/// </summary>
public sealed partial class ROSViewModel : ObservableObject, IDisposable
{
    #region Statics

    public static readonly string[] ActionOptions = { "subscribe", "publish" };

    #endregion

    #region Fields

    private readonly ROS _block;

    #endregion

    #region Properties

    public ROS Block => _block;

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private string _connectionStatus  = "Disconnected";
    [ObservableProperty] private long   _messageCount;
    [ObservableProperty] private string _lastValue         = "—";
    [ObservableProperty] private bool   _isSettingsExpanded;

    // Editable copies of connection params
    [ObservableProperty] private string _editIpAddress   = string.Empty;
    [ObservableProperty] private string _editAction      = string.Empty;
    [ObservableProperty] private string _editTopic       = string.Empty;
    [ObservableProperty] private string _editMessageType = string.Empty;
    [ObservableProperty] private int    _editNumChannels = 1;

    #endregion

    #region Constructor

    public ROSViewModel(ROS block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        // Mirror initial state
        IsConnected      = block.IsConnected;
        ConnectionStatus = block.ConnectionStatus;
        MessageCount     = block.MessageCount;

        // Populate editable fields from block
        SyncEditFields();

        block.OnMessageReceived += HandleMessage;

        block.PropertyChanged += (_, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ROS.IsConnected):
                        IsConnected = block.IsConnected;
                        break;
                    case nameof(ROS.ConnectionStatus):
                        ConnectionStatus = block.ConnectionStatus;
                        break;
                    case nameof(ROS.MessageCount):
                        MessageCount = block.MessageCount;
                        break;
                }
            });
        };
    }

    #endregion

    #region Handlers

    private void HandleMessage(Vector v)
    {
        Dispatcher.UIThread.Post(() =>
        {
            MessageCount = _block.MessageCount;
            LastValue = v.Count <= 5
                ? string.Join(", ", System.Linq.Enumerable.Range(0, v.Count).Select(i => $"{v[i]:F2}"))
                : string.Join(", ", System.Linq.Enumerable.Range(0, 5).Select(i => $"{v[i]:F2}")) + "…";
        }, DispatcherPriority.Background);
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void ToggleSettingsExpanded() => IsSettingsExpanded = !IsSettingsExpanded;

    [RelayCommand]
    private void ApplyAndReconnect()
    {
        // Push edits back into the block
        _block.IpAddress   = EditIpAddress;
        _block.Action      = EditAction;
        _block.Topic       = EditTopic;
        _block.MessageType = EditMessageType;
        _block.NumChannels = EditNumChannels;

        _block.Reconnect();
        IsSettingsExpanded = false;
    }

    [RelayCommand]
    private void Reconnect() => _block.Reconnect();

    #endregion

    #region Helpers

    private void SyncEditFields()
    {
        EditIpAddress   = _block.IpAddress;
        EditAction      = _block.Action;
        EditTopic       = _block.Topic;
        EditMessageType = _block.MessageType;
        EditNumChannels = _block.NumChannels;
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        _block.OnMessageReceived -= HandleMessage;
    }

    #endregion
}