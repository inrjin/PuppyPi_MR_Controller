using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PuppyPi
{
    // Deliberately accepts only the v2 server's flat, eight-field ACK schema.
    // JsonUtility alone silently defaults missing fields and coerces some types.
    public static class V2AckParser
    {
        private static readonly Regex Field = new Regex(
            "\\G\\s*\"(?<key>[a-z_]+)\"\\s*:\\s*(?<value>\"[a-zA-Z0-9_-]*\"|true|false|-?(?:0|[1-9][0-9]*))\\s*",
            RegexOptions.CultureInvariant);

        public static bool TryParse(string json, out V2Ack ack)
        {
            ack = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 2048) return false;
            json = json.Trim();
            if (json.Length < 2 || json[0] != '{' || json[json.Length - 1] != '}') return false;
            var fields = new Dictionary<string, string>();
            int index = 1;
            while (index < json.Length - 1)
            {
                Match match = Field.Match(json, index);
                if (!match.Success) return false;
                string key = match.Groups["key"].Value;
                if (fields.ContainsKey(key)) return false;
                fields.Add(key, match.Groups["value"].Value);
                index += match.Length;
                if (index == json.Length - 1) break;
                if (json[index++] != ',' || index >= json.Length - 1) return false;
            }
            if (fields.Count != 8 || !fields.TryGetValue("protocol", out string protocol) || protocol != "2" ||
                !fields.TryGetValue("type", out string type) || type != "\"ack\"" ||
                !fields.TryGetValue("sequence", out string seq) || !long.TryParse(seq, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long sequence) ||
                !fields.TryGetValue("client_id", out string id) || !Quoted(id) ||
                !fields.TryGetValue("accepted", out string accepted) || !Boolean(accepted) ||
                !fields.TryGetValue("emergency_stop", out string emergency) || !Boolean(emergency) ||
                !fields.TryGetValue("state", out string state) || !Quoted(state) ||
                !fields.TryGetValue("reason", out string reason) || !Quoted(reason)) return false;
            ack = new V2Ack { protocol = 2, type = "ack", sequence = sequence, client_id = Unquote(id),
                accepted = accepted == "true", emergency_stop = emergency == "true", state = Unquote(state), reason = Unquote(reason) };
            return true;
        }

        private static bool Boolean(string value) => value == "true" || value == "false";
        private static bool Quoted(string value) => value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"';
        private static string Unquote(string value) => value.Substring(1, value.Length - 2);
    }
}
