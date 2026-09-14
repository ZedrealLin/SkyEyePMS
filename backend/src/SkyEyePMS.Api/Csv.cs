using System.Globalization;
using System.Text;

namespace SkyEyePMS.Api;

/// <summary>CSV 导出（UTF-8 BOM，Excel 直接打开中文不乱码）</summary>
public static class Csv
{
    public static IResult Result(HttpContext ctx, string filename,
        IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", row.Select(Escape)));

        ctx.Response.Headers.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
    }

    private static string Escape(object? value)
    {
        var s = value switch
        {
            null => string.Empty,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
        return s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;
    }
}
