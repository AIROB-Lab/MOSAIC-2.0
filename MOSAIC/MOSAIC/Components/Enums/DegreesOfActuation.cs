namespace MOSAIC.Components.Enums;

/// <summary>
/// Degrees of Actuation (DOA) used across the system.
/// </summary>
public enum DegreesOfActuation
{
    CArmAngular,
    CArmOrbital,
    CArmInOut,
    CArmDistalProximal,
    CArmPlatformRotating,

    // --- Blender-arm / hand DOFs ---
    ThumbFlexion,            // 0
    ThumbRotation,           // 1
    Index,                   // 2
    Middle,                  // 3
    Ring,                    // 4
    Little,                  // 5
    WristFlexionExtension,   // 6
    WristUlnarRadial,        // 7
    WristPronationSupination,// 8
    HandOpenClose,           // 9
    ElbowFlexion,            // 10
    ElbowExtension,          // 11
}

public static class DegreesOfActuationExtensions
{
    /// <summary>Human-friendly label for UI.</summary>
    public static string Label(this DegreesOfActuation d) => d switch
    {
        DegreesOfActuation.CArmAngular => "C-arm Angular",
        DegreesOfActuation.CArmOrbital => "C-arm Orbital",
        DegreesOfActuation.CArmInOut => "C-arm In/Out",
        DegreesOfActuation.CArmDistalProximal => "C-arm Distal/Proximal",
        DegreesOfActuation.CArmPlatformRotating => "C-arm Platform Rotating",

        DegreesOfActuation.ThumbFlexion => "Thumb Flexion",
        DegreesOfActuation.ThumbRotation => "Thumb Rotation",
        DegreesOfActuation.Index => "Index",
        DegreesOfActuation.Middle => "Middle",
        DegreesOfActuation.Ring => "Ring",
        DegreesOfActuation.Little => "Little",
        DegreesOfActuation.WristFlexionExtension => "Wrist Flexion/Extension",
        DegreesOfActuation.WristUlnarRadial => "Wrist Ulnar/Radial",
        DegreesOfActuation.WristPronationSupination => "Wrist Supination/Pronation",
        DegreesOfActuation.HandOpenClose => "Hand Open/Close",
        
        DegreesOfActuation.ElbowExtension => "Elbow Extension",
        DegreesOfActuation.ElbowFlexion => "Elbow Flexion",
        _ => d.ToString()
    };
}