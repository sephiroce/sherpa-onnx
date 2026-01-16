using Microsoft.Win32;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;

namespace SherpaOnnxASR
{
    public partial class SettingsWindow : Window
    {
        private SherpaOnnxService Sherpa => SherpaOnnxService.Instance;

        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
            UpdateModelStatus();
        }

        private void LoadSettings()
        {
            EncoderBox.Text = AppSettings.EncoderPath;
            DecoderBox.Text = AppSettings.DecoderPath;
            JoinerBox.Text = AppSettings.JoinerPath;
            TokensBox.Text = AppSettings.TokensPath;
            SampleWavBox.Text = AppSettings.SampleWavPath;
            MicGainBox.Text = AppSettings.MicGain.ToString("F1");
            DebugSaveCheck.IsChecked = Sherpa.SaveDebugAudio;
        }

        private void SaveSettings()
        {
            AppSettings.EncoderPath = EncoderBox.Text;
            AppSettings.DecoderPath = DecoderBox.Text;
            AppSettings.JoinerPath = JoinerBox.Text;
            AppSettings.TokensPath = TokensBox.Text;
            AppSettings.SampleWavPath = SampleWavBox.Text;
            
            // Parse Mic Gain and log if changed
            float oldGain = AppSettings.MicGain;
            if (float.TryParse(MicGainBox.Text, out float gain) && gain >= 0.1f && gain <= 20.0f)
            {
                if (gain != oldGain)
                {
                    AppSettings.MicGain = gain;
                    ResultsBox.Text += $"\n🎤 Mic Gain changed: {oldGain:F1}x → {gain:F1}x";
                }
            }
            else
            {
                // Invalid value - keep old value and restore textbox
                MicGainBox.Text = oldGain.ToString("F1");
                ResultsBox.Text += $"\n⚠️ Invalid Mic Gain value. Keeping {oldGain:F1}x";
            }
        }

        private void UpdateModelStatus()
        {
            bool isLoaded = Sherpa.IsModelLoaded;
            string provider = Sherpa.CurrentProvider;
            
            // Show status in ResultsBox instead of removed ModelStatusText
            if (isLoaded)
            {
                ResultsBox.Text = $"✓ Model loaded ({provider})";
            }
            else
            {
                ResultsBox.Text = "Model not loaded. Configure paths and click 'Load Model'.";
            }
            
            DecodeSampleBtn.IsEnabled = isLoaded && !string.IsNullOrEmpty(SampleWavBox.Text);
        }

        private void BrowseFile(System.Windows.Controls.TextBox box, string filter)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog() == true) box.Text = dlg.FileName;
        }

        private void BrowseFolder(System.Windows.Controls.TextBox box, string description)
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = description;
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                box.Text = dlg.SelectedPath;
            }
        }

        private void BrowseEncoder_Click(object sender, RoutedEventArgs e) => BrowseFile(EncoderBox, "ONNX|*.onnx|All|*.*");
        private void BrowseDecoder_Click(object sender, RoutedEventArgs e) => BrowseFile(DecoderBox, "ONNX|*.onnx|All|*.*");
        private void BrowseJoiner_Click(object sender, RoutedEventArgs e) => BrowseFile(JoinerBox, "ONNX|*.onnx|All|*.*");
        private void BrowseTokens_Click(object sender, RoutedEventArgs e) => BrowseFile(TokensBox, "TXT|*.txt|All|*.*");
        private void BrowseSampleWav_Click(object sender, RoutedEventArgs e) 
        {
            BrowseFolder(SampleWavBox, "Select folder containing sample .wav files");
            UpdateModelStatus();
        }
        
        private void DebugSave_Changed(object sender, RoutedEventArgs e)
        {
            Sherpa.SaveDebugAudio = DebugSaveCheck.IsChecked == true;
        }

        private async void LoadModel_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            
            if (!AppSettings.IsConfigured)
            {
                ResultsBox.Text = "Error: Please configure all model paths first.";
                return;
            }

            LoadModelBtn.IsEnabled = false;
            LoadModelBtn.Content = "⏳ Loading...";
            ResultsBox.Text = "Loading model...\n";

            bool success = await Sherpa.LoadModelAsync(
                progress => Dispatcher.Invoke(() => ResultsBox.Text += progress + "\n")
            );

            LoadModelBtn.IsEnabled = true;
            LoadModelBtn.Content = "📦 Load Model";
            
            if (success)
            {
                ResultsBox.Text += $"\n✓ Model loaded successfully with {Sherpa.CurrentProvider} provider";
            }
            else
            {
                ResultsBox.Text += "\n✗ Failed to load model";
            }
            
            UpdateModelStatus();
        }

        private async void DecodeSample_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            
            if (!Sherpa.IsModelLoaded)
            {
                ResultsBox.Text = "Error: Load model first.";
                return;
            }

            if (string.IsNullOrEmpty(AppSettings.SampleWavPath))
            {
                ResultsBox.Text = "Error: Set Sample Wav path first.";
                return;
            }

            DecodeSampleBtn.IsEnabled = false;
            DecodeSampleBtn.Content = "⏳ Decoding...";
            ResultsBox.Text = "Decoding sample files...\n";

            var results = await Sherpa.DecodeSampleFilesAsync(
                AppSettings.SampleWavPath,
                progress => Dispatcher.Invoke(() => ResultsBox.Text += progress + "\n")
            );

            ResultsBox.Text = $"--- Decoded {results.Count} files ---\n\n";
            foreach (var (file, text, duration) in results)
            {
                ResultsBox.Text += $"📂 {file} ({duration:F1}s)\n";
                ResultsBox.Text += $"💬 {text}\n";
                ResultsBox.Text += "---\n";
            }

            DecodeSampleBtn.IsEnabled = true;
            DecodeSampleBtn.Content = "🎵 Decode Sample";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            DialogResult = true;
            Close();
        }
    }
}