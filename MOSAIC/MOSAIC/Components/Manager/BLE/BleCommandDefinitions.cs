using System;
using System.Collections.Generic;
using System.Linq;

namespace MOSAIC.Components.Manager.BLE;

public abstract class BleCommandDefinitions<TCommandEnum> where TCommandEnum : Enum
{
    public abstract List<Dictionary<TCommandEnum, BleCommand>> Commands { get; }

    public BleCommand GetCommand(TCommandEnum commandEnum)
    {
        return Commands
            .SelectMany(dict => dict)
            .FirstOrDefault(kvp => kvp.Key.Equals(commandEnum))
            .Value;
    }

    public List<BleCommand> GetAllCommands()
    {
        return Commands
            .SelectMany(dict => dict.Values)
            .ToList();
    }

    public abstract List<BleCommand> GetInitialNotificationCommands();
}