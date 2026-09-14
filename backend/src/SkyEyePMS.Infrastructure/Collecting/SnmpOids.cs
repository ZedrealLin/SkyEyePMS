using Lextm.SharpSnmpLib;

namespace SkyEyePMS.Infrastructure.Collecting;

/// <summary>Printer MIB (RFC 3805) + Host Resources MIB (RFC 2790) 采集所需 OID</summary>
public static class SnmpOids
{
    public static readonly ObjectIdentifier SysDescr = new("1.3.6.1.2.1.1.1.0");
    public static readonly ObjectIdentifier SysName = new("1.3.6.1.2.1.1.5.0");

    // Host Resources
    public static readonly ObjectIdentifier HrDeviceStatus = new("1.3.6.1.2.1.25.3.2.1.5");
    public static readonly ObjectIdentifier HrPrinterStatus = new("1.3.6.1.2.1.25.3.5.1.1");
    public static readonly ObjectIdentifier HrPrinterDetectedErrorState = new("1.3.6.1.2.1.25.3.5.1.2");

    // Printer MIB
    public static readonly ObjectIdentifier PrtMarkerLifeCount = new("1.3.6.1.2.1.43.10.2.1.3"); // 列：prtMarkerLifeCount
    /// <summary>设备序列号（prtGeneralTable 第 1 行，资产台账用）</summary>
    public static readonly ObjectIdentifier PrtGeneralSerialNumber = new("1.3.6.1.2.1.43.5.1.1.17.1");

    // prtMarkerSuppliesTable：.1.1 表，列 index(1) type(2) description(3) unit(4) maxCapacity(5) level(6)
    public static readonly ObjectIdentifier PrtMarkerSupplies = new("1.3.6.1.2.1.43.11.1.1");
    public static readonly ObjectIdentifier SuppliesType = new("1.3.6.1.2.1.43.11.1.1.2");
    public static readonly ObjectIdentifier SuppliesDescription = new("1.3.6.1.2.1.43.11.1.1.3");
    public static readonly ObjectIdentifier SuppliesUnit = new("1.3.6.1.2.1.43.11.1.1.4");
    public static readonly ObjectIdentifier SuppliesMaxCapacity = new("1.3.6.1.2.1.43.11.1.1.5");
    public static readonly ObjectIdentifier SuppliesLevel = new("1.3.6.1.2.1.43.11.1.1.6");

    // prtInputTable：列 index(1) type(2) ... mediaMaxCapacity(6) mediaLevel(7) mediaName(8) name(9)
    public static readonly ObjectIdentifier PrtInput = new("1.3.6.1.2.1.43.8.2.1");
    public static readonly ObjectIdentifier InputMediaMaxCapacity = new("1.3.6.1.2.1.43.8.2.1.6");
    public static readonly ObjectIdentifier InputMediaLevel = new("1.3.6.1.2.1.43.8.2.1.7");
    public static readonly ObjectIdentifier InputMediaName = new("1.3.6.1.2.1.43.8.2.1.8");
    public static readonly ObjectIdentifier InputName = new("1.3.6.1.2.1.43.8.2.1.9");

    // prtAlertTable：列 index(1) severityLevel(2) group(3) groupIndex(4) location(5) code(6) description(7)
    public static readonly ObjectIdentifier PrtAlert = new("1.3.6.1.2.1.43.18.1.1");
    public static readonly ObjectIdentifier AlertSeverity = new("1.3.6.1.2.1.43.18.1.1.2");
    public static readonly ObjectIdentifier AlertGroup = new("1.3.6.1.2.1.43.18.1.1.3");
    public static readonly ObjectIdentifier AlertCode = new("1.3.6.1.2.1.43.18.1.1.6");
    public static readonly ObjectIdentifier AlertDescription = new("1.3.6.1.2.1.43.18.1.1.7");
}
