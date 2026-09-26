# Per-vendor deltas for the rsqrtss-dependent goldens

The stock goldens were cut on an AMD Ryzen 9 9950X. `rsqrtss` is
implementation-defined, so on another vendor every stock-normalised quantity
moves in the low bits. Each file here is that vendor's delta against the
goldens for one fact: a `count N` line, then `index value` for every line that
differs. See `VendorGolden.cs`.

**`GenuineIntel/` is self-captured.** It was recorded from this port's own
output on an Intel Xeon with `SS_CAPTURE_VENDOR_GOLDENS=1`, not from the
reference tools. It pins the port's Intel behaviour against regressions; it is
not independent evidence of parity with stock on Intel. That evidence is the
AMD run against the reference goldens. Output from the reference tools run on
an Intel machine can replace these files without touching a fact.

To recapture on a host of the same vendor:

    SS_CAPTURE_VENDOR_GOLDENS=1 dotnet test src/SourceSharp.MapTools.slnx -c Release
