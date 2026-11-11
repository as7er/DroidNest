using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DroidNest
{
    // Client preferences live beside the persistent runtime, never inside phone data.
    internal sealed class ClientSettings
    {
        internal bool AutoStartPhone = true;
        private readonly string path;

        private ClientSettings(string path) { this.path = path; }

        internal static ClientSettings Load(string root)
        {
            string path = Path.Combine(root, "settings.json");
            ClientSettings settings = new ClientSettings(path);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return settings;
                Dictionary<string, object> values = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 }
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                object value;
                if (values != null && values.TryGetValue("autoStartPhone", out value) && value is bool)
                    settings.AutoStartPhone = (bool)value;
            }
            catch (Exception ex)
            {
                // A damaged preference file must never block the client; defaults apply.
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                    ex is InvalidOperationException || ex is FormatException)) throw;
            }
            return settings;
        }

        internal void Save()
        {
            string text = new JavaScriptSerializer().Serialize(
                new Dictionary<string, object> { { "autoStartPhone", AutoStartPhone } });
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException)
            {
                File.Copy(temporary, path, true);
                File.Delete(temporary);
            }
        }
    }
}
