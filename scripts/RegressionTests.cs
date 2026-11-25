#if GATEWAY_REGRESSION_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuanxinGateway
{
    internal static class RegressionTests
    {
        private static int failures;

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            using (Form runner = new Form { Opacity = 0, ShowInTaskbar = false })
            {
                runner.Shown += async (sender, args) =>
                {
                    try
                    {
                        await RestoreDuringRefresh();
                        await FailedRestoreCanBeRetried();
                        await FailedSnapshotCanBeRetried();
                        CommandErrorsAreReadable();
                        NativeStructLayoutMatchesWindowsSdk();
                        ParseSavedRoutesFiltersTargetAndHandlesSingleObject();
                        MetricFilterIgnoresHighMetricRaRoutes();
                        VerifyAppliedFamilyIgnoresHighMetricRaRoutes();
                        NormalizeBackupRemovesTargetDns();
                        RouteRepairWaitsForStability();
                        RouteRepairStopsSafely();
                        DnsProbeValidatesResponses();
                        AutoStartArgumentsRoundTrip();
                        BackupIdentityCannotFallBackToAnotherAdapter();
                        await DisconnectedNetworkRestoresSafely();
                        await UnknownNetworkDoesNotRestore();
                        await DriftIsRepairedWithoutEnable();
                        await HealthFailuresAreConsecutive();
                        await HealthFallbackStopsAutomaticEnable();
                        Console.WriteLine("PASS: all gateway regression tests");
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Console.Error.WriteLine(ex);
                    }
                    finally { runner.Close(); }
                };
                Application.Run(runner);
            }
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        private static MainForm CreateForm(FakeOperations operations, Action<string> errors)
        {
            MainForm form = new MainForm(operations, errors);
            Get<System.Windows.Forms.Timer>(form, "refreshTimer").Stop();
            IntPtr handle = form.Handle;
            return form;
        }

        private static T Get<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        private static void Refresh(MainForm form)
        {
            typeof(MainForm).GetMethod("RefreshTimer_Tick", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(form, new object[] { form, EventArgs.Empty });
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static async Task Within(Task task)
        {
            Assert(await Task.WhenAny(task, Task.Delay(5000)) == task, "Test operation timed out.");
            await task;
        }

        private static async Task RestoreDuringRefresh()
        {
            FakeOperations operations = new FakeOperations { BlockFirstRead = true, BlockRestore = true };
            string error = null;
            using (MainForm form = CreateForm(operations, message => error = message))
            {
                Set(form, "pendingSsid", "test");
                Set(form, "pendingSsidSince", DateTime.UtcNow.AddSeconds(-20));
                Set(form, "lastObservationAt", DateTime.UtcNow);
                Refresh(form);
                await Within(operations.ReadStarted.Task);
                Assert(Get<Button>(form, "powerButton").Enabled, "Refresh disabled the power button.");

                Task action = form.ExecutePowerActionAsync();
                await form.ExecutePowerActionAsync();
                Assert(Get<bool>(form, "isProcessing"), "Manual click was ignored during refresh.");
                Assert(Get<Label>(form, "routeStateLabel").Text == "正在核对网络状态", "Click has no immediate visual feedback.");
                Assert(operations.ReadCount == 1, "Manual action started a competing snapshot read.");
                operations.ReadGate.Set();
                await Within(operations.RestoreStarted.Task);
                Assert(Get<bool>(form, "isProcessing"), "Refresh cleared the manual operation's busy state.");
                Assert(!Get<Button>(form, "powerButton").Enabled, "Refresh re-enabled a busy button.");
                Assert(Get<Label>(form, "routeStateLabel").Text == "正在恢复原始网络", "Snapshot hid the restore progress.");
                operations.RestoreGate.Set();
                await Within(action);
                Assert(error == null, "Restore failed: " + error);
                Assert(operations.RestoreCount == 1 && operations.EnableCount == 0, "Restore was duplicated or automatic enable raced it.");
                Assert(!Get<bool>(form, "hasOriginalConfig"), "Successful restore did not clear pending restore UI.");
                Assert(Get<Button>(form, "powerButton").Enabled, "Button stayed disabled after restore.");
                Assert(Get<Label>(form, "routeStateLabel").Text.Contains("未启用旁路由"), "Restored state was not displayed.");
                Console.WriteLine("PASS: restore click during refresh, immediate feedback, no duplicate calls, correct final UI");
            }
        }

        private static async Task FailedRestoreCanBeRetried()
        {
            FakeOperations operations = new FakeOperations { FailRestore = true };
            string error = null;
            using (MainForm form = CreateForm(operations, message => error = message))
            {
                await Within(form.ExecutePowerActionAsync());
                Assert(error != null && error.Contains("test restore failure"), "Restore error was not surfaced.");
                Assert(Get<bool>(form, "hasOriginalConfig"), "Failed restore lost its backup state.");
                Assert(Get<Button>(form, "powerButton").Enabled, "Failed restore left button disabled.");
                Assert(Get<Label>(form, "detailLabel").Text.Contains("test restore failure"), "Error was not retained in the UI.");
                operations.FailRestore = false;
                error = null;
                await Within(form.ExecutePowerActionAsync());
                Assert(error == null && !operations.HasOriginal, "Restore could not be retried.");
                Console.WriteLine("PASS: restore error is visible, backup retained, retry succeeds");
            }
        }

        private static async Task FailedSnapshotCanBeRetried()
        {
            FakeOperations operations = new FakeOperations { FailRead = true };
            string error = null;
            using (MainForm form = CreateForm(operations, message => error = message))
            {
                await Within(form.ExecutePowerActionAsync());
                Assert(error != null, "Snapshot error was swallowed.");
                Assert(Get<Button>(form, "powerButton").Enabled, "Snapshot error left button disabled.");
                operations.FailRead = false;
                error = null;
                await Within(form.ExecutePowerActionAsync());
                Assert(error == null && !operations.HasOriginal, "A failed shared snapshot was cached forever.");
                Console.WriteLine("PASS: snapshot error is visible and manual restore can be retried");
            }
        }

        private static object ManagerCall(string name, params object[] args)
        {
            try
            {
                return typeof(GatewayManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, args);
            }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }

        private static object Original(bool contaminated)
        {
            Type type = typeof(GatewayManager).GetNestedType("OriginalConfig", BindingFlags.NonPublic);
            object original = Activator.CreateInstance(type, true);
            type.GetField("Gateway").SetValue(original, contaminated ? "192.168.3.187" : "192.168.3.1");
            type.GetField("TargetGateway").SetValue(original, "192.168.3.187");
            type.GetField("TargetGatewayIpv6").SetValue(original, "fe80::187");
            type.GetField("DnsServers").SetValue(original, new List<string>(contaminated
                ? new[] { "192.168.3.187", "fe80::187" } : new[] { "192.168.3.1" }));
            type.GetField("IsDnsDhcp").SetValue(original, !contaminated);
            type.GetField("IsIpv6DnsDhcp").SetValue(original, !contaminated);
            string json = "[{\"PolicyStore\":\"ActiveStore\",\"DestinationPrefix\":\"0.0.0.0/0\",\"NextHop\":\"192.168.3.1\",\"RouteMetric\":0},"
                + "{\"PolicyStore\":\"ActiveStore\",\"DestinationPrefix\":\"::/0\",\"NextHop\":\"fe80::1\",\"RouteMetric\":16}";
            if (contaminated)
                json += ",{\"PolicyStore\":\"PersistentStore\",\"DestinationPrefix\":\"0.0.0.0/0\",\"NextHop\":\"192.168.3.187\",\"RouteMetric\":1},"
                    + "{\"PolicyStore\":\"ActiveStore\",\"DestinationPrefix\":\"0.0.0.0/0\",\"NextHop\":\"192.168.3.187\",\"RouteMetric\":1}";
            type.GetField("RoutesJson").SetValue(original, json + "]");
            ManagerCall("NormalizeBackup", original);
            return original;
        }

        private const string NetworkStubs = @"
$script:mockRoutes=@(
 [pscustomobject]@{PolicyStore='PersistentStore';InterfaceIndex=12;DestinationPrefix='0.0.0.0/0';NextHop='192.168.3.187';RouteMetric=1},
 [pscustomobject]@{PolicyStore='ActiveStore';InterfaceIndex=12;DestinationPrefix='0.0.0.0/0';NextHop='192.168.3.187';RouteMetric=1},
 [pscustomobject]@{PolicyStore='ActiveStore';InterfaceIndex=12;DestinationPrefix='::/0';NextHop='fe80::187';RouteMetric=1},
 [pscustomobject]@{PolicyStore='ActiveStore';InterfaceIndex=88;DestinationPrefix='0.0.0.0/0';NextHop='192.168.3.187';RouteMetric=1}
);
$script:deletes=@();$script:dnsCalls=@();
function Get-NetRoute {
 [CmdletBinding()]param($PolicyStore)
 $script:mockRoutes | Where-Object { $_.PolicyStore -eq $PolicyStore }
}
function Remove-NetRoute {
 [CmdletBinding(SupportsShouldProcess=$true)]param($PolicyStore,$InterfaceIndex,$DestinationPrefix,$NextHop)
 if($script:denyDelete){throw 'test: route deletion denied'}
 $script:deletes+=($PolicyStore+':'+$NextHop);
 $script:mockRoutes=@($script:mockRoutes | Where-Object {
   -not ($_.InterfaceIndex -eq $InterfaceIndex -and $_.DestinationPrefix -eq $DestinationPrefix -and $_.NextHop -eq $NextHop -and $_.PolicyStore -eq $PolicyStore)
 });
}
function New-NetRoute {
 [CmdletBinding()]param($PolicyStore='ActiveStore',$InterfaceIndex,$DestinationPrefix,$NextHop,$RouteMetric)
 $script:mockRoutes += [pscustomobject]@{PolicyStore=$PolicyStore;InterfaceIndex=$InterfaceIndex;DestinationPrefix=$DestinationPrefix;NextHop=$NextHop;RouteMetric=$RouteMetric}
}
function Set-NetRoute {
 [CmdletBinding(SupportsShouldProcess=$true)]param([Parameter(ValueFromPipeline=$true)]$InputObject,$RouteMetric)
 process {$InputObject.RouteMetric=$RouteMetric}
}
function Set-DnsClientServerAddress {
 [CmdletBinding(SupportsShouldProcess=$true)]param([Parameter(ValueFromPipeline=$true)]$InputObject,$ServerAddresses,[switch]$ResetServerAddresses)
 process {$script:dnsCalls+=($InputObject.AddressFamilyName + ':' + $(if($ResetServerAddresses){'dhcp'}else{$ServerAddresses -join ','}))}
}
function Get-DnsClientServerAddress {
 [CmdletBinding()]param($InterfaceIndex,$AddressFamily)
 [pscustomobject]@{InterfaceIndex=$InterfaceIndex;AddressFamilyName=$AddressFamily;AddressFamily=if($AddressFamily -eq 'IPv4'){2}else{23};ServerAddresses=@()}
}
function Clear-DnsClientCache {[CmdletBinding()]param()}
function Register-DnsClient {[CmdletBinding()]param()}
";

        private static void CommandErrorsAreReadable()
        {
            Assert((string)ManagerCall("CleanCommandError", "plain <test> error", "") == "plain <test> error",
                "Plain command errors were not preserved.");
            Assert((string)ManagerCall("CleanCommandError", "  ", "") == "命令执行失败。",
                "Empty command error did not fall back to default message.");
            Console.WriteLine("PASS: native command errors preserved");
        }

        private static void NativeStructLayoutMatchesWindowsSdk()
        {
            int rowSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeNetwork.MIB_IPFORWARD_ROW2));
            int inetSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeNetwork.SOCKADDR_INET));
            Assert(inetSize == 28, "SOCKADDR_INET size mismatch: " + inetSize);
            Assert(rowSize == 104, "MIB_IPFORWARD_ROW2 size mismatch: " + rowSize);
            List<RouteEntry> live = NativeNetwork.GetDefaultRoutes(0);
            Assert(live != null, "GetDefaultRoutes returned null.");
            Console.WriteLine("PASS: native MIB_IPFORWARD_ROW2 layout (104 bytes) and live GetIpForwardTable2 query succeeded");
        }

        private static void ParseSavedRoutesFiltersTargetAndHandlesSingleObject()
        {
            object original = Original(true);
            System.Collections.IList parsed = (System.Collections.IList)ManagerCall("ParseSavedRoutes", original);
            Assert(parsed.Count == 2, "Contaminated target routes were not filtered: " + parsed.Count);

            original.GetType().GetField("RoutesJson").SetValue(original,
                "{\"PolicyStore\":\"ActiveStore\",\"DestinationPrefix\":\"0.0.0.0/0\",\"NextHop\":\"192.168.3.1\",\"RouteMetric\":0}");
            System.Collections.IList single = (System.Collections.IList)ManagerCall("ParseSavedRoutes", original);
            Assert(single.Count == 1, "Single-object RoutesJson from legacy PowerShell backup failed to parse.");
            Console.WriteLine("PASS: ParseSavedRoutes filters target routes and handles legacy single-object JSON");
        }

        private static void MetricFilterIgnoresHighMetricRaRoutes()
        {
            List<RouteEntry> routes = new List<RouteEntry>
            {
                new RouteEntry { Family = System.Net.Sockets.AddressFamily.InterNetwork, DestinationPrefix = "0.0.0.0/0", NextHop = "192.168.30.153", Metric = 0 },
                new RouteEntry { Family = System.Net.Sockets.AddressFamily.InterNetworkV6, DestinationPrefix = "::/0", NextHop = "fe80::20c:29ff:fe2b:cd7d", Metric = 0 },
                new RouteEntry { Family = System.Net.Sockets.AddressFamily.InterNetworkV6, DestinationPrefix = "::/0", NextHop = "fe80::92f6:44ff:fe68:2290", Metric = 4096 }
            };
            Assert(!GatewayManager.HasCompetingDefaultRoute(routes, "192.168.30.153", "fe80::20c:29ff:fe2b:cd7d"),
                "High-metric RA route (4096) was falsely treated as competing drift.");

            routes.Add(new RouteEntry { Family = System.Net.Sockets.AddressFamily.InterNetwork, DestinationPrefix = "0.0.0.0/0", NextHop = "192.168.30.1", Metric = 0 });
            Assert(GatewayManager.HasCompetingDefaultRoute(routes, "192.168.30.153", "fe80::20c:29ff:fe2b:cd7d"),
                "Equal-metric (0) competing route was not detected.");
            Console.WriteLine("PASS: metric-based drift filter ignores high-metric RA routes and catches metric 0 conflicts");
        }

        private static void VerifyAppliedFamilyIgnoresHighMetricRaRoutes()
        {
            List<RouteEntry> routes = new List<RouteEntry>
            {
                new RouteEntry { Family = System.Net.Sockets.AddressFamily.InterNetworkV6, DestinationPrefix = "::/0", NextHop = "fe80::20c:29ff:fe2b:cd7d", Metric = 0 },
                new RouteEntry { Family = System.Net.Sockets.AddressFamily.InterNetworkV6, DestinationPrefix = "::/0", NextHop = "fe80::92f6:44ff:fe68:2290", Metric = 4096 }
            };
            List<string> dns = new List<string> { "fe80::20c:29ff:fe2b:cd7d" };
            ManagerCall("VerifyAppliedFamily", routes, dns, "::/0", System.Net.Sockets.AddressFamily.InterNetworkV6, "fe80::20c:29ff:fe2b:cd7d", "IPv6");

            routes.Add(new RouteEntry { Family = System.Net.Sockets.AddressFamily.InterNetworkV6, DestinationPrefix = "::/0", NextHop = "fe80::1", Metric = 0 });
            bool rejected = false;
            try
            {
                ManagerCall("VerifyAppliedFamily", routes, dns, "::/0", System.Net.Sockets.AddressFamily.InterNetworkV6, "fe80::20c:29ff:fe2b:cd7d", "IPv6");
            }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Equal-metric competing IPv6 route was not rejected by VerifyAppliedFamily.");
            Console.WriteLine("PASS: VerifyAppliedFamily allows high-metric RA routes and rejects metric 0 conflicts");
        }

        private static void NormalizeBackupRemovesTargetDns()
        {
            object original = Original(true);
            Type type = original.GetType();
            type.GetField("BackupVersion").SetValue(original, 2);
            List<string> dns = new List<string> { "192.168.3.187", "fe80::187", "192.168.3.1" };
            type.GetField("DnsServers").SetValue(original, dns);
            type.GetField("IsDnsDhcp").SetValue(original, false);
            type.GetField("IsIpv6DnsDhcp").SetValue(original, false);
            ManagerCall("NormalizeBackup", original);
            Assert(dns.Count == 1 && dns[0] == "192.168.3.1", "Bypass DNS remained in a version 2 backup.");
            Assert(!(bool)type.GetField("IsDnsDhcp").GetValue(original), "Original static IPv4 DNS was changed to DHCP.");
            Assert((bool)type.GetField("IsIpv6DnsDhcp").GetValue(original), "Empty IPv6 DNS was not reset to DHCP.");
            Console.WriteLine("PASS: backup normalization removes bypass DNS consistently across backup versions");
        }

        private static void RouteRepairWaitsForStability()
        {
            int repairs = 0, observations = 0, verified = 0, recorded = 0, waits = 0;
            GatewayManager.RunStableRouteRepair(() => { }, () => repairs++,
                () => ++observations > 1, () => verified++, phase => recorded++, () => waits++);
            Assert(repairs == 2 && observations == 4 && verified == 1 && recorded == 4 && waits == 4,
                "Transient reinjection did not retry and require three stable observations.");

            repairs = 0;
            bool failed = false;
            try
            {
                GatewayManager.RunStableRouteRepair(() => { }, () => repairs++, () => false,
                    () => { throw new Exception("Must not verify unstable routes"); }, phase => { }, () => { });
            }
            catch (InvalidOperationException) { failed = true; }
            Assert(failed && repairs == 3, "Persistent drift was not limited to three repairs.");

            repairs = 0;
            verified = 0;
            bool lateConflict = false;
            GatewayManager.RunStableRouteRepair(() => { }, () => { repairs++; lateConflict = false; },
                () => !lateConflict,
                () =>
                {
                    if (++verified == 1)
                    {
                        lateConflict = true;
                        throw new InvalidOperationException("IPv6 default route reinjected during verification");
                    }
                }, phase => { }, () => { });
            Assert(repairs == 2 && verified == 2, "Late reinjection was not retried.");
            Console.WriteLine("PASS: transient/late route reinjection waits for stability, persistent drift is bounded");
        }

        private static void RouteRepairStopsSafely()
        {
            int repairs = 0, checks = 0;
            bool failed = false;
            try
            {
                GatewayManager.RunStableRouteRepair(
                    () => { if (++checks == 2) throw new InvalidOperationException("network changed"); },
                    () => repairs++, () => true, () => { }, phase => { }, () => { });
            }
            catch (InvalidOperationException) { failed = true; }
            Assert(failed && repairs == 1, "Network identity change did not stop retries.");
            failed = false;
            repairs = 0;
            try
            {
                GatewayManager.RunStableRouteRepair(() => { },
                    () => { repairs++; throw new UnauthorizedAccessException("denied"); },
                    () => true, () => { }, phase => { }, () => { });
            }
            catch (UnauthorizedAccessException) { failed = true; }
            Assert(failed && repairs == 1, "Permission errors were retried.");
            failed = false;
            repairs = 0;
            try
            {
                GatewayManager.RunStableRouteRepair(() => { }, () => repairs++, () => true,
                    () => { throw new InvalidOperationException("DNS mismatch"); }, phase => { }, () => { });
            }
            catch (InvalidOperationException) { failed = true; }
            Assert(failed && repairs == 1, "Non-route verification errors were retried.");
            Console.WriteLine("PASS: network changes, permissions and DNS errors abort route retries");
        }

        private static async Task RefreshAndWait(MainForm form)
        {
            Refresh(form);
            await Within(Task.Run(async () =>
            {
                while (Get<bool>(form, "isRefreshing")) await Task.Delay(10);
            }));
        }

        private static void BackupIdentityCannotFallBackToAnotherAdapter()
        {
            object original = Original(false);
            Type type = original.GetType();
            type.GetField("InterfaceId").SetValue(original, "{00000000-0000-0000-0000-000000000000}");
            type.GetField("InterfaceAlias").SetValue(original, "WLAN");
            type.GetField("BackupVersion").SetValue(original, 2);
            type.GetField("NetworkAddress").SetValue(original, "192.168.3.0/255.255.255.0");
            WifiConfig current = new WifiConfig
            {
                InterfaceId = "different-interface", InterfaceAlias = "WLAN", IsConnected = true,
                NetworkAddress = "192.168.3.0/255.255.255.0"
            };
            Assert(!(bool)ManagerCall("IsSameNetwork", original, current), "Reused adapter alias overrode saved GUID.");
            Assert(ManagerCall("GetOriginalNetwork", original, null, null) == null,
                "Missing saved adapter fell back to another interface.");
            current.InterfaceId = (string)type.GetField("InterfaceId").GetValue(original);
            current.IsConnected = false;
            Assert(!(bool)ManagerCall("IsSameNetwork", original, current), "Disconnected adapter considered original connected network.");
            Console.WriteLine("PASS: saved GUID is authoritative, no fallback to another adapter, disconnected identity rejected");
        }

        private static void PrimeNetwork(MainForm form, FakeOperations operations)
        {
            string key = operations.Ssid ?? "disconnected";
            Set(form, "pendingSsid", key);
            Set(form, "pendingSsidSince", DateTime.UtcNow.AddSeconds(-20));
            Set(form, "lastObservationAt", DateTime.UtcNow);
            Set(form, "lastNetworkKey", key);
            Set(form, "lastSsid", operations.Ssid ?? "test");
            Set(form, "nextHealthCheck", DateTime.MinValue);
        }

        private static async Task DisconnectedNetworkRestoresSafely()
        {
            FakeOperations operations = new FakeOperations { Ssid = null, AutoEnable = false };
            using (MainForm form = CreateForm(operations, message => { }))
            {
                PrimeNetwork(form, operations);
                await RefreshAndWait(form);
                Assert(operations.RestoreCount == 1 && operations.EnableCount == 0,
                    "Known disconnection did not restore when automatic enable was disabled.");
            }
            Console.WriteLine("PASS: disconnected original adapter restores independently of automatic enable");
        }

        private static async Task UnknownNetworkDoesNotRestore()
        {
            FakeOperations operations = new FakeOperations { Ssid = null, NetworkKnown = false };
            using (MainForm form = CreateForm(operations, message => { }))
            {
                PrimeNetwork(form, operations);
                await RefreshAndWait(form);
                Assert(operations.RestoreCount == 0 && operations.RepairCount == 0,
                    "Unknown Wi-Fi identity caused network changes.");
                Assert(Get<string>(form, "pendingSsid") == null, "Unknown state did not reset debounce.");
                operations.NetworkKnown = true;
                await RefreshAndWait(form);
                Assert(operations.RestoreCount == 0, "Disconnection skipped the stability delay.");
            }
            Console.WriteLine("PASS: unknown Wi-Fi does not mutate routes; confirmed disconnect is debounced");
        }

        private static async Task DriftIsRepairedWithoutEnable()
        {
            FakeOperations operations = new FakeOperations
            {
                IsActive = true, CanMaintain = true, NeedsRestore = false, Drift = true, ConfigSsid = "test"
            };
            using (MainForm form = CreateForm(operations, message => { }))
            {
                PrimeNetwork(form, operations);
                await RefreshAndWait(form);
                Assert(operations.RepairCount == 1 && operations.EnableCount == 0 && operations.RestoreCount == 0,
                    "Drift repair toggled the network instead of maintaining it.");
                Assert(operations.HasOriginal, "Route repair lost its backup.");
                operations.Drift = true;
                await RefreshAndWait(form);
                Assert(operations.RepairCount == 2, "A later DHCP injection was not repaired.");
            }
            Console.WriteLine("PASS: repeated route drift repaired without re-enabling or overwriting backup");
        }

        private static async Task HealthFailuresAreConsecutive()
        {
            FakeOperations operations = new FakeOperations
            {
                IsActive = true, CanMaintain = true, NeedsRestore = false, Healthy = false, ConfigSsid = "test"
            };
            using (MainForm form = CreateForm(operations, message => { }))
            {
                PrimeNetwork(form, operations);
                await RefreshAndWait(form);
                Assert(operations.HealthCount == 1 && operations.RestoreCount == 0, "One failed probe switched networks.");
                await RefreshAndWait(form);
                Assert(operations.HealthCount == 1, "Health probe ignored its rate limit.");
                operations.Healthy = true;
                Set(form, "nextHealthCheck", DateTime.MinValue);
                await RefreshAndWait(form);
                Assert(Get<int>(form, "gatewayHealthFailureCount") == 0, "A healthy round did not reset failure count.");
                operations.Healthy = false;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    Set(form, "nextHealthCheck", DateTime.MinValue);
                    await RefreshAndWait(form);
                }
                Assert(operations.RestoreCount == 0, "Non-consecutive failures triggered fallback.");
            }
            Console.WriteLine("PASS: health checks rate limited, successes reset failures, no premature fallback");
        }

        private static async Task HealthFallbackStopsAutomaticEnable()
        {
            FakeOperations operations = new FakeOperations
            {
                IsActive = true, CanMaintain = true, NeedsRestore = false, Healthy = false, ConfigSsid = "test"
            };
            using (MainForm form = CreateForm(operations, message => { }))
            {
                PrimeNetwork(form, operations);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Set(form, "nextHealthCheck", DateTime.MinValue);
                    await RefreshAndWait(form);
                }
                Assert(operations.RestoreCount == 1 && !operations.HasOriginal, "Three failures did not restore.");
                await RefreshAndWait(form);
                Assert(operations.EnableCount == 0 && Get<bool>(form, "userManuallyDisabled"),
                    "Automatic enable undid the health fallback.");
            }
            Console.WriteLine("PASS: three failed health rounds restore once and inhibit automatic re-enable");
        }

        private static void DnsProbeValidatesResponses()
        {
            byte[] query = { 17, 22, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1 };
            byte[] reply = (byte[])query.Clone();
            reply[2] = 0x81;
            reply[3] = 0x83;
            Assert((bool)ManagerCall("IsHealthyDnsReply", query, reply), "NXDOMAIN recursion response rejected.");
            reply[3] = 0x82;
            Assert(!(bool)ManagerCall("IsHealthyDnsReply", query, reply), "SERVFAIL considered healthy.");
            reply[3] = 0x83;
            reply[0]++;
            Assert(!(bool)ManagerCall("IsHealthyDnsReply", query, reply), "Unrelated DNS transaction accepted.");
            Assert(!(bool)ManagerCall("IsHealthyDnsReply", query, new byte[5]), "Truncated DNS response accepted.");
            Console.WriteLine("PASS: DNS health accepts valid recursion and rejects failures/unrelated/truncated packets");
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static void AutoStartArgumentsRoundTrip()
        {
            string path = @"C:\Program Files\元信旁路由\GatewaySwitch.exe";
            string action = (string)ManagerCall("QuoteWindowsCommandLineArgument", path) + " --minimized";
            string command = "schtasks.exe /create /tr " + (string)ManagerCall("QuoteWindowsCommandLineArgument", action) + " /sc onlogon";
            int count;
            IntPtr arguments = CommandLineToArgvW(command, out count);
            Assert(arguments != IntPtr.Zero, "Command line parsing failed.");
            try
            {
                string parsed = System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(arguments, 3 * IntPtr.Size));
                Assert(parsed == "\"" + path + "\" --minimized", "Scheduled task action did not preserve executable quoting.");
            }
            finally { LocalFree(arguments); }
            Assert((string)ManagerCall("QuoteWindowsCommandLineArgument", @"C:\folder\") == "\"C:\\folder\\\\\"",
                "Trailing slash escaped the closing quote.");
            Console.WriteLine("PASS: Windows parses scheduled action with spaces, Unicode and nested quotes correctly");
        }
        private sealed class FakeOperations : IGatewayOperations
        {
            public bool BlockFirstRead;
            public bool BlockRestore;
            public bool FailRead;
            public bool FailRestore;
            public bool HasOriginal = true;
            public bool IsActive;
            public bool AutoEnable = true;
            public bool NetworkKnown = true;
            public bool NeedsRestore = true;
            public bool CanMaintain;
            public bool Drift;
            public bool Healthy = true;
            public string Ssid = "test";
            public string ConfigSsid = "different-network";
            public int ReadCount;
            public int RestoreCount;
            public int EnableCount;
            public int RepairCount;
            public int HealthCount;
            public readonly ManualResetEventSlim ReadGate = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim RestoreGate = new ManualResetEventSlim(false);
            public readonly TaskCompletionSource<bool> ReadStarted = new TaskCompletionSource<bool>();
            public readonly TaskCompletionSource<bool> RestoreStarted = new TaskCompletionSource<bool>();

            public NetworkSnapshot GetSnapshot()
            {
                int count = Interlocked.Increment(ref ReadCount);
                if (count == 1)
                {
                    ReadStarted.TrySetResult(true);
                    if (BlockFirstRead && !ReadGate.Wait(4000)) throw new TimeoutException("Read gate timed out.");
                }
                if (FailRead) throw new InvalidOperationException("test snapshot failure");
                return new NetworkSnapshot
                {
                    Network = new WifiConfig { InterfaceAlias = "WLAN", IsWifi = true, IsConnected = Ssid != null },
                    Ssid = Ssid,
                    ConfigSsid = ConfigSsid,
                    AutoEnable = AutoEnable,
                    GatewayIp = "192.168.3.187",
                    HasOriginal = HasOriginal,
                    IsActive = IsActive,
                    NetworkStateKnown = NetworkKnown,
                    NetworkKey = Ssid ?? "disconnected",
                    NeedsRestore = NeedsRestore,
                    CanMaintain = CanMaintain,
                    HasCompetingDefaultRoute = Drift
                };
            }

            public void Enable(string targetGateway) { Interlocked.Increment(ref EnableCount); }

            public void RepairTarget(string targetGateway)
            {
                Interlocked.Increment(ref RepairCount);
                Drift = false;
                IsActive = true;
            }

            public bool CheckHealth(string targetGateway)
            {
                Interlocked.Increment(ref HealthCount);
                return Healthy;
            }

            public void Restore(string targetGateway)
            {
                Interlocked.Increment(ref RestoreCount);
                RestoreStarted.TrySetResult(true);
                if (BlockRestore && !RestoreGate.Wait(4000)) throw new TimeoutException("Restore gate timed out.");
                if (FailRestore) throw new InvalidOperationException("test restore failure");
                HasOriginal = false;
                IsActive = false;
            }
        }
    }
}
#endif
