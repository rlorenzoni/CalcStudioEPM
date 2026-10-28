using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace CalcStudioEPM.Config
{
    /// <summary>Where a deploy sends the rule.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum DeployMethod
    {
        /// <summary>Calculation Manager only — the rule still has to be deployed to Planning from Calc Manager.</summary>
        CmOnly,

        /// <summary>Calculation Manager plus the Planning (HP) final calculation. Only possible
        /// when every %Template call in the rule can be inlined locally (no DTPs).</summary>
        CmAndHp,
    }

    /// <summary>Display names for <see cref="DeployMethod"/>.</summary>
    public static class DeployMethodNames
    {
        /// <summary>Full name — connection settings.</summary>
        public static string Long(DeployMethod m) => m == DeployMethod.CmAndHp
            ? "Try to Deploy also Planning Final Calculation (Limited for complex templates)"
            : "Deploy Calculation Manager Only";

        /// <summary>Short name — the Deploy button's drop-down.</summary>
        public static string Short(DeployMethod m) => m == DeployMethod.CmAndHp ? "Deploy CM+HP" : "Deploy CM";
    }

    /// <summary>
    /// Holds all connection settings for one EPM Cloud project/folder.
    /// <see cref="LocalFolderPath"/> is the primary key — each folder has at most one entry.
    /// <see cref="EncryptedPassword"/> is stored as a DPAPI-protected, base64-encoded string.
    /// </summary>
    public class ConnectionConfig
    {
        /// <summary>User-defined display name, e.g. "Vision Production"</summary>
        public string Name { get; set; }

        /// <summary>Base service URL, e.g. https://planning-xxx.pbcs.us2.oraclecloud.com</summary>
        public string ServiceBaseUrl { get; set; }

        /// <summary>EPM application name, e.g. Vision</summary>
        public string Application { get; set; }

        /// <summary>Login username, e.g. identity_domain.username or email</summary>
        public string Username { get; set; }

        /// <summary>DPAPI-protected password stored as base64 string; empty unless
        /// <see cref="SavePassword"/>.</summary>
        public string EncryptedPassword { get; set; }

        /// <summary>Keep the password in connections.json. When false the user is asked for it
        /// (once per Notepad++ session, or saved from that prompt) — see Credentials.
        /// Connections saved before this setting existed had their password saved.</summary>
        public bool SavePassword { get; set; } = true;

        /// <summary>Absolute path to the local working folder on disk (primary key)</summary>
        public string LocalFolderPath { get; set; }

        /// <summary>What the Deploy button does when clicked (its drop-down offers both).
        /// Connections saved before this setting existed default to Calculation Manager only.</summary>
        public DeployMethod DefaultDeployMethod { get; set; } = DeployMethod.CmOnly;

        /// <summary>Download dimensions and members on Pull so Local Syntax Validation can check
        /// member names. Each pull asks whether to update them (they change rarely).</summary>
        public bool PullDimensionMembers { get; set; }

        /// <summary>A connection to look at, not change (e.g. production): pulled files are
        /// marked read-only on disk and Deploy is disabled. Pull and Execute still work.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>Offer the IPM artifacts on Pull: Auto Predict jobs, calendars and events, exported
        /// as their own package and written to {Connection}.Scripts\IPM for editing. Off by default.</summary>
        public bool PullIpm { get; set; }
    }
}
