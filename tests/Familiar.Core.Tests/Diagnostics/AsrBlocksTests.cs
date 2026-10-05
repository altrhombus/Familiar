using Familiar.Core.Diagnostics;

namespace Familiar.Core.Tests.Diagnostics;

public class AsrBlocksTests
{
    // Shape of a Defender event 1121 from a 25H2 cumulative update build (trimmed).
    private const string Event1121 = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Windows Defender" Guid="{11cd958a-c507-4ef3-b3f2-5fd9dfbd2c78}" />
            <EventID>1121</EventID>
            <TimeCreated SystemTime="2026-10-05T18:50:42.1234567Z" />
          </System>
          <EventData>
            <Data Name="Product Name">Microsoft Defender Antivirus</Data>
            <Data Name="ID">56A863A9-875E-4185-98A7-B882C64B5CE5</Data>
            <Data Name="Detection Time">2026-10-05T18:50:42.120Z</Data>
            <Data Name="Path">C:\FamiliarWork\20261005-133102\mount\Windows\WinSxS\Temp\PendingRenames\22325a6c.amd64_windows-defender-drivers_ksld.sys_f004bf4f</Data>
            <Data Name="Process Name">C:\FamiliarWork\20261005-133102\scratch\84BCCF31\DismHost.exe</Data>
          </EventData>
        </Event>
        """;

    [Fact]
    public void Parse_ReadsRulePathAndProcess()
    {
        var block = AsrBlocks.Parse(Event1121);

        Assert.NotNull(block);
        Assert.Equal("Block abuse of exploited vulnerable signed drivers", block.RuleName);
        Assert.EndsWith("ksld.sys_f004bf4f", block.Path, StringComparison.Ordinal);
        Assert.EndsWith("DismHost.exe", block.ProcessName, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 10, 5, 18, 50, 42, DateTimeKind.Utc).ToLocalTime().Date, block.Time.Date);
    }

    [Fact]
    public void Parse_UnknownRule_FallsBackToGuid()
    {
        var block = AsrBlocks.Parse(Event1121.Replace("56A863A9-875E-4185-98A7-B882C64B5CE5", "11111111-2222-3333-4444-555555555555", StringComparison.Ordinal));

        Assert.Equal("11111111-2222-3333-4444-555555555555", block?.RuleName);
    }

    [Fact]
    public void Parse_EventWithoutRule_IsIgnored()
    {
        Assert.Null(AsrBlocks.Parse("""<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><EventData><Data Name="Path">x</Data></EventData></Event>"""));
    }

    [Fact]
    public void Find_OnThisMachine_ReturnsOnlyMatchingFolder()
    {
        // Reads the real log (or nothing, if Defender is absent); a folder that never existed has no blocks.
        Assert.Empty(AsrBlocks.Find(DateTime.Now.AddDays(-1), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
