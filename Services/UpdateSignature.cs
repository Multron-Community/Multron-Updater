using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace MultronUpdater.Services
{
    public static class UpdateSignature
    {
        public const string ManifestName = "update.json";
        public const string SignatureName = "update.json.sig";
        public const string ManifestApp = "multron-updater";
        private static readonly string[] TrustedHosts = { "github.com", "api.github.com" };

        public static bool IsTrustedUrl(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && TrustedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

        public static Version Normalize(Version version) =>
            new(version.Major, version.Minor, Math.Max(version.Build, 0));

        private static InvalidOperationException NotVerified() =>
            new("The update signature could not be verified, so the update was not installed.");

        public static IReadOnlyDictionary<string, string> VerifyManifest(byte[] manifest, string signature, string publicKey, Version version, Version current)
        {
            if (string.IsNullOrWhiteSpace(publicKey))
                throw NotVerified();
            byte[] signatureBytes;
            try
            {
                signatureBytes = Convert.FromBase64String(signature.Trim());
            }
            catch (FormatException)
            {
                throw NotVerified();
            }

            using (var key = ECDsa.Create())
            {
                try
                {
                    key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
                }
                catch (Exception ex) when (ex is FormatException || ex is CryptographicException)
                {
                    throw NotVerified();
                }
                if (!key.VerifyData(manifest, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    throw NotVerified();
            }

            string? name;
            string? signedText;
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var doc = JsonDocument.Parse(manifest);
                var root = doc.RootElement;
                name = root.GetProperty("name").GetString();
                signedText = root.GetProperty("version").GetString();
                foreach (var file in root.GetProperty("files").EnumerateObject())
                {
                    var hash = file.Value.ValueKind == JsonValueKind.String ? file.Value.GetString() : null;
                    if (hash != null && hash.Length == 64 && hash.All(Uri.IsHexDigit))
                        hashes[file.Name] = hash.ToLowerInvariant();
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException)
            {
                throw NotVerified();
            }
            if (name != ManifestApp || !Version.TryParse(signedText, out var signedVersion))
                throw NotVerified();

            var signed = Normalize(signedVersion);
            if (signed != Normalize(version))
                throw new InvalidOperationException($"The signed update is for version {signed}, not {Normalize(version)}, so it was not installed.");
            if (signed <= Normalize(current))
                throw new InvalidOperationException($"Version {signed} is not newer than the installed version {Normalize(current)}, so it was not installed.");
            return hashes;
        }

        public static string ExpectedHash(IReadOnlyDictionary<string, string> hashes, string fileName) =>
            hashes.TryGetValue(fileName, out var hash) ? hash : throw NotVerified();

        public static string Sha256File(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        public static void VerifyFile(string path, string expectedHash)
        {
            if (!string.Equals(Sha256File(path), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded update does not match its signed checksum and was not installed.");
        }
    }
}
