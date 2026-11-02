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
                        RestoreScriptsRemoveBothStores(false);
                        RestoreScriptsRemoveBothStores(true);
                        RestoreDoesNotReintroduceTargetFromCleanBackup();
                        RestorePreservesPersistentRouteStore();
                        RestoreDeletionFailureIsReported();
                        UnsafeBackupIsRejected();
                        RouteGuardHandlesDhcp();
                        RouteGuardPreservesFallbackOnFailure();
                        DnsProbeValidatesResponses();
                        AutoStartArgumentsRoundTrip();
                        RestoreAfterDisconnectDoesNotCopyOldRoutes();
                        BackupIdentityCannotFallBackToAnotherAdapter();
                        Ipv4RestoreDoesNotAdoptNewIpv6Settings();
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
            ManagerCall("RepairTargetBackup", original);
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
function netsh {$script:dnsCalls+=($args -join ' ');$global:LASTEXITCODE=0}
function Clear-DnsClientCache {[CmdletBinding()]param()}
function Register-DnsClient {[CmdletBinding()]param()}
";

        private static void CommandErrorsAreReadable()
        {
            string xml = "#< CLIXML\r\n<Objs xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
                + "<Obj S=\"progress\"><S>loading module</S></Obj>"
                + "<S S=\"Error\">route &lt;test&gt; denied_x000D__x000A_</S>"
                + "<S S=\"Error\">second line &amp; details</S></Objs>";
            string error = (string)ManagerCall("CleanCommandError", xml, "");
            Assert(error == "route <test> denied\r\nsecond line & details", "CLIXML errors were not decoded correctly.");
            Assert((string)ManagerCall("CleanCommandError", "plain <test> error", "") == "plain <test> error",
                "Plain command errors were stripped as XML.");
            Assert(!(ManagerCall("CleanCommandError", "#< CLIXML\n<Objs broken", "") as string).Contains("<Objs"),
                "Malformed CLIXML was displayed raw.");
            Console.WriteLine("PASS: CLIXML errors decoded, progress excluded, plain errors preserved");
        }

        private static void RestoreScriptsRemoveBothStores(bool contaminated)
        {
            object original = Original(contaminated);
            string restore = (string)ManagerCall("BuildRestoreCommand", original, 12, true);
            string verify = (string)ManagerCall("BuildVerifyRoutesCommand", original, 12, true);
            string assertions = @"
if($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 12 -and (Normalize-Address $_.NextHop) -in @('192.168.3.187','fe80::187')}){throw 'target route restored from backup'}
if(-not ($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 12 -and $_.NextHop -eq '192.168.3.1' -and $_.RouteMetric -eq 0})){throw ('original gateway or metric not restored: ' + ($script:mockRoutes | ConvertTo-Json -Compress))}
if(-not ($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 12 -and (Normalize-Address $_.NextHop) -eq 'fe80::1' -and $_.RouteMetric -eq 16})){throw 'original IPv6 gateway not restored'}
if(-not ($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 88})){throw 'unrelated interface changed'}
if($script:deletes[0] -ne 'PersistentStore:192.168.3.187'){throw 'persistent route not removed first'}
if(@($script:dnsCalls | Where-Object {$_ -like '*source=dhcp*'}).Count -ne 2){throw 'DHCP DNS not restored for both families'}
";
            ManagerCall("RunPowerShell", NetworkStubs + restore + assertions + verify);
            Console.WriteLine("PASS: actual restore script, both stores/families, original metric, unrelated adapter; contaminated=" + contaminated);
        }

        private static void RestoreDeletionFailureIsReported()
        {
            string restore = (string)ManagerCall("BuildRestoreCommand", Original(false), 12, true);
            try
            {
                ManagerCall("RunPowerShell", NetworkStubs + "$script:denyDelete=$true;" + restore);
                throw new Exception("Route deletion failure was swallowed.");
            }
            catch (InvalidOperationException ex)
            {
                Assert(ex.Message.Contains("test: route deletion denied") && !ex.Message.Contains("<Objs"),
                    "Deletion failure was not readable: " + ex.Message);
            }
            Console.WriteLine("PASS: actual PowerShell deletion errors are not swallowed or shown as XML");
        }

        private static void RestoreDoesNotReintroduceTargetFromCleanBackup()
        {
            object original = Original(false);
            Type type = original.GetType();
            type.GetField("RoutesJson").SetValue(original,
                "[{\"PolicyStore\":\"ActiveStore\",\"DestinationPrefix\":\"0.0.0.0/0\",\"NextHop\":\"192.168.3.1\",\"RouteMetric\":0},"
                + "{\"PolicyStore\":\"ActiveStore\",\"DestinationPrefix\":\"0.0.0.0/0\",\"NextHop\":\"192.168.3.187\",\"RouteMetric\":1}]");
            string restore = (string)ManagerCall("BuildRestoreCommand", original, 12, true);
            ManagerCall("RunPowerShell", NetworkStubs + restore
                + "if($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 12 -and (Normalize-Address $_.NextHop) -eq '192.168.3.187'}){throw 'target route was restored from a clean backup'};");
            Console.WriteLine("PASS: restore filters target routes even when backup gateway is different");
        }

        private static void RestorePreservesPersistentRouteStore()
        {
            object original = Original(false);
            Type type = original.GetType();
            type.GetField("RoutesJson").SetValue(original,
                "[{\"PolicyStore\":\"PersistentStore\",\"DestinationPrefix\":\"0.0.0.0/0\",\"NextHop\":\"192.168.3.1\",\"RouteMetric\":7}]");
            string restore = (string)ManagerCall("BuildRestoreCommand", original, 12, true);
            ManagerCall("RunPowerShell", NetworkStubs + restore
                + "$persistent=$script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 12 -and $_.PolicyStore -eq 'PersistentStore' -and $_.NextHop -eq '192.168.3.1'};"
                + "if(-not $persistent -or $persistent.RouteMetric -ne 7){throw 'persistent original route was not restored to PersistentStore'};");
            Console.WriteLine("PASS: restore preserves PersistentStore routes and metrics");
        }

        private static void UnsafeBackupIsRejected()
        {
            object original = Original(true);
            original.GetType().GetField("RoutesJson").SetValue(original,
                "[{\"PolicyStore\":\"ActiveStore\",\"DestinationPrefix\":\"0.0.0.0/0\",\"NextHop\":\"192.168.3.187\",\"RouteMetric\":1}]");
            string restore = (string)ManagerCall("BuildRestoreCommand", original, 12, true);
            string script = NetworkStubs + "$rejected=$false;try{" + restore + "}catch{"
                + "if($_.Exception.Message -notlike '*无法确定原网关*'){throw};$rejected=$true};"
                + "if(-not $rejected -or $script:deletes.Count -ne 0){throw 'unsafe backup changed live routes'};";
            ManagerCall("RunPowerShell", script);
            Console.WriteLine("PASS: backup without a non-target gateway rejected before network changes");
        }


        private static async Task RefreshAndWait(MainForm form)
        {
            Refresh(form);
            await Within(Task.Run(async () =>
            {
                while (Get<bool>(form, "isRefreshing")) await Task.Delay(10);
            }));
        }

        private static void RestoreAfterDisconnectDoesNotCopyOldRoutes()
        {
            object original = Original(false);
            string restore = (string)ManagerCall("BuildRestoreCommand", original, 12, false);
            string verify = (string)ManagerCall("BuildVerifyRoutesCommand", original, 12, false);
            ManagerCall("RunPowerShell", NetworkStubs + restore + verify
                + "if($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 12}){throw 'old routes copied to disconnected/new network'};"
                + "if(-not ($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 88})){throw 'wired adapter changed'};"
                + "if(@($script:dnsCalls | Where-Object {$_ -like '*source=dhcp*'}).Count -ne 2){throw 'DNS not reset'};");
            Console.WriteLine("PASS: disconnected/changed network cleanup only removes owned routes on saved interface");
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

        private static void Ipv4RestoreDoesNotAdoptNewIpv6Settings()
        {
            object original = Original(false);
            original.GetType().GetField("TargetGatewayIpv6").SetValue(original, "");
            string restore = (string)ManagerCall("BuildRestoreCommand", original, 12, false);
            ManagerCall("RunPowerShell", NetworkStubs + restore
                + "if(-not ($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 12 -and $_.DestinationPrefix -eq '::/0'})){throw 'IPv4 restore removed unrelated IPv6 route'};"
                + "if($script:dnsCalls | Where-Object {$_ -like '*ipv6*'}){throw 'IPv4 restore changed IPv6 DNS'};");
            Console.WriteLine("PASS: restoring IPv4 backup does not adopt later IPv6 settings");
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

        private static void RouteGuardHandlesDhcp()
        {
            string repair = (string)ManagerCall("BuildRepairTargetCommand", 12, "192.168.3.187", "fe80::187");
            string detect = (string)ManagerCall("BuildRouteDriftCommand", 12, "192.168.3.187", "fe80::187");
            string injection = @"
$script:mockRoutes += @(
[pscustomobject]@{PolicyStore='ActiveStore';InterfaceIndex=12;DestinationPrefix='0.0.0.0/0';NextHop='192.168.3.1';RouteMetric=0},
[pscustomobject]@{PolicyStore='ActiveStore';InterfaceIndex=12;DestinationPrefix='::/0';NextHop='fe80::1';RouteMetric=0},
[pscustomobject]@{PolicyStore='ActiveStore';InterfaceIndex=12;DestinationPrefix='10.0.0.0/8';NextHop='192.168.3.1';RouteMetric=0}
);";
            string checks = @"
if($script:deletes | Where-Object {$_ -like '*192.168.3.187' -or $_ -like '*fe80::187'}){throw 'guard deleted working target route'}
if(-not ($script:mockRoutes | Where-Object {$_.InterfaceIndex -eq 88})){throw 'guard changed another adapter'}
if(-not ($script:mockRoutes | Where-Object {$_.DestinationPrefix -eq '10.0.0.0/8'})){throw 'guard changed non-default route'}
if($script:dnsCalls.Count -ne 0){throw 'guard rewrote DNS unnecessarily'}
";
            ManagerCall("RunPowerShell", NetworkStubs + injection
                + "if((& {" + detect + "}) -ne 'true'){throw 'DHCP metric zero not detected'};"
                + repair + checks
                + "if((& {" + detect + "}) -ne 'false'){throw 'route drift still present'};"
                + injection + repair + checks);
            string ipv4Only = (string)ManagerCall("BuildRepairTargetCommand", 12, "192.168.3.187", "");
            ManagerCall("RunPowerShell", NetworkStubs + ipv4Only
                + "if(-not ($script:mockRoutes | Where-Object {$_.DestinationPrefix -eq '::/0' -and $_.RouteMetric -eq 1})){throw 'IPv4-only guard changed IPv6'};");
            Console.WriteLine("PASS: real guard script repairs DHCP metric 0 repeatedly, preserves target/other adapters/non-default/IPv6-only routes");
        }

        private static void RouteGuardPreservesFallbackOnFailure()
        {
            string repair = (string)ManagerCall("BuildRepairTargetCommand", 12, "192.168.3.187", "");
            string script = NetworkStubs + @"
$script:mockRoutes=@([pscustomobject]@{PolicyStore='ActiveStore';InterfaceIndex=12;DestinationPrefix='0.0.0.0/0';NextHop='192.168.3.1';RouteMetric=0});
function New-NetRoute {[CmdletBinding()]param($PolicyStore,$InterfaceIndex,$DestinationPrefix,$NextHop,$RouteMetric);throw 'cannot add target'}
$failed=$false;try{" + repair + @"}catch{$failed=$true}
if(-not $failed -or $script:deletes.Count -ne 0){throw 'fallback removed before target established'}
";
            ManagerCall("RunPowerShell", script);
            ManagerCall("RunPowerShell", NetworkStubs
                + "$script:denyDelete=$true;$script:mockRoutes += [pscustomobject]@{PolicyStore='ActiveStore';InterfaceIndex=12;DestinationPrefix='0.0.0.0/0';NextHop='192.168.3.1';RouteMetric=0};"
                + "$failed=$false;try{" + repair + "}catch{$failed=$true};if(-not $failed){throw 'guard deletion error swallowed'};");
            Console.WriteLine("PASS: guard keeps fallback if target creation fails and reports deletion failure");
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
