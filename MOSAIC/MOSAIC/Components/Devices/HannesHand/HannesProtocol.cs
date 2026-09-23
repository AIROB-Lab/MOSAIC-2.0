namespace MOSAIC.Components.Devices.HannesHand;

/// <summary>Protocol enums and helpers for Hannes device.</summary>
internal static class HannesProtocol
{
    public const byte Start = 0x25; // '%'
    public const byte End   = 0x26; // '&'
    public const byte CR    = 0x0D;
    public const byte LF    = 0x0A;

    internal enum Cmd : byte
    {
        EnterCMDMode,
        ExitCMDMode,
        ACK,
        NACK,
        // ... (other values omitted)
        RefControl = 14,
        SimultRefControl = 30
    }

    internal enum Ref : byte
    {
        REF_JOINT_SET,
        REF_HAND,
        REF_WRIST_PS,
        REF_WRIST_FE,
        REF_ELBOW,
        REF_CONTROL_MODE,
        REF_THUMB,
        REF_3DDIGIT
    }

    internal enum ControlMode : byte
    {
        EMG_CONTROL,
        HMI_CONTROL,
        EMC_TEST_CONTROL,
        UNITY_CONTROL
    }

    internal enum StreamMode { Cmd, Stream }
}