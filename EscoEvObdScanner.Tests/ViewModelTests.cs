using System.Collections;
using System.Globalization;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;
using EscoEvObdScanner.AppLogic.ViewModels;

namespace EscoEvObdScanner.Tests
{
    /// <summary>The view models change the UI language, the culture and the results folder for the whole process: never beside other tests.</summary>
    [CollectionDefinition(nameof(ViewModelTests), DisableParallelization = true)]
    public class ViewModelCollection { }

    /// <summary>
    /// The app's view models without the Windows app, on the demo car, with
    /// settings, car files and results in a folder of each test's own: the
    /// comparison after a language change, what a connection keeps of the vehicle,
    /// and a broken drive history kept rather than overwritten.
    /// </summary>
    [Collection(nameof(ViewModelTests))]
    public sealed class ViewModelTests : IDisposable
    {
        /// <summary>A platform that shows nothing and asks nothing; what it was told is kept.</summary>
        sealed class TestHost : IAppHost
        {
            public List<(string Text, Notice Kind)> Told { get; } = new();
            public bool Ask(string text, Notice kind, bool defaultNo = false) => false;
            public void Tell(string text, Notice kind) => Told.Add((text, kind));
            public string? PickCarFile(string? folder) => null;
            public void AddVehicle(IReadOnlyList<CarFileChoice> files, string? carPath, Func<string, string, string?> add) { }
            public string? PickRecording(IReadOnlyList<RecordingChoice> choices) => null;
            public void Open(string path) { }
            public IReadOnlyList<PortInfo> Ports() => Array.Empty<PortInfo>();
            public void LanguageChanged() { }
            public IDisposable Every(TimeSpan interval, Action tick) => new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
            public IRowView RowView(IList rows, Predicate<object> filter) => throw new NotSupportedException();
            public event EventHandler? RequerySuggested { add { } remove { } }
            public void RequeryCommands() { }
        }

        readonly (string Root, string? Car, CultureInfo Culture, CultureInfo? Default, IAppHost Host, string Language) _saved =
            (Results.Root, Results.Car, CultureInfo.CurrentCulture, CultureInfo.DefaultThreadCurrentCulture, AppHost.I, Loc.I.Language);
        readonly TestHost _host = new();
        readonly List<Exception> _failed = new();
        readonly List<AppState> _states = new();

        public ViewModelTests()
        {
            AppHost.I = _host;
            AppPaths.UseIsolatedFolders(Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N")));
            AsyncCommand.Failed += _failed.Add;
        }

        public void Dispose()
        {
            AsyncCommand.Failed -= _failed.Add;
            foreach (var state in _states) state.CloseLog();
            Loc.I.Language = _saved.Language;
            (Results.Root, Results.Car) = (_saved.Root, _saved.Car);
            (CultureInfo.CurrentCulture, CultureInfo.DefaultThreadCurrentCulture) = (_saved.Culture, _saved.Default);
            AppHost.I = _saved.Host;
        }

        [Fact]
        public void BrokenDrivesFileThatCannotBeMovedAsideIsCopiedBeforeTheFirstSave()
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            string file = Path.Combine(AppPaths.ConfigDir, "drives-locked.json");
            File.WriteAllText(file, "[ broken");

            DriveHistory history;
            // open without delete sharing: the move aside fails, the read does not
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                history = DriveHistory.Load("locked");
            Assert.True(File.Exists(file));

            history.Save(new Drive(new DateTime(2026, 10, 14, 8, 0, 0), 12, 2.4, 8));

            var kept = Directory.GetFiles(AppPaths.ConfigDir, "drives-locked.unread-*.json");
            Assert.Equal("[ broken", File.ReadAllText(Assert.Single(kept)));
            Assert.Single(history.Drives);
        }

        AppState NewState()
        {
            var state = new AppState();
            _states.Add(state);
            return state;
        }

        static void TakeSnap(Scanner scanner, Catalog catalog, string note)
        {
            using var w = new StreamWriter(Results.NewFile(Results.Snaps, "snap"));
            Snapshot.Take(scanner, catalog, Snapshot.Targets(catalog), note, w);
        }

