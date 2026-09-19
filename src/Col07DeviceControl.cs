using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Diagnostic helper: find Acer Col07 consumer-control HID and
/// disable/enable it via SetupAPI. Requires elevation on most systems.
/// Does not change B0 quarantine behavior by itself.
/// </summary>
internal static class Col07DeviceControl
{
    // Known Acer HID-over-I2C consumer-control collection.
    private const string InstanceIdNeedle = @"HID\1025174B&COL07";
    private const string HardwareIdNeedle = @"HID\VEN_1025&DEV_174B&COL07";

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint SPDRP_DEVICEDESC_FALLBACK = SPDRP_DEVICEDESC;

    private const uint DIF_PROPERTYCHANGE = 0x00000012;
    private const uint DICS_ENABLE = 0x00000001;
    private const uint DICS_DISABLE = 0x00000002;
    private const uint DICS_FLAG_GLOBAL = 0x00000001;

    private static readonly IntPtr InvalidHandle = new(-1);

    public readonly record struct DeviceMatch(
        string InstanceId,
        string Description,
        string HardwareIds
    );

    public static bool TryFind(out DeviceMatch match, Action<string> log)
    {
        match = default;

        IntPtr infoSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_ALLCLASSES
        );

        if (infoSet == IntPtr.Zero || infoSet == InvalidHandle)
        {
            int error = Marshal.GetLastWin32Error();
            log($"DIAG Col07: SetupDiGetClassDevs failed. Win32 {error}.");
            return false;
        }

        try
        {
            var data = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };

            for (uint index = 0;
                 SetupDiEnumDeviceInfo(infoSet, index, ref data);
                 index++)
            {
                if (!TryGetInstanceId(infoSet, data, out string instanceId))
                    continue;

                TryGetRegistryString(
                    infoSet,
                    data,
                    SPDRP_HARDWAREID,
                    out string hardwareIds
                );

                bool instanceHit = ContainsIgnoreCase(
                    instanceId,
                    InstanceIdNeedle
                );

                bool hardwareHit = ContainsIgnoreCase(
                    hardwareIds,
                    HardwareIdNeedle
                ) || ContainsIgnoreCase(
                    hardwareIds,
                    InstanceIdNeedle
                );

                if (!instanceHit && !hardwareHit)
                    continue;

                TryGetRegistryString(
                    infoSet,
                    data,
                    SPDRP_DEVICEDESC_FALLBACK,
                    out string description
                );

                match = new DeviceMatch(
                    instanceId,
                    string.IsNullOrWhiteSpace(description)
                        ? "(no description)"
                        : description,
                    string.IsNullOrWhiteSpace(hardwareIds)
                        ? "(no hardware ids)"
                        : hardwareIds.Replace('\0', ' ')
                );

                log(
                    $"DIAG Col07: Found device instanceId={match.InstanceId} " +
                    $"desc={match.Description}"
                );

                return true;
            }

            log(
                "DIAG Col07: No matching Acer Col07 consumer-control " +
                "device was found among present PnP devices."
            );

            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    public static bool TrySetEnabled(
        string instanceId,
        bool enable,
        Action<string> log)
    {
        string action = enable ? "ENABLE" : "DISABLE";

        IntPtr infoSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_ALLCLASSES
        );

        if (infoSet == IntPtr.Zero || infoSet == InvalidHandle)
        {
            int error = Marshal.GetLastWin32Error();
            log(
                $"DIAG Col07: SetupDiGetClassDevs failed during {action}. " +
                $"Win32 {error}."
            );
            return false;
        }

        try
        {
            if (!TryLocateByInstanceId(
                    infoSet,
                    instanceId,
                    out SP_DEVINFO_DATA data))
            {
                // After disable, the device may drop out of DIGCF_PRESENT.
                SetupDiDestroyDeviceInfoList(infoSet);
                infoSet = IntPtr.Zero;

                infoSet = SetupDiGetClassDevs(
                    IntPtr.Zero,
                    null,
                    IntPtr.Zero,
                    DIGCF_ALLCLASSES
                );

                if (infoSet == IntPtr.Zero || infoSet == InvalidHandle)
                {
                    int error = Marshal.GetLastWin32Error();
                    infoSet = IntPtr.Zero;
                    log(
                        $"DIAG Col07: SetupDiGetClassDevs(ALL) failed " +
                        $"during {action}. Win32 {error}."
                    );
                    return false;
                }

                if (!TryLocateByInstanceId(
                        infoSet,
                        instanceId,
                        out data))
                {
                    log(
                        $"DIAG Col07: Could not re-locate {instanceId} " +
                        $"for {action}."
                    );
                    return false;
                }
            }

            var header = new SP_CLASSINSTALL_HEADER
            {
                cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                InstallFunction = DIF_PROPERTYCHANGE
            };

            var propChange = new SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = header,
                StateChange = enable ? DICS_ENABLE : DICS_DISABLE,
                Scope = DICS_FLAG_GLOBAL,
                HwProfile = 0
            };

