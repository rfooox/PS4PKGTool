#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PS4PKGTool.Utilities.TrophyMetadata
{
    public sealed class TrophyMetadataParser
    {
        public IReadOnlyList<TrophyInfo> Parse(ReadOnlySpan<byte> xmlBytes, string? expectedNpCommunicationId = null)
        {
            string xml;
            try
            {
                xml = new UTF8Encoding(false, true).GetString(xmlBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("奖杯元数据不是有效的 UTF-8。", ex);
            }

            XDocument document;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 16 * 1024 * 1024
                };
                using var stringReader = new StringReader(xml);
                using XmlReader reader = XmlReader.Create(stringReader, settings);
                document = XDocument.Load(reader, LoadOptions.None);
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException("奖杯元数据 XML 无效。", ex);
            }

            XElement root = document.Root ?? throw new InvalidDataException("奖杯元数据 XML 没有根元素。");
            if (!string.Equals(root.Name.LocalName, "trophyconf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"不支持的奖杯元数据根元素 '{root.Name.LocalName}'。");

            string? documentId = Child(root, "npcommid")?.Value.Trim();
            if (expectedNpCommunicationId != null && !string.Equals(documentId, expectedNpCommunicationId, StringComparison.Ordinal))
                throw new InvalidDataException($"解密后的元数据 NP Communication ID '{documentId}' 与 '{expectedNpCommunicationId}' 不匹配。");

            var groups = root.Elements().Where(e => e.Name.LocalName == "group")
                .Select(e => new { Id = ParseRequiredInt(e, "id"), Name = Child(e, "name")?.Value.Trim() })
                .ToDictionary(g => g.Id, g => string.IsNullOrWhiteSpace(g.Name) ? $"Group {g.Id:000}" : g.Name!);

            var trophies = new List<TrophyInfo>();
            foreach (XElement element in root.Elements().Where(e => e.Name.LocalName == "trophy"))
            {
                int id = ParseRequiredInt(element, "id");
                int? groupId = ParseOptionalInt(element.Attribute("gid")?.Value, allowNegativeOne: false);
                if (groupId == 0) groupId = null;
                int? platinumId = ParseOptionalInt(element.Attribute("pid")?.Value, allowNegativeOne: true);
                string hidden = element.Attribute("hidden")?.Value ?? "no";
                if (!hidden.Equals("yes", StringComparison.OrdinalIgnoreCase) && !hidden.Equals("no", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"奖杯 {id:000} 的隐藏值 '{hidden}' 无效。");

                trophies.Add(new TrophyInfo
                {
                    Id = id,
                    Name = Child(element, "name")?.Value.Trim() ?? string.Empty,
                    Description = Child(element, "detail")?.Value.Trim() ?? string.Empty,
                    Grade = ParseGrade(element.Attribute("ttype")?.Value),
                    IsHidden = hidden.Equals("yes", StringComparison.OrdinalIgnoreCase),
                    GroupId = groupId,
                    GroupName = groupId.HasValue ? groups.GetValueOrDefault(groupId.Value, $"Group {groupId:000}") : "本体游戏",
                    PlatinumId = platinumId
                });
            }

            if (trophies.Count == 0)
                throw new InvalidDataException("奖杯元数据不包含任何奖杯定义。");
            if (trophies.Select(t => t.Id).Distinct().Count() != trophies.Count)
                throw new InvalidDataException("奖杯元数据包含重复的奖杯 ID。");
            return trophies;
        }

        private static XElement? Child(XElement parent, string localName) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

        private static int ParseRequiredInt(XElement element, string attributeName)
        {
            string? value = element.Attribute(attributeName)?.Value;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result < 0)
                throw new InvalidDataException($"元素 {element.Name.LocalName} 的 {attributeName} '{value}' 无效。");
            return result;
        }

        private static int? ParseOptionalInt(string? value, bool allowNegativeOne)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int result))
                throw new InvalidDataException($"奖杯关系 ID '{value}' 无效。");
            if (allowNegativeOne && result == -1) return null;
            if (result < 0) throw new InvalidDataException($"奖杯关系 ID '{value}' 无效。");
            return result;
        }

        private static TrophyGrade ParseGrade(string? value) => value?.ToUpperInvariant() switch
        {
            "B" => TrophyGrade.Bronze,
            "S" => TrophyGrade.Silver,
            "G" => TrophyGrade.Gold,
            "P" => TrophyGrade.Platinum,
            _ => TrophyGrade.Unknown
        };
    }
}
