using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Keeps the values of secret-looking environment variables (<c>ANTHROPIC_API_KEY</c>,
/// <c>GITHUB_TOKEN</c>, <c>PGPASSWORD</c>, ...) out of tool results — and therefore out of the
/// model's context, the provider request, and the persisted session log.
///
/// <para>
/// Shell children inherit the parent environment, so <c>env</c>, <c>printenv</c>,
/// <c>echo $TOKEN</c>, a verbose CLI, or the <c>shell_get_env</c> tool would otherwise all hand a
/// live credential to the model. The model never needs the value itself — a command can
/// reference <c>$NAME</c> and the shell expands it — so the value is replaced with
/// <see cref="Placeholder"/> on the way out, the same way OpenHands' <c>SecretRegistry</c>
/// masks tool output.
/// </para>
///
/// <para>
/// Values found inside the files the FileSystem deny rules protect (<c>.env</c>, private keys, ...)
/// are masked too — see <see cref="KnownSecretFiles"/> — so <c>cat .e*</c> or a script that opens
/// the file itself doesn't print what the file tools refuse to read.
/// </para>
///
/// <para>
/// <b>Limitation:</b> this is exact-value masking. It stops accidental exposure, not a model that
/// deliberately re-encodes a value (<c>echo $KEY | base64</c>); it also only knows about
/// variables present in the process environment and the tracked deny-ruled files. Use a sandbox /
/// HITL approval for the adversarial case.
/// </para>
/// </summary>
internal static class EnvSecretMasker
{
    internal const string Placeholder = "<secret-hidden>";

    // Shorter values are too likely to be ordinary words/flags ("true", "1", "prod") that would
    // mangle unrelated output when replaced everywhere they appear.
    internal const int MinValueLength = 8;

    // A credential-ish word as a whole '_'/'-'/'.'-delimited token (so KEYBOARD_LAYOUT and
    // SSH_AUTH_SOCK don't match), or as the tail of the name (so PGPASSWORD / GITHUB_TOKEN do).
    private static readonly Regex SecretWord = new(
        @"(?:^|[_.\-])(?:API_?KEY|ACCESS_?KEY|SECRET_?KEY|PRIVATE_?KEY|KEY|TOKEN|SECRET|PASSWORD|PASSWD|PASSPHRASE|CREDENTIALS?|CONNECTION_?STRING)(?:$|[_.\-])" +
        @"|(?:APIKEY|TOKEN|SECRET|PASSWORD|PASSWD|PASSPHRASE)$" +
        @"|^MYSQL_PWD$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A secret-shaped word in the name doesn't make the value a secret when the name says it's a
    // pointer to one (AWS_ACCESS_KEY_ID, GITHUB_TOKEN_FILE, TOKEN_URL): masking those would
    // corrupt ordinary paths and URLs in output for no protection.
    private static readonly Regex PointerSuffix = new(
        @"(?:_|^)(?:ID|IDS|FILE|FILES|PATH|PATHS|DIR|URL|URI|HOST|PORT|NAME|SOCK|ENDPOINT|ENV|ENVVAR|VAR|VARS)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsSensitiveName(string? name) =>
        !string.IsNullOrEmpty(name) && SecretWord.IsMatch(name) && !PointerSuffix.IsMatch(name);

    /// <summary>
    /// Replaces every current secret-env-var value found in <paramref name="text"/> with
    /// <see cref="Placeholder"/>. Re-reads the environment on each call so a variable set by
    /// <c>shell_set_env</c> mid-session is covered too.
    /// </summary>
    internal static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        foreach (var secret in CurrentSecretValues())
        {
            if (text.Contains(secret, StringComparison.Ordinal))
                text = text.Replace(secret, Placeholder, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>
    /// <see cref="Mask(string?)"/> for a tool's return value, whatever its type. Tools made by <c>AIFunctionFactory</c>
    /// return a <see cref="JsonElement"/>, so a masked result comes back as one too; anything else is masked through
    /// its JSON form, which is what the model would see. A result without a secret is returned as it was.
    /// </summary>
    internal static object? MaskResult(object? result)
    {
        switch (result)
        {
            case null:
                return null;
            case string text:
                return Mask(text);
            case JsonElement { ValueKind: JsonValueKind.String } element:
                var value = element.GetString();
                var maskedValue = Mask(value);
                return maskedValue == value ? result : JsonSerializer.SerializeToElement(maskedValue, AIJsonUtilities.DefaultOptions);
        }
        string json;
        try
        {
            json = result is JsonElement raw ? raw.GetRawText() : JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException)
        {
            return result;   // the framework can't serialize it for the model either
        }
        var masked = Mask(json);
        if (masked == json) return result;
        try
        {
            using var doc = JsonDocument.Parse(masked);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return masked;   // never the unmasked original
        }
    }

    /// <summary>True when <paramref name="text"/> holds the value of a current secret.</summary>
    internal static bool ContainsSecret(string? text) =>
        !string.IsNullOrEmpty(text) && CurrentSecrets().Any(s => text.Contains(s.Value, StringComparison.Ordinal));

    /// <summary>
    /// What each secret value in <paramref name="text"/> is: the environment variable it belongs to, or "a protected
    /// file" for a value from one of <see cref="KnownSecretFiles"/>. Never the value itself.
    /// </summary>
    internal static IReadOnlySet<string> SecretNamesIn(string? text)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return names;
        foreach (var (name, value) in CurrentSecrets())
            if (text.Contains(value, StringComparison.Ordinal)) names.Add(name);
        return names;
    }

    private const string ProtectedFileName = "a protected file";

    private static IEnumerable<(string Name, string Value)> CurrentSecrets()
    {
        foreach (var value in KnownSecretFiles.Values())
            yield return (ProtectedFileName, value);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name
                && entry.Value is string value
                && value.Length >= MinValueLength
                && IsSensitiveName(name))
            {
                yield return (name, value);
            }
        }
    }

    // Longest first so a value that contains a shorter one is replaced whole, not left with the
    // tail of it exposed.
    private static List<string> CurrentSecretValues() =>
        CurrentSecrets().Select(s => s.Value).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToList();
}
