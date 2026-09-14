namespace SkyEyePMS.Core.Reading;

/// <summary>hrPrinterDetectedErrorState（RFC 2790）位定义，OCTET STRING，MSB-first</summary>
[Flags]
public enum PrinterErrorFlag
{
    None = 0,
    LowPaper = 1 << 0,
    NoPaper = 1 << 1,
    LowToner = 1 << 2,
    NoToner = 1 << 3,
    DoorOpen = 1 << 4,
    Jammed = 1 << 5,
    Offline = 1 << 6,
    ServiceRequested = 1 << 7,
    InputTrayMissing = 1 << 8,
    OutputTrayMissing = 1 << 9,
    MarkerSupplyMissing = 1 << 10,
    OutputNearFull = 1 << 11,
    OutputFull = 1 << 12,
    InputNearFull = 1 << 13,
    InputFull = 1 << 14
}

public static class PrinterErrorState
{
    private static readonly (int Bit, PrinterErrorFlag Flag, string Text)[] Map =
    {
        (0, PrinterErrorFlag.LowPaper, "纸张不足"),
        (1, PrinterErrorFlag.NoPaper, "缺纸"),
        (2, PrinterErrorFlag.LowToner, "耗材不足"),
        (3, PrinterErrorFlag.NoToner, "耗材耗尽"),
        (4, PrinterErrorFlag.DoorOpen, "舱门未关闭"),
        (5, PrinterErrorFlag.Jammed, "卡纸"),
        (6, PrinterErrorFlag.Offline, "设备离线"),
        (7, PrinterErrorFlag.ServiceRequested, "需要维修"),
        (8, PrinterErrorFlag.InputTrayMissing, "纸盒缺失"),
        (9, PrinterErrorFlag.OutputTrayMissing, "出纸托盘缺失"),
        (10, PrinterErrorFlag.MarkerSupplyMissing, "耗材缺失"),
        (11, PrinterErrorFlag.OutputNearFull, "出纸托盘将满"),
        (12, PrinterErrorFlag.OutputFull, "出纸托盘已满"),
        (13, PrinterErrorFlag.InputNearFull, "纸盒将满"),
        (14, PrinterErrorFlag.InputFull, "纸盒已满")
    };

    /// <summary>解析 OCTET STRING：bit n 位于 byte[n/8] 的最高位起第 (n%8) 位，返回位标志组合</summary>
    public static PrinterErrorFlag Parse(byte[] data)
    {
        var result = PrinterErrorFlag.None;
        if (data == null || data.Length == 0) return result;
        foreach (var (bit, flag, _) in Map)
        {
            int byteIndex = bit / 8;
            if (byteIndex >= data.Length) continue;
            int bitIndex = 7 - (bit % 8);
            if ((data[byteIndex] & (1 << bitIndex)) != 0)
                result |= flag;
        }
        return result;
    }

    public static string Describe(PrinterErrorFlag flag) =>
        Map.FirstOrDefault(m => m.Flag == flag).Text ?? flag.ToString();

    public static IReadOnlyList<string> DescribeAll(PrinterErrorFlag flags) =>
        Map.Where(m => m.Flag != PrinterErrorFlag.None && flags.HasFlag(m.Flag))
           .Select(m => m.Text)
           .ToList();
}
