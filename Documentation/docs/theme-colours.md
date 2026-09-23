# MOSAIC Theme Colours

> **Rule: never use raw hex values in AXAML. Always use a theme resource key.**
>
> Raw values like `Background="#1E1E1E"` or `Foreground="#FFFFFF"` break the light/dark
> theme switch. Use `{DynamicResource ThemeCardBrush}` instead and the correct value is
> resolved automatically at runtime.

---

## How to use colours in AXAML

```xml
<!-- ✅ Correct — follows theme -->
<Border Background="{DynamicResource ThemeCardBrush}"
        BorderBrush="{DynamicResource ThemeBorderBrush}"
        BorderThickness="1">
    <TextBlock Text="Hello"
               Foreground="{DynamicResource ThemeTextPrimaryBrush}"/>
</Border>

<!-- ❌ Wrong — hardcoded, breaks light mode -->
<Border Background="#1E1E1E" BorderBrush="#2A2A2A">
    <TextBlock Text="Hello" Foreground="#FFFFFF"/>
</Border>
```

Use `DynamicResource` (not `StaticResource`) for all `Theme*` keys so the UI updates
immediately when the user switches theme at runtime.

---

## Backgrounds

| Key | Dark | Light | Use |
|---|---|---|---|
| `ThemeBackgroundBrush` | `#0F0F0F` | `#F8FAFC` | App-level window background |
| `ThemeBackgroundAltBrush` | `#1A1A1A` | `#F1F5F9` | Sidebar, panels |
| `ThemeSurfaceBrush` | `#252525` | `#FFFFFF` | Elevated surfaces |
| `ThemeSurfaceHoverBrush` | `#2A2A2A` | `#F8FAFC` | Hover state on surfaces |
| `ThemeCardBrush` | `#1E1E1E` | `#FFFFFF` | Block card background |
| `ThemeCardHeaderBrush` | `#252525` | `#F8FAFC` | Card header strip |
| `ThemeCardGradient` | dark gradient | subtle white | Card background gradient (LinearGradientBrush) |
| `ThemeSectionBrush` | `#0D0D0D` | `#F8FAFC` | Inset section areas |

---

## Text

| Key | Dark | Light | Use |
|---|---|---|---|
| `ThemeTextPrimaryBrush` | `#FFFFFF` | `#0F172A` | Main labels, headings |
| `ThemeTextSecondaryBrush` | `#B0B0B0` | `#475569` | Sub-labels, descriptions |
| `ThemeTextMutedBrush` | `#808080` | `#64748B` | Placeholder text, hints |
| `ThemeTextDimmedBrush` | `#606060` | `#94A3B8` | De-emphasised info |
| `ThemeTextDisabledBrush` | `#404040` | `#CBD5E1` | Disabled controls |

---

## Borders

| Key | Dark | Light | Use |
|---|---|---|---|
| `ThemeBorderBrush` | `#2A2A2A` | `#E2E8F0` | Default card/panel border |
| `ThemeBorderLightBrush` | `#333333` | `#F1F5F9` | Subtle dividers |
| `ThemeBorderFocusBrush` | `#3B82F6` | `#3B82F6` | Input focus ring |

---

## Accent

| Key | Dark | Light | Use |
|---|---|---|---|
| `ThemeAccentBrush` | `#3B82F6` | `#3B82F6` | Buttons, active states, links |
| `ThemeAccentHoverBrush` | `#2563EB` | `#2563EB` | Hover on accent elements |
| `ThemeAccentMutedBrush` | `#1E3A5F` | `#DBEAFE` | Accent-tinted backgrounds |

---

## Inputs

| Key | Dark | Light | Use |
|---|---|---|---|
| `ThemeInputBackgroundBrush` | `#1A1A1A` | `#FFFFFF` | TextBox, ComboBox background |
| `ThemeInputBorderBrush` | `#333333` | `#E2E8F0` | Input border at rest |
| `ThemeInputFocusBrush` | `#3B82F6` | `#3B82F6` | Input border when focused |

---

## Scope / Chart

