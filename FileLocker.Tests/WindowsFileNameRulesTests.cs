namespace FileLocker.Tests;

public sealed class WindowsFileNameRulesTests
{
    [Theory]
    [InlineData("CON", true)]
    [InlineData("con.txt", true)]
    [InlineData("COM1.exe", true)]
    [InlineData("LPT9", true)]
    [InlineData("COM\u00B9.txt", true)]
    [InlineData("LPT\u00B2.log", true)]
    [InlineData("COM0.exe", false)]
    [InlineData("LPT10.exe", false)]
    [InlineData("console.exe", false)]
    [InlineData("FileLocker-Setup.exe", false)]
    public void IsReservedDeviceName_MatchesWindowsReservedDeviceNames(string fileName, bool expected)
    {
        Assert.Equal(expected, WindowsFileNameRules.IsReservedDeviceName(fileName));
    }
}
