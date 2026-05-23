using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace fuseraft.Cli.Commands;

public sealed class UpdateSettings : CommandSettings
{
    [CommandOption("--check")]
    [Description("Check for a newer release without installing.")]
    public bool CheckOnly { get; set; }
}

public sealed class UpdateCommand : AsyncCommand<UpdateSettings>
{
    private const string Repo      = "fuseraft/fuseraft-cli";
    private const string ApiUrl    = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string UserAgent = "fuseraft-cli";

    protected override async Task<int> ExecuteAsync(
        CommandContext context, UpdateSettings settings, CancellationToken cancellationToken)
    {
        var currentVer = typeof(UpdateCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        AnsiConsole.MarkupLine($"[dim]Current version:[/] {Markup.Escape(currentVer)}");
        AnsiConsole.Markup("[dim]Checking github.com/" + Repo + " for updates…[/]");

        string releaseJson;
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        try
        {
            releaseJson = await http.GetStringAsync(ApiUrl, cancellationToken);
            AnsiConsole.MarkupLine(" [green]done[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]✗ Could not reach GitHub:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        string? tag, latestVersion;
        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            tag           = doc.RootElement.GetProperty("tag_name").GetString();
            latestVersion = tag?.TrimStart('v');
        }
        catch
        {
            AnsiConsole.MarkupLine("[red]✗ Could not parse GitHub release response.[/]");
            return 1;
        }

        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(latestVersion))
        {
            AnsiConsole.MarkupLine("[red]✗ Could not determine the latest release tag.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[dim]Latest release:[/]  {Markup.Escape(latestVersion)}");

        if (IsUpToDate(currentVer, latestVersion))
        {
            AnsiConsole.MarkupLine("[green]✓ Already up to date.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[cyan]Update available:[/] {Markup.Escape(currentVer)} → {Markup.Escape(latestVersion)}");

        if (settings.CheckOnly) return 0;

        var rid = DetectRid();
        if (rid is null)
        {
            AnsiConsole.MarkupLine("[red]✗ Unsupported platform. Download manually from:[/]");
            AnsiConsole.MarkupLine($"[dim]  https://github.com/{Repo}/releases/tag/{Markup.Escape(tag)}[/]");
            return 1;
        }

        var archive     = $"fuseraft-{latestVersion}-{rid}.tar.gz";
        var downloadUrl = $"https://github.com/{Repo}/releases/download/{tag}/{archive}";

        if (!releaseJson.Contains($"\"{archive}\""))
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Release asset '{Markup.Escape(archive)}' not found in release {Markup.Escape(tag)}.[/]");
            AnsiConsole.MarkupLine("[dim]The release may not have a build for this platform yet.[/]");
            return 1;
        }

        AnsiConsole.Markup($"[dim]Downloading {Markup.Escape(archive)}…[/]");
        byte[] archiveBytes;
        try
        {
            archiveBytes = await http.GetByteArrayAsync(downloadUrl, cancellationToken);
            AnsiConsole.MarkupLine(" [green]done[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]✗ Download failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.Markup("[dim]Extracting…[/]");
        byte[]? newBinary;
        try
        {
            newBinary = await ExtractBinaryAsync(archiveBytes, cancellationToken);
            AnsiConsole.MarkupLine(" [green]done[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]✗ Extraction failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (newBinary is null)
        {
            AnsiConsole.MarkupLine("[red]✗ fuseraft binary not found in the archive.[/]");
            return 1;
        }

        var binaryPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
        {
            AnsiConsole.MarkupLine("[red]✗ Could not determine the path of the running binary.[/]");
            return 1;
        }

        var tmpPath = binaryPath + ".new";
        try
        {
            await File.WriteAllBytesAsync(tmpPath, newBinary, cancellationToken);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(tmpPath,
                    UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute  |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            File.Move(tmpPath, binaryPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            AnsiConsole.MarkupLine($"[red]✗ Install failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]✓ fuseraft updated to {Markup.Escape(latestVersion)}.[/]");
        return 0;
    }

    private static string? DetectRid()
    {
        string osTag;
        if      (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   osTag = "linux";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     osTag = "osx";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) osTag = "win";
        else return null;

        var archTag = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.Arm64 => "arm64",
            _                  => null,
        };

        return archTag is null ? null : $"{osTag}-{archTag}";
    }

    private static bool IsUpToDate(string current, string latest)
    {
        var baseVer = StripMeta(current);
        return Version.TryParse(baseVer, out var cv) &&
               Version.TryParse(latest,  out var lv) &&
               cv >= lv;
    }

    private static string StripMeta(string v)
    {
        var i = v.IndexOf('+'); if (i >= 0) v = v[..i];
            i = v.IndexOf('-'); if (i >= 0) v = v[..i];
        return v.Trim();
    }

    private static async Task<byte[]?> ExtractBinaryAsync(byte[] tarGzBytes, CancellationToken ct)
    {
        using var ms   = new MemoryStream(tarGzBytes);
        using var gzip = new GZipStream(ms, CompressionMode.Decompress);
        using var tar  = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
        {
            var name = Path.GetFileName(entry.Name);
            if ((name.Equals("fuseraft",     StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("fuseraft.exe", StringComparison.OrdinalIgnoreCase)) &&
                entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile &&
                entry.DataStream is not null)
            {
                using var buf = new MemoryStream();
                await entry.DataStream.CopyToAsync(buf, ct);
                return buf.ToArray();
            }
        }

        return null;
    }
}
