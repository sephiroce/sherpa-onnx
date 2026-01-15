using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MoshiLiveCaption
{
    public partial class MainWindow : Window
    {
        private AudioService? _audioService;
        private bool _isProcessing = false;
        private string _accumulatedText = "";
        
        // Metrics
        private double _totalAudioSeconds = 0;
        private bool _isVadActive = false;

        // Use singleton service
        private SherpaOnnxService Sherpa => SherpaOnnxService.Instance;

        public MainWindow()
        {
            InitializeComponent();
            _audioService = new AudioService();

            // 1. AudioService 로그 및 VAD 연결
            _audioService.OnLog += Log;
            _audioService.OnVadStatus += (active) => {
                _isVadActive = active;
                Dispatcher.Invoke(UpdateMetrics);
            };
            
            // 2. Audio -> Sherpa 연결
            _audioService.OnAudioAvailable += (samples) => Sherpa.AcceptWaveform(samples);
            _audioService.OnAudioProcessed += (sec) => {
                _totalAudioSeconds += sec;
                Dispatcher.Invoke(UpdateMetrics);
            };

            // 3. Sherpa -> UI 결과 표시 연결
            Sherpa.OnTextReceived += OnTextReceived;
            
            UpdateModelStatus();
        }

        private void UpdateModelStatus()
        {
            string provider = Sherpa.CurrentProvider;
            bool isLoaded = Sherpa.IsModelLoaded;
            
            ControlBtn.IsEnabled = isLoaded;
            
            if (isLoaded)
            {
                StatusText.Text = $"Model: {provider}";
                StatusText.Foreground = provider == "CUDA" ? Brushes.LightGreen : Brushes.LightBlue;
                ControlBtn.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)); // Blue
                ControlBtn.Foreground = Brushes.White;
                
                if (!_isProcessing && CaptionText.Text.Contains("Settings"))
                {
                    CaptionText.Text = "Model loaded. Click Start to begin.";
                }
            }
            else
            {
                StatusText.Text = "Model: None";
                StatusText.Foreground = Brushes.Gray;
            }
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Stop processing before opening settings
            if (_isProcessing) Stop();
            
            var win = new SettingsWindow();
            win.Owner = this;
            win.ShowDialog();
            
            // Update status after settings closed (model may have been loaded)
            UpdateModelStatus();
        }

        private void ControlBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) Stop();
            else Start();
        }

        private void Start()
        {
            if (!Sherpa.IsModelLoaded)
            {
                MessageBox.Show("Open Settings and load model first.", "Model Not Loaded");
                return;
            }

            try
            {
                _accumulatedText = "";
                _totalAudioSeconds = 0;
                CaptionText.Text = "Listening...";
                
                Sherpa.Start();
                _audioService?.StartCapture();

                _isProcessing = true;
                ControlBtn.Content = "⏹ Stop";
                ControlBtn.Background = Brushes.Crimson;
                
                UpdateMetrics();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
                Stop();
            }
        }

        private void Stop()
        {
            _audioService?.StopCapture();
            Sherpa.Stop(); // Stops decode loop but keeps model in memory
            
            _isProcessing = false;
            ControlBtn.Content = "▶ Start";
            ControlBtn.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            _isVadActive = false;
            
            UpdateMetrics();
        }

        private void OnTextReceived(string text, bool isFinal)
        {
            Dispatcher.Invoke(() =>
            {
                if (_accumulatedText == "" && CaptionText.Text == "Listening...")
                    CaptionText.Text = "";

                // Add line break after sentence-ending punctuation
                string processedText = text;
                processedText = processedText.Replace(".", ".\n");
                processedText = processedText.Replace("?", "?\n");
                processedText = processedText.Replace("!", "!\n");
                processedText = processedText.Replace("。", "。\n");
                processedText = processedText.Replace("？", "？\n");
                processedText = processedText.Replace("！", "！\n");
                
                // Avoid double line breaks
                processedText = processedText.Replace("\n\n", "\n");

                _accumulatedText += processedText;

                // 길이 제한
                if (int.TryParse(MaxLenBox.Text, out int maxLen) && maxLen > 0)
                {
                    if (_accumulatedText.Length > maxLen)
                        _accumulatedText = _accumulatedText.Substring(_accumulatedText.Length - maxLen);
                }

                CaptionText.Text = _accumulatedText;
                ScrollArea.ScrollToBottom();
                UpdateMetrics();
            });
        }

        private void UpdateMetrics()
        {
            string provider = Sherpa.CurrentProvider;
            string txtLen = $"Chars: {_accumulatedText.Length}";
            
            if (_isVadActive)
            {
                StatusText.Text = $"Audio: {_totalAudioSeconds:F1}s [{provider}] | {txtLen}";
                StatusText.Foreground = Brushes.LightGreen;
            }
            else
            {
                StatusText.Text = _isProcessing ? $"Silence... [{provider}]" : $"Ready [{provider}]";
                StatusText.Foreground = _isProcessing ? Brushes.Gray : Brushes.White;
            }
        }

        private void Log(string msg) => Console.WriteLine(msg);
        
        private void CloseBtn_Click(object sender, RoutedEventArgs e) 
        {
            Stop();
            _audioService?.Dispose();
            Close();
        }
        
        private void Window_MouseDown(object sender, MouseButtonEventArgs e) 
        { 
            if (e.ChangedButton == MouseButton.Left) this.DragMove(); 
        }
    }
}