        [Fact]
        public void ComparisonIsWordedAgainInTheNewLanguage()
        {
            var state = NewState();
            Loc.I.Language = "fi";
            var (car, scanner, catalog) = TestData.DemoCar();
            TakeSnap(scanner, catalog, "before");
            car.SetData(0x6B4, 0xD410, new byte[] { 0x78, 0x00 });
            car.SetData(0x6B4, 0xD440, new byte[] { 0x0E, 0x2D, 0x0E, 0x2E });      // fewer cells: a change of length
            TakeSnap(scanner, catalog, "after");
            var vm = new CompareViewModel(state);
            var (a, b) = (vm.A, vm.B);
            Assert.Equal(2, vm.Files.Count);

            vm.Compare.Execute(null);
            Assert.Empty(_failed);
            Assert.StartsWith("Muuttui: 2, ennallaan: ", vm.Status);
            var cells = vm.Rows.Single(r => r.Did == "0xD440");
            Assert.StartsWith("pituus ", cells.Change);

            Loc.I.Language = "en";

            Assert.StartsWith("Changed: 2, unchanged: ", vm.Status);
            Assert.Equal(2, vm.Rows.Count);
            Assert.StartsWith("length ", vm.Rows.Single(r => r.Did == "0xD440").Change);
            // the same snaps listed and chosen, not read again
            Assert.Equal(2, vm.Files.Count);
            Assert.Equal(a, vm.A);
            Assert.Equal(b, vm.B);
            Assert.Equal("after", Snapshot.Load(vm.B!.Path).Note);
            Assert.Empty(_failed);
        }

        [Fact]
        public void ProgressLinesAreNotWordedAgain()
        {
            // no comparison yet: a language change leaves the status empty
            var vm = new CompareViewModel(NewState());
            Loc.I.Language = "fi";
            Loc.I.Language = "en";
            Assert.Equal("", vm.Status);
            Assert.Empty(_failed);
        }

        [Fact]
        public void TheDateTakenIntoUseIsTheChosenCarsOwn()
        {
            var state = NewState();
            var live = new LiveViewModel(state);
            state.UseCatalog(Path.Combine(AppPaths.BundledCarsDir, "stellantis-ecmp.json"));
            live.InUseText = "2023-05-01";
            Assert.NotEqual("", live.InUseText);
            live.InUseKmText = "abc";
            Assert.NotEqual("", live.InputError);
            var raised = new List<string?>();
            live.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            // another car file, used without a vehicle: nothing taken into use is known of it
            state.UseCatalog(Path.Combine(AppPaths.BundledCarsDir, "kia-eniro.json"));

            Assert.Equal("", live.InUseText);
            Assert.Equal("", live.InputError);
            Assert.Contains(nameof(LiveViewModel.InUseText), raised);
            Assert.Contains(nameof(LiveViewModel.LifeYearNote), raised);
            Assert.Empty(_failed);
        }

        [Fact]
        public void SettingsKeepTheInUseEntriesWithoutCase()
        {
            var settings = Settings.Load();
            settings.InUse["Kia-ENiro"] = new InUseEntry { Since = new DateTime(2023, 5, 1), Km = 12 };
            settings.Save();
            Assert.Equal(12, Settings.Load().InUse["kia-eniro"].Km);
        }

        [Fact]
        public void SettingsThatCouldNotBeReadAreKeptBeforeTheFirstSave()
        {
            var first = Settings.Load();
            first.Port = "COM9";
            first.Save();
            Settings unread;
            using (new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
                unread = Settings.Load();
            Assert.Null(unread.Port);

            unread.Save();

            var kept = Directory.GetFiles(AppPaths.ConfigDir, "settings.unread-*.json");
            Assert.Contains("COM9", File.ReadAllText(Assert.Single(kept)));
            unread.Save();                                  // once only
            Assert.Single(Directory.GetFiles(AppPaths.ConfigDir, "settings.unread-*.json"));
        }

        [Fact]
        public void BrokenSettingsAreKeptBeforeTheFirstSave()
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            foreach (var old in Directory.GetFiles(AppPaths.ConfigDir, "settings.unread-*.json")) File.Delete(old);
            File.WriteAllText(AppPaths.SettingsFile, """{ "Port": "COM7", "PriceCents": 16, """);   // cut short

            var broken = Settings.Load();
            Assert.Null(broken.Port);
            broken.Save();

            var kept = Directory.GetFiles(AppPaths.ConfigDir, "settings.unread-*.json");
            Assert.Contains("COM7", File.ReadAllText(Assert.Single(kept)));
        }

