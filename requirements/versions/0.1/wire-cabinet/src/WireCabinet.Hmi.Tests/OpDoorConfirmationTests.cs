using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Flows;
using WireCabinet.Hmi.Services;
using WireCabinet.Hmi.Views;
using WireCabinet.Slots;
using WireCabinet.Slots.Hardware;
using Xunit;

namespace WireCabinet.Hmi.Tests;

[CollectionDefinition("WpfStaticApp", DisableParallelization = true)]
public sealed class WpfStaticAppCollection;

[Collection("WpfStaticApp")]
public sealed class OpDoorConfirmationTests
{
    [Fact]
    public async Task RealOpGateRequiresActualClosedDiForReturnAndIssueEvenAfterManualClick()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var saved = new[] { "Bootstrap", "Flows", "UiGate", "SlotHardwarePoll", "StationGate" }
                .ToDictionary(p => p, p => typeof(App).GetProperty(p)!.GetValue(null));
            var path = Path.Combine(Path.GetTempPath(), $"wc-op-close-{Guid.NewGuid():N}.db");
            try
            {
                var app = new Application();
                app.Resources.MergedDictionaries.Add(new() { Source = new Uri("/WireCabinet.Hmi;component/Styles/IndustrialTheme.xaml", UriKind.Relative) });
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var db = new SqliteDb(path); db.Initialize(true);
                var catalog = SqlCatalog.Load();
                SetApp("StationGate", null); SetApp("UiGate", new HmiUiGateService());
                foreach (var node in new[] { "closeReturnSlotDoor", "closeIssueSlotDoor" })
                foreach (var fault in new[] { "open", "missing", "failed", "null", "stale", "fresh-unknown", "fresh-error", "ready", "mock" })
                {
                    Assert.False(db.Execute("UPDATE app_slot SET door_state='open',lock_state='unlocked',wire_lot_no='AUDIT-LOT' WHERE slot_id=1;",
                        new Dictionary<string, object?>()).HasError);
                    var hardware = new Hardware { IsConfigured = fault != "mock", FreshClosed = fault is "ready" or "mock" };
                    if (fault != "missing")
                        hardware.Data["F-A01"] = new() { SlotCode = "F-A01", Wired = true, ReadOk = fault != "failed",
                            LockClosed = fault == "null" ? null : fault != "open" };
                    if (fault == "fresh-unknown") hardware.FreshClosed = null;
                    hardware.ThrowOnRead = fault == "fresh-error";
                    var bootstrap = (AppBootstrap)RuntimeHelpers.GetUninitializedObject(typeof(AppBootstrap));
                    var slots = new FlowSlotController(new SlotControlService(db, hardware, new DoorOperationGate()), db);
                    SetBootstrap(bootstrap, "FlowSlots", slots); SetBootstrap(bootstrap, "Hardware", hardware);
                    SetApp("Bootstrap", bootstrap);
                    using var poll = new SlotHardwarePollService(hardware);
                    typeof(SlotHardwarePollService).GetField("_snapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(poll, hardware.Data);
                    SetApp("SlotHardwarePoll", poll);
                    var runner = new FlowSessionRunner(new() { Catalog = catalog, AppDb = new SqliteAppDb(db), Mes = new NoMes(), Slot = slots });
                    runner.Begin(new() { FlowId = "door-gate-test", Start = node, Nodes = new() { [node] = new() { Id = node, Type = NodeType.UserInput } } });
                    var flows = new FlowCoordinator(bootstrap);
                    typeof(FlowCoordinator).GetField("_runner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(flows, runner);
                    SetApp("Flows", flows);
                    var view = new OpView();
                    var calls = 0; var seen = true;
                    Func<(bool Ok, string Message, FlowPauseReason Reason)> advance = () => { calls++; return (true, "", FlowPauseReason.Terminal); };
                    var task = (Task<bool>)typeof(OpView).GetMethod("TryAdvanceDoorClosedCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(view, new object[] { 1L, true, node, (Func<bool>)(() => seen), (Action<bool>)(v => seen = v), advance })!;
                    var allowed = Pump(task);
                    Assert.Equal(fault is "ready" or "mock", allowed);
                    Assert.Equal(allowed ? 1 : 0, calls);
                    Assert.True(flows.IsFlowActive);
                    Assert.Equal(node, flows.CurrentNodeId);
                    Assert.Equal("AUDIT-LOT", db.Query("SELECT wire_lot_no FROM app_slot WHERE slot_id=1;", new Dictionary<string, object?>()).First!["wire_lot_no"]);
                }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                foreach (var pair in saved) SetApp(pair.Key, pair.Value);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static T Pump<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
    private static void SetApp(string name, object? value) => typeof(App).GetProperty(name)!.SetValue(null, value);
    private static void SetBootstrap(AppBootstrap app, string name, object value) =>
        typeof(AppBootstrap).GetField("<" + name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);
    private sealed class NoMes : IMesGateway
    {
        public MesMode Mode => MesMode.SqliteMock;
        public DbResult Run(SqlCatalogItem item, IDictionary<string, object?> p) => throw new InvalidOperationException("No MES access in door-gate test");
    }
    private sealed class Hardware : ISlotHardwareService
    {
        public bool IsConfigured { get; init; }
        public bool? FreshClosed { get; set; }
        public bool ThrowOnRead { get; set; }
        public Dictionary<string, SlotHardwareSnapshot> Data { get; } = new();
        public IReadOnlyDictionary<string, bool> ModuleHealth => new Dictionary<string, bool>();
        public Task<bool> OpenLockAsync(string no, CancellationToken ct = default) => throw new InvalidOperationException("No hardware actions allowed");
        public Task<bool?> ReadLockClosedAsync(string no, CancellationToken ct = default) =>
            ThrowOnRead ? Task.FromException<bool?>(new IOException("injected DI failure")) : Task.FromResult(FreshClosed);
        public Task<IReadOnlyDictionary<string, SlotHardwareSnapshot>> ReadAllWiredSnapshotsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, SlotHardwareSnapshot>>(Data);
        public Task RefreshHealthAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
