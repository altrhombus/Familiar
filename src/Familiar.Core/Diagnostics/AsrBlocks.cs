using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace Familiar.Core.Diagnostics;

/// <summary>A Defender attack surface reduction (ASR) rule blocking a file operation.</summary>
public sealed record AsrBlock(DateTime Time, Guid RuleId, string Path, string ProcessName)
{
    public string RuleName => AsrBlocks.RuleNames.TryGetValue(RuleId, out var name) ? name : RuleId.ToString();
}

/// <summary>
/// Finds ASR blocks that hit a build. Servicing writes drivers and system binaries that some ASR rules treat as
/// suspicious, and the resulting DISM errors (often 0x800f0922) never mention Defender.
/// </summary>
public static class AsrBlocks
{
    private const string LogName = "Microsoft-Windows-Windows Defender/Operational";
    private const int BlockedEventId = 1121;

    internal static readonly Dictionary<Guid, string> RuleNames = new()
    {
        [new Guid("56a863a9-875e-4185-98a7-b882c64b5ce5")] = "Block abuse of exploited vulnerable signed drivers",
        [new Guid("c0033c00-d16d-4114-a5a0-dc9b3a7d2ceb")] = "Block use of copied or impersonated system tools",
        [new Guid("01443614-cd74-433a-b99e-2ecdc07bfc25")] = "Block executable files from running unless they meet a prevalence, age, or trusted list criterion",
        [new Guid("c1db55ab-c21a-4637-bb3f-a12568109d35")] = "Use advanced protection against ransomware",
    };

    /// <summary>Blocks since <paramref name="since"/> on files under <paramref name="folder"/>. Empty if the log cannot be read.</summary>
    public static IReadOnlyList<AsrBlock> Find(DateTime since, string folder)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        var query = new EventLogQuery(LogName, PathType.LogName,
            $"*[System[(EventID={BlockedEventId}) and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]");

        var blocks = new List<AsrBlock>();
        try
        {
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (Parse(record.ToXml()) is { } block && block.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        blocks.Add(block);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            // Diagnostics only: a missing or unreadable Defender log must not hide the original failure.
        }

        return blocks;
    }

    internal static AsrBlock? Parse(string eventXml)
    {
        var document = XDocument.Parse(eventXml);
        var data = document.Descendants().Where(e => e.Name.LocalName == "Data")
            .ToDictionary(e => (string?)e.Attribute("Name") ?? string.Empty, e => e.Value, StringComparer.OrdinalIgnoreCase);
        var time = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;

        if (!data.TryGetValue("ID", out var id) || !Guid.TryParse(id, out var ruleId) || !data.TryGetValue("Path", out var path))
        {
            return null;
        }

        return new AsrBlock(
            DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created) ? created.ToLocalTime() : DateTime.MinValue,
            ruleId,
            path,
            data.GetValueOrDefault("Process Name") ?? string.Empty);
    }
}
