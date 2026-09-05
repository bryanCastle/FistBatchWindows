using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoOrganizer.Controls;
using PhotoOrganizer.Models;
using PhotoOrganizer.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace PhotoOrganizer;

public sealed partial class MainPage : Page
{
    private const int ThumbnailDecodeWidth = 320;
    private const int ViewerDecodeWidth = 2200;
    private const int MaxConcurrentThumbnailLoads = 4;
    private const double TileWidth = 158;
    private const double TileHeight = 106;

    /// <summary>
    /// How far outside the viewport a date section may sit before its tiles are created or torn
    /// down. Twenty thousand empty buttons still cost more than the pictures they represent, so a
    /// day that is not on screen does not get tiles until it is about to be.
    /// </summary>
    private const double SectionTileLoadDistance = 900;

    private const double SectionTileUnloadDistance = 2800;

    /// <summary>
    /// How far outside the viewport a tile may sit and still start loading, so that scrolling
    /// reveals finished thumbnails rather than empty frames.
    /// </summary>
    private const double ThumbnailPreloadDistance = 800;

    /// <summary>
    /// How many pictures either side of the current one are decoded in advance and then kept
    /// decoded, so that moving between them is a straight swap rather than a wait. Forward gets
    /// the larger share because working through a folder almost always runs that way.
    ///
    /// The window is deliberately shallow. A viewer picture is held as finished pixels, so each
    /// one costs approximately <see cref="ViewerDecodeWidth"/> squared times four bytes, and these
    /// figures put the ceiling near eighty megabytes rather than letting it grow with the folder.
    /// </summary>
    private const int ViewerPrefetchAhead = 3;

    private const int ViewerPrefetchBehind = 2;

    /// <summary>
    /// How close to an edge the pointer has to come before that edge's chrome fades in. The bottom
    /// band is deeper because the action bar itself is taller than the title bar.
    /// </summary>
    private const double TopChromeRevealHeight = 104;

    private const double BottomChromeRevealHeight = 184;
    private const double ChromeFadeMilliseconds = 140;

    private const float ZoomPerWheelNotch = 1.25f;
    private const double WheelDeltaPerNotch = 120;
    private const float DoubleTapZoomFactor = 2.5f;
    private const float ZoomEpsilon = 0.001f;

    private readonly List<PhotoItem> _photos = [];
    private readonly Dictionary<PhotoItem, Button> _tilesByPhoto = new();
    private readonly Dictionary<TimelineSectionKey, TimelineSection> _sectionsByDate = new();
    private readonly SemaphoreSlim _thumbnailThrottle = new(MaxConcurrentThumbnailLoads);

    /// <summary>
    /// Decoded viewer pixels, keyed by path. Bounded by the prefetch window rather than by a
    /// count, so it always holds the pictures next to the one on screen and nothing else.
    /// Each display wraps a copy of these pixels in a fresh <see cref="SoftwareBitmapSource"/>.
    /// </summary>
    private readonly Dictionary<string, SoftwareBitmap> _viewerBitmaps = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _viewerPrefetchesRunning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Prefetching runs one picture at a time. Several full decodes at once would compete with the
    /// picture the user is actually waiting on, which is the opposite of the intent.
    /// </summary>
    private readonly SemaphoreSlim _viewerPrefetchThrottle = new(1);
    private readonly Stack<ReversibleAction> _undoStack = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly SolidColorBrush _tileBackgroundBrush = new(Color.FromArgb(255, 17, 17, 17));
    private readonly SolidColorBrush _tileBorderBrush = new(Color.FromArgb(255, 62, 62, 62));
    private readonly SolidColorBrush _mutedTextBrush = new(Color.FromArgb(255, 175, 175, 175));
    private readonly SolidColorBrush _selectedTileBorderBrush = new(Color.FromArgb(255, 220, 220, 220));
    private readonly HashSet<PhotoItem> _selectedPhotos = [];

    private CancellationTokenSource _loadCancellation = new();
    private string? _selectedFolder;
    private int _currentIndex = -1;
    private int _viewerGeneration;
    private bool _rulesDismissed;
    private bool _isUndoing;
    private bool _isTopChromeShown;
    private bool _isBottomChromeShown;
    private ViewStage _stage = ViewStage.Welcome;
    private Point _panOrigin;
    private double _panOriginHorizontalOffset;
    private double _panOriginVerticalOffset;
    private bool _isPanning;
    private bool _isSelectMode;
    private bool _isBatchMoving;
    private PhotoItem? _selectionAnchor;
    private string _activeTypeFilter = "All";

    public MainPage()
    {
        InitializeComponent();
        Loaded += MainPage_Loaded;

        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusBanner.Visibility = Visibility.Collapsed;
        };