            if (!SetupDiSetClassInstallParams(
                    infoSet,
                    ref data,
                    ref propChange,
                    (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
            {
                int error = Marshal.GetLastWin32Error();
                log(
                    $"DIAG Col07: SetupDiSetClassInstallParams({action}) " +
                    $"failed. Win32 {error}."
                );
                return false;
            }

            if (!SetupDiCallClassInstaller(
                    DIF_PROPERTYCHANGE,
                    infoSet,
                    ref data))
            {
                int error = Marshal.GetLastWin32Error();
                log(
                    $"DIAG Col07: SetupDiCallClassInstaller({action}) " +
                    $"failed. Win32 {error}." +
                    (error == 5
                        ? " Access denied — restart AcerInputFix elevated " +
                          "to cycle Col07."
                        : "")
                );
                return false;
            }

            log($"DIAG Col07: {action} succeeded for {instanceId}.");
            return true;
        }
        finally
        {
            if (infoSet != IntPtr.Zero && infoSet != InvalidHandle)
                SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    /// <summary>
    /// Disable Col07, wait, re-enable. Returns true only if both steps work.
    /// </summary>
    public static bool TryCycle(
        Action<string> log,
        int settleMs = 750)
    {
        if (!TryFind(out DeviceMatch match, log))
            return false;

        log(
            $"DIAG Col07: Beginning disable/enable cycle " +
            $"(settle={settleMs} ms). Volume/media/brightness keys " +
            "will be unavailable while disabled."
        );

        if (!TrySetEnabled(match.InstanceId, enable: false, log))
            return false;

        Thread.Sleep(settleMs);

        if (!TrySetEnabled(match.InstanceId, enable: true, log))
        {
            log(
                "DIAG Col07: ERROR — device left DISABLED after failed " +
                "re-enable. Use Device Manager to enable " +
                "'HID-compliant consumer control device' (Col07) manually."
            );
            return false;
        }

        log("DIAG Col07: Cycle complete; Col07 should be present again.");
        return true;
    }

    private static bool TryLocateByInstanceId(
        IntPtr infoSet,
        string instanceId,
        out SP_DEVINFO_DATA data)
    {
        data = new SP_DEVINFO_DATA
        {
            cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
        };

        for (uint index = 0;
             SetupDiEnumDeviceInfo(infoSet, index, ref data);
             index++)
        {
            if (!TryGetInstanceId(infoSet, data, out string current))
                continue;

            if (string.Equals(
                    current,
                    instanceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetInstanceId(
        IntPtr infoSet,
        SP_DEVINFO_DATA data,
        out string instanceId)
    {
        instanceId = "";

        uint required = 0;
        SetupDiGetDeviceInstanceId(
            infoSet,
            ref data,
            null,
            0,
            ref required
        );

        if (required == 0)
            return false;

        var buffer = new StringBuilder((int)required);

        if (!SetupDiGetDeviceInstanceId(
                infoSet,
                ref data,
                buffer,
                required,
                ref required))
        {
            return false;
        }

        instanceId = buffer.ToString();
        return !string.IsNullOrEmpty(instanceId);
    }

    private static bool TryGetRegistryString(
        IntPtr infoSet,
        SP_DEVINFO_DATA data,
        uint property,
        out string value)
    {
        value = "";

        uint required = 0;
        uint regType = 0;

        SetupDiGetDeviceRegistryProperty(
            infoSet,
            ref data,
            property,
            ref regType,
            null,
            0,
            ref required
        );

        if (required == 0)
            return false;

        byte[] buffer = new byte[required];

        if (!SetupDiGetDeviceRegistryProperty(
                infoSet,
                ref data,
                property,
                ref regType,
                buffer,
                required,
                ref required))
        {
            return false;
        }

        // REG_SZ or REG_MULTI_SZ as Unicode.
        value = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return true;
    }

    private static bool ContainsIgnoreCase(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr ClassGuid,
        string? Enumerator,
        IntPtr hwndParent,
        uint Flags
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet,
        uint MemberIndex,
        ref SP_DEVINFO_DATA DeviceInfoData
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(
        IntPtr DeviceInfoSet
    );

    [DllImport(
        "setupapi.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        StringBuilder? DeviceInstanceId,
        uint DeviceInstanceIdSize,
        ref uint RequiredSize
    );

    [DllImport(
        "setupapi.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property,
        ref uint PropertyRegDataType,
        byte[]? PropertyBuffer,
        uint PropertyBufferSize,
        ref uint RequiredSize
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_PROPCHANGE_PARAMS ClassInstallParams,
        uint ClassInstallParamsSize
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction,
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData
    );
}
