using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;
using Microsoft.ML.OnnxRuntime; // NuGet 패키지 필요

namespace MoshiLiveCaption
{
    /// <summary>
    /// Singleton-like service for Sherpa-Onnx model management.
    /// Model stays loaded until explicitly unloaded or replaced.
    /// </summary>
    public class SherpaOnnxService : IDisposable
    {
        private static SherpaOnnxService? _instance;
        public static SherpaOnnxService Instance => _instance ??= new SherpaOnnxService();
        
        private OnlineRecognizer? _recognizer;
        private OnlineStream? _stream;
        private Thread? _decodeThread;
        private volatile bool _isRunning;
        private readonly object _lock = new object();
        
        public string CurrentProvider { get; private set; } = "None";
        public bool IsModelLoaded => _recognizer != null;
        
        public event Action<string, bool>? OnTextReceived;

        private SherpaOnnxService() { }

        /// <summary>
        /// Load or reload model. Model stays in memory until this is called again or Dispose.
        /// </summary>
        public async Task<bool> LoadModelAsync(Action<string>? onProgress = null)
        {
            if (!AppSettings.IsConfigured) 
            {
                onProgress?.Invoke("Model paths not configured");
                return false;
            }

            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        onProgress?.Invoke("Unloading previous model...");
                        
                        // Stop any running decode
                        _isRunning = false;
                        _decodeThread?.Join(200);
                        
                        // Dispose old stream (but keep recognizer reference until new one is ready)
                        _stream?.Dispose();
                        _stream = null;
                        
                        onProgress?.Invoke("Loading new model...");
                        
                        var config = new OnlineRecognizerConfig();
                        config.FeatConfig.SampleRate = 16000;
                        config.FeatConfig.FeatureDim = 80;

                        config.ModelConfig.Transducer.Encoder = AppSettings.FullEncoderPath;
                        config.ModelConfig.Transducer.Decoder = AppSettings.FullDecoderPath;
                        config.ModelConfig.Transducer.Joiner = AppSettings.FullJoinerPath;
                        config.ModelConfig.Tokens = AppSettings.FullTokensPath;
                        config.ModelConfig.NumThreads = 1;
                        config.ModelConfig.Debug = 0;
                        config.DecodingMethod = "greedy_search";
                        config.EnableEndpoint = 1;

                        OnlineRecognizer? newRecognizer = null;
                        string provider = "CPU";
                        
                        // Check if CUDA is available first
                        bool isCudaAvailable = false;
                        try
                        {
                            var providers = Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders();
                            isCudaAvailable = providers.Any(p => p.Contains("CUDA", StringComparison.OrdinalIgnoreCase));
                            onProgress?.Invoke($"Available providers: {string.Join(", ", providers)}");
                        }
                        catch (Exception ex)
                        {
                            onProgress?.Invoke($"Could not check providers: {ex.Message}");
                        }
                        
                        // Try CUDA if available
                        if (isCudaAvailable)
                        {
                            try 
                            {
                                onProgress?.Invoke("CUDA available, trying CUDA provider...");
                                config.ModelConfig.Provider = "cuda";
                                newRecognizer = new OnlineRecognizer(config);
                                provider = "CUDA";
                                onProgress?.Invoke("CUDA provider loaded successfully");
                            }
                            catch (Exception ex)
                            {
                                onProgress?.Invoke($"CUDA load failed: {ex.Message}");
                                newRecognizer = null;
                            }
                        }
                        else
                        {
                            onProgress?.Invoke("CUDA not available in OnnxRuntime");
                        }
                        
                        // Fallback to CPU if CUDA failed or not available
                        if (newRecognizer == null)
                        {
                            onProgress?.Invoke("Loading with CPU provider...");
                            config.ModelConfig.Provider = "cpu";
                            newRecognizer = new OnlineRecognizer(config);
                            provider = "CPU";
                        }

                        // Only now dispose old recognizer and replace
                        _recognizer?.Dispose();
                        _recognizer = newRecognizer;
                        _stream = _recognizer.CreateStream();
                        CurrentProvider = provider;
                        
                        onProgress?.Invoke($"Model loaded with {provider} provider");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        onProgress?.Invoke($"Load failed: {ex.Message}");
                        CurrentProvider = "Error";
                        return false;
                    }
                }
            });
        }

        public void Start()
        {
            if (_recognizer == null || _stream == null) return;
            if (_isRunning) return;
            
            _isRunning = true;
            _decodeThread = new Thread(DecodeLoop);
            _decodeThread.IsBackground = true;
            _decodeThread.Start();
        }

        public void AcceptWaveform(float[] samples)
        {
            if (!_isRunning || _stream == null) return;
            lock (_lock)
            {
                _stream?.AcceptWaveform(16000, samples);
            }
        }

        private string _lastSentText = "";
        
        private void DecodeLoop()
        {
            _lastSentText = "";
            
            while (_isRunning && _recognizer != null && _stream != null)
            {
                try
                {
                    lock (_lock)
                    {
                        if (_recognizer == null || _stream == null) break;
                        
                        if (_recognizer.IsReady(_stream))
                        {
                            _recognizer.Decode(_stream);
                            var result = _recognizer.GetResult(_stream);
                            
                            // Only send the NEW portion of text (incremental)
                            if (!string.IsNullOrEmpty(result.Text) && result.Text.Length > _lastSentText.Length)
                            {
                                string newText = result.Text.Substring(_lastSentText.Length);
                                if (!string.IsNullOrWhiteSpace(newText))
                                {
                                    OnTextReceived?.Invoke(newText, false);
                                }
                                _lastSentText = result.Text;
                            }

                            // On endpoint (pause detected), add line break and reset
                            if (_recognizer.IsEndpoint(_stream))
                            {
                                // Only add line break if there was actual text (not just whitespace)
                                if (!string.IsNullOrWhiteSpace(_lastSentText))
                                {
                                    OnTextReceived?.Invoke("\n", true);
                                }
                                _recognizer.Reset(_stream);
                                _lastSentText = ""; // Reset tracking
                            }
                        }
                    }
                    Thread.Sleep(10);
                }
                catch { break; }
            }
        }

        /// <summary>
        /// Decode sample wav files from a folder
        /// </summary>
        public async Task<List<(string file, string text, double duration)>> DecodeSampleFilesAsync(
            string folderPath, 
            Action<string>? onProgress = null)
        {
            var results = new List<(string file, string text, double duration)>();
            
            if (!Directory.Exists(folderPath)) 
            {
                onProgress?.Invoke("Folder not found: " + folderPath);
                return results;
            }

            var wavFiles = Directory.GetFiles(folderPath, "*.wav").Take(10).ToArray();
            if (wavFiles.Length == 0)
            {
                onProgress?.Invoke("No .wav files found");
                return results;
            }

            if (_recognizer == null)
            {
                onProgress?.Invoke("Model not loaded");
                return results;
            }

            await Task.Run(() =>
            {
                foreach (var file in wavFiles)
                {
                    lock (_lock)
                    {
                        if (_recognizer == null) break;

                        try
                        {
                            onProgress?.Invoke($"Processing: {Path.GetFileName(file)}");
                            
                            // NAudio로 파일 읽기 및 16kHz 리샘플링
                            using var reader = new AudioFileReader(file);
                            
                            ISampleProvider sampler = reader;
                            if (reader.WaveFormat.SampleRate != 16000)
                                sampler = new WdlResamplingSampleProvider(reader, 16000);
                            
                            if (reader.WaveFormat.Channels > 1)
                                sampler = new StereoToMonoSampleProvider(sampler);

                            var sampleList = new List<float>();
                            var buffer = new float[4096];
                            int read;
                            while((read = sampler.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                for(int i = 0; i < read; i++) sampleList.Add(buffer[i]);
                            }
                            
                            var floatSamples = sampleList.ToArray();
                            double duration = floatSamples.Length / 16000.0;

                            using var fileStream = _recognizer.CreateStream();
                            
                            // Add audio samples
                            fileStream.AcceptWaveform(16000, floatSamples);
                            
                            // Add 0.5 second silence padding at the end to prevent cutting
                            var tailPadding = new float[(int)(16000 * 0.2)];
                            fileStream.AcceptWaveform(16000, tailPadding);
                            
                            fileStream.InputFinished();

                            while (_recognizer.IsReady(fileStream))
                            {
                                _recognizer.Decode(fileStream);
                            }

                            var result = _recognizer.GetResult(fileStream);
                            results.Add((Path.GetFileName(file), result.Text, duration));
                        }
                        catch (Exception ex)
                        {
                            onProgress?.Invoke($"Error {Path.GetFileName(file)}: {ex.Message}");
                        }
                    }
                }
            });

            return results;
        }

        /// <summary>
        /// Stop decode loop but keep model in memory
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _decodeThread?.Join(200);
            
            // Reset stream but keep model loaded
            lock (_lock)
            {
                if (_recognizer != null && _stream != null)
                {
                    _stream?.Dispose();
                    _stream = _recognizer.CreateStream();
                }
            }
        }

        /// <summary>
        /// Fully unload model and release memory
        /// </summary>
        public void Unload()
        {
            Stop();
            lock (_lock)
            {
                _stream?.Dispose();
                _recognizer?.Dispose();
                _stream = null;
                _recognizer = null;
                CurrentProvider = "None";
            }
        }

        public void Dispose() => Unload();
    }
}