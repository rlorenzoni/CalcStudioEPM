using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace CalcStudioEPM.Config
{
    /// <summary>
    /// Export / import of connections as a JSON file, e.g. to share them with a colleague or
    /// move them to another PC.
    ///
    /// Saved passwords are DPAPI-protected (only this Windows user can read them), so they
    /// can't be copied as they are. They are exported only when the user gives an encryption
    /// key: each one is encrypted with AES-256-CBC and authenticated with HMAC-SHA256, both
    /// keys derived from the user's key with PBKDF2-SHA256. The same key is needed to import
    /// them; without it the connections import without passwords.
    /// </summary>
    public static class ConnectionTransfer
    {
        public const string FormatName = "CalcStudio.Connections";
        public const int    FormatVersion = 1;
        private const int   Iterations = 200_000;
        private static readonly byte[] KeyCheckLabel = Encoding.UTF8.GetBytes("CalcStudio key check");

        public sealed class ExportFile
        {
            [JsonProperty("format")]     public string Format { get; set; }
            [JsonProperty("version")]    public int Version { get; set; }
            [JsonProperty("exportedAt")] public DateTime ExportedAt { get; set; }
            /// <summary>Null when the file has no passwords.</summary>
            [JsonProperty("passwordProtection", NullValueHandling = NullValueHandling.Ignore)]
            public Protection PasswordProtection { get; set; }
            [JsonProperty("connections")] public List<ExportedConnection> Connections { get; set; } = new List<ExportedConnection>();

            [JsonIgnore] public bool HasPasswords =>
                PasswordProtection != null && Connections.Any(c => !string.IsNullOrEmpty(c.Password));
        }

        public sealed class Protection
        {
            [JsonProperty("kdf")]        public string Kdf { get; set; } = "PBKDF2-SHA256";
            [JsonProperty("iterations")] public int Iterations { get; set; }
            [JsonProperty("salt")]       public string Salt { get; set; }
            [JsonProperty("cipher")]     public string Cipher { get; set; } = "AES-256-CBC+HMAC-SHA256";
            /// <summary>HMAC of a fixed label: tells a wrong key apart before anything is imported.</summary>
            [JsonProperty("keyCheck")]   public string KeyCheck { get; set; }
        }

        /// <summary>A connection as exported: <see cref="ConnectionConfig"/> without the DPAPI
        /// password, plus the password encrypted with the export key (or null).</summary>
        public sealed class ExportedConnection
        {
            public string Name { get; set; }
            public string ServiceBaseUrl { get; set; }
            public string Application { get; set; }
            public string Username { get; set; }
            public bool SavePassword { get; set; }
            public string LocalFolderPath { get; set; }
            public DeployMethod DefaultDeployMethod { get; set; }
            public bool PullDimensionMembers { get; set; }
            public bool ReadOnly { get; set; }
            public bool PullIpm { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Password { get; set; }
        }

        // ------------------------------------------------------------------ //
        //  Export
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Writes <paramref name="connections"/> to <paramref name="path"/>. With a non-empty
        /// <paramref name="key"/> their saved passwords are included, encrypted with it.
        /// Returns how many passwords were exported.
        /// </summary>
        public static int Export(string path, IEnumerable<ConnectionConfig> connections, string key)
        {
            var file = new ExportFile { Format = FormatName, Version = FormatVersion, ExportedAt = DateTime.UtcNow };
            Keys keys = null;
            if (!string.IsNullOrEmpty(key))
            {
                byte[] salt = Random(16);
                keys = Derive(key, salt, Iterations);
                file.PasswordProtection = new Protection
                {
                    Iterations = Iterations,
                    Salt       = Convert.ToBase64String(salt),
                    KeyCheck   = Convert.ToBase64String(Hmac(keys.Mac, KeyCheckLabel)),
                };
            }

            int passwords = 0;
            foreach (ConnectionConfig c in connections)
            {
                string password = null;
                if (keys != null && c.SavePassword && !string.IsNullOrEmpty(c.EncryptedPassword))
                {
                    try { password = Encrypt(keys, ConfigManager.DecryptPassword(c.EncryptedPassword)); passwords++; }
                    catch { /* saved by another Windows user: export without it */ }
                }
                file.Connections.Add(new ExportedConnection
                {
                    Name = c.Name, ServiceBaseUrl = c.ServiceBaseUrl, Application = c.Application,
                    Username = c.Username, SavePassword = c.SavePassword, LocalFolderPath = c.LocalFolderPath,
                    DefaultDeployMethod = c.DefaultDeployMethod, PullDimensionMembers = c.PullDimensionMembers,
                    ReadOnly = c.ReadOnly, PullIpm = c.PullIpm, Password = password,
                });
            }
            if (passwords == 0) file.PasswordProtection = null;

            File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented), new UTF8Encoding(false));
            return passwords;
        }

        // ------------------------------------------------------------------ //
        //  Import
        // ------------------------------------------------------------------ //

        /// <summary>Reads an export file; throws <see cref="InvalidDataException"/> when it isn't one.</summary>
        public static ExportFile Read(string path)
        {
            ExportFile file;
            try { file = JsonConvert.DeserializeObject<ExportFile>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex) { throw new InvalidDataException("The file is not a CalcStudio connections export: " + ex.Message, ex); }
            if (file == null || file.Format != FormatName || file.Connections == null)
                throw new InvalidDataException("The file is not a CalcStudio connections export.");
            if (file.Version > FormatVersion)
                throw new InvalidDataException($"The file was exported by a newer CalcStudio (format {file.Version}). Update the plugin to import it.");

            // The password is sent to the service URL, so a file can't bring in a plain-http one
            foreach (ExportedConnection c in file.Connections)
                if (c == null || !Uri.TryCreate(c.ServiceBaseUrl?.Trim(), UriKind.Absolute, out Uri u) || u.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException(
                        $"The connection '{c?.Name}' in the file has no https:// service URL ('{c?.ServiceBaseUrl}'), so it can't be imported.");
            // A huge iteration count would hang Notepad++ while deriving the key
            if (file.PasswordProtection != null &&
                (file.PasswordProtection.Iterations < 10_000 || file.PasswordProtection.Iterations > 5_000_000))
                throw new InvalidDataException("The file's password protection settings are not valid.");
            return file;
        }

        /// <summary>True when <paramref name="key"/> is the one the file's passwords were encrypted with.</summary>
        public static bool IsKeyCorrect(ExportFile file, string key)
        {
            if (file.PasswordProtection == null || string.IsNullOrEmpty(key)) return false;
            try
            {
                Keys keys = KeysFor(file, key);
                return FixedEquals(Hmac(keys.Mac, KeyCheckLabel), Convert.FromBase64String(file.PasswordProtection.KeyCheck));
            }
            catch (FormatException) { return false; }   // damaged salt / key check in the file
        }

        /// <summary>
        /// The file's connection as a <see cref="ConnectionConfig"/> for this PC. With the right
        /// <paramref name="key"/> its password is saved again (DPAPI, this Windows user);
        /// without one it is imported without a password, and the user is asked for it on use.
        /// </summary>
        public static ConnectionConfig ToConfig(ExportFile file, ExportedConnection c, string key)
        {
            string password = null;
            if (!string.IsNullOrEmpty(c.Password) && !string.IsNullOrEmpty(key) && file.PasswordProtection != null)
                password = Decrypt(KeysFor(file, key), c.Password);

            return new ConnectionConfig
            {
                Name = c.Name, ServiceBaseUrl = c.ServiceBaseUrl, Application = c.Application,
                Username = c.Username, LocalFolderPath = c.LocalFolderPath,
                DefaultDeployMethod = c.DefaultDeployMethod, PullDimensionMembers = c.PullDimensionMembers,
                ReadOnly = c.ReadOnly, PullIpm = c.PullIpm,
                SavePassword      = password != null,
                EncryptedPassword = password != null ? ConfigManager.EncryptPassword(password) : string.Empty,
            };
        }

        // ------------------------------------------------------------------ //
        //  Crypto
        // ------------------------------------------------------------------ //

        private sealed class Keys { public byte[] Enc, Mac; }

        private static readonly Dictionary<string, Keys> _derived = new Dictionary<string, Keys>();

        private static Keys KeysFor(ExportFile file, string key)
        {
            Protection p = file.PasswordProtection;
            string id = p.Salt + "\u0001" + p.Iterations + "\u0001" + key;
            lock (_derived)
            {
                if (!_derived.TryGetValue(id, out Keys k))
                    _derived[id] = k = Derive(key, Convert.FromBase64String(p.Salt), p.Iterations);
                return k;
            }
        }

        private static Keys Derive(string key, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(key), salt, iterations, HashAlgorithmName.SHA256))
            {
                byte[] bytes = kdf.GetBytes(64);
                return new Keys { Enc = bytes.Take(32).ToArray(), Mac = bytes.Skip(32).ToArray() };
            }
        }

        /// <summary>base64( IV[16] | ciphertext | HMAC-SHA256(IV | ciphertext)[32] )</summary>
        private static string Encrypt(Keys keys, string plain)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = keys.Enc; aes.IV = Random(16); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] ct;
                using (ICryptoTransform enc = aes.CreateEncryptor()) ct = enc.TransformFinalBlock(data, 0, data.Length);
                byte[] body = aes.IV.Concat(ct).ToArray();
                return Convert.ToBase64String(body.Concat(Hmac(keys.Mac, body)).ToArray());
            }
        }

        private static string Decrypt(Keys keys, string encoded)
        {
            byte[] all = Convert.FromBase64String(encoded);
            if (all.Length < 16 + 16 + 32) throw new CryptographicException("The exported password is damaged.");
            byte[] body = all.Take(all.Length - 32).ToArray();
            if (!FixedEquals(Hmac(keys.Mac, body), all.Skip(all.Length - 32).ToArray()))
                throw new CryptographicException("Wrong encryption key, or the exported password is damaged.");
            using (Aes aes = Aes.Create())
            {
                aes.Key = keys.Enc; aes.IV = body.Take(16).ToArray(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform dec = aes.CreateDecryptor())
                    return Encoding.UTF8.GetString(dec.TransformFinalBlock(body, 16, body.Length - 16));
            }
        }

        private static byte[] Hmac(byte[] key, byte[] data)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(data);
        }

        private static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        private static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
