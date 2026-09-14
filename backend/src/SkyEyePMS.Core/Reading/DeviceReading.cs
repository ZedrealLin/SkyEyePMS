namespace SkyEyePMS.Core.Reading;

/// <summary>hrPrinterStatus（RFC 2790）</summary>
public static class HrPrinterStatusValues
{
    public const int Other = 1;
    public const int Unknown = 2;
    public const int Idle = 3;
    public const int Printing = 4;
    public const int Warmup = 5;

    public static string Describe(int? v) => v switch
    {
        Other => "其他",
        Unknown => "未知",
        Idle => "空闲",
        Printing => "打印中",
        Warmup => "预热中",
        _ => "未知"
    };
}

/// <summary>一次成功 SNMP 采集得到的设备快照（协议无关，便于测试）</summary>
public class DeviceReading
{
    public bool Online { get; set; }
    public int? HrPrinterStatus { get; set; }
    public int? HrDeviceStatus { get; set; }
    /// <summary>hrPrinterDetectedErrorState 原始位串</summary>
    public byte[]? DetectedErrorState { get; set; }
    public long? PageCount { get; set; }
    public string? SysName { get; set; }
    public string? SysDescr { get; set; }
    /// <summary>设备序列号（prtGeneralSerialNumber，可选采集）</summary>
    public string? SerialNumber { get; set; }
    public List<SupplyReading> Supplies { get; set; } = new();
    public List<InputTrayReading> InputTrays { get; set; } = new();
    public List<DeviceAlertReading> AlertTableEntries { get; set; } = new();

    public PrinterErrorFlag ErrorFlags =>
        DetectedErrorState == null ? PrinterErrorFlag.None : PrinterErrorState.Parse(DetectedErrorState);
}

public record SupplyReading(
    int Index,
    int? Type,
    string? Description,
    int? Unit,
    long? MaxCapacity,
    long? Level)
{
    /// <summary>百分比（0-100）。level/max 可计算时才有值；-3(少量)/-2(未知)/-1(其他) 返回 null</summary>
    public double? Percent =>
        MaxCapacity is > 0 && Level is >= 0
            ? Math.Round(Math.Min(100.0, 100.0 * Level.Value / MaxCapacity.Value), 1)
            : null;

    /// <summary>是否为可告警的非废料耗材（跳过 wasteToner/wasteInk/wasteWax）</summary>
    public bool IsAlertableSupply => Type is not (4 or 8 or 14);
}

public record InputTrayReading(int Index, string? Name, long? MaxCapacity, long? Level)
{
    /// <summary>prtInputMediaLevel = 0 表示纸盒已空</summary>
    public bool IsEmpty => Level == 0;
}

public record DeviceAlertReading(int Index, int Severity, int Group, int Code, string? Description)
{
    public bool IsCritical => Severity == 3;
    public bool MentionsJam() => Description != null &&
        (Description.Contains("jam", StringComparison.OrdinalIgnoreCase) ||
         Description.Contains("卡纸", StringComparison.Ordinal));
}
