# Per-vendor deltas for the estimate-dependent goldens

The stock goldens were cut on an AMD Ryzen 9 9950X. `rsqrtss` and `rcpss` are
implementation-defined, so on another vendor every stock-normalised quantity
moves in the low bits. On arm64 the estimates are ARM's own instructions
(`FloatEstimate.cs`), which ARM defines exactly, so one `Arm64/` directory
serves every arm64 CPU. Each file here is that vendor's delta against the
goldens for one fact: a `count N` line, then `index value` for every line that
differs. See `VendorGolden.cs`.

**`GenuineIntel/` is self-captured.** It was recorded from this port's own
output on an Intel Xeon with `SS_CAPTURE_VENDOR_GOLDENS=1`, not from the
reference tools. It pins the port's Intel behaviour against regressions; it is
not independent evidence of parity with stock on Intel. That evidence is the
AMD run against the reference goldens. Output from the reference tools run on
an Intel machine can replace these files without touching a fact.

**`Arm64/` is self-captured too**, from this port on a GitHub Apple Silicon
runner, for the same purpose. The stock tools never ran on ARM, so there is no
reference output for it to be compared with.

To recapture in CI, run the CI workflow by hand with **capture** ticked and
commit the files from the `rsqrt-vendor-<os>` artifact. To recapture locally on
a host of the same vendor:

    SS_CAPTURE_VENDOR_GOLDENS=1 dotnet test src/SourceSharp.MapTools.slnx -c Release

**A capture run always fails.** Every fact that wrote a delta fails with
"captured ... review and commit it" and the file's path, so a capture can
never be mistaken for a green run: a fact that passed would only have
compared the port's output with the values it had just written from it. On
the reference vendor (AMD) there is nothing to capture, and those facts pass
as usual. In CI the Intel and Apple Silicon test jobs go red on a capture run;
the `rsqrt-vendor-<os>` artifact is uploaded regardless. Review the deltas
(their size is how far the vendor moves the result), commit them, and rerun
without the variable: that run is the one that checks them.