| Key | Dark | Light | Use |
|---|---|---|---|
| `ThemeScopeBackgroundBrush` | `#0A0C0F` | `#FFFFFF` | Chart area background |
| `ThemeScopeGridBrush` | `#1A1A2E` | `#F1F5F9` | Chart grid lines |
| `ThemeScopeAxisBrush` | `#404040` | `#94A3B8` | Axis lines and labels |

---

## Canvas / Pipeline Graph

| Key | Dark | Light | Use |
|---|---|---|---|
| `ThemeCanvasBrush` | `#0A1628` | `#E2E8F0` | Pipeline canvas background |
| `ThemeCanvasGridBrush` | `#1E3A5F` | `#CBD5E1` | Canvas dot/grid |
| `ThemeCanvasNodeBrush` | `#1A1A1A` | `#FFFFFF` | Node background |
| `ThemeCanvasNodeBorderBrush` | `#3B82F6` | `#3B82F6` | Node border |

---

## Block Category Colours (theme-independent)

These are fixed — they do not change between light and dark mode. Use them to colour
the category indicator strip on a card header, not for backgrounds or text.

| Key | Colour | Category |
|---|---|---|
| `CategoryDevicesBrush` | `#0EA5E9` sky blue | Devices |
| `CategoryFlowControlBrush` | `#14B8A6` teal | Flow Control |
| `CategorySignalProcessingBrush` | `#8B5CF6` purple | Signal Processing |
| `CategoryMachineLearningBrush` | `#6366F1` indigo | Machine Learning |
| `CategoryAnalyticsBrush` | `#EC4899` pink | Analytics |
| `CategoryTestsBrush` | `#E11D74` rose | Tests |
| `StreamingSourcesBrush` | `#F59E0B` amber | Streaming / Sources |

```xml
<!-- Category strip on a card header -->
<Border Height="3"
        Background="{DynamicResource CategoryDevicesBrush}"
        CornerRadius="4 4 0 0"/>
```

---

## Status Colours (theme-independent)

Use these for status indicators, dot icons, and status text. Do not invent new status colours.

| Key | Colour | Meaning |
|---|---|---|
| `StatusNormalBrush` | `#22C55E` green | Running normally |
| `StatusWarningBrush` | `#F59E0B` amber | Lagging / degraded |
| `StatusErrorBrush` | `#EF4444` red | Error / stumbling |
| `StatusIdleBrush` | `#6B7280` grey | Idle / not started |

```xml
<Ellipse Width="8" Height="8"
         Fill="{DynamicResource StatusNormalBrush}"/>
```

---

## Apply the colours to the tutorial card

This is a replacement for the contents of `GainCardView.axaml` in
[Build Your Own Block](new-block-guide.md). Keep that tutorial's ViewModel,
code-behind and selector registration. It uses only properties the basic Gain
example actually provides.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:MOSAIC.ViewModels.SignalProcessing"
             x:Class="MOSAIC.Views.Cards.SignalProcessing.GainCardView"
             x:DataType="vm:GainViewModel">
  <Border Background="{DynamicResource ThemeCardBrush}"
          BorderBrush="{DynamicResource ThemeBorderBrush}"
          BorderThickness="1" CornerRadius="8" Padding="16">
    <StackPanel Spacing="10">
      <Border Height="3"
              Background="{DynamicResource CategorySignalProcessingBrush}"/>
      <TextBlock Text="{Binding Block.Name}"
                 Foreground="{DynamicResource ThemeTextPrimaryBrush}"
                 FontWeight="SemiBold"/>
      <TextBlock Text="Gain multiplier"
                 Foreground="{DynamicResource ThemeTextSecondaryBrush}"/>
      <Slider Minimum="-10" Maximum="10" Value="{Binding Gain, Mode=TwoWay}"/>
      <TextBlock Text="{Binding Gain, StringFormat='{}{0:F2}'}"/>
      <Button Content="Apply" Command="{Binding ApplyCommand}"/>
      <TextBlock Text="{Binding Message}" TextWrapping="Wrap"/>
    </StackPanel>
  </Border>
</UserControl>
```

Switch between light and dark themes to check text, borders and controls.
The basic Gain model has no `Viz` property. Add a plot only after following
[Visualization Panel Integration](visualization-panel.md); adding a binding alone
cannot create the model's visualization.
