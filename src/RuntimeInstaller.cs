using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace DroidNest
{
    internal sealed class InstallationPlan
    {
        internal readonly RuntimeInstaller Owner;
        internal readonly IList<RuntimeDownload> Downloads;
        public string Summary { get; private set; }
        public string Licenses { get; private set; }
        public long DownloadBytes { get; private set; }

        internal InstallationPlan(RuntimeInstaller owner, IList<RuntimeDownload> downloads, string summary, string licenses)
        {
            Owner = owner;
            Downloads = new List<RuntimeDownload>(downloads).AsReadOnly();
            Summary = summary;
            Licenses = licenses;
            DownloadBytes = downloads.Sum(d => d.Size);
        }
    }

    internal sealed class RuntimeDownload
    {
        internal readonly string Id, Name, Url, Digest, Algorithm, Prefix, Destination;
        internal readonly long Size;
        internal RuntimeDownload(string id, string name, string url, string digest, string algorithm,
            long size, string prefix, string destination)
        {
            if (size <= 0 || !Regex.IsMatch(digest, algorithm == "SHA256" ? "^[a-fA-F0-9]{64}$" : "^[a-fA-F0-9]{40}$"))
                throw new InvalidDataException("Upstream download metadata has an invalid size or checksum: " + name);
            RuntimeInstaller.RequireHttps(url);
            Id = id; Name = name; Url = url; Digest = digest.ToLowerInvariant(); Algorithm = algorithm;
            Size = size; Prefix = prefix; Destination = destination;
        }
    }

    // The consent window is the only caller of InstallAsync. Planning fetches bounded metadata only.
    // Neither this class nor its child processes ever write to the user's data directory.
    internal sealed class RuntimeInstaller
    {
        private const string Repository = "https://dl.google.com/android/repository/";
        private const string GoogleCdn = "https://redirector.gvt1.com/edgedl/android/repository/";
        private const string WebViewHash = "a98312a1427ca5c029e30009cc4a002e90affe9370e42b7f93a88ee6425c1278";
        private const long WebViewSize = 358908264;
        private const string LfsEndpoint = "https://review.lineageos.org/LineageOS/android_external_chromium-webview_prebuilt_x86_64.git/info/lfs/objects/batch";
        private const string CertificateFingerprint = "a40da80a59d170caa950cf15c18c454d47a39b26989d8b640ecd745ba71bf5dc";
        private const string ImagePath = "sdk/system-images/android-30/default/x86_64";
        private readonly string root, appRoot;
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly string[] RequiredFiles = BuildRequiredFiles();

        internal RuntimeInstaller(string root, string appRoot)
        {
            this.root = Path.GetFullPath(root);
            this.appRoot = Path.GetFullPath(appRoot);
        }

        internal static bool IsInstalled(string root)
        {
            try
            {
                string runtime = Path.Combine(root, "runtime");
                foreach (string relative in RequiredFiles)
                {
                    string path = SafePath(runtime, relative);
                    if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
                }
                string properties = File.ReadAllText(SafePath(runtime, ImagePath + "/source.properties"));
                if (!Regex.IsMatch(properties, @"(?m)^AndroidVersion\.ApiLevel\s*=\s*30\s*$") ||
                    !Regex.IsMatch(properties, @"(?m)^SystemImage\.Abi\s*=\s*x86_64\s*$")) return false;
                string receipt = Path.Combine(runtime, "installation.json");
                // Explicitly imported legacy runtimes predate receipts; still require every runtime dependency.
                if (!File.Exists(receipt)) return true;
                if (new FileInfo(receipt).Length > 8 * 1024 * 1024) return false;
                var document = JsonObject(File.ReadAllText(receipt));
                if (Convert.ToInt32(document["format"], CultureInfo.InvariantCulture) != 1) return false;
                var lengths = (Dictionary<string, object>)document["files"];
                foreach (string relative in RequiredFiles) if (!lengths.ContainsKey(relative)) return false;
                foreach (var item in lengths)
                {
                    string path = SafePath(runtime, item.Key);
                    if (!File.Exists(path) || new FileInfo(path).Length != Convert.ToInt64(item.Value, CultureInfo.InvariantCulture)) return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                    ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
                    return false;
                throw;
            }
        }

        internal Task<InstallationPlan> GetPlanAsync(CancellationToken token)
        {
            return Task.Run(async delegate
            {
                XDocument repository = ParseXml(await MetadataAsync(Repository + "repository2-3.xml", null, token).ConfigureAwait(false));
                XDocument images = ParseXml(await MetadataAsync(Repository + "sys-img/android/sys-img2-3.xml", null, token).ConfigureAwait(false));
                var licenses = new Dictionary<string, string>();
                var downloads = new List<RuntimeDownload>();
                downloads.Add(AndroidPackage(repository, "emulator", "37.2.13", "emulator", "emulator/", "runtime/sdk/emulator", Repository, licenses));
                downloads.Add(AndroidPackage(repository, "platform-tools", "37.0.1", "platform-tools", "platform-tools/", "runtime/sdk/platform-tools", Repository, licenses));
                downloads.Add(AndroidPackage(images, "system-images;android-30;default;x86_64", "11", "image", "x86_64/", "runtime/" + ImagePath, Repository + "sys-img/android/", licenses));
                downloads.Add(AndroidPackage(repository, "build-tools;35.0.0", "35.0.0", "build-tools", null, "tools/build-tools", Repository, licenses));
                const string jdkMetadata = "https://api.adoptium.net/v3/assets/release_name/eclipse/jdk-21.0.13%2B9?architecture=x64&image_type=jdk&jvm_impl=hotspot&os=windows";
                var jdk = JsonObject(await MetadataAsync(jdkMetadata, null, token).ConfigureAwait(false));
                if ((string)jdk["release_name"] != "jdk-21.0.13+9" || (string)jdk["release_type"] != "ga")
                    throw new InvalidDataException("Adoptium did not return the pinned stable JDK21.0.13+9 release.");
                var binary = ((System.Collections.IEnumerable)jdk["binaries"]).Cast<Dictionary<string, object>>().Single(b =>
                    (string)b["architecture"] == "x64" && (string)b["os"] == "windows" && (string)b["image_type"] == "jdk");
                var package = (Dictionary<string, object>)binary["package"];
                string jdkHash = (string)package["checksum"];
                if (jdkHash != "a4dbaa05fca4bd89d84584e58c459bd732efebb1722af80ab13c6516832004f9")
                    throw new InvalidDataException("Pinned Adoptium archive checksum changed. Do not install; update the client from its trusted release source.");
                downloads.Add(new RuntimeDownload("jdk", "Eclipse Temurin JDK 21.0.13+9 (private signing tool)", (string)package["link"],
                    jdkHash, "SHA256", Convert.ToInt64(package["size"], CultureInfo.InvariantCulture), null, "tools/jdk"));
                downloads.Add(new RuntimeDownload("scrcpy", "scrcpy 5.0.1 Windows x64", "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip",
                    "b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5", "SHA256", 11264634, "scrcpy-win64-v5.0.1/", "runtime/scrcpy-win64-v5.0.1"));
                downloads.Add(new RuntimeDownload("webview", "LineageOS Chromium WebView 156.0.8078.47 (commit a71893ba05acc77405e46262d458b47ab2b11e54)",
                    LfsEndpoint, WebViewHash, "SHA256", WebViewSize, null, "tools/webview.apk"));
                const string keys = "https://raw.githubusercontent.com/aosp-mirror/platform_build/android-11.0.0_r48/target/product/security/";
                downloads.Add(new RuntimeDownload("key", "PUBLIC AOSP development test key (android-11.0.0_r48)", keys + "testkey.pk8",
                    "495675d32e89a149d5abe191f4e9c0e218b9068714e9b53a7c91e164a0741a23", "SHA256", 1217, null, "tools/testkey.pk8"));
                downloads.Add(new RuntimeDownload("certificate", "PUBLIC AOSP development certificate (android-11.0.0_r48)", keys + "testkey.x509.pem",
                    "a4384ba815b9499a5ce349b4e33c1755278873fe2eac150a068823f526e6dbde", "SHA256", 1675, null, "tools/testkey.x509.pem"));
                var summary = new StringBuilder();
                summary.AppendLine("运行时将安装到：" + Path.Combine(root, "runtime"));
                summary.AppendLine("手机数据保存在：" + Path.Combine(root, "data") + "（安装/修复不会改动）");
                summary.AppendLine("将下载并校验以下固定版本；无需已有 SDK/JDK，也不修改 PATH 或系统 Java：");
                foreach (RuntimeDownload d in downloads)
                {
                    summary.AppendLine(String.Format(CultureInfo.InvariantCulture, "\r\n{0}\r\n  {1:N2} MiB ({2} bytes)\r\n  {3}\r\n  {4}: {5}", d.Name, d.Size / 1048576.0, d.Size, d.Url, d.Algorithm, d.Digest));
                }
                summary.AppendLine("\r\nGoogle 下载可能通过其 HTTPS redirector.gvt1.com CDN；WebView 通过 LineageOS Git LFS 授权的 HTTPS 对象存储。所有校验通过后才解压或执行。");
                summary.AppendLine("JDK/build-tools 仅在本地准备阶段使用；WebView 使用公开 AOSP 测试密钥重新签名并验证。该开发镜像不适合敏感数据或生产用途，不包含 Google Play。安装不代替您取得许可或再分发授权。");
                summary.AppendLine("预留至少 12 GiB 空间（含缓存、解压和签名）；修复还需保留旧运行时备份。取消后可重试并复用校验通过的缓存。仅点击同意并安装后才下载组件。");
                var terms = new StringBuilder();
                foreach (var license in licenses) terms.AppendLine(license.Key + "\r\n" + license.Value + "\r\n");
                terms.AppendLine("其余组件许可及依赖许可（请打开以下上游链接阅读；解压后保留原始 NOTICE/LICENSE）：");
                terms.AppendLine("Android SDK: https://developer.android.com/studio/terms\r\nAOSP / public development keys: https://source.android.com/docs/setup/about/licenses\r\nhttps://www.apache.org/licenses/LICENSE-2.0");
                terms.AppendLine("scrcpy Apache-2.0: https://github.com/Genymobile/scrcpy/blob/v5.0.1/LICENSE\r\nSDL: https://github.com/libsdl-org/SDL/blob/main/LICENSE.txt\r\nFFmpeg: https://ffmpeg.org/legal.html\r\nlibusb: https://github.com/libusb/libusb/blob/master/COPYING");
                terms.AppendLine("Chromium WebView and third-party code: https://www.chromium.org/chromium-os/licenses/\r\nhttps://github.com/LineageOS/android_external_chromium-webview\r\nhttps://github.com/LineageOS/android_external_chromium-webview_prebuilt_x86_64/tree/a71893ba05acc77405e46262d458b47ab2b11e54");
                terms.AppendLine("Eclipse Temurin / OpenJDK GPLv2 with Classpath Exception: https://openjdk.org/legal/gplv2+ce.html\r\nhttps://adoptium.net/about\r\nBundled third-party notices: https://github.com/adoptium/jdk21u/tree/jdk-21.0.13%2B9/src/java.base/share/legal");
                return new InstallationPlan(this, downloads, summary.ToString(), terms.ToString());
            }, token);
        }

        internal Task InstallAsync(InstallationPlan plan, IProgress<string> progress, CancellationToken token)
        {
            if (plan == null || !Object.ReferenceEquals(plan.Owner, this)) throw new ArgumentException("Use the consented plan produced by this installer.", "plan");
            return Task.Run(async delegate
            {
                Directory.CreateDirectory(root);
                string workspace = SafePath(root, ".installer");
                Directory.CreateDirectory(workspace);
                string cache = SafePath(workspace, "cache");
                Directory.CreateDirectory(cache);
                // File-share exclusion also covers concurrent processes and is released after a crash.
                using (var installLock = new FileStream(SafePath(workspace, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    string stage = SafePath(workspace, "stage-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stage);
                    try
                    {
                        foreach (RuntimeDownload download in plan.Downloads)
                        {
                            token.ThrowIfCancellationRequested();
                            string file = await DownloadAsync(download, cache, progress, token).ConfigureAwait(false);
                            string destination = SafePath(stage, download.Destination);
                            if (download.Id == "webview" || download.Id == "key" || download.Id == "certificate")
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                                await CopyFileAsync(file, destination, token).ConfigureAwait(false);
                            }
                            else
                            {
                                Report(progress, "正在安全解压 " + download.Name);
                                await ExtractAsync(file, destination, download.Prefix, token).ConfigureAwait(false);
                            }
                        }
                        Report(progress, "正在用公开 AOSP 测试密钥本地签名 WebView（无需系统 Java）…");
                        await SignWebViewAsync(stage, token).ConfigureAwait(false);
                        if (!IsInstalled(stage)) throw new InvalidDataException("Prepared runtime is incomplete. No existing runtime or phone data has been replaced.");
                        string runtime = SafePath(stage, "runtime");
                        File.WriteAllText(Path.Combine(runtime, "installation-licenses.txt"), plan.Licenses, new UTF8Encoding(false));
                        File.WriteAllText(Path.Combine(runtime, "installation-plan.txt"), plan.Summary, new UTF8Encoding(false));
                        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        foreach (string file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
                            lengths.Add(file.Substring(runtime.Length + 1).Replace('\\', '/'), new FileInfo(file).Length);
                        File.WriteAllText(Path.Combine(runtime, "installation.json"), new JavaScriptSerializer().Serialize(new {
                            format = 1, installedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), files = lengths
                        }), new UTF8Encoding(false));
                        token.ThrowIfCancellationRequested();
                        string target = SafePath(root, "runtime");
                        string backup = SafePath(root, "runtime.previous-" + Guid.NewGuid().ToString("N"));
                        bool moved = false;
                        // Cancellation stops before this short commit section, never between the two renames.
                        if (Directory.Exists(target)) { Directory.Move(target, backup); moved = true; }
                        try { Directory.Move(runtime, target); }
                        catch { if (moved && !Directory.Exists(target)) Directory.Move(backup, target); throw; }
                        Report(progress, "运行时安装完成；手机数据未改动。" + (moved ? " 旧运行时保留在：" + backup : ""));
                    }
                    finally { RemoveOwnedStage(stage); }
                }
            }, token);
        }

        private static RuntimeDownload AndroidPackage(XDocument document, string packagePath, string revision, string id,
            string prefix, string destination, string baseUrl, IDictionary<string, string> licenses)
        {
            XElement package = document.Descendants().Where(e => e.Name.LocalName == "remotePackage" && (string)e.Attribute("path") == packagePath)
                .SingleOrDefault(e => Revision(e) == revision && Child(e, "channelRef") != null && (string)Child(e, "channelRef").Attribute("ref") == "channel-0");
            if (package == null) throw new InvalidDataException("Official Android metadata no longer provides pinned stable " + packagePath + " " + revision + ". Update DroidNest; no unverified substitute will be installed.");
            XElement archive = Child(package, "archives").Elements().FirstOrDefault(a =>
                (Child(a, "host-os") == null || Child(a, "host-os").Value == "windows") &&
                (Child(a, "host-arch") == null || Child(a, "host-arch").Value == "x64") &&
                (Child(a, "host-bits") == null || Child(a, "host-bits").Value == "64"));
            if (archive == null) throw new InvalidDataException("No Windows x64 archive: " + packagePath);
            XElement complete = Child(archive, "complete");
            XElement checksum = Child(complete, "checksum");
            string algorithm = ((string)checksum.Attribute("type") ?? "sha1").Replace("-", "").ToUpperInvariant();
            if (algorithm != "SHA1" && algorithm != "SHA256") throw new InvalidDataException("Unsupported upstream checksum: " + algorithm);
            string licenseId = (string)Child(package, "uses-license").Attribute("ref");
            XElement license = document.Descendants().Single(e => e.Name.LocalName == "license" && (string)e.Attribute("id") == licenseId);
            licenses[licenseId] = license.Value;
            Uri url = new Uri(new Uri(baseUrl), Child(complete, "url").Value);
            if (url.Host != "dl.google.com" || !url.AbsolutePath.StartsWith("/android/repository/", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected Android archive origin: " + url);
            return new RuntimeDownload(id, packagePath + " " + revision, url.AbsoluteUri, checksum.Value.Trim(), algorithm,
                Int64.Parse(Child(complete, "size").Value, CultureInfo.InvariantCulture), prefix, destination);
        }

        private static XElement Child(XElement element, string name)
        {
            return element.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        }

        private static string Revision(XElement package)
        {
            XElement revision = Child(package, "revision");
            return String.Join(".", revision.Elements().Where(e => e.Name.LocalName != "preview").Select(e => e.Value));
        }

        private static XDocument ParseXml(string text)
        {
            using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                return XDocument.Load(reader);
        }

        private static Dictionary<string, object> JsonObject(string text)
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(text);
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DroidNest-RuntimeInstaller/1.0");
            return client;
        }

        internal static void RequireHttps(string address)
        {
            Uri uri;
            if (!Uri.TryCreate(address, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("Refusing non-HTTPS download/redirect: " + address);
        }

        private static async Task<HttpResponseMessage> OpenAsync(string address, string body, CancellationToken token)
        {
            for (int redirect = 0; redirect < 10; redirect++)
            {
                RequireHttps(address);
                using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, address))
                {
                    if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/vnd.git-lfs+json");
                    HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status <= 399)
                    {
                        Uri location = response.Headers.Location;
                        response.Dispose();
                        if (location == null) throw new IOException("HTTPS redirect has no Location: " + address);
                        address = new Uri(new Uri(address), location).AbsoluteUri;
                        if (status == 303) body = null;
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        string error = "HTTP " + status + " " + response.ReasonPhrase + " from " + new Uri(address).GetLeftPart(UriPartial.Path);
                        response.Dispose();
                        throw new HttpRequestException(error);
                    }
                    return response;
                }
            }
            throw new HttpRequestException("Too many HTTPS redirects: " + address);
        }

        private static async Task<string> MetadataAsync(string address, string body, CancellationToken token)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                string url = GoogleAddress(address, attempt);
                try
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(90));
                        using (HttpResponseMessage response = await OpenAsync(url, body, deadline.Token).ConfigureAwait(false))
                        using (deadline.Token.Register(response.Dispose))
                        using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new MemoryStream())
                        {
                            await CopyBoundedAsync(input, output, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                            return Encoding.UTF8.GetString(output.ToArray());
                        }
                    }
                }
                catch (Exception ex)
                {
                    token.ThrowIfCancellationRequested();
                    if (!(ex is HttpRequestException || ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)) throw;
                    last = ex;
                }
                if (attempt < 2) await Task.Delay(1000 * (attempt + 1), token).ConfigureAwait(false);
            }
            throw NetworkError(address, last);
        }

        private static string GoogleAddress(string address, int attempt)
        {
            return attempt > 0 && address.StartsWith(Repository, StringComparison.Ordinal) ? GoogleCdn + address.Substring(Repository.Length) : address;
        }

        private static IOException NetworkError(string url, Exception cause)
        {
            return new IOException("无法从 " + new Uri(url).GetLeftPart(UriPartial.Path) + " 下载。请检查网络、HTTPS 代理/防火墙、系统时间及证书，然后重试；已验证缓存会保留，不会修改手机数据。\r\n" + cause.Message, cause);
        }

        private static async Task<string> ResolveWebViewAsync(CancellationToken token)
        {
            string body = new JavaScriptSerializer().Serialize(new { operation = "download", transfers = new[] { "basic" }, objects = new[] { new { oid = WebViewHash, size = WebViewSize } } });
            var response = JsonObject(await MetadataAsync(LfsEndpoint, body, token).ConfigureAwait(false));
            var item = ((System.Collections.IEnumerable)response["objects"]).Cast<Dictionary<string, object>>().First();
            if ((string)item["oid"] != WebViewHash || Convert.ToInt64(item["size"], CultureInfo.InvariantCulture) != WebViewSize)
                throw new InvalidDataException("LineageOS LFS returned an unexpected WebView object.");
            if (item.ContainsKey("error")) throw new IOException("LineageOS no longer serves the pinned WebView: " + new JavaScriptSerializer().Serialize(item["error"]));
            var actions = (Dictionary<string, object>)item["actions"];
            var download = (Dictionary<string, object>)actions["download"];
            string url = (string)download["href"];
            RequireHttps(url);
            return url;
        }

        private static async Task<string> DownloadAsync(RuntimeDownload download, string cache, IProgress<string> progress, CancellationToken token)
        {
            string final = SafePath(cache, download.Algorithm + "-" + download.Digest);
            if (File.Exists(final))
            {
                Report(progress, "正在验证缓存 " + download.Name);
                if (new FileInfo(final).Length == download.Size && HashFile(final, download.Algorithm, token) == download.Digest) return final;
                File.Delete(final);
            }
            Exception last = null;
            string partial = final + ".partial";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Report(progress, "正在下载 " + download.Name + "（" + (attempt + 1) + "/3）");
                    string url = download.Id == "webview" ? await ResolveWebViewAsync(token).ConfigureAwait(false) : GoogleAddress(download.Url, attempt);
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(90));
                        using (HttpResponseMessage response = await OpenAsync(url, null, deadline.Token).ConfigureAwait(false))
                        using (deadline.Token.Register(response.Dispose))
                        using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                        using (HashAlgorithm hash = download.Algorithm == "SHA256" ? (HashAlgorithm)SHA256.Create() : SHA1.Create())
                        {
                            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != download.Size)
                                throw new InvalidDataException("Download size differs from authenticated metadata: " + download.Name);
                            byte[] buffer = new byte[131072];
                            long total = 0;
                            var clock = Stopwatch.StartNew();
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) != 0)
                            {
                                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                                total += count;
                                if (total > download.Size) throw new InvalidDataException("Download exceeds expected size: " + download.Name);
                                hash.TransformBlock(buffer, 0, count, buffer, 0);
                                await output.WriteAsync(buffer, 0, count, deadline.Token).ConfigureAwait(false);
                                if (clock.ElapsedMilliseconds >= 750)
                                {
                                    Report(progress, String.Format(CultureInfo.InvariantCulture, "{0}: {1:N1}/{2:N1} MiB ({3:0}%)", download.Name, total / 1048576.0, download.Size / 1048576.0, total * 100.0 / download.Size));
                                    clock.Restart();
                                }
                            }
                            hash.TransformFinalBlock(new byte[0], 0, 0);
                            if (total != download.Size || Hex(hash.Hash) != download.Digest)
                                throw new InvalidDataException("Checksum/size verification failed: " + download.Name + ". The file will not be executed.");
                            output.Flush(true);
                        }
                    }
                    File.Move(partial, final);
                    return final;
                }
                catch (Exception ex)
                {
                    token.ThrowIfCancellationRequested();
                    if (!(ex is IOException || ex is HttpRequestException || ex is OperationCanceledException || ex is ObjectDisposedException)) throw;
                    last = ex;
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
                if (attempt < 2) await Task.Delay(1500 * (attempt + 1), token).ConfigureAwait(false);
            }
            throw NetworkError(download.Url, last);
        }

        private static async Task ExtractAsync(string archiveFile, string destination, string prefix, CancellationToken token)
        {
            Directory.CreateDirectory(destination);
            using (var file = File.OpenRead(archiveFile))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
            {
                if (prefix == null)
                {
                    string first = archive.Entries.Where(e => e.FullName.IndexOf('/') > 0).Select(e => e.FullName.Split('/')[0]).FirstOrDefault();
                    if (String.IsNullOrEmpty(first)) throw new InvalidDataException("Archive does not contain an expected root directory.");
                    prefix = first + "/";
                }
                long expanded = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.FullName == prefix.TrimEnd('/')) continue;
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected ZIP layout: " + entry.FullName);
                    int type = (entry.ExternalAttributes >> 16) & 0xf000;
                    if (type == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("ZIP links are not allowed: " + entry.FullName);
                    string relative = entry.FullName.Substring(prefix.Length);
                    if (relative.Length == 0) continue;
                    bool directory = relative.EndsWith("/", StringComparison.Ordinal);
                    string target = SafePath(destination, directory ? relative.TrimEnd('/') : relative);
                    expanded = checked(expanded + entry.Length);
                    if (expanded > 16L * 1024 * 1024 * 1024) throw new InvalidDataException("ZIP exceeds the 16 GiB extraction safety limit.");
                    if (directory) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        long written = await CopyBoundedAsync(input, output, entry.Length, token).ConfigureAwait(false);
                        if (written != entry.Length) throw new InvalidDataException("Truncated ZIP entry: " + entry.FullName);
                    }
                }
            }
        }

        // Reject Windows ADS, device names, normalization aliases, traversal and existing junctions.
        private static string SafePath(string directory, string relative)
        {
            if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Invalid relative path: " + relative);
            string[] parts = relative.Replace('\\', '/').Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Unsafe filesystem path: " + relative);
            }
            string result = Path.GetFullPath(Path.Combine(directory, Path.Combine(parts)));
            string parent = result;
            while (!String.IsNullOrEmpty(parent))
            {
                if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Runtime installation refuses filesystem links/junctions: " + parent);
                parent = Path.GetDirectoryName(parent);
            }
            return result;
        }

        private async Task SignWebViewAsync(string stage, CancellationToken token)
        {
            string tools = Path.Combine(stage, "tools");
            string java = SafePath(tools, "jdk/bin/java.exe");
            string signer = SafePath(tools, "build-tools/lib/apksigner.jar");
            string helper = SafePath(appRoot, "tools/SignWebView.java");
            if (!File.Exists(helper)) throw new FileNotFoundException("Client ZIP is missing tools/SignWebView.java. Extract the complete DroidNest release ZIP before retrying.", helper);
            string certificate = SafePath(tools, "testkey.x509.pem");
            string pem = File.ReadAllText(certificate);
            byte[] der = Convert.FromBase64String(pem.Replace("-----BEGIN CERTIFICATE-----", "").Replace("-----END CERTIFICATE-----", ""));
            using (var cert = new X509Certificate2(der))
            using (var sha = SHA256.Create())
                if (Hex(sha.ComputeHash(cert.RawData)) != CertificateFingerprint) throw new InvalidDataException("Unexpected public AOSP test certificate.");
            string apk = SafePath(stage, "runtime/webview-local.apk");
            await RunJavaAsync(java, new[] { "-Xmx1200m", "--class-path", signer, helper, SafePath(tools, "webview.apk"), SafePath(tools, "testkey.pk8"), certificate }, apk, token).ConfigureAwait(false);
            string verification = await RunJavaAsync(java, new[] { "-jar", signer, "verify", "--verbose", "--print-certs", apk }, null, token).ConfigureAwait(false);
            if (verification.IndexOf(CertificateFingerprint, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException("Signed WebView verification did not report the pinned AOSP certificate.");
        }

        private static async Task<string> RunJavaAsync(string java, string[] args, string outputFile, CancellationToken token)
        {
            var info = new ProcessStartInfo(java, String.Join(" ", args.Select(Quote))) {
                WorkingDirectory = Path.GetDirectoryName(java), UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (string key in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) info.EnvironmentVariables.Remove(key);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var process = new Process { StartInfo = info })
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(10));
                token.ThrowIfCancellationRequested();
                process.Start();
                using (deadline.Token.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }))
                {
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    string result = "";
                    try
                    {
                        if (outputFile == null) result = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                        else using (var file = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                            await CopyBoundedAsync(process.StandardOutput.BaseStream, file, WebViewSize + 32 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                        while (!process.WaitForExit(100)) deadline.Token.ThrowIfCancellationRequested();
                        string error = await errors.ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (deadline.IsCancellationRequested) throw new TimeoutException("Private Java signing/verification exceeded 10 minutes.");
                        if (process.ExitCode != 0) throw new InvalidOperationException("Local WebView signing/verification failed (Java exit " + process.ExitCode + "): " + error);
                        return result;
                    }
                    finally
                    {
                        if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                    }
                }
            }
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken token)
        {
            byte[] buffer = new byte[131072];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > maximum) throw new InvalidDataException("Input exceeds its declared size limit.");
                await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
            }
            return total;
        }

        private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await CopyBoundedAsync(input, output, input.Length, token).ConfigureAwait(false);
        }

        private static string HashFile(string path, string algorithm, CancellationToken token)
        {
            using (var stream = File.OpenRead(path))
            using (HashAlgorithm hash = algorithm == "SHA256" ? (HashAlgorithm)SHA256.Create() : SHA1.Create())
            {
                byte[] buffer = new byte[131072];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, buffer, 0); }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(hash.Hash);
            }
        }

        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static void Report(IProgress<string> progress, string message) { if (progress != null) progress.Report(message); }

        private static void RemoveOwnedStage(string directory)
        {
            try
            {
                // Only freshly created staging is removed. Old runtimes, cache and all data are retained.
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string[] BuildRequiredFiles()
        {
            var files = new List<string> {
                "sdk/emulator/emulator.exe", "sdk/emulator/qemu/windows-x86_64/qemu-system-x86_64.exe",
                "sdk/platform-tools/adb.exe", "sdk/platform-tools/AdbWinApi.dll", "sdk/platform-tools/AdbWinUsbApi.dll", "webview-local.apk"
            };
            foreach (string file in new[] { "emulator-check.exe", "qemu-img.exe", "mksdcard.exe", "source.properties", "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "lib/hardware-properties.ini", "lib64/libgfxstream_backend.dll", "lib64/libandroid-emu-shared.dll", "lib64/libshadertranslator.dll", "lib64/gles_swiftshader/libEGL.dll", "lib64/gles_swiftshader/libGLESv2.dll", "lib64/vulkan/vulkan-1.dll", "lib64/vulkan/vk_swiftshader.dll", "lib64/qt/lib/Qt6CoreAndroidEmu.dll", "lib64/qt/lib/Qt6GuiAndroidEmu.dll", "lib64/qt/lib/Qt6WidgetsAndroidEmu.dll" })
                files.Add("sdk/emulator/" + file);
            foreach (string file in new[] { "scrcpy.exe", "scrcpy-server", "SDL3.dll", "avcodec-63.dll", "avformat-63.dll", "avutil-61.dll", "swresample-7.dll", "libusb-1.0.dll", "AdbWinApi.dll", "AdbWinUsbApi.dll" })
                files.Add("scrcpy-win64-v5.0.1/" + file);
            foreach (string file in new[] { "system.img", "userdata.img", "ramdisk.img", "kernel-ranchu", "vendor.img", "encryptionkey.img", "source.properties", "advancedFeatures.ini" })
                files.Add(ImagePath + "/" + file);
            return files.ToArray();
        }
    }
}