        AddShortcut(VirtualKey.Left, (_, args) =>
        {
            if (_stage == ViewStage.Photo)
            {
                ShowPreviousPhoto();
                args.Handled = true;
            }
        });
        AddShortcut(VirtualKey.Right, (_, args) =>
        {
            if (_stage == ViewStage.Photo)
            {
                ShowNextPhoto();
                args.Handled = true;
            }
        });
        // The button's own enabled state doubles as the guard against a second key press landing
        // while a move is still in flight, which a keyboard accelerator would otherwise bypass.
        AddShortcut(VirtualKey.D, async (_, args) =>
        {
            if (_stage == ViewStage.Photo && DeleteButton.IsEnabled)
            {
                args.Handled = true;
                await DeleteCurrentPhotoAsync();
                return;
            }

            if (_stage == ViewStage.Timeline && _selectedPhotos.Count > 0 && DeleteSelectedButton.IsEnabled)
            {
                args.Handled = true;
                await DeleteSelectedPhotosAsync();
            }
        });
        AddShortcut(VirtualKey.S, async (_, args) =>
        {
            if (_stage != ViewStage.Photo || !LikeButton.IsEnabled)
            {
                return;
            }

            args.Handled = true;
            await LikeCurrentPhotoAsync();
        });
        AddShortcut(VirtualKey.Escape, (_, args) =>
        {
            if (_stage == ViewStage.Photo)
            {
                SetStage(ViewStage.Timeline);
                args.Handled = true;
                return;
            }

            if (_stage == ViewStage.Timeline && (_isSelectMode || _selectedPhotos.Count > 0))
            {
                ExitSelectMode();
                args.Handled = true;
            }
        });
        AddShortcut(VirtualKey.A, (_, args) =>
        {
            if (_stage != ViewStage.Timeline)
            {
                return;
            }

            args.Handled = true;
            SelectAllPhotos();
        }, VirtualKeyModifiers.Control);
        AddShortcut(VirtualKey.O, async (_, args) =>
        {
            args.Handled = true;
            await PickFolderAsync();
        }, VirtualKeyModifiers.Control);
        AddShortcut(VirtualKey.Z, async (_, args) =>
        {
            args.Handled = true;
            await UndoLastActionAsync();
        }, VirtualKeyModifiers.Control);
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        var startupFolder = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(Directory.Exists);
        if (startupFolder is not null)
        {
            await LoadFolderAsync(startupFolder);
        }
    }

    private void AddShortcut(
        VirtualKey key,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler,
        VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        KeyboardAccelerators.Add(accelerator);
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e) => await PickFolderAsync();

    private async Task PickFolderAsync()
    {
        if (_isBatchMoving)
        {
            return;
        }

        try
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            var folderPath = NativeFolderPicker.PickFolder(windowHandle);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await LoadFolderAsync(folderPath);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"The folder could not be opened: {ex.Message}");
        }
    }

    private async Task LoadFolderAsync(string folderPath)
    {
        var cancellationToken = RestartLoadCancellation();
        PhotoImageLoader.ResetPreviewCache();
        ShowLoading(true, "Reading picture dates\u2026");

        try
        {
            var loaded = await Task.Run(
                () => PhotoLibraryService.LoadLibrary(folderPath, cancellationToken),
                cancellationToken);

            _photos.Clear();
            _photos.AddRange(loaded.Order(Comparer<PhotoItem>.Create(ComparePhotoOrder)));

            // Undo history and decoded pictures both belong to the previous folder.
            _undoStack.Clear();
            ClearViewerImages();
            ExitSelectMode();
            _selectedFolder = folderPath;
            _currentIndex = -1;
            FolderPathText.Text = folderPath;
            BuildTimeline();
            SetStage(ViewStage.Timeline);

            if (_photos.Count == 0)
            {
                ShowStatus("No supported pictures were found in this folder.");
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer folder selection.
        }
        catch (Exception ex)
        {
            ShowStatus($"The pictures could not be loaded: {ex.Message}");
        }
        finally
        {
            ShowLoading(false);
        }
    }

    /// <summary>
    /// Cancels thumbnail work from the previous folder. The old source is not disposed because
    /// in-flight loaders may still be waiting on its token.
    /// </summary>
    private CancellationToken RestartLoadCancellation()
    {
        _loadCancellation.Cancel();
        _loadCancellation = new CancellationTokenSource();
        return _loadCancellation.Token;
    }

    private void BuildTimeline()
    {
        TimelineStack.Children.Clear();
        _tilesByPhoto.Clear();
        _sectionsByDate.Clear();
        UpdateEmptyTimelineVisibility();

        // Undated pictures form a trailing section so they never displace real dates.
        var groups = GetTimelinePhotos()
            .GroupBy(photo => photo.TakenAt?.LocalDateTime.Date)
            .OrderBy(group => group.Key.HasValue ? 0 : 1)
            .ThenByDescending(group => group.Key ?? DateTime.MinValue);

        foreach (var group in groups)
        {
            var section = CreateSection(group.Key, group.Count());
            _sectionsByDate[new TimelineSectionKey(group.Key)] = section;
            TimelineStack.Children.Add(section.Root);

            var ordered = group
                .OrderBy(photo => photo.TakenAt ?? DateTimeOffset.MinValue)
                .ThenBy(photo => photo.FileName, StringComparer.OrdinalIgnoreCase);

            foreach (var photo in ordered)
            {
                section.Items.Add(photo);
            }

            AttachSectionHeadingSelection(section);
            AttachSectionVirtualization(section);
        }
    }

    private void TypeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeFilterComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string filter)
        {
            return;
        }

        _activeTypeFilter = filter;
        if (_stage != ViewStage.Timeline)
        {
            return;
        }

        ClearSelection();
        BuildTimeline();
    }

    private IEnumerable<PhotoItem> GetTimelinePhotos()
        => _photos.Where(MatchesActiveTypeFilter);

    private bool MatchesActiveTypeFilter(PhotoItem photo)
    {
        var extension = Path.GetExtension(photo.Path);
        return _activeTypeFilter switch
        {
            "All" => true,
            "JPEG" => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                      || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            "PNG" => extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
            "HEIC" => extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                      || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase),
            "TIFF" => extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
                      || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase),
            "WEBP" => extension.Equals(".webp", StringComparison.OrdinalIgnoreCase),
            "GIF" => extension.Equals(".gif", StringComparison.OrdinalIgnoreCase),
            "BMP" => extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase),
            "RAW" => PictureFormats.IsRaw(extension),
            _ => true,
        };
    }

    private TimelineSection CreateSection(DateTime? date, int photoCount)
    {
        var titleText = new TextBlock
        {
            Text = date?.ToString("MMMM d, yyyy") ?? "Not dated",
            FontSize = 19,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var countText = new TextBlock
        {
            Text = FormatPhotoCount(photoCount),
            Foreground = _mutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
        };
        heading.Children.Add(titleText);
        heading.Children.Add(countText);

        var photos = new PhotoWrapPanel
        {
            HorizontalSpacing = 9,
            VerticalSpacing = 9,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // The date is kept on the panel so a restored picture can find where its section belongs.
        var root = new StackPanel { Spacing = 12, Tag = date };
        root.Children.Add(heading);
        root.Children.Add(photos);

        return new TimelineSection
        {
            Root = root,
            Heading = heading,
            Photos = photos,
            CountText = countText,
        };
    }

    private void AttachSectionHeadingSelection(TimelineSection section)
    {
        section.Heading.Tapped += (_, args) =>
        {
            if (!_isSelectMode && _selectedPhotos.Count == 0)
            {
                return;
            }

            // The transparent part of a date heading reads as timeline background. It is an
            // intuitive place to cancel selection, not an invisible "select this whole day"
            // target that can accidentally include an entire shoot.
            ExitSelectMode();
            args.Handled = true;
        };
    }

    /// <summary>
    /// Tiles are created when the date is about to appear and dropped again once it has scrolled
    /// well off screen, so a library of tens of thousands of pictures does not keep a button and a
    /// bitmap for every day at once.
    /// </summary>
    private void AttachSectionVirtualization(TimelineSection section)
    {
        section.Root.EffectiveViewportChanged += (_, args) =>
        {
            var distance = Math.Max(args.BringIntoViewDistanceX, args.BringIntoViewDistanceY);
            if (distance <= SectionTileLoadDistance)
            {
                EnsureSectionTiles(section);
            }
            else if (distance >= SectionTileUnloadDistance)
            {
                UnloadSectionTiles(section);
            }
        };
    }

    private void EnsureSectionTiles(TimelineSection section)
    {
        if (section.TilesLoaded)
        {
            return;
        }

        foreach (var photo in section.Items)
        {
            var tile = CreateTile(photo);
            _tilesByPhoto[photo] = tile;
            section.Photos.Children.Add(tile);
        }

        section.TilesLoaded = true;
    }

    private void UnloadSectionTiles(TimelineSection section)
    {
        if (!section.TilesLoaded)
        {
            return;
        }

        foreach (var child in section.Photos.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is PhotoItem photo)
            {
                _tilesByPhoto.Remove(photo);
            }
        }

        section.Photos.Children.Clear();
        section.TilesLoaded = false;
    }

    private Button CreateTile(PhotoItem photo)
    {
        // Uniform keeps the whole frame visible. Filling the tile would crop portrait pictures
        // down to a sliver of their top edge, which defeats scanning a folder quickly.
        var image = new Image { Stretch = Stretch.Uniform };
        var tile = new Button
        {
            Width = TileWidth,
            Height = TileHeight,
            Padding = new Thickness(0),
            Background = _tileBackgroundBrush,
            BorderBrush = _tileBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Content = image,
            Tag = photo,
        };

        ApplyTileSelectionStyle(tile, _selectedPhotos.Contains(photo));
        ToolTipService.SetToolTip(tile, BuildTileTooltip(photo));
        AutomationProperties.SetName(tile, $"Open {photo.FileName}");
        tile.Click += TimelinePhoto_Click;
        AttachLazyThumbnail(tile, image, photo);
        return tile;
    }

    private static string BuildTileTooltip(PhotoItem photo) => photo.TakenAt is { } takenAt
        ? $"{photo.FileName}\n{takenAt:MMMM d, yyyy h:mm tt}"
        : $"{photo.FileName}\nNo date recorded";

    private static string FormatPhotoCount(int count)
        => count == 1 ? "\u00b7 1 picture" : $"\u00b7 {count} pictures";

    /// <summary>
    /// Defers thumbnail decoding until the tile is near the viewport. Loading every picture up
    /// front is what makes large folders feel slow, and RAW previews make that cost worse.
    /// </summary>
    private void AttachLazyThumbnail(Button tile, Image target, PhotoItem photo)
    {
        var cancellationToken = _loadCancellation.Token;

        void OnViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            var distance = Math.Max(args.BringIntoViewDistanceX, args.BringIntoViewDistanceY);
            if (distance > ThumbnailPreloadDistance)
            {
                return;
            }

            sender.EffectiveViewportChanged -= OnViewportChanged;
            _ = LoadThumbnailAsync(target, photo, cancellationToken);
        }

        tile.EffectiveViewportChanged += OnViewportChanged;
    }

    private async Task LoadThumbnailAsync(Image target, PhotoItem photo, CancellationToken cancellationToken)
    {
        try
        {
            await _thumbnailThrottle.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var source = await PhotoImageLoader.CreateThumbnailAsync(
                photo,
                ThumbnailDecodeWidth,
                cancellationToken);

            if (source is not null && !cancellationToken.IsCancellationRequested && target.Parent is not null)
            {
                target.Source = source;
            }
        }
        catch (OperationCanceledException)
        {
            // The folder changed while this thumbnail was loading.
        }
        catch (Exception)
        {
            // An undecodable tile stays blank rather than interrupting the timeline.
        }
        finally
        {
            _thumbnailThrottle.Release();
        }
    }

    private void TimelinePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PhotoItem photo })
        {
            return;
        }

        var shift = IsModifierDown(VirtualKey.Shift);
        var control = IsModifierDown(VirtualKey.Control);
        if (_isSelectMode || shift || control)
        {
            ApplyTimelineClick(photo, shift);
            return;
        }

        ClearSelection();
        _isSelectMode = false;
        UpdateSelectionBar();
        _currentIndex = _photos.IndexOf(photo);
        if (_currentIndex >= 0)
        {
            ShowCurrentPhoto(keepPreviousWhileLoading: false);
            SetStage(ViewStage.Photo);
        }
    }

    private static bool IsModifierDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void SelectModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSelectMode)
        {
            ExitSelectMode();
            return;
        }

        _isSelectMode = true;
        UpdateSelectionBar();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        => await DeleteSelectedPhotosAsync();

    private async void MoveSelected_Click(object sender, RoutedEventArgs e)
        => await MoveSelectedToNamedFolderAsync();

    private void ExitSelectMode()
    {
        _isSelectMode = false;
        ClearSelection();
    }

    private void ClearSelection()
    {
        if (_selectedPhotos.Count == 0 && !_isSelectMode)
        {
            UpdateSelectionBar();
            return;
        }

        var previouslySelected = _selectedPhotos.ToList();
        _selectedPhotos.Clear();
        _selectionAnchor = null;
        foreach (var photo in previouslySelected)
        {
            RefreshTileSelection(photo);
        }

        UpdateSelectionBar();
    }

    private void SelectAllPhotos()
    {
        _isSelectMode = true;
        var visiblePhotos = GetTimelinePhotos().ToList();
        foreach (var photo in visiblePhotos)
        {
            _selectedPhotos.Add(photo);
        }

        _selectionAnchor = visiblePhotos.FirstOrDefault();
        RefreshLoadedTileSelection();
        UpdateSelectionBar();
    }

    private void ToggleSectionSelection(TimelineSection section)
    {
        _isSelectMode = true;
        var allSelected = section.Items.Count > 0 && section.Items.All(_selectedPhotos.Contains);
        foreach (var photo in section.Items)
        {
            if (allSelected)
            {
                _selectedPhotos.Remove(photo);
            }
            else
            {
                _selectedPhotos.Add(photo);
            }

            RefreshTileSelection(photo);
        }

        _selectionAnchor = section.Items.FirstOrDefault();
        UpdateSelectionBar();
    }

    private void ApplyTimelineClick(PhotoItem photo, bool shift)
    {
        _isSelectMode = true;
        if (shift && _selectionAnchor is not null)
        {
            SelectRange(_selectionAnchor, photo);
            UpdateSelectionBar();
            return;
        }

        if (!_selectedPhotos.Remove(photo))
        {
            _selectedPhotos.Add(photo);
        }

        _selectionAnchor = photo;
        RefreshTileSelection(photo);
        UpdateSelectionBar();
    }

    private void SelectRange(PhotoItem from, PhotoItem to)
    {
        var visiblePhotos = GetTimelinePhotos().ToList();
        var start = visiblePhotos.IndexOf(from);
        var end = visiblePhotos.IndexOf(to);
        if (start < 0 || end < 0)
        {
            return;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        for (var index = start; index <= end; index++)
        {
            _selectedPhotos.Add(visiblePhotos[index]);
            RefreshTileSelection(visiblePhotos[index]);
        }
    }

    private void RefreshLoadedTileSelection()
    {
        foreach (var (photo, tile) in _tilesByPhoto)
        {
            ApplyTileSelectionStyle(tile, _selectedPhotos.Contains(photo));
        }
    }

    private void RefreshTileSelection(PhotoItem photo)
    {
        if (_tilesByPhoto.TryGetValue(photo, out var tile))
        {
            ApplyTileSelectionStyle(tile, _selectedPhotos.Contains(photo));
        }
    }

    private void ApplyTileSelectionStyle(Button tile, bool selected)
    {
        tile.BorderBrush = selected ? _selectedTileBorderBrush : _tileBorderBrush;
        tile.BorderThickness = new Thickness(selected ? 3 : 1);
        tile.Background = selected
            ? new SolidColorBrush(Color.FromArgb(255, 36, 36, 36))
            : _tileBackgroundBrush;
    }

    private void UpdateSelectionBar()
    {
        var count = _selectedPhotos.Count;
        SelectionBar.Visibility = _isSelectMode || count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectModeButton.Content = _isSelectMode ? "Done" : "Select";
        SelectionCountText.Text = count switch
        {
            0 => "Select pictures, or click a date to take the whole day.",
            1 => "1 selected",
            _ => $"{count} selected",
        };

        var canAct = count > 0 && !_isBatchMoving;
        DeleteSelectedButton.IsEnabled = canAct;
        MoveSelectedButton.IsEnabled = canAct;
    }

    private IReadOnlyList<PhotoItem> SnapshotSelection()
        => _photos.Where(_selectedPhotos.Contains).ToList();

    private async Task DeleteSelectedPhotosAsync()
    {
        if (_selectedFolder is null || _isBatchMoving)
        {
            return;
        }

        var selected = SnapshotSelection();
        if (selected.Count == 0)
        {
            return;
        }

        var libraryRoot = _selectedFolder;
        await MoveSelectedPhotosAsync(
            selected,
            ReversibleActionKind.Deleted,
            PhotoLibraryService.DeletedFolderName,
            photo => PhotoLibraryService.MoveToDeleted(photo.Path, libraryRoot));
    }

    private async Task MoveSelectedToNamedFolderAsync()
    {
        if (_selectedFolder is null || _isBatchMoving)
        {
            return;
        }

        var selected = SnapshotSelection();
        if (selected.Count == 0)
        {
            return;
        }

        var folderName = await PromptForFolderNameAsync();
        if (folderName is null)
        {
            return;
        }

        var libraryRoot = _selectedFolder;
        await MoveSelectedPhotosAsync(
            selected,
            ReversibleActionKind.Sorted,
            folderName,
            photo => PhotoLibraryService.MoveToNamedFolder(photo.Path, libraryRoot, folderName));
    }

    private async Task<string?> PromptForFolderNameAsync()
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "Folder name",
            MaxLength = 80,
        };
        var errorText = new TextBlock
        {
            Foreground = _mutedTextBrush,
            TextWrapping = TextWrapping.WrapWholeWords,
            Text = "Pictures move into this folder beside the library.",
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(nameBox);
        content.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Add to new folder",
            Content = content,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            IsPrimaryButtonEnabled = false,
        };

        nameBox.TextChanged += (_, _) =>
        {
            var error = PhotoLibraryService.ValidateFolderName(nameBox.Text);
            dialog.IsPrimaryButtonEnabled = error is null;
            errorText.Text = error
                ?? "Pictures move into this folder beside the library.";
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var name = nameBox.Text.Trim();
        var validation = PhotoLibraryService.ValidateFolderName(name);
        if (validation is not null)
        {
            ShowStatus(validation);
            return null;
        }

        return name;
    }

    /// <summary>
    /// Moves the current selection on a background thread, then removes those tiles in one pass.
    /// A single undo entry covers the whole batch, so Ctrl+Z does not have to be pressed once
    /// per picture.
    /// </summary>
    private async Task MoveSelectedPhotosAsync(
        IReadOnlyList<PhotoItem> selected,
        ReversibleActionKind kind,
        string destinationName,
        Func<PhotoItem, string> move)
    {
        _isBatchMoving = true;
        UpdateSelectionBar();
        ShowStatus($"Moving {selected.Count} picture{(selected.Count == 1 ? "" : "s")}\u2026");

        var moved = new List<MovedPicture>(selected.Count);
        try
        {
            await Task.Run(() =>
            {
                foreach (var photo in selected)
                {
                    var destination = move(photo);
                    moved.Add(new MovedPicture(photo, destination));
                }
            });
        }
        catch (Exception ex)
        {
            if (moved.Count > 0)
            {
                _undoStack.Push(new ReversibleAction(kind, moved, destinationName));
                RemovePhotosFromTimeline(moved.Select(item => item.Photo).ToList());
            }

            _isBatchMoving = false;
            UpdateSelectionBar();
            ShowStatus($"Stopped after {moved.Count} of {selected.Count}: {ex.Message}");
            return;
        }

        _undoStack.Push(new ReversibleAction(kind, moved, destinationName));
        RemovePhotosFromTimeline(selected);
        ClearSelection();
        _isSelectMode = true;
        _isBatchMoving = false;
        UpdateSelectionBar();

        var verb = kind == ReversibleActionKind.Deleted ? "Deleted" : "Moved";
        ShowStatus($"{verb} {moved.Count} picture{(moved.Count == 1 ? "" : "s")} to {destinationName}. Ctrl+Z brings them back.");
    }

    private void RemovePhotosFromTimeline(IReadOnlyList<PhotoItem> photos)
    {
        foreach (var photo in photos)
        {
            var index = _photos.IndexOf(photo);
            if (index < 0)
            {
                continue;
            }

            _photos.RemoveAt(index);
            if (index < _currentIndex)
            {
                _currentIndex--;
            }
            else if (index == _currentIndex)
            {
                _currentIndex = Math.Min(_currentIndex, _photos.Count - 1);
            }

            RemoveTile(photo);
            _selectedPhotos.Remove(photo);
            if (_viewerBitmaps.Remove(photo.Path, out var pixels))
            {
                pixels.Dispose();
            }
        }

        if (_photos.Count == 0)
        {
            _currentIndex = -1;
        }

        UpdateEmptyTimelineVisibility();
    }

    /// <summary>
    /// Draws the current picture. When <paramref name="keepPreviousWhileLoading"/> is set, the
    /// picture already on screen stays until the replacement is decoded, which is what removes the
    /// black frame while stepping through a folder. Opening a picture from the timeline clears it
    /// instead, because whatever was last viewed bears no relation to the one being opened and
    /// standing in for it would mislead rather than smooth.
    /// </summary>
    private void ShowCurrentPhoto(bool keepPreviousWhileLoading = true)
    {
        if (!TryGetCurrentPhoto(out var photo))
        {
            return;
        }

        PhotoNameText.Text = photo.FileName;
        PhotoPositionText.Text = BuildPositionText(photo);
        BackButton.IsEnabled = CanMoveBy(-1);
        ForwardButton.IsEnabled = CanMoveBy(1);
        DeleteButton.IsEnabled = true;
        LikeButton.IsEnabled = true;
        RulesPanel.Visibility = _rulesDismissed ? Visibility.Collapsed : Visibility.Visible;

        var generation = ++_viewerGeneration;
        if (!keepPreviousWhileLoading)
        {
            PhotoImage.Source = null;
        }

        _ = DisplayCurrentPhotoAsync(photo, generation, _loadCancellation.Token);
    }

    /// <summary>
    /// The generation guard discards results from pictures the user has already navigated past.
    /// A late result is still worth keeping, because it is a picture near where the user now is.
    /// </summary>
    private async Task DisplayCurrentPhotoAsync(PhotoItem photo, int generation, CancellationToken cancellationToken)
    {
        try
        {
            // A neighbour the prefetch is already decoding should not be decoded a second time.
            if (_viewerPrefetchesRunning.Contains(photo.Path))
            {
                await WaitForCachedViewerBitmapAsync(photo, cancellationToken);
            }

            if (!_viewerBitmaps.TryGetValue(photo.Path, out var pixels))
            {
                pixels = await PhotoImageLoader.CreateViewerBitmapAsync(
                    photo,
                    ViewerDecodeWidth,
                    cancellationToken);

                if (pixels is not null)
                {
                    _viewerBitmaps[photo.Path] = pixels;
                }
            }

            if (generation != _viewerGeneration || cancellationToken.IsCancellationRequested)
            {
                TrimViewerBitmaps();
                return;
            }

            if (pixels is null)
            {
                PhotoImage.Source = null;
                ShowStatus("No viewable image could be read from this file.");
                return;
            }

            await ShowViewerPixelsAsync(pixels);
            ResetZoomIfNeeded();
            TrimViewerBitmaps();
            StartViewerPrefetch();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (Exception ex)
        {
            ShowStatus($"This picture could not be displayed: {ex.Message}");
        }
    }

    /// <summary>
    /// Wraps a copy of the cached pixels in a new source every time. A <see cref="SoftwareBitmapSource"/>
    /// is closed by XAML when the Image moves on, so showing the same source object again crashes
    /// the process with <c>RO_E_CLOSED</c>.
    /// </summary>
    private async Task ShowViewerPixelsAsync(SoftwareBitmap pixels)
    {
        var display = SoftwareBitmap.Copy(pixels);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(display);
        PhotoImage.Source = source;
    }

    /// <summary>
    /// Decodes the pictures around the current one, nearest first, so that reaching them costs
    /// nothing. Already decoded and already running pictures are skipped, which makes this safe to
    /// call after every move.
    /// </summary>
    private void StartViewerPrefetch()
    {
        foreach (var index in EnumeratePrefetchIndexes())
        {
            var photo = _photos[index];
            if (_viewerBitmaps.ContainsKey(photo.Path) || !_viewerPrefetchesRunning.Add(photo.Path))
            {
                continue;
            }

            _ = PrefetchViewerBitmapAsync(photo, _loadCancellation.Token);
        }
    }

    private async Task WaitForCachedViewerBitmapAsync(PhotoItem photo, CancellationToken cancellationToken)
    {
        while (_viewerPrefetchesRunning.Contains(photo.Path) && !_viewerBitmaps.ContainsKey(photo.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(16, cancellationToken);
        }
    }

    /// <summary>
    /// Nearest neighbours first, and forward before back at equal distance, so the most likely
    /// next picture is the one that finishes first.
    /// </summary>
    private IEnumerable<int> EnumeratePrefetchIndexes()
    {
        var furthest = Math.Max(ViewerPrefetchAhead, ViewerPrefetchBehind);
        for (var distance = 1; distance <= furthest; distance++)
        {
            if (distance <= ViewerPrefetchAhead && _currentIndex + distance < _photos.Count)
            {
                yield return _currentIndex + distance;
            }

            if (distance <= ViewerPrefetchBehind && _currentIndex - distance >= 0)
            {
                yield return _currentIndex - distance;
            }
        }
    }

    private async Task PrefetchViewerBitmapAsync(PhotoItem photo, CancellationToken cancellationToken)
    {
        try
        {
            await _viewerPrefetchThrottle.WaitAsync(cancellationToken);
            try
            {
                if (!IsWithinPrefetchWindow(photo) || _viewerBitmaps.ContainsKey(photo.Path))
                {
                    return;
                }

                var pixels = await PhotoImageLoader.CreateViewerBitmapAsync(
                    photo,
                    ViewerDecodeWidth,
                    cancellationToken);

                if (pixels is null || cancellationToken.IsCancellationRequested)
                {
                    pixels?.Dispose();
                    return;
                }

                if (!IsWithinPrefetchWindow(photo))
                {
                    pixels.Dispose();
                    return;
                }

                _viewerBitmaps[photo.Path] = pixels;
                TrimViewerBitmaps();
            }
            finally
            {
                _viewerPrefetchThrottle.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The folder changed, so this picture is no longer wanted.
        }
        catch (Exception)
        {
            // A picture that will not decode ahead of time is simply left to report its failure
            // when the user actually opens it.
        }
        finally
        {
            _viewerPrefetchesRunning.Remove(photo.Path);
        }
    }

    private bool IsWithinPrefetchWindow(PhotoItem photo)
    {
        var index = _photos.IndexOf(photo);
        return index >= 0
            && index >= _currentIndex - ViewerPrefetchBehind
            && index <= _currentIndex + ViewerPrefetchAhead;
    }

    /// <summary>
    /// Drops decoded pictures that the window has moved away from, which is what keeps this a
    /// fixed cost rather than one that grows with every picture visited. A picture removed from the
    /// folder falls out here too, because it is no longer at any index. The Image holds its own
    /// copy of the pixels on screen, so disposing the cache entry is safe.
    /// </summary>
    private void TrimViewerBitmaps()
    {
        if (_viewerBitmaps.Count == 0)
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = _currentIndex - ViewerPrefetchBehind; index <= _currentIndex + ViewerPrefetchAhead; index++)
        {
            if (index >= 0 && index < _photos.Count)
            {
                keep.Add(_photos[index].Path);
            }
        }

        foreach (var path in _viewerBitmaps.Keys.Where(path => !keep.Contains(path)).ToList())
        {
            if (_viewerBitmaps.Remove(path, out var pixels))
            {
                pixels.Dispose();
            }
        }
    }

    private void ClearViewerImages()
    {
        foreach (var pixels in _viewerBitmaps.Values)
        {
            pixels.Dispose();
        }

        _viewerBitmaps.Clear();
    }

    /// <summary>
    /// A <see cref="ScrollViewer"/> measures its content unconstrained, so the surface needs an
    /// explicit size for the picture to fit the frame at a zoom factor of 1.
    /// </summary>
    private void PhotoScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PhotoZoomSurface.Width = e.NewSize.Width;
        PhotoZoomSurface.Height = e.NewSize.Height;
    }

    /// <summary>
    /// The picture surface sits inside a <see cref="ScrollViewer"/> and the chrome sits outside it,
    /// so this is handled on the view itself as well. Pointer events bubble, which means one handler
    /// here also sees movement over the bars once they are showing.
    /// </summary>
    private void PhotoView_PointerMoved(object sender, PointerRoutedEventArgs e) => UpdateChromeForPointer(e);

    private void PhotoView_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Moving onto a revealed button also raises this through bubbling, so the bars are only
        // dismissed once the pointer has genuinely left the view.
        if (IsPointerWithin(PhotoView, e))
        {
            return;
        }

        ShowTopChrome(false);
        ShowBottomChrome(false);
    }

    private void UpdateChromeForPointer(PointerRoutedEventArgs e)
    {
        var pointerY = e.GetCurrentPoint(PhotoView).Position.Y;
        ShowTopChrome(pointerY <= TopChromeRevealHeight);
        ShowBottomChrome(pointerY >= PhotoView.ActualHeight - BottomChromeRevealHeight);
    }

    private static bool IsPointerWithin(FrameworkElement element, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(element).Position;
        return position.X >= 0
            && position.Y >= 0
            && position.X <= element.ActualWidth
            && position.Y <= element.ActualHeight;
    }

    private void ShowTopChrome(bool show)
    {
        if (_isTopChromeShown == show)
        {
            return;
        }

        _isTopChromeShown = show;
        ApplyChromeVisibility(TopChrome, show);
    }

    private void ShowBottomChrome(bool show)
    {
        if (_isBottomChromeShown == show)
        {
            return;
        }

        _isBottomChromeShown = show;
        ApplyChromeVisibility(BottomChrome, show);
    }

    private void ApplyChromeVisibility(FrameworkElement chrome, bool show)
    {
        // Hit testing is dropped immediately so a bar on its way out cannot swallow a click.
        chrome.IsHitTestVisible = show;
        if (show)
        {
            chrome.Visibility = Visibility.Visible;
        }

        var fade = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(ChromeFadeMilliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(fade, chrome);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        if (!show)
        {
            storyboard.Completed += (_, _) => CollapseIfStillHidden(chrome);
        }

        storyboard.Begin();
    }

    /// <summary>
    /// Guards against the pointer returning mid-fade, which would otherwise collapse a bar that has
    /// already been asked to come back.
    /// </summary>
    private void CollapseIfStillHidden(FrameworkElement chrome)
    {
        var isShown = ReferenceEquals(chrome, TopChrome) ? _isTopChromeShown : _isBottomChromeShown;
        if (!isShown)
        {
            chrome.Visibility = Visibility.Collapsed;
        }
    }

    private void PhotoZoomSurface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PhotoScroller);
        var notches = point.Properties.MouseWheelDelta / WheelDeltaPerNotch;
        if (notches == 0)
        {
            return;
        }

        // Handled stops the ScrollViewer from also scrolling in response to the same notch.
        e.Handled = true;
        ZoomTowards(point.Position, (float)(PhotoScroller.ZoomFactor * Math.Pow(ZoomPerWheelNotch, notches)));
    }

    /// <summary>
    /// The rules card floats over the middle of the picture, which is exactly where a hand rests,
    /// so a wheel notch aimed at zooming would otherwise die on the card until it is dismissed.
    /// Forwarding is safe because the wheel handler anchors on the scroll viewer rather than on
    /// whichever element raised the event.
    /// </summary>
    private void RulesPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        => PhotoZoomSurface_PointerWheelChanged(sender, e);

    private void PhotoZoomSurface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (IsZoomedIn())
        {
            ResetZoom();
            return;
        }

        ZoomTowards(e.GetPosition(PhotoScroller), DoubleTapZoomFactor);
    }

    /// <summary>
    /// Holds the point under the cursor still while the zoom factor changes, so zooming follows
    /// whatever the user is pointing at instead of drifting towards the middle of the frame.
    /// </summary>
    private void ZoomTowards(Point viewportPoint, float requestedZoom)
    {
        var currentZoom = PhotoScroller.ZoomFactor;
        var targetZoom = Math.Clamp(requestedZoom, PhotoScroller.MinZoomFactor, PhotoScroller.MaxZoomFactor);
        if (Math.Abs(targetZoom - currentZoom) < ZoomEpsilon)
        {
            return;
        }

        var growth = targetZoom / currentZoom;
        PhotoScroller.ChangeView(
            (PhotoScroller.HorizontalOffset + viewportPoint.X) * growth - viewportPoint.X,
            (PhotoScroller.VerticalOffset + viewportPoint.Y) * growth - viewportPoint.Y,
            targetZoom,
            disableAnimation: true);
    }

    private void PhotoZoomSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PhotoScroller);
        if (!IsZoomedIn() || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _panOrigin = point.Position;
        _panOriginHorizontalOffset = PhotoScroller.HorizontalOffset;
        _panOriginVerticalOffset = PhotoScroller.VerticalOffset;
        _isPanning = PhotoZoomSurface.CapturePointer(e.Pointer);
        e.Handled = _isPanning;
    }

    /// <summary>
    /// Offsets are measured against the viewport rather than the picture, so the anchor stays
    /// still while the content moves underneath the cursor.
    /// </summary>
    private void PhotoZoomSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning)
        {
            // Dragging the picture around should not keep flashing the chrome, so the bars are only
            // reconsidered while the pointer is moving freely.
            UpdateChromeForPointer(e);
            return;
        }

        var position = e.GetCurrentPoint(PhotoScroller).Position;
        PhotoScroller.ChangeView(
            _panOriginHorizontalOffset - (position.X - _panOrigin.X),
            _panOriginVerticalOffset - (position.Y - _panOrigin.Y),
            zoomFactor: null,
            disableAnimation: true);
        e.Handled = true;
    }

    private void PhotoZoomSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        // Cleared first because releasing capture raises PointerCaptureLost back into this handler.
        _isPanning = false;
        PhotoZoomSurface.ReleasePointerCapture(e.Pointer);
    }

    private bool IsZoomedIn() => PhotoScroller.ZoomFactor > PhotoScroller.MinZoomFactor + ZoomEpsilon;

    private void ResetZoom()
    {
        _isPanning = false;
        PhotoScroller.ChangeView(0, 0, PhotoScroller.MinZoomFactor, disableAnimation: true);
    }

    /// <summary>
    /// <see cref="ScrollViewer.ChangeView"/> redraws the frame even when nothing has changed, and
    /// that redraw is visible as a flash between pictures that are already at fit. Skip it when
    /// the viewer is already showing a fitted picture.
    /// </summary>
    private void ResetZoomIfNeeded()
    {
        _isPanning = false;
        if (!IsZoomedIn() && PhotoScroller.HorizontalOffset == 0 && PhotoScroller.VerticalOffset == 0)
        {
            return;
        }

        PhotoScroller.ChangeView(0, 0, PhotoScroller.MinZoomFactor, disableAnimation: true);
    }

    private string BuildPositionText(PhotoItem photo)
    {
        var (start, end) = GetBatchBounds(_currentIndex);
        var position = $"{_currentIndex - start + 1} of {end - start + 1}";
        return photo.TakenAt is { } takenAt
            ? $"{position}  \u00b7  {takenAt:MMM d, yyyy}"
            : $"{position}  \u00b7  No date";
    }

    /// <summary>
    /// Dated days are contiguous in the list, newest day first, so a linear walk from the current
    /// picture finds the day's edges without scanning the rest of the library.
    /// </summary>
    private (int Start, int End) GetBatchBounds(int index)
    {
        var date = _photos[index].TakenAt?.LocalDateTime.Date;
        var start = index;
        while (start > 0 && SameDateBatch(_photos[start - 1], date))
        {
            start--;
        }

        var end = index;
        while (end + 1 < _photos.Count && SameDateBatch(_photos[end + 1], date))
        {
            end++;
        }

        return (start, end);
    }

    private static bool SameDateBatch(PhotoItem photo, DateTime? date)
        => photo.TakenAt?.LocalDateTime.Date == date;

    /// <summary>
    /// Forward follows the timeline: the rest of this day, then older days. It does not cross
    /// from dated pictures into Not dated, or the other way, so finishing the last dated day
    /// stops rather than dumping the viewer into a different kind of batch.
    /// </summary>
    private bool CanMoveBy(int delta)
    {
        var target = _currentIndex + delta;
        return _currentIndex >= 0
            && target >= 0
            && target < _photos.Count
            && _photos[_currentIndex].HasDate == _photos[target].HasDate;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => ShowPreviousPhoto();

    private void ForwardButton_Click(object sender, RoutedEventArgs e) => ShowNextPhoto();

    private void ShowPreviousPhoto()
    {
        if (_stage == ViewStage.Photo && CanMoveBy(-1))
        {
            _currentIndex--;
            ShowCurrentPhoto();
        }
    }

    private void ShowNextPhoto()
    {
        if (_stage == ViewStage.Photo && CanMoveBy(1))
        {
            _currentIndex++;
            ShowCurrentPhoto();
        }
    }

    private async void LikeButton_Click(object sender, RoutedEventArgs e) => await LikeCurrentPhotoAsync();

    private async Task LikeCurrentPhotoAsync()
    {
        if (_selectedFolder is null || _stage != ViewStage.Photo || !TryGetCurrentPhoto(out var photo))
        {
            return;
        }

        SetPhotoActionsEnabled(false);
        try
        {
            var destination = await Task.Run(() => PhotoLibraryService.MoveToLiked(photo.Path, _selectedFolder));
            _undoStack.Push(SingleAction(
                ReversibleActionKind.Liked,
                photo,
                destination,
                PhotoLibraryService.LikedFolderName));
            RemoveCurrentPhoto($"Liked \u2014 moved to {destination}");
        }
        catch (Exception ex)
        {
            SetPhotoActionsEnabled(true);
            ShowStatus($"This picture could not be moved: {ex.Message}");
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e) => await DeleteCurrentPhotoAsync();

    private async Task DeleteCurrentPhotoAsync()
    {
        if (_selectedFolder is null || _stage != ViewStage.Photo || !TryGetCurrentPhoto(out var photo))
        {
            return;
        }

        SetPhotoActionsEnabled(false);
        try
        {
            var destination = await Task.Run(
                () => PhotoLibraryService.MoveToDeleted(photo.Path, _selectedFolder));

            _undoStack.Push(SingleAction(
                ReversibleActionKind.Deleted,
                photo,
                destination,
                PhotoLibraryService.DeletedFolderName));
            RemoveCurrentPhoto(
                $"Deleted \u2014 held in the {PhotoLibraryService.DeletedFolderName} folder. Ctrl+Z brings it back.");
        }
        catch (Exception moveFailure)
        {
            await DeleteThroughRecycleBinAsync(photo, moveFailure);
        }
    }

    /// <summary>
    /// The Deleted folder cannot always be written, so the Recycle Bin stays as a second chance.
    /// Undo still works from there, through the shell rather than a move back.
    /// </summary>
    private async Task DeleteThroughRecycleBinAsync(PhotoItem photo, Exception moveFailure)
    {
        try
        {
            await Task.Run(() => PhotoLibraryService.MoveToRecycleBin(photo.Path));
            _undoStack.Push(SingleAction(
                ReversibleActionKind.Deleted,
                photo,
                movedTo: null,
                PhotoLibraryService.DeletedFolderName));
            RemoveCurrentPhoto("Deleted \u2014 in Recycle Bin. Ctrl+Z brings it back.");
        }
        catch (Exception)
        {
            // The first failure explains why the picture is still here; the fallback's does not.
            SetPhotoActionsEnabled(true);
            ShowStatus($"This picture could not be deleted: {moveFailure.Message}");
        }
    }

    private bool TryGetCurrentPhoto(out PhotoItem photo)
    {
        if (_currentIndex >= 0 && _currentIndex < _photos.Count)
        {
            photo = _photos[_currentIndex];
            return true;
        }

        photo = null!;
        return false;
    }

    private void RemoveCurrentPhoto(string status)
    {
        var removed = _photos[_currentIndex];
        _photos.RemoveAt(_currentIndex);
        RemoveTile(removed);
        UpdateEmptyTimelineVisibility();
        ShowStatus(status);

        if (_photos.Count == 0)
        {
            _currentIndex = -1;
            PhotoImage.Source = null;
            ClearViewerImages();
            SetStage(ViewStage.Timeline);
            return;
        }

        _currentIndex = Math.Min(_currentIndex, _photos.Count - 1);
        ShowCurrentPhoto();
    }

    /// <summary>
    /// Reverses the most recent like or delete. A failed attempt goes back on the stack so a
    /// picture is never dropped from the undo history without having been restored.
    /// </summary>
    private async Task UndoLastActionAsync()
    {
        if (_isUndoing || _isBatchMoving || _stage == ViewStage.Welcome)
        {
            return;
        }

        if (!_undoStack.TryPop(out var action))
        {
            ShowStatus("There is nothing to undo.");
            return;
        }

        _isUndoing = true;
        SetPhotoActionsEnabled(false);
        try
        {
            var remaining = new List<MovedPicture>(action.Pictures);
            var restored = 0;
            while (remaining.Count > 0)
            {
                var move = remaining[0];
                if (!await RestoreMovedPictureAsync(move))
                {
                    _undoStack.Push(action with { Pictures = remaining });
                    ShowStatus(
                        restored == 0
                            ? $"{move.Photo.FileName} could not be brought back from {DescribeLocation(action)}."
                            : $"Restored {restored}, then {move.Photo.FileName} could not be brought back from {DescribeLocation(action)}.");
                    return;
                }

                remaining.RemoveAt(0);
                ReinsertPhoto(move.Photo);
                restored++;
            }

            ShowStatus(
                restored == 1
                    ? $"Restored {action.Pictures[0].Photo.FileName}."
                    : $"Restored {restored} pictures.");
        }
        catch (Exception ex)
        {
            _undoStack.Push(action);
            ShowStatus($"Undo failed: {ex.Message}");
        }
        finally
        {
            _isUndoing = false;
            SetPhotoActionsEnabled(_currentIndex >= 0);
        }
    }

    private static ReversibleAction SingleAction(
        ReversibleActionKind kind,
        PhotoItem photo,
        string? movedTo,
        string destinationName)
        => new(kind, [new MovedPicture(photo, movedTo)], destinationName);

    private static Task<bool> RestoreMovedPictureAsync(MovedPicture move)
        => move.MovedTo is { } movedTo
            ? Task.Run(() => PhotoLibraryService.TryMoveBack(movedTo, move.Photo.Path))
            : RecycleBinService.TryRestoreAsync(move.Photo.Path);

    private static string DescribeLocation(ReversibleAction action)
    {
        if (action.Pictures.Count > 0 && action.Pictures.All(picture => picture.MovedTo is null))
        {
            return "the Recycle Bin";
        }

        return $"the {action.DestinationName} folder";
    }

    private void ReinsertPhoto(PhotoItem photo)
    {
        var index = FindPhotoInsertIndex(photo);
        _photos.Insert(index, photo);
        InsertTile(photo);
        UpdateEmptyTimelineVisibility();

        // Restoring while viewing a picture jumps to the one that came back; from the timeline the
        // current position is only shifted so it keeps pointing at the same picture.
        if (_stage == ViewStage.Photo)
        {
            _currentIndex = index;
            ShowCurrentPhoto();
            return;
        }

        if (index <= _currentIndex)
        {
            _currentIndex++;
        }
    }

    /// <summary>
    /// Same order as the timeline: dated days newest first, pictures within a day in capture
    /// order, undated pictures last. Viewer next/back walks this list, which is why finishing a
    /// day continues into the next older day instead of into a day opened earlier.
    /// </summary>
    private int FindPhotoInsertIndex(PhotoItem photo)
    {
        for (var index = 0; index < _photos.Count; index++)
        {
            if (ComparePhotoOrder(photo, _photos[index]) < 0)
            {
                return index;
            }
        }

        return _photos.Count;
    }

    private static int ComparePhotoOrder(PhotoItem left, PhotoItem right)
    {
        var byPresence = (left.HasDate ? 0 : 1).CompareTo(right.HasDate ? 0 : 1);
        if (byPresence != 0)
        {
            return byPresence;
        }

        var leftDay = left.TakenAt?.LocalDateTime.Date ?? DateTime.MinValue;
        var rightDay = right.TakenAt?.LocalDateTime.Date ?? DateTime.MinValue;
        var byDay = rightDay.CompareTo(leftDay);
        return byDay != 0 ? byDay : CompareTileOrder(left, right);
    }

    /// <summary>
    /// Puts a single tile back in its sorted position, recreating the date section when the undone
    /// action had emptied it. Rebuilding the timeline instead would re-decode every thumbnail.
    /// </summary>
    private void InsertTile(PhotoItem photo)
    {
        if (!MatchesActiveTypeFilter(photo))
        {
            return;
        }

        var date = photo.TakenAt?.LocalDateTime.Date;
        var sectionKey = new TimelineSectionKey(date);
        if (!_sectionsByDate.TryGetValue(sectionKey, out var section))
        {
            section = CreateSection(date, photoCount: 0);
            _sectionsByDate[sectionKey] = section;
            TimelineStack.Children.Insert(FindSectionInsertIndex(date), section.Root);
            AttachSectionHeadingSelection(section);
            AttachSectionVirtualization(section);
        }

        var itemIndex = FindItemInsertIndex(section, photo);
        section.Items.Insert(itemIndex, photo);
        if (section.TilesLoaded)
        {
            var tile = CreateTile(photo);
            _tilesByPhoto[photo] = tile;
            section.Photos.Children.Insert(itemIndex, tile);
        }

        section.CountText.Text = FormatPhotoCount(section.Items.Count);
    }

    private static int FindItemInsertIndex(TimelineSection section, PhotoItem photo)
    {
        for (var index = 0; index < section.Items.Count; index++)
        {
            if (CompareTileOrder(photo, section.Items[index]) < 0)
            {
                return index;
            }
        }

        return section.Items.Count;
    }

    private int FindSectionInsertIndex(DateTime? date)
    {
        for (var index = 0; index < TimelineStack.Children.Count; index++)
        {
            if (TimelineStack.Children[index] is FrameworkElement element
                && CompareSectionOrder(date, element.Tag as DateTime?) < 0)
            {
                return index;
            }
        }

        return TimelineStack.Children.Count;
    }

    /// <summary>
    /// Newest dates lead and undated pictures trail, matching how the timeline is first built.
    /// </summary>
    private static int CompareSectionOrder(DateTime? left, DateTime? right)
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }

        return right is null ? -1 : right.Value.CompareTo(left.Value);
    }

    /// <summary>
    /// Within a section pictures run in capture order with the file name breaking ties.
    /// </summary>
    private static int CompareTileOrder(PhotoItem left, PhotoItem right)
    {
        var byDate = (left.TakenAt ?? DateTimeOffset.MinValue).CompareTo(right.TakenAt ?? DateTimeOffset.MinValue);
        return byDate != 0
            ? byDate
            : string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes a single tile instead of rebuilding the timeline, which previously re-decoded
    /// every visible thumbnail after each like or delete.
    /// </summary>
    private void RemoveTile(PhotoItem photo)
    {
        var sectionKey = new TimelineSectionKey(photo.TakenAt?.LocalDateTime.Date);
        if (!_sectionsByDate.TryGetValue(sectionKey, out var section))
        {
            return;
        }

        section.Items.Remove(photo);
        if (_tilesByPhoto.Remove(photo, out var tile))
        {
            section.Photos.Children.Remove(tile);
        }

        if (section.Items.Count == 0)
        {
            TimelineStack.Children.Remove(section.Root);
            _sectionsByDate.Remove(sectionKey);
            return;
        }

        section.CountText.Text = FormatPhotoCount(section.Items.Count);
    }

    private void UpdateEmptyTimelineVisibility()
    {
        var hasPictures = _photos.Count > 0;
        var hasMatchingPictures = GetTimelinePhotos().Any();
        EmptyTimelinePanel.Visibility = hasMatchingPictures ? Visibility.Collapsed : Visibility.Visible;
        EmptyTimelineText.Text = hasPictures
            ? "No pictures match this type. Choose a different type to see the rest of the folder."
            : "Choose a folder containing JPG, PNG, HEIC, WebP, GIF, BMP, TIFF, or camera RAW pictures.";
    }

    private void SetPhotoActionsEnabled(bool enabled)
    {
        BackButton.IsEnabled = enabled && CanMoveBy(-1);
        ForwardButton.IsEnabled = enabled && CanMoveBy(1);
        DeleteButton.IsEnabled = enabled;
        LikeButton.IsEnabled = enabled;
    }

    private void ReturnToTimeline_Click(object sender, RoutedEventArgs e) => SetStage(ViewStage.Timeline);

    private void CloseRules_Click(object sender, RoutedEventArgs e)
    {
        _rulesDismissed = true;
        RulesPanel.Visibility = Visibility.Collapsed;
    }

    private void PhotoImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        => ShowStatus("Windows could not display this picture. Its image codec may not be installed.");

    private void SetStage(ViewStage stage)
    {
        _stage = stage;

        // Leaving the viewer resets the chrome, so opening the next picture starts uncluttered
        // rather than inheriting whatever was showing when the pointer last left.
        if (stage != ViewStage.Photo)
        {
            ShowTopChrome(false);
            ShowBottomChrome(false);
        }

        WelcomeView.Visibility = stage == ViewStage.Welcome ? Visibility.Visible : Visibility.Collapsed;
        TimelineView.Visibility = stage == ViewStage.Timeline ? Visibility.Visible : Visibility.Collapsed;
        PhotoView.Visibility = stage == ViewStage.Photo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowLoading(bool show, string message = "Loading pictures\u2026")
    {
        LoadingText.Text = message;
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusBanner.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>
    /// Wraps the nullable section date so it can be a dictionary key. Undated pictures share a
    /// single section, keyed by the absence of a date.
    /// </summary>
    private readonly record struct TimelineSectionKey(DateTime? Date);

    private enum ReversibleActionKind
    {
        Deleted,
        Liked,
        Sorted,
    }

    private sealed record MovedPicture(PhotoItem Photo, string? MovedTo);

    /// <summary>
    /// A change that Ctrl+Z can reverse. One entry may cover a whole timeline selection so a
    /// batch delete or folder move is undone together. <see cref="MovedPicture.MovedTo"/> is
    /// where each file sits now, or null when a delete fell back to the Recycle Bin.
    /// </summary>
    private sealed record ReversibleAction(
        ReversibleActionKind Kind,
        IReadOnlyList<MovedPicture> Pictures,
        string DestinationName);

    private sealed class TimelineSection
    {
        public required StackPanel Root { get; init; }
        public required FrameworkElement Heading { get; init; }
        public required PhotoWrapPanel Photos { get; init; }
        public required TextBlock CountText { get; init; }
        public List<PhotoItem> Items { get; } = [];
        public bool TilesLoaded { get; set; }
    }

    private enum ViewStage
    {
        Welcome,
        Timeline,
        Photo,
    }
}
