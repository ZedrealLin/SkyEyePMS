namespace SkyEyePMS.Core.Models;

/// <summary>操作审计：谁在什么时间做了什么（管理员在"审计日志"页查看）</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>操作人用户名（系统操作为 system）</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>动作，如 printer.create / settings.save / alert.ack</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>作用对象，如打印机名 / 设置键 / 告警 ID</summary>
    public string? Target { get; set; }
    /// <summary>补充信息</summary>
    public string? Detail { get; set; }
}
