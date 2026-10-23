using System.Net;
using System.Security.Cryptography;
using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.WebPush;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Browser notifications: the message encryption (RFC 8291, against the RFC's own example), the VAPID signature, the
/// devices on the store (who registers, who gets what, what a backup never carries), the built-in sender and its rule, and
/// what a notification shows.
/// </summary>
public sealed class BrowserPushTests : IDisposable
{
    private static readonly Guid RootId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SensorId = new("33333333-3333-3333-3333-333333333333");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-push-" + Guid.NewGuid().ToString("N"));

    public BrowserPushTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    // ---- the protocol ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_encryption_matches_the_example_of_RFC_8291()
    {
        var asPrivate = WebPushEncryption.FromBase64Url("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw")!;
        var asPublic = WebPushEncryption.FromBase64Url("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")!;
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = asPrivate,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] }
        });

        var body = WebPushEncryption.Encrypt(
            "When I grow up, I want to be a watermelon"u8,
            WebPushEncryption.FromBase64Url("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            WebPushEncryption.FromBase64Url("BTBZMqHH6r4Tts7J_aSIgg"),
            sender,
            WebPushEncryption.FromBase64Url("DGv6ra1nlYgDCS1FRnbzlw"));

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            WebPushEncryption.ToBase64Url(body));
    }

    [Fact]
    public void A_message_is_signed_with_the_instance_key_which_survives_a_restart()
    {
        var path = Path.Combine(_dir, "push-keys.json");
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dir, "dp")));
        var keys = new WebPushKeys(protection, path);
        var header = keys.Authorization("https://fcm.googleapis.com/fcm/send/abc", "mailto:ops@example.com", DateTimeOffset.UtcNow);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith(", k=" + keys.PublicKey, header);
        var token = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = token.Split('.');
        Assert.True(keys.Verify(parts[0] + "." + parts[1], WebPushEncryption.FromBase64Url(parts[2])!));
        var claims = Encoding.UTF8.GetString(WebPushEncryption.FromBase64Url(parts[1])!);
        Assert.Contains("\"aud\":\"https://fcm.googleapis.com\"", claims);
        Assert.Contains("\"sub\":\"mailto:ops@example.com\"", claims);

        Assert.Equal(keys.PublicKey, new WebPushKeys(protection, path).PublicKey); // the same pair after a restart
        Assert.DoesNotContain(keys.PublicKey, File.ReadAllText(path).Replace("\"publicKey\": \"" + keys.PublicKey, string.Empty)); // the private half is sealed
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("142.250.185.106", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("172.20.0.3", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("2001:4860:4860::8888", true)]
    public void A_push_only_ever_goes_to_a_public_address(string address, bool expected) =>
        Assert.Equal(expected, WebPushClient.IsPublic(IPAddress.Parse(address)));

    // ---- devices, sender, rule --------------------------------------------------------------------------------------------

    private static readonly string Key = WebPushEncryption.ToBase64Url([0x04, .. new byte[64]]);
    private static readonly string Auth = WebPushEncryption.ToBase64Url(new byte[16]);

    private (InMemoryMonitoringWorkspaceStore Store, SqliteTelemetryRepository Telemetry) Boot(int schemaVersion = InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion)
    {
        var path = Path.Combine(_dir, $"ws-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "setupCompletedUtc": "2026-10-01T00:00:00Z",
              "users": [
                { "id": "aaaaaaaa-0000-0000-0000-000000000001", "username": "admin", "email": "admin@example.com", "role": "Admin", "isEnabled": true, "passwordHash": "x" },
                { "id": "aaaaaaaa-0000-0000-0000-000000000002", "username": "viewer", "email": "viewer@example.com", "role": "Viewer", "isEnabled": true, "passwordHash": "x" }
              ],
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "{{RootId}}", "name": "Primary Probe", "children": [
                { "$kind": "sensor", "id": "{{SensorId}}", "parentId": "{{RootId}}", "name": "UPS", "sensorTypeKey": "nut-ups", "target": "nas" }
              ] }
            }
            """);
        var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new PushTestEnvironment(_dir), new MatmonRuntimeOptions { WorkspacePath = path }, new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(), telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        return (store, telemetry);
    }

    private static readonly Guid Admin = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Viewer = new("aaaaaaaa-0000-0000-0000-000000000002");

    [Fact]
    public void A_device_is_registered_once_per_browser_and_its_user_can_remove_it()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            var first = store.RegisterPushDevice(Admin, "https://fcm.googleapis.com/fcm/send/one", Key, Auth, "Chrome on Android");
            var again = store.RegisterPushDevice(Admin, "https://fcm.googleapis.com/fcm/send/one", Key, Auth, "Chrome on Android");
            Assert.Equal(first.Id, again.Id);
            Assert.Single(store.GetPushDevices(Admin));

            // The same browser signed in as someone else now takes that someone's notifications.
            store.RegisterPushDevice(Viewer, "https://fcm.googleapis.com/fcm/send/one", Key, Auth, "Chrome on Android");
            Assert.Empty(store.GetPushDevices(Admin));
            Assert.Single(store.GetPushDevices(Viewer));

            Assert.False(store.RemovePushDevice(first.Id, Admin)); // not the admin's any more
            Assert.True(store.RemovePushDevice(first.Id, Viewer));
            Assert.Equal(0, store.CountPushDevices());
        }
    }

    [Fact]
    public void A_subscription_that_cannot_work_is_refused_in_words()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            Assert.Contains("push address", Assert.Throws<InvalidOperationException>(() => store.RegisterPushDevice(Admin, "http://push.example/1", Key, Auth, null)).Message);
            Assert.Contains("keys", Assert.Throws<InvalidOperationException>(() => store.RegisterPushDevice(Admin, "https://push.example/1", "abc", Auth, null)).Message);
        }
    }

    [Fact]
    public void A_user_group_reaches_its_members_devices_and_an_email_receiver_the_users_with_that_address()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RegisterPushDevice(Admin, "https://push.example/admin", Key, Auth, null);
            store.RegisterPushDevice(Viewer, "https://push.example/viewer", Key, Auth, null);

            Assert.Equal(2, store.ResolvePushDevices(NotificationReceiverDefaults.AllUsersReceiverId, null).Count);
            Assert.Equal("https://push.example/admin", Assert.Single(store.ResolvePushDevices(NotificationReceiverDefaults.AllAdminsReceiverId, null)).Endpoint);
            Assert.Equal("https://push.example/viewer", Assert.Single(store.ResolvePushDevices(null, "nobody@example.com, VIEWER@example.com")).Endpoint);
        }
    }

    [Fact]
    public void Devices_never_ride_a_backup_and_go_with_their_user()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RegisterPushDevice(Viewer, "https://push.example/viewer", Key, Auth, null);
            var package = Encoding.UTF8.GetString(store.CreateBackupBytes(WorkspaceBackupSection.All));
            Assert.DoesNotContain("push.example", package);

            store.DeleteUser(Viewer);
            Assert.Equal(0, store.CountPushDevices());
        }
    }

    [Fact]
    public void An_existing_workspace_gets_the_browser_sender_and_its_rule_once()
    {
        var (store, telemetry) = Boot(schemaVersion: 9);
        using (telemetry) using (store)
        {
            var sender = Assert.Single(store.Workspace.NotificationSenders, candidate => candidate.Kind == NotificationEndpointKind.Push);
            var rule = Assert.Single(store.Workspace.NotificationRules, candidate => candidate.SenderId == sender.Id);
            Assert.Equal(NotificationReceiverDefaults.AllUsersReceiverId, rule.ReceiverId);
            Assert.True(rule.Enabled);
            Assert.Contains(SensorState.Critical, rule.TriggerStates);
        }
    }

    [Fact]
    public void A_browser_rule_without_registered_devices_says_why_and_with_them_builds_a_notification()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            var factory = new NotificationDeliveryFactory(store);
            var sender = store.Workspace.NotificationSenders.Single(candidate => candidate.Kind == NotificationEndpointKind.Push);
            var route = new NotificationRoute("Browser notifications", sender.Id, NotificationReceiverDefaults.AllUsersReceiverId, null, null, null, null, null);
            var context = new NotificationTemplateContext();
            context.SetValue("state.label", "Critical");
            context.SetValue("state.urgent", "1");
            context.SetValue("element.name", "UPS");
            context.SetValue("element.path", "Primary Probe / UPS");
            context.SetValue("element.id", SensorId.ToString());
            context.SetValue("element.kind", "Sensor");
            context.SetValue("alert.id", "42");
            context.SetValue("message", "on battery");

            var (none, problem) = factory.Create(route, context, store.GetWorkspaceClone());
            Assert.Null(none);
            Assert.Contains("nobody", problem);

            store.RegisterPushDevice(Admin, "https://push.example/admin", Key, Auth, null);
            var (delivery, _) = factory.Create(route, context, store.GetWorkspaceClone());

            Assert.Equal(NotificationTransport.Push, delivery!.Transport);
            Assert.Single(delivery.PushDevices);
            var notification = delivery.PushNotification!;
            Assert.Equal($"/monitoring/sensor/{SensorId}", notification.Url);
            Assert.Equal("alert-42", notification.Tag);
            Assert.True(notification.Urgent);
            Assert.DoesNotContain(notification.Title, notification.Body); // the subject is not said twice
            Assert.Contains("on battery", notification.Body);
        }
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", "Safari on iPhone")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0", "Edge on Windows")]
    [InlineData(null, "Browser")]
    public void A_device_is_named_after_its_browser_and_system(string? userAgent, string expected) =>
        Assert.Equal(expected, PushDeviceNames.FromUserAgent(userAgent));
}

file sealed class PushTestEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
