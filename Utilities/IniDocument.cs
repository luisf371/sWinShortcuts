using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace sWinShortcuts.Utilities;

public sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _sectionOrder = [];

    public static IniDocument Load(string path)
    {
        var document = new IniDocument();

        if (!File.Exists(path))
        {
            return document;
        }

        string currentSection = string.Empty;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                document.EnsureSection(currentSection);
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (string.IsNullOrEmpty(currentSection))
            {
                currentSection = "Default";
                document.EnsureSection(currentSection);
            }

            document.SetValue(currentSection, key, value);
        }

        return document;
    }

    public string? GetValue(string section, string key)
    {
        if (_sections.TryGetValue(section, out var kvp) && kvp.TryGetValue(key, out var value))
        {
            return value;
        }

        return null;
    }

    public IReadOnlyDictionary<string, string> GetSection(string section)
    {
        if (_sections.TryGetValue(section, out var data))
        {
            return data;
        }

        return new Dictionary<string, string>();
    }

    public void SetValue(string section, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            section = "Default";
        }

        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key cannot be null or empty.", nameof(key));
        }

        EnsureSection(section);

        var bucket = _sections[section];

        if (string.IsNullOrWhiteSpace(value))
        {
            bucket.Remove(key);
        }
        else
        {
            bucket[key] = value;
        }
    }

    public void RemoveSection(string section)
    {
        if (_sections.Remove(section))
        {
            _sectionOrder.RemoveAll(s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Save(string path)
    {
        var builder = new StringBuilder();

        foreach (var section in _sectionOrder)
        {
            if (!string.Equals(section, "Default", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('[').Append(section).AppendLine("]");
            }

            if (!_sections.TryGetValue(section, out var data) || data.Count == 0)
            {
                builder.AppendLine();
                continue;
            }

            foreach (var kvp in data)
            {
                builder.Append(kvp.Key).Append('=').AppendLine(kvp.Value);
            }

            builder.AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, builder.ToString().TrimEnd() + Environment.NewLine);
    }

    private void EnsureSection(string section)
    {
        if (_sections.ContainsKey(section))
        {
            return;
        }

        _sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _sectionOrder.Add(section);
    }
}
