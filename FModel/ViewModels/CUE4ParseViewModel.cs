using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Wwise;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;
using EpicManifestParser.Objects;
using FModel.Creator;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using Newtonsoft.Json;
using Serilog;
using SkiaSharp;

namespace FModel.ViewModels
{
    public class CUE4ParseViewModel : ViewModel
    {
        private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
        private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;
        private readonly Regex _package = new(@"^(?!global|pakchunk.+optional\-).+(pak|utoc)$", // should be universal
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly Regex _fnLive = new(@"^FortniteGame(/|\\)Content(/|\\)Paks(/|\\)(pakchunk(?:0|10.*|\w+)-WindowsClient|global)\.(pak|utoc)$",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private FGame _game;
        public FGame Game
        {
            get => _game;
            set => SetProperty(ref _game, value);
        }

        private bool _modelIsOverwritingMaterial;
        public bool ModelIsOverwritingMaterial
        {
            get => _modelIsOverwritingMaterial;
            set => SetProperty(ref _modelIsOverwritingMaterial, value);
        }

        public AbstractVfsFileProvider Provider { get; }
        public GameDirectoryViewModel GameDirectory { get; }
        public AssetsFolderViewModel AssetsFolder { get; }
        public SearchViewModel SearchVm { get; }
        public TabControlViewModel TabControl { get; }
        public int LocalizedResourcesCount { get; set; }
        public bool HotfixedResourcesDone { get; set; } = false;
        public int VirtualPathCount { get; set; }

        public CUE4ParseViewModel(string gameDirectory)
        {
            switch (gameDirectory)
            {
                case Constants._FN_LIVE_TRIGGER:
                {
                    Game = FGame.FortniteGame;
                    Provider = new StreamedFileProvider("FortniteLive", true,
                        new VersionContainer(
                            UserSettings.Default.OverridedGame[Game],
                            customVersions: UserSettings.Default.OverridedCustomVersions[Game],
                            optionOverrides: UserSettings.Default.OverridedOptions[Game]));
                    break;
                }
                case Constants._VAL_LIVE_TRIGGER:
                {
                    Game = FGame.ShooterGame;
                    Provider = new StreamedFileProvider("ValorantLive", true,
                        new VersionContainer(
                            UserSettings.Default.OverridedGame[Game],
                            customVersions: UserSettings.Default.OverridedCustomVersions[Game],
                            optionOverrides: UserSettings.Default.OverridedOptions[Game]));
                    break;
                }
                default:
                {
                    Game = gameDirectory.SubstringBeforeLast("\\Content").SubstringAfterLast("\\").ToEnum(FGame.Unknown);
                    var versions = new VersionContainer(UserSettings.Default.OverridedGame[Game],
                        customVersions: UserSettings.Default.OverridedCustomVersions[Game],
                        optionOverrides: UserSettings.Default.OverridedOptions[Game]);

                    if (Game == FGame.StateOfDecay2)
                        Provider = new DefaultFileProvider(new DirectoryInfo(gameDirectory), new List<DirectoryInfo>
                            {
                                new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\Paks"),
                                new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\DisabledPaks")
                            },
                            SearchOption.AllDirectories, true, versions);
                    else
                        Provider = new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, true, versions);

                    break;
                }
            }

            GameDirectory = new GameDirectoryViewModel();
            AssetsFolder = new AssetsFolderViewModel();
            SearchVm = new SearchViewModel();
            TabControl = new TabControlViewModel();
        }

        public async Task Initialize()
        {
            await _threadWorkerView.Begin(cancellationToken =>
            {
                switch (Provider)
                {
                    case StreamedFileProvider p:
                        switch (p.LiveGame)
                        {
                            case "FortniteLive":
                            {
                                var manifestInfo = _apiEndpointView.EpicApi.GetManifest(cancellationToken);
                                if (manifestInfo == null)
                                {
                                    throw new Exception("Could not load latest Fortnite manifest, you may have to switch to your local installation.");
                                }

                                byte[] manifestData;
                                var chunksDir = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data"));
                                var manifestPath = Path.Combine(chunksDir.FullName, manifestInfo.FileName);
                                if (File.Exists(manifestPath))
                                {
                                    manifestData = File.ReadAllBytes(manifestPath);
                                }
                                else
                                {
                                    manifestData = manifestInfo.DownloadManifestData();
                                    File.WriteAllBytes(manifestPath, manifestData);
                                }

                                var manifest = new Manifest(manifestData, new ManifestOptions
                                {
                                    ChunkBaseUri = new Uri("http://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/ChunksV4/", UriKind.Absolute),
                                    ChunkCacheDirectory = chunksDir
                                });

                                foreach (var fileManifest in manifest.FileManifests)
                                {
                                    if (!_fnLive.IsMatch(fileManifest.Name)) continue;

                                    var casStream = manifest.FileManifests.FirstOrDefault(x => x.Name.Equals(fileManifest.Name.Replace(".utoc", ".ucas")));
                                    p.Initialize(fileManifest.Name, new[] {fileManifest.GetStream(), casStream.GetStream()});
                                }

                                FLogger.AppendInformation();
                                FLogger.AppendText($"Fortnite has been loaded successfully in {manifest.ParseTime.TotalMilliseconds}ms", Constants.WHITE, true);
                                FLogger.AppendWarning();
                                FLogger.AppendText($"Mappings must match '{manifest.BuildVersion}' in order to avoid errors", Constants.WHITE, true);
                                break;
                            }
                            case "ValorantLive":
                            {
                                var manifestInfo = _apiEndpointView.ValorantApi.GetManifest(cancellationToken);
                                if (manifestInfo == null)
                                {
                                    throw new Exception("Could not load latest Valorant manifest, you may have to switch to your local installation.");
                                }

                                for (var i = 0; i < manifestInfo.Paks.Length; i++)
                                {
                                    p.Initialize(manifestInfo.Paks[i].GetFullName(), new[] {manifestInfo.GetPakStream(i)});
                                }

                                FLogger.AppendInformation();
                                FLogger.AppendText($"Valorant '{manifestInfo.Header.GameVersion}' has been loaded successfully", Constants.WHITE, true);
                                break;
                            }
                        }

                        break;
                    case DefaultFileProvider d:
                        d.Initialize();
                        break;
                }

                foreach (var vfs in Provider.UnloadedVfs) // push files from the provider to the ui
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (vfs.Length <= 365 || !_package.IsMatch(vfs.Name)) continue;

                    GameDirectory.Add(vfs);
                }
            });
        }

