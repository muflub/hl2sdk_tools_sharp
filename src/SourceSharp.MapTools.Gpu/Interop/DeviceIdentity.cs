//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// What a Vulkan device and its driver say about themselves, kept for the
/// self-test report so a rejection read on someone else's machine names the
/// exact driver build that gave the answer.
/// </summary>
/// <param name="DeviceName">The device's name.</param>
/// <param name="VendorId">The PCI vendor id (0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel, 0x10005 Mesa).</param>
/// <param name="DeviceId">The vendor's device id.</param>
/// <param name="DriverId">The driver id's enum name, e.g. <c>NvidiaProprietary</c>.</param>
/// <param name="DriverName">The driver's own name string.</param>
/// <param name="DriverInfo">The driver's info string, usually its version.</param>
/// <param name="DriverVersion">The raw <c>driverVersion</c>, whose packing is the vendor's choice.</param>
/// <param name="ApiVersion">The raw Vulkan API version the device supports.</param>
/// <param name="Conformance">The conformance-test version the driver claims, dotted.</param>
/// <remarks>
/// Only identity is kept: no device UUID, LUID or serial, nothing that
/// tells one machine from another of the same model. The report travels
/// in logs and bug reports, and a driver build is all it needs.
/// </remarks>
internal readonly record struct DeviceIdentity(
    string DeviceName,
    uint VendorId,
    uint DeviceId,
    string DriverId,
    string DriverName,
    string DriverInfo,
    uint DriverVersion,
    uint ApiVersion,
    string Conformance)
{
    /// <summary>NVIDIA's PCI vendor id.</summary>
    internal const uint NvidiaVendor = 0x10DE;

    /// <summary>What a device that was never selected reports.</summary>
    public static DeviceIdentity Unknown { get; } = new("?", 0, 0, "?", "?", "?", 0, 0, "?");

    /// <summary>
    /// Decodes <c>driverVersion</c>. Vulkan leaves its packing to the
    /// vendor: NVIDIA packs 10.8.8.6 bits, Intel's Windows driver 18.14,
    /// and everyone else (Mesa included) the API version's 10.10.12 layout.
    /// </summary>
    /// <param name="vendorId">The PCI vendor id.</param>
    /// <param name="driverId">The driver id's enum name.</param>
    /// <param name="version">The raw version.</param>
    /// <returns>The dotted version.</returns>
    public static string FormatDriverVersion(uint vendorId, string driverId, uint version)
    {
        if (vendorId == NvidiaVendor)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{version >> 22}.{(version >> 14) & 0xFF}.{(version >> 6) & 0xFF}.{version & 0x3F}");
        }

        if (driverId == "IntelProprietaryWindows")
        {
            return string.Create(CultureInfo.InvariantCulture, $"{version >> 14}.{version & 0x3FFF}");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}");
    }

    /// <summary>Decodes a Vulkan API version (variant bits dropped).</summary>
    /// <param name="version">The raw version.</param>
    /// <returns>major.minor.patch.</returns>
    public static string FormatApiVersion(uint version) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(version >> 22) & 0x7F}.{(version >> 12) & 0x3FF}.{version & 0xFFF}");

    /// <summary>One line naming the device and the driver build, for the report.</summary>
    /// <returns>The line.</returns>
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"device '{DeviceName}' (vendor 0x{VendorId:X4}, device 0x{DeviceId:X4}); driver {DriverId} "
            + $"'{DriverName}' '{DriverInfo}', driver version {FormatDriverVersion(VendorId, DriverId, DriverVersion)}; "
            + $"Vulkan {FormatApiVersion(ApiVersion)}; conformance {Conformance}");
}
