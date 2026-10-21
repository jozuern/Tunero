using System.Net;
using System.Text.Json;
using Optimizer.Core.Updates;

namespace Optimizer.Core.Tests;

/// <summary>The opt-in release check and the license texts that ship in the exe.</summary>
public class ReleaseTests
{
    private static readonly Version Current = new(0, 3, 0, 0); // assembly versions have four parts

    [Theory]
    [InlineData("v0.4.0", ReleaseCheckStatus.NewerAvailable)]
    [InlineData("0.3.1", ReleaseCheckStatus.NewerAvailable)]
    [InlineData("v0.3.0", ReleaseCheckStatus.UpToDate)]
    [InlineData("v0.2.9", ReleaseCheckStatus.UpToDate)]
    [InlineData("v0.4.0-beta", ReleaseCheckStatus.Error)]
    [InlineData("latest", ReleaseCheckStatus.Error)]
    public void ComparesTagWithRunningVersion(string tag, ReleaseCheckStatus expected) =>
        Assert.Equal(expected, ReleaseCheck.Evaluate(Current, $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false}""").Status);

    [Fact]
    public void PreReleasesAndBrokenAnswersAreNotOffered()
    {
        Assert.Equal(ReleaseCheckStatus.NoRelease, ReleaseCheck.Evaluate(Current, """{"tag_name":"v9.0.0","prerelease":true}""").Status);
        Assert.Equal(ReleaseCheckStatus.Error, ReleaseCheck.Evaluate(Current, "not json").Status);
        Assert.Equal(ReleaseCheckStatus.Error, ReleaseCheck.Evaluate(Current, """{"tag_name":7}""").Status);
    }

    [Fact]
    public async Task AsksGitHubWithUserAgentAndHandlesMissingRelease()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, "{}");
        using var check = new ReleaseCheck(handler);
        Assert.Equal(ReleaseCheckStatus.NoRelease, (await check.CheckAsync(Current)).Status);
        // By the repository's id: it survives a rename of the repository (jozuern/PCOptimizer up to 0.7.3).
        Assert.Equal("https://api.github.com/repositories/1412188031/releases/latest", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Tunero/0.3.0", handler.Request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task NewerReleaseIsReported()
    {
        using var check = new ReleaseCheck(new FakeHandler(HttpStatusCode.OK, """{"tag_name":"v1.0.0","html_url":"https://example.com/elsewhere"}"""));
        var result = await check.CheckAsync(Current);
        Assert.Equal((ReleaseCheckStatus.NewerAvailable, new Version(1, 0, 0)), (result.Status, result.Latest));
        // The link the app opens is fixed, never the URL from the answer.
        Assert.Equal("https://github.com/jozuern/Tunero/releases/latest", ReleaseCheck.LatestReleaseUrl);
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    // ---------------- Self-update ----------------

    private const string Download = "https://github.com/jozuern/Tunero/releases/download/v1.0.0/";

    private static string ReleaseJson(string exeUrl, string shaUrl, string? sigUrl = Download + "Tunero.exe.sig") => $$"""
        {"tag_name":"v1.0.0","assets":[
          {"name":"Tunero.exe","browser_download_url":"{{exeUrl}}"},
          {{(sigUrl is null ? "" : $$"""{"name":"Tunero.exe.sig","browser_download_url":"{{sigUrl}}"},""")}}
          {"name":"Tunero.exe.sha256","browser_download_url":"{{shaUrl}}"}]}
        """;

    /// <summary>
    /// The bridge across the rename: a release made before the repository was renamed has its links under
    /// jozuern/PCOptimizer, and a release can carry the exe under its old name only.
    /// </summary>
    [Fact]
    public void ReleasesFromBeforeTheRenameAreFound()
    {
        const string legacy = "https://github.com/jozuern/PCOptimizer/releases/download/v1.0.0/";
        var json = $$"""
            {"tag_name":"v1.0.0","assets":[
              {"name":"PCOptimizer.exe","browser_download_url":"{{legacy}}PCOptimizer.exe"},
              {"name":"PCOptimizer.exe.sha256","browser_download_url":"{{legacy}}PCOptimizer.exe.sha256"},
              {"name":"PCOptimizer.exe.sig","browser_download_url":"{{legacy}}PCOptimizer.exe.sig"}]}
            """;
        var assets = ReleaseCheck.Evaluate(Current, json).Assets;
        Assert.NotNull(assets);
        Assert.Equal(legacy + "PCOptimizer.exe", assets.ExeUrl);
        Assert.Equal(legacy + "PCOptimizer.exe.sig", assets.SignatureUrl);
    }

    [Fact]
    public void TheNewExeNameWinsOverTheOldCopy()
    {
        var json = $$"""
            {"tag_name":"v1.0.0","assets":[
              {"name":"PCOptimizer.exe","browser_download_url":"{{Download}}PCOptimizer.exe"},
              {"name":"PCOptimizer.exe.sha256","browser_download_url":"{{Download}}PCOptimizer.exe.sha256"},
              {"name":"PCOptimizer.exe.sig","browser_download_url":"{{Download}}PCOptimizer.exe.sig"},
              {"name":"Tunero.exe","browser_download_url":"{{Download}}Tunero.exe"},
              {"name":"Tunero.exe.sha256","browser_download_url":"{{Download}}Tunero.exe.sha256"},
              {"name":"Tunero.exe.sig","browser_download_url":"{{Download}}Tunero.exe.sig"}]}
            """;
        Assert.Equal(Download + "Tunero.exe", ReleaseCheck.Evaluate(Current, json).Assets!.ExeUrl);
    }

    /// <summary>A throwaway release key; the tests never use the project's real key.</summary>
    private static (string PublicPem, Func<byte[], string> Sign) TestKey()
    {
        var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return (key.ExportSubjectPublicKeyInfoPem(), data => Convert.ToBase64String(key.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256)));
    }

    [Fact]
    public void TheAppCarriesAP256ReleaseKey()
    {
        Assert.True(UpdateSignature.IsConfigured, "src/Optimizer.Core/Updates/update-key.pem is missing (scripts/new-update-key.ps1)");
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(UpdateSignature.PublicKeyPem);
        Assert.Equal(256, key.KeySize);
    }

    /// <summary>The offline backup key (when present) is a P-256 key of its own, and a release signed with it is accepted.</summary>
    [Fact]
    public void TheBackupKeyIsAnotherP256Key()
    {
        Assert.All(UpdateSignature.PublicKeyPems, pem =>
        {
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportFromPem(pem);
            Assert.Equal(256, key.KeySize);
        });
        Assert.Equal(UpdateSignature.PublicKeyPems.Count, UpdateSignature.PublicKeyPems.Distinct().Count());
    }

    [Fact]
    public void SignatureBindsKeyVersionAndChecksum()
    {
        var (pem, sign) = TestKey();
        var (otherPem, _) = TestKey();
        var hash = new string('a', 64);
        var version = new Version(1, 2, 3);
        var signature = sign(UpdateSignature.Statement(version, hash));
        // The release workflow signs exactly this text, with the lowercase hash.
        // The signed statement keeps the app's old name: every installed version, also those from before the rename, checks it.
        Assert.Equal("PCOptimizer 1.2.3\n" + hash + "\n", System.Text.Encoding.UTF8.GetString(UpdateSignature.Statement(version, hash.ToUpperInvariant())));
        var workflow = File.ReadAllText(Path.Combine(RepoPaths.Root, ".github", "workflows", "release.yml"));
        Assert.Contains("GetBytes(\"PCOptimizer $version`n$hash`n\")", workflow);
        Assert.Contains(".Hash.ToLowerInvariant()", workflow);
        Assert.True(UpdateSignature.Verify(version, hash, signature, pem));
        Assert.True(UpdateSignature.Verify(version, hash.ToUpperInvariant(), signature + "\n", pem));
        Assert.False(UpdateSignature.Verify(version, hash, signature, otherPem));            // another key
        Assert.False(UpdateSignature.Verify(new Version(1, 2, 4), hash, signature, pem));      // an older release offered as newer
        Assert.False(UpdateSignature.Verify(version, new string('b', 64), signature, pem));   // another exe
        Assert.False(UpdateSignature.Verify(version, hash, "not base64", pem));
    }

    /// <summary>
    /// Every workflow and script that signs or checks a release (release.yml signs, winget, Chocolatey and the hand-run
    /// package scripts check) uses the same statement as the app, over the lowercase hash: a copy that drifted would accept
    /// or refuse other releases than the app. The check itself lives once, in verify-release.ps1.
    /// </summary>
    [Fact]
    public void EveryReleaseCheckVerifiesTheAppsStatement()
    {
        var workflows = Path.Combine(RepoPaths.Root, ".github", "workflows");
        var files = Directory.EnumerateFiles(workflows, "*.yml")
            .Append(Path.Combine(RepoPaths.Root, "scripts", "verify-release.ps1"));
        var statements = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("\"PCOptimizer $", StringComparison.Ordinal)) continue;
            statements.Add(Path.GetFileName(file));
            var statement = System.Text.RegularExpressions.Regex.Match(text, "\"PCOptimizer \\$[^\"`]+`n\\$hash`n\"");
            Assert.True(statement.Success, $"{Path.GetFileName(file)}: the statement is not \"PCOptimizer <version>`n$hash`n\"");
            Assert.Contains(".Hash.ToLowerInvariant()", text);
        }
        Assert.Equal(new[] { "release.yml", "verify-release.ps1" }, statements.Order().ToArray());
        // The workflows that check a release run the one check (release.yml on the files it just signed), no copy of it.
        foreach (var workflow in new[] { "release.yml", "winget.yml", "chocolatey.yml" })
        {
            var text = File.ReadAllText(Path.Combine(workflows, workflow));
            Assert.Contains("scripts/verify-release.ps1 -Version $env:TAG.TrimStart('v') -Folder ", text);
            Assert.DoesNotContain("VerifyData", text);
        }
        // The hand-run package scripts take their hash from the verified check, never from the .sha256 file alone.
        foreach (var script in new[] { "chocolatey-package.ps1", "scoop-manifest.ps1", "winget-manifest.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(RepoPaths.Root, "scripts", script));
            Assert.Contains("verify-release.ps1", text);
            Assert.DoesNotContain("Invoke-RestMethod \"$exeUrl.sha256\"", text);
        }
        // Scoop's Excavator would take a new release's .sha256 unchecked: the manifest has no checkver or autoupdate.
        var scoop = File.ReadLines(Path.Combine(RepoPaths.Root, "scripts", "scoop-manifest.ps1")).Where(l => !l.TrimStart().StartsWith('#'));
        Assert.DoesNotContain(scoop, l => l.Contains("checkver", StringComparison.Ordinal) || l.Contains("autoupdate", StringComparison.Ordinal));
    }

    /// <summary>
    /// A signed release passes two hands: the maintainer's PC (sign-release.ps1) and the second run of release.yml. Both
    /// sign or accept only this tag's attested build, from GitHub's runners, signed by the configured publisher, with
    /// nothing changed but the signature; a check that drifted in one of them would let the other's guard stand alone.
    /// </summary>
    [Fact]
    public void BothSidesOfASignedReleaseCheckTheSame()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoPaths.Root, ".github", "workflows", "release.yml"));
        var script = File.ReadAllText(Path.Combine(RepoPaths.Root, "scripts", "sign-release.ps1"));
        foreach (var text in new[] { workflow, script })
        {
            Assert.Contains("Tunero-unsigned.exe", text);
            Assert.Contains("CODE_SIGNING_SUBJECT", text);
            Assert.Contains("check-signed-build.ps1", text);
            Assert.Contains("gh attestation verify", text);
            Assert.Contains("/.github/workflows/release.yml\" --source-ref \"refs/tags/", text);
            Assert.Contains("--deny-self-hosted-runners", text);
            Assert.Contains("TimeStamperCertificate", text);
        }
        Assert.Contains("--signer-workflow \"$env:GH_REPO/.github/workflows/release.yml\"", workflow);
        Assert.Contains("--signer-workflow \"$Repository/.github/workflows/release.yml\"", script);
        // SHA-256 for the file digest and the timestamp (Certum's timestamp server), as Certum documents for signtool.
        Assert.Contains("/fd sha256 /tr $TimestampUrl /td sha256", script);
        Assert.Contains("\"http://time.certum.pl\"", script);
        // The second run builds nothing: it may start only from a tag, and only after the attested build exists.
        Assert.Contains("workflow_dispatch", workflow);
        Assert.Contains("if: github.event_name == 'push'", workflow);
    }

    [Fact]
    public void AReleaseWithoutSignatureOffersNoSelfUpdate() =>
        Assert.Null(ReleaseCheck.Evaluate(Current, ReleaseJson(Download + "Tunero.exe", Download + "Tunero.exe.sha256", sigUrl: null)).Assets);

    [Fact]
    public void AssetsAreTakenOnlyFromThisRepository()
    {
        var ok = ReleaseCheck.Evaluate(Current, ReleaseJson(Download + "Tunero.exe", Download + "Tunero.exe.sha256"));
        Assert.Equal(Download + "Tunero.exe", ok.Assets?.ExeUrl);
        var elsewhere = ReleaseCheck.Evaluate(Current, ReleaseJson("https://example.com/Tunero.exe", Download + "Tunero.exe.sha256"));
        Assert.Null(elsewhere.Assets);
    }

    [Fact]
    public void ReadsSha256sumFormat()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, Updater.ParseChecksum($"{hash}  Tunero.exe\n"));
        Assert.Null(Updater.ParseChecksum("not a hash"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task KeepsTheDownloadOnlyWhenSignedAndTheChecksumMatches(bool matching, bool signedByReleaseKey)
    {
        var exe = "new exe bytes"u8.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(matching ? exe : [1, 2, 3])).ToLowerInvariant();
        var release = ReleaseCheck.Evaluate(Current, ReleaseJson(Download + "Tunero.exe", Download + "Tunero.exe.sha256"));
        var (pem, sign) = TestKey();
        var (_, signOther) = TestKey();
        var signature = (signedByReleaseKey ? sign : signOther)(UpdateSignature.Statement(new Version(1, 0, 0), hash));
        var folder = Path.Combine(Path.GetTempPath(), "pco-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var updater = new Updater(new RoutingHandler(new()
            {
                [Download + "Tunero.exe"] = exe,
                [Download + "Tunero.exe.sha256"] = System.Text.Encoding.ASCII.GetBytes($"{hash}  Tunero.exe\n"),
                [Download + "Tunero.exe.sig"] = System.Text.Encoding.ASCII.GetBytes(signature),
            }), pem);
            var result = await updater.DownloadAsync(release, folder);
            if (!signedByReleaseKey)
            {
                Assert.Equal(UpdateOutcome.SignatureInvalid, result.Outcome);
                Assert.False(Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any());
            }
            else if (matching)
            {
                Assert.Equal(UpdateOutcome.Ready, result.Outcome);
                Assert.Equal(exe, File.ReadAllBytes(result.FilePath!));
            }
            else
            {
                Assert.Equal(UpdateOutcome.ChecksumMismatch, result.Outcome);
                Assert.Empty(Directory.EnumerateFiles(folder));
            }
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A new exe that did not start (antivirus, a changed file) makes way for the previous one again.</summary>
    [Fact]
    public void RollBackPutsThePreviousExeBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pco-rollback-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var running = Path.Combine(folder, "Tunero.exe");
            var downloaded = Path.Combine(folder, "Tunero-1.0.0.exe");
            File.WriteAllText(running, "old");
            File.WriteAllText(downloaded, "new");
            Updater.Install(downloaded, running);

            Assert.True(Updater.RollBack(running));
            Assert.Equal("old", File.ReadAllText(running));
            Assert.False(File.Exists(running + ".old"));

            // Nothing to put back: the exe in place stays.
            Assert.False(Updater.RollBack(running));
            Assert.Equal("old", File.ReadAllText(running));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A folder replaced by a link cannot be held: the rollback changes nothing there.</summary>
    [Fact]
    public void RollBackRefusesAFolderThatIsALink()
    {
        var root = Path.Combine(Path.GetTempPath(), "pco-rollback-link-" + Guid.NewGuid().ToString("N"));
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var link = Path.Combine(root, "link");
        try
        {
            File.WriteAllText(Path.Combine(real, "Tunero.exe"), "new");
            File.WriteAllText(Path.Combine(real, "Tunero.exe.old"), "old");
            TestFolders.Junction(link, real);

            Assert.False(Updater.RollBack(Path.Combine(link, "Tunero.exe")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(real, "Tunero.exe")));
            Assert.True(File.Exists(Path.Combine(real, "Tunero.exe.old")));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public void InstallSwapsTheExeAndCleanUpRemovesTheOldOne()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pco-install-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var running = Path.Combine(folder, "Tunero.exe");
            var downloaded = Path.Combine(folder, "Tunero-1.0.0.exe");
            File.WriteAllText(running, "old");
            File.WriteAllText(downloaded, "new");
            Updater.Install(downloaded, running);
            Assert.Equal("new", File.ReadAllText(running));
            Assert.Equal("old", File.ReadAllText(running + ".old"));
            Updater.CleanUp(running, folder);
            Assert.False(File.Exists(running + ".old"));
            Assert.False(File.Exists(downloaded));
            Assert.True(File.Exists(running));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private sealed class RoutingHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // ---------------- Shipped licenses ----------------

    private sealed record Component(string Name, string? Package, string? Version, string License, string Source, List<string> Files);

    private static List<Component> Manifest() =>
        JsonSerializer.Deserialize<List<Component>>(File.ReadAllText(Path.Combine(RepoPaths.App, "Licenses", "licenses.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void EveryLicenseFileExists()
    {
        foreach (var file in Manifest().SelectMany(c => c.Files).Distinct())
        {
            // Tunero.txt is the repository's LICENSE, embedded under that name.
            var path = file == "Tunero.txt" ? Path.Combine(RepoPaths.Root, "LICENSE") : Path.Combine(RepoPaths.App, "Licenses", file);
            Assert.True(File.Exists(path), $"{file} is listed in licenses.json but missing");
        }
        // GitHub recognizes the license only from the unchanged GNU text.
        var license = File.ReadAllText(Path.Combine(RepoPaths.Root, "LICENSE"));
        Assert.StartsWith("                    GNU GENERAL PUBLIC LICENSE", license);
        Assert.Contains("Version 3, 29 June 2007", license);
    }

    /// <summary>
    /// Every NuGet package the app ships needs its license in the exe and a line in THIRD-PARTY-NOTICES.md: a new
    /// dependency fails here until both are added. Read from the committed lock file, which restore keeps current.
    /// </summary>
    [Fact]
    public void EveryShippedPackageHasItsLicense()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.App, "packages.lock.json")));
        var listed = Manifest().Where(c => c.Package is not null).ToDictionary(c => c.Package!, c => c.Version, StringComparer.OrdinalIgnoreCase);
        var notices = File.ReadAllText(Path.Combine(RepoPaths.Root, "docs", "THIRD-PARTY-NOTICES.md"));
        var missing = new List<string>();
        foreach (var target in doc.RootElement.GetProperty("dependencies").EnumerateObject())
        foreach (var lib in target.Value.EnumerateObject())
        {
            if (lib.Value.GetProperty("type").GetString() == "Project") continue;
            var (id, version) = (lib.Name, lib.Value.GetProperty("resolved").GetString());
            // runtime.<rid>.runtime.native.* packages carry native files for other platforms (the win-x64 exe does not
            // contain them), runtime.native.* is the empty package that selects one of them. Any other "runtime." id counts.
            if (System.Text.RegularExpressions.Regex.IsMatch(id, @"^runtime\.([a-z0-9-]+\.)?runtime\.native\.|^runtime\.native\.",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            if (!listed.TryGetValue(id, out var v) || v != version) missing.Add($"{id} {version} (licenses.json)");
            // The id as a whole name: "WPF-UI" inside "WPF-UI.Abstractions" does not count for WPF-UI.
            if (!System.Text.RegularExpressions.Regex.IsMatch(notices, $@"(?<![\w.-]){System.Text.RegularExpressions.Regex.Escape(id)}(?![\w-]|\.[A-Za-z])",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                missing.Add($"{id} (THIRD-PARTY-NOTICES.md)");
        }
        // The self-contained runtime comes from the SDK that global.json pins, not from a package reference.
        var runtime = ShippedRuntime();
        foreach (var pack in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
            if (listed.GetValueOrDefault(pack) != runtime) missing.Add($"{pack} {runtime} (licenses.json)");
        if (!notices.Contains($"runtime {runtime}", StringComparison.Ordinal)) missing.Add($".NET runtime {runtime} (THIRD-PARTY-NOTICES.md)");
        Assert.Empty(missing);
    }

    /// <summary>
    /// The runtime the published exe carries: the one bundled with the SDK that global.json selects ("patch": the
    /// pinned SDK when it is installed, otherwise the newest installed patch of its feature band; "latestPatch", the
    /// default: always the newest). The runtime this test runs on can be newer: a Windows or Visual Studio update
    /// installs patches without changing what the build ships.
    /// </summary>
    private static string ShippedRuntime()
    {
        var fallback = $"{Environment.Version.Major}.{Environment.Version.Minor}.{Environment.Version.Build}";
        var sdkJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "global.json"))).RootElement.GetProperty("sdk");
        var pinned = Version.Parse(sdkJson.GetProperty("version").GetString()!);
        var exactFirst = sdkJson.TryGetProperty("rollForward", out var roll) && roll.GetString() is "patch" or "disable";
        // ...\dotnet\shared\Microsoft.NETCore.App\<version>\ -> ...\dotnet
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var sdks = Path.Combine(dotnet, "sdk");
        if (!Directory.Exists(sdks)) return fallback;
        var sdk = Directory.EnumerateDirectories(sdks)
            .Select(d => Version.TryParse(Path.GetFileName(d), out var v) ? (Folder: d, Version: v) : default)
            .Where(x => x.Version is { } v && v.Major == pinned.Major && v.Minor == pinned.Minor && v.Build / 100 == pinned.Build / 100 && v >= pinned)
            .OrderByDescending(x => exactFirst && x.Version == pinned).ThenByDescending(x => x.Version).FirstOrDefault().Folder;
        var props = sdk is null ? null : Path.Combine(sdk, "Microsoft.NETCoreSdk.BundledVersions.props");
        if (props is null || !File.Exists(props)) return fallback;
        var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(props), @"<BundledNETCoreAppPackageVersion>([0-9]+\.[0-9]+\.[0-9]+)<");
        return match.Success ? match.Groups[1].Value : fallback;
    }

    /// <summary>
    /// Files the sensor libraries carry inside their DLLs, reviewed for their own licenses: the PawnIO modules
    /// (LGPL-2.1, pawnio-modules.txt) and the PCI and USB ID databases (BSD-3-Clause, pci-ids.txt, usb-ids.txt). A new
    /// version that embeds something else fails here until its license is checked and listed.
    /// </summary>
    [Fact]
    public void EmbeddedFilesOfDependenciesAreReviewed()
    {
        string[] reviewed =
        [
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.AMDFamily0F.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.AMDFamily10.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.AMDFamily17.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.IntelMSR.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.IsaBridgeEC.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.LpcACPIEC.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.LpcCrOSEC.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.RyzenSMU.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.SmbusI801.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.SmbusNCT6793.bin",
            "LibreHardwareMonitorLib: LibreHardwareMonitor.Resources.PawnIo.SmbusPIIX4.bin",
            "DiskInfoToolkit: DiskInfoToolkit.Resources.pci.ids.gz",
            "DiskInfoToolkit: DiskInfoToolkit.Resources.usb.ids.gz",
        ];
        var bin = AppContext.BaseDirectory;
        var dlls = new[]
        {
            Path.Combine(bin, "runtimes", "win-x64", "lib", "net10.0", "LibreHardwareMonitorLib.dll"),
            Path.Combine(bin, "DiskInfoToolkit.dll"), Path.Combine(bin, "RAMSPDToolkit-NDD.dll"), Path.Combine(bin, "BlackSharp.Core.dll"),
            Path.Combine(bin, "HidSharp.dll"),
        };
        var found = new List<string>();
        foreach (var dll in dlls)
        {
            Assert.True(File.Exists(dll), $"{dll} missing");
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(dll));
            var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in reader.ManifestResources)
            {
                var name = reader.GetString(reader.GetManifestResource(handle).Name);
                // Compiled .resx strings belong to the library itself.
                if (!name.EndsWith(".resources", StringComparison.Ordinal)) found.Add($"{Path.GetFileNameWithoutExtension(dll)}: {name}");
            }
        }
        Assert.Equal(reviewed.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
    }
}
