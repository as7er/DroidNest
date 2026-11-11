using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DroidNest
{
    // The AVD configuration is regenerated on every start. Structural keys stay owned by the client;
    // a small allowlist of tuning keys is carried over so a user-edited config.ini survives upgrades.
    internal static class AvdConfig
    {
        internal const string DefaultGpuMode = "swiftshader";
        private static readonly string[] GpuModes = { "auto", "host", "swiftshader", "swiftshader_indirect", "angle", "angle_indirect" };

        internal static string Merge(string existing, string imageDirectory)
        {
            Dictionary<string, string> values = Parse(existing);
            int width = ReadInt(values, "hw.lcd.width", 320, 4096, 720);
            int height = ReadInt(values, "hw.lcd.height", 320, 4096, 1280);
            int density = ReadInt(values, "hw.lcd.density", 120, 640, 320);
            int cores = ReadInt(values, "hw.cpu.ncore", 1, 8, 2);
            int memory = ReadInt(values, "hw.ramSize", 512, 8192, 2048);
            int heap = ReadInt(values, "vm.heapSize", 16, 1024, 256);
            string disk = ReadDisk(values, "disk.dataPartition.size");
            string skin = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
            return String.Join("\n", new string[] {
                "AvdId=DroidNest", "avd.ini.displayname=DroidNest", "avd.ini.encoding=UTF-8",
                "abi.type=x86_64", "tag.id=default", "tag.display=Default", "target=android-30",
                "image.sysdir.1=" + imageDirectory.Replace('\\', '/') + "/",
                "hw.cpu.arch=x86_64", "hw.cpu.ncore=" + cores.ToString(CultureInfo.InvariantCulture),
                "hw.ramSize=" + memory.ToString(CultureInfo.InvariantCulture), "vm.heapSize=" + heap.ToString(CultureInfo.InvariantCulture),
                "disk.dataPartition.size=" + disk,
                "hw.lcd.width=" + width.ToString(CultureInfo.InvariantCulture),
                "hw.lcd.height=" + height.ToString(CultureInfo.InvariantCulture),
                "hw.lcd.density=" + density.ToString(CultureInfo.InvariantCulture),
                "hw.keyboard=yes", "hw.mainKeys=no", "hw.dPad=no", "hw.trackBall=no", "hw.touchScreen=multi-touch",
                "hw.gpu.enabled=yes", "hw.gpu.mode=" + ReadGpu(values), "hw.audioInput=yes", "hw.battery=yes",
                "hw.gps=yes", "hw.accelerometer=yes", "hw.sensors.orientation=yes",
                "hw.camera.back=emulated", "hw.camera.front=emulated",
                "hw.sdCard=no", "showDeviceFrame=no", "skin.dynamic=yes", "skin.name=" + skin,
                "fastboot.forceColdBoot=yes", "fastboot.forceFastBoot=no", "fastboot.chosenSnapshotFile=",
                "PlayStore.enabled=false", ""
            });
        }

        // The emulator command line overrides config.ini, so the launch flag must agree with the file.
        internal static string GpuMode(string mergedConfig)
        {
            return ReadGpu(Parse(mergedConfig));
        }

        private static Dictionary<string, string> Parse(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrEmpty(text)) return values;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string key = line.Substring(0, separator).Trim();
                if (key.Length == 0) continue;
                values[key] = line.Substring(separator + 1).Trim();
            }
            return values;
        }

        private static int ReadInt(IDictionary<string, string> values, string key, int minimum, int maximum, int fallback)
        {
            string text;
            int parsed;
            if (!values.TryGetValue(key, out text) || !Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)) return fallback;
            return parsed < minimum || parsed > maximum ? fallback : parsed;
        }

        private static string ReadDisk(IDictionary<string, string> values, string key)
        {
            string text;
            if (!values.TryGetValue(key, out text)) return "4G";
            Match match = Regex.Match(text, @"^([0-9]{1,5})([MG])$", RegexOptions.IgnoreCase);
            long bytes;
            if (match.Success)
            {
                long amount = Int64.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                bytes = match.Groups[2].Value.Equals("G", StringComparison.OrdinalIgnoreCase) ? amount << 30 : amount << 20;
                if (bytes < (512L << 20) || bytes > (64L << 30)) return "4G";
                return amount.ToString(CultureInfo.InvariantCulture) + match.Groups[2].Value.ToUpperInvariant();
            }
            // The emulator rewrites 4G/8G as a byte count; keep that value instead of resetting it.
            if (Regex.IsMatch(text, @"^[0-9]{1,12}$") &&
                Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out bytes) &&
                bytes >= (512L << 20) && bytes <= (64L << 30))
                return text;
            return "4G";
        }

        private static string ReadGpu(IDictionary<string, string> values)
        {
            string text;
            if (!values.TryGetValue("hw.gpu.mode", out text)) return DefaultGpuMode;
            foreach (string mode in GpuModes)
                if (String.Equals(mode, text, StringComparison.OrdinalIgnoreCase)) return mode;
            return DefaultGpuMode;
        }

        internal static void WriteAtomic(string path, string content)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException)
            {
                // Filesystems without replace support still get a complete file, never a partial one.
                File.Copy(temporary, path, true);
                File.Delete(temporary);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temporary, path, true);
                File.Delete(temporary);
            }
        }
    }
}
