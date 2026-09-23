using System.Collections.Generic;
using System.Linq;

namespace MOSAIC.Components.Manager.BLE.Commands;

public enum MyoCommandEnum
{
    //InitialNotifications
    EMG01,
    EMG02,
    EMG03,
    EMG04,
    //InitialCmds
    NoSleep,
    FiltEMG_NoIMU_NoClass,
    VibrateLong,
    VibrateLong2,
    //EndCmds
    NoFiltEMG_NoIMU_NoClass,
    NormalSleep,
    VibrateShort,
    VibrateShort2
}

public class MyoCommandDefinitions : BleCommandDefinitions<MyoCommandEnum>
{
    public override List<Dictionary<MyoCommandEnum, BleCommand>> Commands { get; } = new List<Dictionary<MyoCommandEnum, BleCommand>>
    {
        InitialNotifications,
        InitialCommands,
        EndCommands,
    };

    public readonly static Dictionary<MyoCommandEnum, BleCommand> InitialNotifications = new()
    {
        {MyoCommandEnum.EMG01, new BleCommand("d5060005-a904-deb9-4748-2c7f4a124842", "d5060105-a904-deb9-4748-2c7f4a124842")},
        {MyoCommandEnum.EMG02, new BleCommand("d5060005-a904-deb9-4748-2c7f4a124842", "d5060205-a904-deb9-4748-2c7f4a124842")},
        {MyoCommandEnum.EMG03, new BleCommand("d5060005-a904-deb9-4748-2c7f4a124842", "d5060305-a904-deb9-4748-2c7f4a124842")},
        {MyoCommandEnum.EMG04, new BleCommand("d5060005-a904-deb9-4748-2c7f4a124842", "d5060405-a904-deb9-4748-2c7f4a124842")},
    };

    private readonly static Dictionary<MyoCommandEnum, BleCommand> InitialCommands = new()
    {
        {MyoCommandEnum.NoSleep, new BleCommand("D5060001-A904-DEB9-4748-2C7F4A124842", "d5060401-a904-deb9-4748-2c7f4a124842", [0x09, 0x01, 0x01])},
        {MyoCommandEnum.FiltEMG_NoIMU_NoClass, new BleCommand("D5060001-A904-DEB9-4748-2C7F4A124842", "d5060401-a904-deb9-4748-2c7f4a124842", [0x01, 0x03, 0x02, 0x00, 0x00])},
        {MyoCommandEnum.VibrateLong, new BleCommand("D5060001-A904-DEB9-4748-2C7F4A124842", "d5060401-a904-deb9-4748-2c7f4a124842", [0x03, 0x01, 0x03])},
        {MyoCommandEnum.VibrateLong2, new BleCommand("D5060001-A904-DEB9-4748-2C7F4A124842", "d5060401-a904-deb9-4748-2c7f4a124842", [0x03, 0x01, 0x03])},
    };

    private readonly static Dictionary<MyoCommandEnum, BleCommand> EndCommands = new()
    {
        {MyoCommandEnum.NoFiltEMG_NoIMU_NoClass, new BleCommand("D5060001-A904-DEB9-4748-2C7F4A124842", "d5060401-a904-deb9-4748-2c7f4a124842", [0x01, 0x03, 0x00, 0x00, 0x00])},
        {MyoCommandEnum.NormalSleep, new BleCommand("D5060001-A904-DEB9-4748-2C7F4A124842", "d5060401-a904-deb9-4748-2c7f4a124842", [0x09, 0x01, 0x00])},
        {MyoCommandEnum.VibrateShort, new BleCommand("D5060001-A904-DEB9-4748-2C7F4A124842", "d5060401-a904-deb9-4748-2c7f4a124842", [0x03, 0x01, 0x01])},
        {MyoCommandEnum.VibrateShort2, new BleCommand("D5060001-A904-DEB9-4748-2C7F4A124842", "d5060401-a904-deb9-4748-2c7f4a124842", [0x03, 0x01, 0x01])},
    };


    public override List<BleCommand> GetInitialNotificationCommands()
    {
        return InitialNotifications.Values.ToList();
    }
}