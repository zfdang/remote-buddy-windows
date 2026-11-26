// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RemoteBuddy.App.ViewModels;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.App.Views.Pages;

public partial class CommonPhrasesPage : UserControl
{
    public CommonPhrasesPage()
    {
        InitializeComponent();
    }

    private void OnPadButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is CommonPhrasesViewModel vm && sender is Button { Tag: RemoteButton button })
        {
            vm.SelectPosition(button);
        }
    }

    private void OnPhraseRowClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is CommonPhrasesViewModel vm && sender is Button { Tag: string id })
        {
            vm.ChoosePhrase(id);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CommonPhrasesViewModel vm) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入常用语备份",
            Filter = "JSON 文件 (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        var owner = Window.GetWindow(this);
        bool? picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (picked != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            vm.TryImportJson(json);
        }
        catch
        {
            vm.TryImportJson(string.Empty);
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CommonPhrasesViewModel vm) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出常用语备份",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = "common-phrases.json"
        };
        var owner = Window.GetWindow(this);
        bool? picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (picked != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        try
        {
            string json = vm.ExportJson();
            File.WriteAllText(dialog.FileName, json);
        }
        catch { }
    }
}
