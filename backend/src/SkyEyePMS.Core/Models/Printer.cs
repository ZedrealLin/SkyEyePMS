namespace SkyEyePMS.Core.Models;

public class Printer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 161;
    /// <summary>SNMP Community 密文（AES-GCM），解密后使用</summary>
    public string CommunityCipher { get; set; } = string.Empty;
    /// <summary>1 = v1, 2 = v2c</summary>
    public int SnmpVersion { get; set; } = 2;
    public string? Location { get; set; }
    public string? Department { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>该台打印机的耗材低阈值覆盖（%），null 使用全局阈值</summary>
    public int? SupplyLowPercentOverride { get; set; }

    // ---- 资产台账 ----
    /// <summary>设备型号（手动维护，建议填公开型号名）</summary>
    public string? Model { get; set; }
    /// <summary>序列号（采集成功后从 prtGeneralSerialNumber 自动回填，可手动修正）</summary>
    public string? SerialNumber { get; set; }
    /// <summary>资产编号（手动维护，对接公司资产系统）</summary>
    public string? AssetTag { get; set; }
    /// <summary>投运日期</summary>
    public DateTime? InstalledAt { get; set; }

    /// <summary>连续采集失败次数（成功后清零）</summary>
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastPollAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public bool? LastOnline { get; set; }
    public int? HrStatus { get; set; }
    public DateTime? StatusChangedAt { get; set; }
    public long? PageCount { get; set; }
    public string? SysName { get; set; }
    public string? SysDescr { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>新建/编辑打印机请求（Community 为明文，入库前加密）</summary>
public class PrinterInput
{
    public string Name { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 161;
    public string? Community { get; set; }
    public int SnmpVersion { get; set; } = 2;
    public string? Location { get; set; }
    public string? Department { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>该台打印机的耗材低阈值覆盖（%），null 用全局值</summary>
    public int? LowSupplyPercentOverride { get; set; }
    /// <summary>设备型号</summary>
    public string? Model { get; set; }
    /// <summary>序列号（留空则由采集自动回填）</summary>
    public string? SerialNumber { get; set; }
    /// <summary>资产编号</summary>
    public string? AssetTag { get; set; }
    /// <summary>投运日期（ISO 日期，可空）</summary>
    public DateTime? InstalledAt { get; set; }
}