        /// <summary>
        /// load virtual files system from GameDirectory
        /// </summary>
        /// <returns></returns>
        public async Task LoadVfs(IEnumerable<FileItem> aesKeys)
        {
            await _threadWorkerView.Begin(cancellationToken =>
            {
                GameDirectory.DeactivateAll();

                // load files using UnloadedVfs to include non-encrypted vfs
                foreach (var key in aesKeys)
                {
                    cancellationToken.ThrowIfCancellationRequested(); // cancel if needed

                    var k = key.Key.Trim();
                    if (k.Length != 66) k = Constants.ZERO_64_CHAR;
                    Provider.SubmitKey(key.Guid, new FAesKey(k));
                }

                // files in MountedVfs will be enabled
                foreach (var file in GameDirectory.DirectoryFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Provider.MountedVfs.FirstOrDefault(x => x.Name == file.Name) is not { } vfs)
                    {
                        if (Provider.UnloadedVfs.FirstOrDefault(x => x.Name == file.Name) is IoStoreReader store)
                            file.FileCount = (int)store.Info.TocEntryCount - 1;

                        continue;
                    }

                    file.IsEnabled = true;
                    file.MountPoint = vfs.MountPoint;
                    file.FileCount = vfs.FileCount;
                }

                Game = Provider.GameName.ToEnum(Game);
            });
        }

