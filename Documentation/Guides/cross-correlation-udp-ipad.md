# Four-channel UDP cross-correlation, including iPad

Open `MOSAIC/MOSAIC/Assets/Examples/BlockTestFiles/CrossCorrelationUdp.json`.
This is the user's UDP → CC → Projector → multiply → power pipeline, with normal
JSON whitespace and names `UDPClient_2` / `Function_1` (no Markdown backslashes).
It is supported by the shared desktop/mobile implementation. No SDK/package
downgrade or change to BLE/platform startup is needed.

## Sender and iPad setup

1. Build and install the updated `net10migration` checkout using the Mac/iPad setup.
   Copy/open the example JSON in MOSAIC. The UDP block starts listening on load.
2. Put the iPad and sender on the same reachable Wi-Fi network; avoid guest-network
   client isolation. Keep MOSAIC open in the foreground.
3. Configure the sender to send **directly to the iPad's Wi-Fi IPv4 address, port 4210**.
   Change the destination from the desktop's IP when switching devices. This JSON
   configures only the receiving port; it cannot change the sender's destination.
4. Send binary **unsigned 16-bit little-endian** values in sample-major order:
   `[ch0, ch1, ch2, ch3, ch0, ch1, ch2, ch3, ...]`.
   One four-channel sample is 8 bytes. No text/CSV, timestamps or packet headers.
   Each datagram must contain complete four-channel samples (a multiple of 8 bytes).
5. For this example, send 50 samples per second per channel. `DesiredRate: 50`
   is the rate used for `rate / lag`; it does not configure the sensor or validate
   its actual sampling frequency. If batching samples into datagrams, use the
   per-channel sample rate, not merely the datagram rate.

The iOS app already declares `NSLocalNetworkUsageDescription`. Allow local-network
access if prompted. Direct incoming UDP unicast does not need the multicast
entitlement. Broadcast/multicast is different: iOS requires the
`com.apple.developer.networking.multicast` entitlement and matching provisioning.
This port does **not** add that restricted entitlement. Prefer direct unicast.
See [Apple TN3179](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy).

## What the parameters mean

`["Lags:1:36", 1000, 1, 4, "LowPass:3", "HighPass:70", "MovMedian:5"]`

- Search lags **1 through 36 inclusive**, measured in samples.
- Retain the latest 1000 samples per channel; compute every accepted datagram.
- Parse 4 interleaved channels; correlate **Ch 0 against Ch 1** by default.
  Choose another pair on the CC card. `All (avg)` averages the configured channels.
  Changing a channel clears the old sample buffers and starts a fresh warm-up.
- Normalize, apply a 3-sample moving mean, then a 5-sample moving median, then
  subtract a forward 70-sample mean. The numbers are **window lengths, not Hz**.
  A window of 1 disables that filter.
- Wait for at least **116 samples per channel** before publishing, about 2.32 seconds
  at 50 samples/s. This gives the configured filters and all requested lags overlap.

The latest feature branch parsed `MovMedian` without applying it. This port actually
applies it between the low-pass and high-pass stages. It also fixes unsafe channel
indices, both selectors initially pointing at channel 0, short-buffer allocations,
zero-lag division, and lost settings on save/reload. These are intentional differences
from that branch's bugs, not a byte-for-byte replay of its output.

## Output contract

Named `Lags` mode publishes `[peakLag, sampleRate / peakLag, 0]`.
The third slot is reserved, matching the feature branch. The CC plots still show
the full correlation curve and processed signals. Flat/unusable data yields zeros;
a zero-lag peak yields a zero rate instead of infinity.

`Projector ["1"]` selects the **second element**, so the final block computes
`(1.5 * 50 / peakLag)^2`. At a peak lag of 8, the final result is **87.890625**.
`Projector.DesiredRate: 200` does not resample or change this numerical result.

Lag convention: `sum(x[i] * y[i - lag])`. A delayed second signal peaks at a negative
lag, which `Lags:1:36` excludes. If the result consistently sits at an edge, check
the channel order or use a signed range such as `Lags:-36:36`.

`Lags:36` means `-36..36`; `Lags:0` means the full available range;
`Lags:0:0` searches only zero lag. Optional `Channels:0:1` persists the selected pair
(`-1` means average). All channel/filter/lag settings survive save/reload.

The older numeric format `[MaxLag, BufferLen, ProcessEveryN]` retains its original
envelope/high-pass preprocessing and **full curve output**, so existing pipelines
do not silently change their meaning.

## Verification from the original integration

- All 1,202 automated tests passed, including the saved example loaded through the
  real JSON parser/graph builder and driven end-to-end over UDP loopback.
- Tests cover unsigned values 32768–65535, byte order, malformed datagram recovery,
  repeated packets, UInt16 sending, known delay/rate, filter formulas, channel
  selection, warm-up, serialization, and backward compatibility.
- Desktop Debug and Android `android-arm64` Debug builds passed with SDK 10.0.303.
- iOS `ios-arm64` managed compilation passed after restoring its dependencies.
  This is not a native iOS build, signing check or proof of Wi-Fi reception on an iPad.

No native iPad runtime test has been performed for this port.
