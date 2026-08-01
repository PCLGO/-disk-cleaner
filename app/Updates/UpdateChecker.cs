using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiskCleanupAssistant.Updates
{
    [DataContract]
    public sealed class UpdateManifest
    {
        [DataMember] public string Version { get; set; }
        [DataMember] public string PublishedUtc { get; set; }
        [DataMember] public int MinimumOsBuild { get; set; }
        [DataMember] public string DownloadUrl { get; set; }
        [DataMember] public string Sha256 { get; set; }
        [DataMember] public string ReleaseNotesUrl { get; set; }
        [DataMember] public string Signature { get; set; }
    }

    public sealed class UpdateChecker
    {
        public const string ManifestUrl = "https://github.com/PCLGO/-disk-cleaner/releases/latest/download/latest.json";
        private static readonly string PublicKeyXml = LoadPublicKey();
        public bool IsConfigured { get { return !string.IsNullOrWhiteSpace(PublicKeyXml) && !PublicKeyXml.StartsWith("REPLACE_", StringComparison.Ordinal); } }

        public async Task<UpdateManifest> CheckAsync(CancellationToken token)
        {
            if (!IsConfigured) return null;
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DiskCleanupAssistant/0.1");
                var json = await client.GetStringAsync(ManifestUrl).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var manifest = Parse(json);
                if (!Verify(manifest)) throw new CryptographicException("更新清单签名无效");
                var current = new Version(Assembly.GetExecutingAssembly().GetName().Version.ToString());
                Version remote;
                return Version.TryParse(manifest.Version, out remote) && remote > current ? manifest : null;
            }
        }

        private static UpdateManifest Parse(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (UpdateManifest)new DataContractJsonSerializer(typeof(UpdateManifest)).ReadObject(stream);
        }

        private static bool Verify(UpdateManifest manifest)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Signature)) return false;
            var canonical = string.Join("\n", manifest.Version, manifest.PublishedUtc, manifest.MinimumOsBuild,
                manifest.DownloadUrl, manifest.Sha256, manifest.ReleaseNotesUrl);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(PublicKeyXml);
                return rsa.VerifyData(Encoding.UTF8.GetBytes(canonical), CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(manifest.Signature));
            }
        }

        private static string LoadPublicKey()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DiskCleanupAssistant.Updates.release-public-key.xml"))
            {
                if (stream == null) return string.Empty;
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd().Trim();
            }
        }
    }
}
