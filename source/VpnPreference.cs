using System;
using System.IO;
using System.Web.Script.Serialization;

namespace AstralSplit
{
    internal static class VpnPreference
    {
        internal const string FileName = "vpn-adapter.json";
        internal static string PathIn(string folder) { return Path.Combine(folder, FileName); }

        // The absence of a preference is the original automatic Astral mode.
        internal static string Read(string path)
        {
            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = new StreamReader(input))
                {
                    if (input.Length > 256) throw InvalidPreference();
                    string json = reader.ReadToEnd().Trim();
                    if (json == "null") return null;
                    string id = new JavaScriptSerializer().DeserializeObject(json) as string;
                    if (Guid.TryParseExact(id, "D", out Guid guid) && guid != Guid.Empty) return guid.ToString("D");
                    throw InvalidPreference();
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                throw InvalidPreference();
            }
        }

        static InvalidDataException InvalidPreference()
        {
            return new InvalidDataException("保存的 VPN 网卡设置有误，请在「VPN 网卡」中重新选择。");
        }

        internal static bool Matches(string adapter, string preference)
        {
            return Guid.TryParse(adapter, out Guid id) &&
                String.Equals(id.ToString("D"), preference, StringComparison.OrdinalIgnoreCase);
        }
    }
}