        /// <summary>The app connected to the demo car with a vehicle that has the car file.</summary>
        async Task<(AppState State, ConnectionViewModel Vm, Vehicle Vehicle)> Connected()
        {
            var state = NewState();
            var vm = new ConnectionViewModel(state);
            var vehicle = new Vehicle { Name = "Test", Car = CarFiles.NameOf(state.CatalogPath) };
            state.VehicleList.Add(vehicle);
            await state.Adapter.ConnectDemoAsync(state.Catalog);
            return (state, vm, vehicle);
        }

        static readonly DateTime LongAgo = new(2026, 1, 2, 3, 4, 5);

        [Fact]
        public async Task BatteryInfoReadDateMovesWithEveryReadFromTheSameCar()
        {
            var (_, vm, vehicle) = await Connected();
            await vm.KeepBatteryInfoAsync(vehicle, verified: true);
            Assert.NotNull(vehicle.BmsSoftware);
            var kept = (vehicle.BatteryMade, vehicle.BmsSoftware, vehicle.BmsHardware);

            // nothing new read, but it is the same car: the date is that of this read
            vehicle.BatteryInfoRead = LongAgo;
            await vm.KeepBatteryInfoAsync(vehicle, verified: true);

            Assert.True(vehicle.BatteryInfoRead > LongAgo);
            Assert.Equal(kept, (vehicle.BatteryMade, vehicle.BmsSoftware, vehicle.BmsHardware));
            Assert.Equal(vehicle.BatteryInfoRead, Vehicles.Load().Single().BatteryInfoRead);
        }

        [Fact]
        public async Task BatteryInfoOfAnUnconfirmedCarOnlyFillsWhatIsEmpty()
        {
            var (_, vm, vehicle) = await Connected();
            vehicle.BmsSoftware = "other car";
            vehicle.BatteryInfoRead = LongAgo;
            await vm.KeepBatteryInfoAsync(vehicle, verified: false);

            Assert.Equal("other car", vehicle.BmsSoftware);
            Assert.NotNull(vehicle.BmsHardware);
            Assert.True(vehicle.BatteryInfoRead > LongAgo);

            // the same values again: nothing filled, the date stays
            vehicle.BatteryInfoRead = LongAgo;
            await vm.KeepBatteryInfoAsync(vehicle, verified: false);
            Assert.Equal(LongAgo, vehicle.BatteryInfoRead);
        }

        [Fact]
        public async Task VinReadDateMovesWithEveryReadFromTheSameCar()
        {
            var (_, vm, vehicle) = await Connected();
            const string vin = "VR7DEM00000000001";
            (vehicle.Vin, vehicle.VinRead) = (vin, LongAgo);

            vm.KeepVin(vehicle, vin, verified: false);
            Assert.Equal(LongAgo, vehicle.VinRead);         // not confirmed: kept as it was

            vm.KeepVin(vehicle, vin, verified: true);
            Assert.True(vehicle.VinRead > LongAgo);
            Assert.Equal(vehicle.VinRead, Vehicles.Load().Single().VinRead);
        }

        [Fact]
        public async Task AnotherVinReplacesTheKeptOneOnlyForTheSameCar()
        {
            var (_, vm, vehicle) = await Connected();
            (vehicle.Vin, vehicle.VinRead) = ("VR7DEM00000000001", LongAgo);

            vm.KeepVin(vehicle, "VR7DEM00000000002", verified: false);
            Assert.Equal("VR7DEM00000000001", vehicle.Vin);
            Assert.Equal(LongAgo, vehicle.VinRead);

            vm.KeepVin(vehicle, "VR7DEM00000000002", verified: true);
            Assert.Equal("VR7DEM00000000002", vehicle.Vin);
            Assert.True(vehicle.VinRead > LongAgo);

            // a vehicle without a VIN takes the first one read
            var fresh = new Vehicle { Name = "Fresh" };
            vm.KeepVin(fresh, "VR7DEM00000000003", verified: false);
            Assert.Equal("VR7DEM00000000003", fresh.Vin);
            Assert.NotNull(fresh.VinRead);
        }
    }
}