        public void ClearProvider()
        {
            if (Provider == null) return;

            AssetsFolder.Folders.Clear();
            SearchVm.SearchResults.Clear();
            Helper.CloseWindow<AdonisWindow>("Search View");
            Provider.UnloadAllVfs();
            GC.Collect();
        }

        public async Task RefreshAes()
        {
            if (Game == FGame.FortniteGame) // game directory dependent, we don't have the provider game name yet since we don't have aes keys
            {
                await _threadWorkerView.Begin(cancellationToken =>
                {
                    var aes = _apiEndpointView.BenbotApi.GetAesKeys(cancellationToken);
                    if (aes?.MainKey == null && aes?.DynamicKeys == null && aes?.Version == null) return;

                    UserSettings.Default.AesKeys[Game] = aes;
                });
            }
        }

        public async Task InitInformation()
        {
            await _threadWorkerView.Begin(cancellationToken =>
            {
                var info = _apiEndpointView.FModelApi.GetNews(cancellationToken);
                if (info == null) return;

                for (var i = 0; i < info.Messages.Length; i++)
                {
                    FLogger.AppendText(info.Messages[i], info.Colors[i], bool.Parse(info.NewLines[i]));
                }
            });
        }

        public async Task InitBenMappings()
        {
            if (Game == FGame.FortniteGame)
            {
                await _threadWorkerView.Begin(cancellationToken =>
                {
                    if (UserSettings.Default.OverwriteMapping && File.Exists(UserSettings.Default.MappingFilePath))
                    {
                        Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(UserSettings.Default.MappingFilePath);
                        FLogger.AppendInformation();
                        FLogger.AppendText($"Mappings pulled from '{UserSettings.Default.MappingFilePath.SubstringAfterLast("\\")}'", Constants.WHITE, true);
                    }
                    else
                    {
                        var mappingsFolder = Path.Combine(UserSettings.Default.OutputDirectory, ".data");
                        var mappings = _apiEndpointView.BenbotApi.GetMappings(cancellationToken);
                        if (mappings is {Length: > 0})
                        {
                            foreach (var mapping in mappings)
                            {
                                if (mapping.Meta.CompressionMethod != "Oodle") continue;

                                var mappingPath = Path.Combine(mappingsFolder, mapping.FileName);
                                if (!File.Exists(mappingPath))
                                {
                                    _apiEndpointView.BenbotApi.DownloadFile(mapping.Url, mappingPath);
                                }

                                Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);
                                FLogger.AppendInformation();
                                FLogger.AppendText($"Mappings pulled from '{mapping.FileName}'", Constants.WHITE, true);
                                break;
                            }
                        }
                        else
                        {
                            var latestUsmaps = new DirectoryInfo(mappingsFolder).GetFiles("*_oo.usmap");
                            if (Provider.MappingsContainer != null || latestUsmaps.Length <= 0) return;

                            var latestUsmapInfo = latestUsmaps.OrderBy(f => f.LastWriteTime).Last();
                            Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(latestUsmapInfo.FullName);
                            FLogger.AppendWarning();
                            FLogger.AppendText($"Mappings pulled from '{latestUsmapInfo.Name}'", Constants.WHITE, true);
                        }
                    }
                });
            }
        }

        public async Task LoadLocalizedResources()
        {
            await LoadGameLocalizedResources();
            await LoadHotfixedLocalizedResources();
            if (LocalizedResourcesCount > 0)
            {
                FLogger.AppendInformation();
                FLogger.AppendText($"{LocalizedResourcesCount} localized resources loaded for '{UserSettings.Default.AssetLanguage.GetDescription()}'", Constants.WHITE, true);
            }
            else
            {
                FLogger.AppendWarning();
                FLogger.AppendText($"Could not load localized resources in '{UserSettings.Default.AssetLanguage.GetDescription()}', language may not exist", Constants.WHITE, true);
            }
        }

        private async Task LoadGameLocalizedResources()
        {
            if (LocalizedResourcesCount > 0) return;
            await _threadWorkerView.Begin(cancellationToken =>
            {
                LocalizedResourcesCount = Provider.LoadLocalization(UserSettings.Default.AssetLanguage, cancellationToken);
                Utils.Typefaces = new Typefaces(this);
            });
        }

        /// <summary>
        /// Load hotfixed localized resources
        /// </summary>
        /// <remarks>Functions only when LoadLocalizedResources is used prior to this (Asval: Why?).</remarks>
        private async Task LoadHotfixedLocalizedResources()
        {
            if (Game != FGame.FortniteGame || HotfixedResourcesDone) return;
            await _threadWorkerView.Begin(cancellationToken =>
            {
                var hotfixes = ApplicationService.ApiEndpointView.BenbotApi.GetHotfixes(cancellationToken, Provider.GetLanguageCode(UserSettings.Default.AssetLanguage));
                if (hotfixes == null) return;

                HotfixedResourcesDone = true;
                foreach (var entries in hotfixes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Provider.LocalizedResources.ContainsKey(entries.Key))
                        Provider.LocalizedResources[entries.Key] = new Dictionary<string, string>();

                    foreach (var keyValue in entries.Value)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Provider.LocalizedResources[entries.Key][keyValue.Key] = keyValue.Value;
                        LocalizedResourcesCount++;
                    }
                }
            });
        }

        public async Task LoadVirtualPaths()
        {
            if (VirtualPathCount > 0) return;
            await _threadWorkerView.Begin(cancellationToken =>
            {
                VirtualPathCount = Provider.LoadVirtualPaths(UserSettings.Default.OverridedGame[Game].GetVersion(), cancellationToken);
                if (VirtualPathCount > 0)
                {
                    FLogger.AppendInformation();
                    FLogger.AppendText($"{VirtualPathCount} virtual paths loaded", Constants.WHITE, true);
                }
                else
                {
                    FLogger.AppendWarning();
                    FLogger.AppendText("Could not load virtual paths, plugin manifest may not exist", Constants.WHITE, true);
                }
            });
        }

        public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder)
        {
            foreach (var asset in folder.AssetsList.Assets)
            {
                Thread.Sleep(10);
                cancellationToken.ThrowIfCancellationRequested();
                try {Extract(asset.FullPath, TabControl.HasNoTabs);} catch {/**/}
            }

            foreach (var f in folder.Folders) ExtractFolder(cancellationToken, f);
        }

        public void ExportFolder(CancellationToken cancellationToken, TreeItem folder)
        {
            foreach (var asset in folder.AssetsList.Assets)
            {
                Thread.Sleep(10);
                cancellationToken.ThrowIfCancellationRequested();
                ExportData(asset.FullPath);
            }

            foreach (var f in folder.Folders) ExportFolder(cancellationToken, f);
        }

        public void SaveFolder(CancellationToken cancellationToken, TreeItem folder)
        {
            foreach (var asset in folder.AssetsList.Assets)
            {
                Thread.Sleep(10);
                cancellationToken.ThrowIfCancellationRequested();
                try {Extract(asset.FullPath, TabControl.HasNoTabs, true);} catch {/**/}
            }

            foreach (var f in folder.Folders) SaveFolder(cancellationToken, f);
        }

        public void ExtractSelected(CancellationToken cancellationToken, IEnumerable<AssetItem> assetItems)
        {
            foreach (var asset in assetItems)
            {
                Thread.Sleep(10);
                cancellationToken.ThrowIfCancellationRequested();
                Extract(asset.FullPath, TabControl.HasNoTabs);
            }
        }

        public void Extract(string fullPath, bool addNewTab = false, bool bulkSave = false)
        {
            Log.Information("User DOUBLE-CLICKED to extract '{FullPath}'", fullPath);

            var directory = fullPath.SubstringBeforeLast('/');
            var fileName = fullPath.SubstringAfterLast('/');
            var ext = fullPath.SubstringAfterLast('.').ToLower();

            if (addNewTab && TabControl.CanAddTabs)
            {
                TabControl.AddTab(fileName, directory);
            }
            else
            {
                TabControl.SelectedTab.Header = fileName;
                TabControl.SelectedTab.Directory = directory;
            }

            TabControl.SelectedTab.ResetDocumentText();
            TabControl.SelectedTab.ScrollTrigger = null;
            TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(ext);
            switch (ext)
            {
                case "uasset":
                case "umap":
                {
                    var exports = Provider.LoadObjectExports(fullPath);
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(exports, Formatting.Indented), bulkSave);

                    if (bulkSave || !exports.Any(CheckExport))
                        TabControl.SelectedTab.Image = null;
                    break;
                }
                case "ini":
                case "txt":
                case "log":
                case "po":
                case "bat":
                case "dat":
                case "cfg":
                case "ide":
                case "ipl":
                case "zon":
                case "xml":
                case "h":
                case "uproject":
                case "uplugin":
                case "upluginmanifest":
                case "csv":
                case "json":
                case "archive":
                case "manifest":
                {
                    TabControl.SelectedTab.Image = null;
                    if (Provider.TrySaveAsset(fullPath, out var data))
                    {
                        using var stream = new MemoryStream(data) {Position = 0};
                        using var reader = new StreamReader(stream);

                        TabControl.SelectedTab.SetDocumentText(reader.ReadToEnd(), bulkSave);
                    }
                    break;
                }
                case "locmeta":
                {
                    TabControl.SelectedTab.Image = null;
                    if (Provider.TryCreateReader(fullPath, out var archive))
                    {
                        var metadata = new FTextLocalizationMetaDataResource(archive);
                        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(metadata, Formatting.Indented), bulkSave);
                    }
                    break;
                }
                case "locres":
                {
                    TabControl.SelectedTab.Image = null;
                    if (Provider.TryCreateReader(fullPath, out var archive))
                    {
                        var locres = new FTextLocalizationResource(archive);
                        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(locres, Formatting.Indented), bulkSave);
                    }
                    break;
                }
                case "bin" when fileName.Contains("AssetRegistry"):
                {
                    TabControl.SelectedTab.Image = null;
                    if (Provider.TryCreateReader(fullPath, out var archive))
                    {
                        var registry = new FAssetRegistryState(archive);
                        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), bulkSave);
                    }
                    break;
                }
                case "bnk":
                case "pck":
                {
                    TabControl.SelectedTab.Image = null;
                    if (Provider.TryCreateReader(fullPath, out var archive))
                    {
                        var wwise = new WwiseReader(archive);
                        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(wwise, Formatting.Indented), bulkSave);
                        foreach (var (name, data) in wwise.WwiseEncodedMedias)
                        {
                            SaveAndPlaySound(fullPath.SubstringBeforeWithLast("/") + name, "WEM", data);
                        }
                    }
                    break;
                }
                case "wem":
                {
                    TabControl.SelectedTab.Image = null;
                    if (Provider.TrySaveAsset(fullPath, out var input))
                        SaveAndPlaySound(fullPath, "WEM", input);

                    break;
                }
                case "udic":
                {
                    TabControl.SelectedTab.Image = null;
                    if (Provider.TryCreateReader(fullPath, out var archive))
                    {
                        var header = new FOodleDictionaryArchive(archive).Header;
                        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(header, Formatting.Indented), bulkSave);
                    }
                    break;
                }
                case "png":
                case "jpg":
                case "bmp":
                {
                    if (Provider.TrySaveAsset(fullPath, out var data))
                    {
                        using var stream = new MemoryStream(data) {Position = 0};
                        TabControl.SelectedTab.SetImage(SKImage.FromBitmap(SKBitmap.Decode(stream)));
                    }
                    break;
                }
                case "svg":
                {
                    if (Provider.TrySaveAsset(fullPath, out var data))
                    {
                        using var stream = new MemoryStream(data) { Position = 0 };
                        var svg = new SkiaSharp.Extended.Svg.SKSvg(new SKSize(512, 512));
                        TabControl.SelectedTab.SetImage(SKImage.FromPicture(svg.Load(stream), new SKSizeI(512, 512)));
                    }
                    break;
                }
                case "ufont":
                case "otf":
                case "ttf":
                    FLogger.AppendWarning();
                    FLogger.AppendText($"Export '{fileName}' raw data and change its extension if you want it to be an installable font file", Constants.WHITE, true);
                    break;
                case "ushaderbytecode":
                case "ushadercode":
                {
                    TabControl.SelectedTab.Image = null;
                    if (Provider.TryCreateReader(fullPath, out var archive))
                    {
                        var ar = new FShaderCodeArchive(archive);
                        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), bulkSave);
                    }
                    break;
                }
                default:
                {
                    FLogger.AppendWarning();
                    FLogger.AppendText($"The package '{fileName}' is of an unknown type.", Constants.WHITE, true);
                    break;
                }
            }
        }

        public void ExtractAndScroll(string fullPath, string objectName)
        {
            Log.Information("User CTRL-CLICKED to extract '{FullPath}'", fullPath);
            TabControl.AddTab(fullPath.SubstringAfterLast('/'), fullPath.SubstringBeforeLast('/'));
            TabControl.SelectedTab.ScrollTrigger = objectName;

            var exports = Provider.LoadObjectExports(fullPath);
            TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(""); // json
            TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(exports, Formatting.Indented), false);

            if (!exports.Any(CheckExport))
                TabControl.SelectedTab.Image = null;
        }

        private bool CheckExport(UObject export) // return true once you wanna stop searching for exports
        {
            switch (export)
            {
                case UTexture2D texture:
                {
                    TabControl.SelectedTab.RenderNearestNeighbor = texture.bRenderNearestNeighbor;
                    TabControl.SelectedTab.SetImage(texture.Decode());
                    return true;
                }
                case UAkMediaAssetData:
                case USoundWave:
                {
                    var shouldDecompress = UserSettings.Default.CompressedAudioMode == ECompressedAudio.PlayDecompressed;
                    export.Decode(shouldDecompress, out var audioFormat, out var data);
                    if (data == null || string.IsNullOrEmpty(audioFormat) || export.Owner == null)
                        return false;

                    SaveAndPlaySound(Path.Combine(TabControl.SelectedTab.Directory, TabControl.SelectedTab.Header.SubstringBeforeLast('.')).Replace('\\', '/'), audioFormat, data);
                    return false;
                }
                case UStaticMesh when UserSettings.Default.PreviewStaticMeshes:
                case USkeletalMesh when UserSettings.Default.PreviewSkeletalMeshes:
                case UMaterialInstance when UserSettings.Default.PreviewMaterials && !ModelIsOverwritingMaterial &&
                                            !(Game == FGame.FortniteGame && export.Owner != null && (export.Owner.Name.EndsWith($"/MI_OfferImages/{export.Name}", StringComparison.OrdinalIgnoreCase) ||
                                                export.Owner.Name.EndsWith($"/RenderSwitch_Materials/{export.Name}", StringComparison.OrdinalIgnoreCase) ||
                                                export.Owner.Name.EndsWith($"/MI_BPTile/{export.Name}", StringComparison.OrdinalIgnoreCase))):
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var modelViewer = Helper.GetWindow<ModelViewer>("Model Viewer", () => new ModelViewer().Show());
                        modelViewer.Load(export);
                    });
                    return true;
                }
                case UMaterialInstance m when ModelIsOverwritingMaterial:
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var modelViewer = Helper.GetWindow<ModelViewer>("Model Viewer", () => new ModelViewer().Show());
                        modelViewer.Overwrite(m);
                    });
                    return true;
                }
                case UStaticMesh when UserSettings.Default.SaveStaticMeshes:
                case USkeletalMesh when UserSettings.Default.SaveSkeletalMeshes:
                case UMaterialInstance when UserSettings.Default.SaveMaterials:
                case USkeleton when UserSettings.Default.SaveSkeletonAsMesh:
                case UAnimSequence when UserSettings.Default.SaveAnimations:
                {
                    SaveExport(export);
                    return true;
                }
                default:
                {
                    using var package = new CreatorPackage(export, UserSettings.Default.CosmeticStyle);
                    if (!package.TryConstructCreator(out var creator)) return false;

                    creator.ParseForInfo();
                    TabControl.SelectedTab.SetImage(creator.Draw());
                    return true;
                }
            }
        }

        private void SaveAndPlaySound(string fullPath, string ext, byte[] data)
        {
            var userDir = Path.Combine(UserSettings.Default.OutputDirectory, "Sounds");
            if (fullPath.StartsWith("/")) fullPath = fullPath[1..];
            var savedAudioPath = Path.Combine(userDir,
                UserSettings.Default.KeepDirectoryStructure ? fullPath : fullPath.SubstringAfterLast('/')).Replace('\\', '/') + $".{ext.ToLower()}";

            if (!UserSettings.Default.IsAutoOpenSounds)
            {
                Directory.CreateDirectory(savedAudioPath.SubstringBeforeLast('/'));
                using var stream = new FileStream(savedAudioPath, FileMode.Create, FileAccess.Write);
                using var writer = new BinaryWriter(stream);
                writer.Write(data);
                writer.Flush();
                return;
            }

            // TODO
            // since we are currently in a thread, the audio player's lifetime (memory-wise) will keep the current thread up and running until fmodel itself closes
            // the solution would be to kill the current thread at this line and then open the audio player without "Application.Current.Dispatcher.Invoke"
            // but the ThreadWorkerViewModel is an idiot and doesn't understand we want to kill the current thread inside the current thread and continue the code
            Application.Current.Dispatcher.Invoke(delegate
            {
                var audioPlayer = Helper.GetWindow<AudioPlayer>("Audio Player", () => new AudioPlayer().Show());
                audioPlayer.Load(data, savedAudioPath);
            });
        }

        private void SaveExport(UObject export)
        {
            var toSave = new Exporter(export, UserSettings.Default.TextureExportFormat, UserSettings.Default.LodExportFormat, UserSettings.Default.MeshExportFormat);
            var toSaveDirectory = new DirectoryInfo(UserSettings.Default.ModelDirectory);
            if (toSave.TryWriteToDir(toSaveDirectory, out var savedFileName))
            {
                Log.Information("Successfully saved {FileName}", savedFileName);
                FLogger.AppendInformation();
                FLogger.AppendText($"Successfully saved {savedFileName}", Constants.WHITE, true);
            }
            else
            {
                Log.Error("{FileName} could not be saved", savedFileName);
                FLogger.AppendError();
                FLogger.AppendText($"Could not save '{savedFileName}'", Constants.WHITE, true);
            }
        }

        public void ExportData(string fullPath)
        {
            var fileName = fullPath.SubstringAfterLast('/');
            var directory = Path.Combine(UserSettings.Default.OutputDirectory, "Exports");

            if (Provider.TrySavePackage(fullPath, out var assets))
            {
                foreach (var kvp in assets)
                {
                    var path = Path.Combine(directory, UserSettings.Default.KeepDirectoryStructure ? kvp.Key : kvp.Key.SubstringAfterLast('/')).Replace('\\', '/');
                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));
                    File.WriteAllBytes(path, kvp.Value);
                }

                Log.Information("{FileName} successfully exported", fileName);
                FLogger.AppendInformation();
                FLogger.AppendText($"Successfully exported '{fileName}'", Constants.WHITE, true);
            }
            else
            {
                Log.Error("{FileName} could not be exported", fileName);
                FLogger.AppendError();
                FLogger.AppendText($"Could not export '{fileName}'", Constants.WHITE, true);
            }
        }
    }
}
