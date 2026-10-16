using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task PromCabinetSettingsReconnectAsync()
    {
        var fixture = new Fixture();
        var first = fixture.Settings.Market.Connections.Single();
        var second = new MarketplaceConnection { Id = "prom-second", Name = "Second Prom", Enabled = true, Marketplace = MarketplaceKind.Prom };
        var rozetka = new MarketplaceConnection { Id = "rz-cabinet-test", Name = "Rozetka", Enabled = true, Marketplace = MarketplaceKind.Rozetka };
        fixture.Settings.Market.Connections.AddRange([second, rozetka]);
        var cabinet = new SettingsCabinetLogin();
        var vm = new MarketplaceSettingsViewModel(fixture.Settings, fixture.Settings,
            new MarketplaceSyncService([fixture.Source, fixture.Rozetka], fixture.Settings, fixture.Cache), cabinet);
        await vm.LoadAsync();
        vm.Token = "synthetic-draft-token";
        True(vm.ConnectPromCabinetCommand.CanExecute(null));
        await ExecuteAsync(vm.ConnectPromCabinetCommand);
        await ExecuteAsync(vm.ConnectPromCabinetCommand);
        Equal(3, vm.Connections.Count);
        True(cabinet.Connections.Select(c => c.Id).SequenceEqual(new[] { first.Id, first.Id }));
        True(cabinet.Connections.All(c => !ReferenceEquals(c, first)), "Login receives a frozen connection, not mutable settings fields.");
        Equal("synthetic-draft-token", vm.Token);
        Equal(0, fixture.Settings.SecretSaves); Equal(0, fixture.Settings.MarketplaceSaves);
        True(vm.PromCabinetStatus.Contains(first.Id));
        vm.Selected = second;
        True(vm.PromCabinetStatus.Contains(second.Id));
        await ExecuteAsync(vm.ConnectPromCabinetCommand);
        Equal(second.Id, cabinet.Connections.Last().Id);
        vm.Selected = first;
        True(vm.PromCabinetStatus.Contains(first.Id));
        Equal("synthetic-draft-token", vm.Token);
        vm.Selected = rozetka;
        Equal("", vm.PromCabinetStatus); True(!vm.ConnectPromCabinetCommand.CanExecute(null));
        vm.ConnectPromCabinetCommand.Execute(null);
        Equal(3, cabinet.Connections.Count);
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Printer.Calls);
    }

    private static async Task PromCabinetSettingsBusyAndDraftAsync()
    {
        var fixture = new Fixture();
        var cabinet = new SettingsCabinetLogin { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new MarketplaceSettingsViewModel(fixture.Settings, fixture.Settings,
            new MarketplaceSyncService([fixture.Source], fixture.Settings, fixture.Cache), cabinet);
        await vm.LoadAsync();
        var selected = vm.Selected;
        var connecting = ExecuteAsync(vm.ConnectPromCabinetCommand);
        True(vm.IsBusy && !vm.CanEdit && !vm.TestCommand.CanExecute(null) && !vm.AddPromCommand.CanExecute(null));
        True(!vm.ConnectPromCabinetCommand.CanExecute(null));
        vm.Selected = null;
        True(ReferenceEquals(selected, vm.Selected));
        vm.ConnectPromCabinetCommand.Execute(null);
        Equal(1, cabinet.Connections.Count);
        var blockedSave = false;
        try { await vm.SaveAsync(); }
        catch (InvalidOperationException) { blockedSave = true; }
        True(blockedSave); Equal(0, fixture.Settings.MarketplaceSaves);
        cabinet.Gate.SetResult(); await connecting;
        True(!vm.IsBusy && vm.CanEdit && vm.ConnectPromCabinetCommand.CanExecute(null));
        cabinet.Gate = null; cabinet.Error = new IOException("synthetic-session-secret");
        await ExecuteAsync(vm.ConnectPromCabinetCommand);
        True(!vm.IsBusy && !vm.PromCabinetStatus.Contains("synthetic-session-secret"));
        cabinet.Error = new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Установіть WebView2 Runtime.");
        await ExecuteAsync(vm.ConnectPromCabinetCommand);
        Equal("Установіть WebView2 Runtime.", vm.PromCabinetStatus);
        cabinet.Error = new OperationCanceledException();
        await ExecuteAsync(vm.ConnectPromCabinetCommand);
        Equal(cabinet.GetStatus(selected!.Id), vm.PromCabinetStatus);

        var draftFixture = new Fixture(); draftFixture.Settings.Market.Connections.Clear();
        var draftCabinet = new SettingsCabinetLogin();
        var draftVm = new MarketplaceSettingsViewModel(draftFixture.Settings, draftFixture.Settings,
            new MarketplaceSyncService([draftFixture.Source], draftFixture.Settings, draftFixture.Cache), draftCabinet);
        await draftVm.LoadAsync();
        draftVm.AddPromCommand.Execute(null);
        True(draftVm.PromCabinetStatus.Contains("Спочатку збережіть"));
        await ExecuteAsync(draftVm.ConnectPromCabinetCommand);
        Equal(0, draftCabinet.Connections.Count); Equal(1, draftVm.Connections.Count);
        await draftVm.SaveAsync();
        await ExecuteAsync(draftVm.ConnectPromCabinetCommand);
        Equal(draftVm.Selected!.Id, draftCabinet.Connections.Single().Id);
        Equal(1, draftVm.Connections.Count); Equal(1, draftFixture.Settings.MarketplaceSaves);
    }

    private sealed class SettingsCabinetLogin : IPromCabinetLoginService
    {
        public List<MarketplaceConnection> Connections { get; } = [];
        public TaskCompletionSource? Gate;
        public Exception? Error;
        public string GetStatus(string connectionId) => "Збережена сесія " + connectionId + "; доступ перевіряється під час друку.";
        public async Task<string> ConnectAsync(MarketplaceConnection connection)
        {
            Connections.Add(connection);
            if (Gate is not null) await Gate.Task;
            if (Error is not null) throw Error;
            return GetStatus(connection.Id);
        }
    }
}
