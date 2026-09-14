using SkyEyePMS.Core.Reading;
using SkyEyePMS.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace SkyEyePMS.Tests;

public class PrinterErrorStateTests
{
    [Fact]
    public void ParsesBits_AcrossBytes_MsbFirst()
    {
        // bit1 = noPaper（MSB-first：byte0 的 0x40）
        var flags = PrinterErrorState.Parse(new byte[] { 0b0100_0000 });
        Assert.True(flags.HasFlag(PrinterErrorFlag.NoPaper));
        Assert.False(flags.HasFlag(PrinterErrorFlag.LowPaper));

        // bit5 = jammed, bit6 = offline（同字节：0x04 | 0x02）
        flags = PrinterErrorState.Parse(new byte[] { 0b0000_0110 });
        Assert.True(flags.HasFlag(PrinterErrorFlag.Jammed));
        Assert.True(flags.HasFlag(PrinterErrorFlag.Offline));

        // bit8 = inputTrayMissing → 第二字节最高位
        flags = PrinterErrorState.Parse(new byte[] { 0x00, 0b1000_0000 });
        Assert.True(flags.HasFlag(PrinterErrorFlag.InputTrayMissing));
    }

    [Fact]
    public void EmptyOrNull_NoFlags()
    {
        Assert.Equal(PrinterErrorFlag.None, PrinterErrorState.Parse(Array.Empty<byte>()));
        Assert.Equal(PrinterErrorFlag.None, PrinterErrorState.Parse(null!));
    }

    [Fact]
    public void DescribeAll_ReturnsChineseText()
    {
        var texts = PrinterErrorState.DescribeAll(PrinterErrorFlag.NoPaper | PrinterErrorFlag.Jammed);
        Assert.Contains("缺纸", texts);
        Assert.Contains("卡纸", texts);
    }
}

public class SupplyReadingTests
{
    [Fact]
    public void Percent_ComputedAndCapped()
    {
        var s = new SupplyReading(1, 3, "Toner", 7, 10000, 8000);
        Assert.Equal(80, s.Percent);
        var over = new SupplyReading(2, 3, "Toner", 7, 100, 150);
        Assert.Equal(100, over.Percent);
    }

    [Theory]
    [InlineData(-1)] // other
    [InlineData(-2)] // unknown
    [InlineData(-3)] // atLeastOne
    public void Percent_NegativeLevels_Null(long level)
    {
        var s = new SupplyReading(1, 3, "Toner", 7, 10000, level);
        Assert.Null(s.Percent);
    }

    [Theory]
    [InlineData(4, false)]  // wasteToner
    [InlineData(8, false)]  // wasteInk
    [InlineData(14, false)] // wasteWax
    [InlineData(3, true)]   // toner
    [InlineData(6, true)]   // inkCartridge
    [InlineData(null, true)]
    public void IsAlertableSupply_WasteExcluded(int? type, bool alertable)
    {
        var s = new SupplyReading(1, type, "X", 7, 100, 50);
        Assert.Equal(alertable, s.IsAlertableSupply);
    }
}

public class AesSecretProtectorTests
{
    private static AesSecretProtector Create(string key = "unit-test-key") =>
        new(Options.Create(new SecurityOptions { EncryptionKey = key }));

    [Fact]
    public void ProtectUnprotect_Roundtrip()
    {
        var p = Create();
        var cipher = p.Protect("my-community-string");
        Assert.StartsWith("enc1:", cipher);
        Assert.True(p.IsEncrypted(cipher));
        Assert.Equal("my-community-string", p.Unprotect(cipher));
    }

    [Fact]
    public void Protect_IsRandomized_SameInputDifferentCipher()
    {
        var p = Create();
        Assert.NotEqual(p.Protect("abc"), p.Protect("abc"));
    }

    [Fact]
    public void Unprotect_Plaintext_PassesThrough()
    {
        var p = Create();
        Assert.Equal("plain-public", p.Unprotect("plain-public"));
    }

    [Fact]
    public void Unprotect_WrongKey_Throws()
    {
        var cipher = Create("key-a").Protect("secret");
        var ex = Assert.Throws<InvalidOperationException>(() => Create("key-b").Unprotect(cipher));
        Assert.Contains("解密失败", ex.Message);
    }

    [Fact]
    public void ProtectEmpty_ReturnsEmpty()
    {
        var p = Create();
        Assert.Equal("", p.Protect(""));
    }
}
