using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using youtube_tool.ViewModels;

namespace youtube_tool
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel();
        }

        private async void SelectFolder_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "다운로드 저장 폴더 선택",
                    AllowMultiple = false
                });

                if (folders.Count > 0 && DataContext is MainWindowViewModel vm)
                {
                    string? localPath = folders[0].TryGetLocalPath();
                    if (string.IsNullOrEmpty(localPath))
                    {
                        localPath = Uri.UnescapeDataString(folders[0].Path.LocalPath);
                    }
                    vm.SaveFolderPath = localPath;
                }
            }
        }
    }
}