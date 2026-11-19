using System;
using System.Collections.Generic;
using System.Linq;

namespace PS4PKGTool.Utilities.PkgInspection
{
    internal static class PkgBuildInfoParser
    {
        private static readonly IReadOnlyDictionary<string, string> FriendlyNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["c_date"] = "创建日期",
                ["sdk_ver"] = "PS4 SDK 版本",
                ["st_type"] = "存储类型",
                ["c_time"] = "创建时间"
            };

        public static IReadOnlyList<PkgInspectionField> Parse(string pubToolInfo)
        {
            if (string.IsNullOrWhiteSpace(pubToolInfo))
                return Array.Empty<PkgInspectionField>();

            var fields = new List<PkgInspectionField>();
            foreach (string token in pubToolInfo.Split(',').Reverse())
            {
                int separator = token.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = token.Substring(0, separator).Trim();
                if (key.Length == 0)
                    continue;

                string value = token.Substring(separator + 1).Trim();
                fields.Add(new PkgInspectionField(FriendlyName(key), value));
            }

            return fields;
        }

        private static string FriendlyName(string key) =>
            FriendlyNames.TryGetValue(key, out string friendlyName) ? friendlyName : key;
    }
}
