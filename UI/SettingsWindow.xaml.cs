using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ScreenClipTool.Config;

namespace ScreenClipTool.UI;

public partial class SettingsWindow : Window
{
    private sealed class Row
    {
        public string Combo { get; init; } = "";
        public string Duration { get; init; } = "";
        public HotkeyBinding Binding { get; init; } = null!;
    }

    private readonly ConfigService _configService;
    private readonly AppConfig _config;
    private readonly ObservableCollection<Row> _rows = new();
    private int _editIndex = -1;

    /// <summary>Levé après sauvegarde ; l'argument indique si la capture doit redémarrer.</summary>
    public event Action<bool>? Saved;

    public SettingsWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;
        _config = configService.Clone(configService.Load());

        BindingList.ItemsSource = _rows;
        foreach (var b in _config.Hotkeys)
            _rows.Add(MakeRow(b));

        FpsBox.Text = _config.Fps.ToString();
        CqBox.Text = _config.Cq.ToString();
        BufferMinutesBox.Text = _config.MaxBufferMinutes.ToString();
        OutDirBox.Text = _config.OutputDir;
        AudioCheck.IsChecked = _config.AudioEnabled;
        MicCheck.IsChecked = _config.MicEnabled;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    private static Row MakeRow(HotkeyBinding b) =>
        new() { Combo = b.Describe(), Duration = b.DescribeDuration(), Binding = b };

    // ---- Éditeur de binding ----

    private void DurationUnit_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DurationValueBox != null)
            DurationValueBox.IsEnabled = DurationUnitBox.SelectedIndex != 2;
    }

    private void AddOrUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (HotkeyInput.KeyName is null)
        {
            MessageBox.Show(this,
                "Cliquez dans le champ de combinaison puis pressez la combinaison de touches souhaitée.",
                "Combinaison manquante", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ClipDuration duration;
        if (DurationUnitBox.SelectedIndex == 2)
        {
            duration = ClipDuration.Full;
        }
        else
        {
            if (!int.TryParse(DurationValueBox.Text.Trim(), out var value) || value <= 0)
            {
                MessageBox.Show(this, "Durée invalide : entrez un nombre entier positif.",
                    "Durée invalide", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            duration = ClipDuration.FromSeconds(DurationUnitBox.SelectedIndex == 1 ? value * 60 : value);
        }

        var candidate = new HotkeyBinding
        {
            Modifiers = new List<string>(HotkeyInput.Modifiers),
            Key = HotkeyInput.KeyName,
            DurationSeconds = duration,
        };

        for (int i = 0; i < _rows.Count; i++)
        {
            if (i == _editIndex) continue;
            if (_rows[i].Binding.SameCombo(candidate))
            {
                MessageBox.Show(this,
                    $"La combinaison {candidate.Describe()} est déjà assignée ({_rows[i].Duration}).",
                    "Doublon", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (_editIndex >= 0)
            _rows[_editIndex] = MakeRow(candidate);
        else
            _rows.Add(MakeRow(candidate));
        SortRows();
        ResetEditor();
    }

    /// <summary>Durée la plus courte en haut, "buffer complet" en bas.</summary>
    private void SortRows()
    {
        var sorted = _rows
            .OrderBy(r => r.Binding.DurationSeconds.IsFull ? long.MaxValue : r.Binding.DurationSeconds.Seconds)
            .ToList();
        _rows.Clear();
        foreach (var row in sorted)
            _rows.Add(row);
    }

    private void BindingList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelected();

    private void EditSelected_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void EditSelected()
    {
        int idx = BindingList.SelectedIndex;
        if (idx < 0) return;
        _editIndex = idx;
        var b = _rows[idx].Binding;
        HotkeyInput.SetCombo(b.Modifiers, b.Key);
        if (b.DurationSeconds.IsFull)
        {
            DurationUnitBox.SelectedIndex = 2;
        }
        else if (b.DurationSeconds.Seconds >= 60 && b.DurationSeconds.Seconds % 60 == 0)
        {
            DurationUnitBox.SelectedIndex = 1;
            DurationValueBox.Text = (b.DurationSeconds.Seconds / 60).ToString();
        }
        else
        {
            DurationUnitBox.SelectedIndex = 0;
            DurationValueBox.Text = b.DurationSeconds.Seconds.ToString();
        }
        AddOrUpdateButton.Content = "Mettre à jour";
        CancelEditButton.Visibility = Visibility.Visible;
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        int idx = BindingList.SelectedIndex;
        if (idx < 0) return;
        _rows.RemoveAt(idx);
        ResetEditor();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => ResetEditor();

    private void ResetEditor()
    {
        _editIndex = -1;
        HotkeyInput.ClearCombo();
        DurationValueBox.Text = "15";
        DurationUnitBox.SelectedIndex = 0;
        AddOrUpdateButton.Content = "Ajouter";
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    // ---- Général ----

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Dossier des clips" };
        if (Directory.Exists(OutDirBox.Text))
            dlg.InitialDirectory = OutDirBox.Text;
        if (dlg.ShowDialog(this) == true)
            OutDirBox.Text = dlg.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseInt(FpsBox.Text, 1, 240, "FPS", out int fps)) return;
        if (!TryParseInt(CqBox.Text, 0, 51, "CQ", out int cq)) return;
        if (!TryParseInt(BufferMinutesBox.Text, 1, 720, "Fenêtre max (minutes)", out int minutes)) return;
        var outDir = OutDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outDir))
        {
            MessageBox.Show(this, "Indiquez un dossier de sortie pour les clips.",
                "Dossier manquant", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_rows.Count == 0)
        {
            MessageBox.Show(this, "Ajoutez au moins un raccourci de sauvegarde de clip.",
                "Aucun raccourci", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previous = _configService.Load();
        _config.Fps = fps;
        _config.Cq = cq;
        _config.MaxBufferMinutes = minutes;
        _config.OutputDir = outDir;
        _config.AudioEnabled = AudioCheck.IsChecked == true;
        _config.MicEnabled = MicCheck.IsChecked == true;
        _config.Hotkeys = _rows.Select(r => r.Binding).ToList();

        // Les paramètres d'encodage changent le format des segments : le buffer
        // doit être vidé et ffmpeg relancé pour rester concaténable en -c copy.
        bool restartCapture = previous.Fps != _config.Fps
                              || previous.Cq != _config.Cq
                              || previous.AudioEnabled != _config.AudioEnabled
                              || previous.MicEnabled != _config.MicEnabled;

        _configService.Save(_config);
        Saved?.Invoke(restartCapture);
        SaveHint.Text = restartCapture
            ? "Enregistré ✓ — capture redémarrée"
            : "Enregistré ✓ — raccourcis appliqués";
    }

    private bool TryParseInt(string text, int min, int max, string label, out int value)
    {
        if (int.TryParse(text.Trim(), out value) && value >= min && value <= max)
            return true;
        MessageBox.Show(this, $"{label} : entrez un entier entre {min} et {max}.",
            "Valeur invalide", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
